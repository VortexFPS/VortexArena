using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Math;
using VortexArena.Formats.Mdl;
using VortexArena.Formats.Sprites;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Game.Loaders.Models;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Legacy;

// What a Quake 1 game inside Xonotic needs of the scene that stock Xonotic does not: models whose file says
// "leave a trail" or "spin" (.mdl header flags), animation the SERVER drives ten times a second on entities it
// networks itself, skins that are pictures inside the model, sprites turned to the view by their own type,
// and the engine's sprite effects. Ports of cl_main.c CL_UpdateNetworkEntity / CL_UpdateNetworkEntityTrail /
// CL_RelinkEffects, cl_parse.c CL_MoveLerpEntityStates and r_sprites.c, each at the size this scene needs.
public sealed partial class GodotLegacyPresentation
{
    private const int EfBrightField = 1, EfRestartAnimBit = 1 << 20, EfTeleportBit = 1 << 21;
    private const int MaxSpriteEffects = 256;        // DarkPlaces: cl.max_effects (MAX_EFFECTS 256)

    /// <summary>What DarkPlaces keeps per network entity beyond its last state (entity_persistent_t, and the
    /// frame lerp of entity_render_t).</summary>
    private sealed class NetEntity
    {
        public long LastFrame = long.MinValue;
        public int ModelIndex, Effects;
        public LegacyNetworkEntityAnimation Animation;
        public LegacyNetworkEntityTrail Trail;
        /// <summary>render.shadertime: cl.time when the entity appeared; a skin group's clock starts there.</summary>
        public double ShaderTime;
        public double LastTime;
    }

    private struct SpriteEffect
    {
        public QcVector Origin;
        public string Model;
        public double StartTime;
        public int StartFrame, EndFrame;
        public float FrameRate;
    }

    private readonly Dictionary<int, NetEntity> _netEntities = new();
    private readonly List<SpriteEffect> _spriteEffects = new();
    private readonly List<Proxy> _spritesToOrient = new();
    private long _sceneFrame;

    /// <summary>Trail segments drawn for network entities whose model or effects ask for one.</summary>
    public long ModelTrailsDrawn { get; private set; }

    private void BeginQuakeFrame()
    {
        _sceneFrame++;
        _spritesToOrient.Clear();
        // Entities that have been gone a while: their record is dropped (a returning one starts afresh anyway).
        if ((_sceneFrame & 1023) == 0 && _netEntities.Count > 0)
        {
            _release.Clear();
            foreach ((int key, NetEntity net) in _netEntities)
                if (_sceneFrame - net.LastFrame > 600) _release.Add(key);
            foreach (int key in _release) _netEntities.Remove(key);
            _release.Clear();
        }
    }

    private void ClearQuakeState()
    {
        _netEntities.Clear();
        _spriteEffects.Clear();
        _spritesToOrient.Clear();
    }

    private double ClientTime => _state?.Time ?? 0;

    // r_lerpmodels (default 1) and r_lerpsprites (default 0), as VM_FrameBlendFromFrameGroupBlend reads them.
    private bool NoLerp(LegacyModelKind kind)
    {
        string name = kind == LegacyModelKind.Sprite ? "r_lerpsprites" : "r_lerpmodels";
        return _cvars.Has(name) ? _cvars.GetFloat(name) == 0 : kind == LegacyModelKind.Sprite;
    }

    /// <summary>
    /// The per-frame work of CL_MoveLerpEntityStates and CL_UpdateNetworkEntity for one network entity that
    /// is not about where it is: the effects its model adds, EF_ROTATE's spin, which poses it is in, and its
    /// trail. Returns the entity's effects with the model's included.
    /// </summary>
    private int UpdateNetworkEntity(int key, in EntityState entity, LegacyModel? model, bool isStatic,
        ref QcVector origin, ref QcVector angles, out NetEntity net)
    {
        if (!_netEntities.TryGetValue(key, out NetEntity? record))
        {
            record = new NetEntity();
            _netEntities[key] = record;
        }
        net = record;
        double time = ClientTime;
        // "models can set flags such as EF_ROCKET" - unless the entity's own effects use those bits. A static
        // entity has its baseline's effects and nothing else (cl_parse.c CL_ParseStatic): a static torch whose
        // model says "rotate" does not.
        uint effects = isStatic ? (uint)entity.Effects : MdlFlags.CombineNetworkEffects((uint)entity.Effects, model?.Effects ?? 0);

        int frame = entity.Frame;
        if (model is not null && frame >= model.NumFrames) frame = 0;      // "if (frame >= model->numframes) frame = 0"

        if (net.LastFrame != _sceneFrame)
        {
            bool fresh = net.LastFrame != _sceneFrame - 1 || net.ModelIndex != entity.ModelIndex;
            if (fresh)
            {
                // CL_ParseStatic: "framegroupblend[0].start = lhrandom(-10, -1)" - a level's torches are each
                // somewhere else in their flicker. (Any spread will do; this one is the same every run.)
                double began = isStatic ? -1.0 - (unchecked((uint)key * 2654435761u) >> 8) % 9000 / 1000.0 : time;
                net.Animation.Reset(frame, began);
                net.Trail.Reset();
                net.ShaderTime = began;
            }
            else if (((net.Effects ^ entity.Effects) & EfTeleportBit) != 0)
            {
                net.Animation.Reset(frame, time);
                net.Trail.Reset();
            }
            else if (((net.Effects ^ entity.Effects) & EfRestartAnimBit) != 0) net.Animation.Restart(frame, time);
            net.ModelIndex = entity.ModelIndex;
            net.Effects = entity.Effects;
            double serverDelta = _state is { } state ? Math.Max(0, state.ServerTime - state.ServerPrevTime) : 0;
            net.Animation.Update(model, frame, time, serverDelta);
        }

        // EF_ROTATE: "angles[1] = ANGLEMOD(100*cl.time)", and the bob of cl_itembobheight (0 by default).
        if ((effects & MdlFlags.EfRotate) != 0)
        {
            angles.Y = MdlFlags.RotateYaw(time);
            float bob = _cvars.Has("cl_itembobheight") ? _cvars.GetFloat("cl_itembobheight") : 0;
            if (bob != 0)
            {
                float speed = _cvars.Has("cl_itembobspeed") ? _cvars.GetFloat("cl_itembobspeed") : 0.5f;
                origin.Z += (float)((Math.Cos(time * speed * (2.0 * Math.PI)) + 1.0) * 0.5 * bob);
            }
        }

        // CL_UpdateNetworkEntityTrail. A static entity (svc_spawnstatic) is not a network entity and leaves none.
        if (!isStatic && net.LastFrame != _sceneFrame)
        {
            string? trail = model is not null ? MdlFlags.TrailEffect(effects) : null;
            // "if (IS_NEXUIZ_DERIVED(gamemode)) trailtype = EFFECT_TR_NEXUIZPLASMA" - overridden by a model's trail.
            if (trail is null && (effects & EfBrightField) != 0) trail = "TR_NEXUIZPLASMA";
            if (entity.TrailEffectNum != 0 && EffectName(entity.TrailEffectNum) is { } named) trail = named;
            if (net.Trail.Step(origin, trail is not null, out QcVector from) && trail is not null && Finite(origin) && Finite(from)
                && (from.X != origin.X || from.Y != origin.Y || from.Z != origin.Z) && Budget())
            {
                // (The C divides the move between the last two SERVER states by their time; the move since the
                // last drawn frame over the time since then is the same velocity.)
                float dt = (float)Math.Clamp(time - net.LastTime, 1.0 / 1000, 0.1);
                NVec3 velocity = (N(origin) - N(from)) / dt;
                if (!_effects.SpawnTrailSegment(trail, N(from), N(origin), velocity)) _effects.Spawn(trail, N(from), N(origin), 1f);
                ModelTrailsDrawn++;
                EffectsSpawned++;
            }
        }
        net.LastFrame = _sceneFrame;
        net.LastTime = time;
        return unchecked((int)effects);
    }

    /// <summary>Shows the poses a network entity's animation state stands for on whatever kind of node its proxy has.</summary>
    private void ApplyNetworkPose(Proxy proxy, NetEntity net, LegacyModel? model)
    {
        if (proxy.Stale || model is null) return;
        if (proxy.Animator is null && proxy.Morph is null && proxy.Sprite is null) return;
        if (!net.Animation.Poses(model, ClientTime, NoLerp(model.Kind), out int a, out int b, out float lerp)) return;
        ShowPoses(proxy, a, b, lerp);
    }

    // Pose a towards pose b by lerp, on a GPU-morphed model, a rebuilt-mesh model (MD3 with a skin, MDL), or a sprite.
    private static void ShowPoses(Proxy proxy, int a, int b, float lerp)
    {
        lerp = float.IsFinite(lerp) ? Math.Clamp(lerp, 0f, 1f) : 0f;
        if (a == proxy.LastFrame && b == proxy.LastFrameB && MathF.Abs(lerp - proxy.LastLerp) <= 0.004f) return;
        if (proxy.Animator is { } animator) animator.SetRawFrameBlend(a, b, lerp);
        else if (proxy.Morph is { } morph) morph.LerpFrames(a, b, lerp);
        // A sprite shows one picture: the heavier of the two (DarkPlaces draws both, each at its share of the
        // alpha, when r_lerpsprites is on; it is off by default).
        proxy.LastFrame = a;
        proxy.LastFrameB = b;
        proxy.LastLerp = lerp;
    }

    /// <summary>
    /// A Quake model's skin for this frame: the picture its skin group shows at the entity's shader time, and
    /// the colormapped form when the entity has pants or shirt colours. The material bookkeeping of the depth
    /// hack and the effect bits is redone when the material changes.
    /// </summary>
    private static void UpdateMdlSkin(Proxy proxy, double shaderTime)
    {
        if (proxy.Mdl is not { } mdl) return;
        bool colormapped = proxy.Tint is { } tint && (tint.Shirt != ModelTint.Black || tint.Pants != ModelTint.Black);
        if (!mdl.UpdateSkin(shaderTime, colormapped)) return;
        proxy.DepthHackAsserted = 0;
        if (proxy.Surfaces is not null)
        {
            // The overrides were made from the material that has just been replaced.
            foreach ((MeshInstance3D instance, int surface, Material? _) in proxy.Surfaces)
                if (GodotObject.IsInstanceValid(instance) && instance.Mesh is { } mesh && surface < mesh.GetSurfaceCount())
                    instance.SetSurfaceOverrideMaterial(surface, null);
            proxy.Surfaces = null;
            proxy.Bits = 0;
        }
    }

    // ---- sprites ---------------------------------------------------------------------------------------

    /// <summary>
    /// A sprite's look for this frame, and its place in the list RenderScene orients once the view is final.
    /// The proxy's placement (origin, and the axes an oriented sprite uses) has already been recorded.
    /// </summary>
    private void SubmitSprite(Proxy proxy, float alpha, int effects, int renderFlags, QcVector colorMod)
    {
        if (proxy.Sprite is not { } sprite) return;
        int frame = proxy.LastFrame == int.MinValue ? 0 : proxy.LastFrame;
        bool additive = (effects & EfAdditive) != 0 || (renderFlags & RfAdditive) != 0;
        Color tint = !Finite(colorMod) || (colorMod.X == 0 && colorMod.Y == 0 && colorMod.Z == 0)
            ? new Color(1, 1, 1, Math.Clamp(alpha, 0f, 1f))
            : new Color(Math.Clamp(colorMod.X, 0f, 8f), Math.Clamp(colorMod.Y, 0f, 8f), Math.Clamp(colorMod.Z, 0f, 8f), Math.Clamp(alpha, 0f, 1f));
        sprite.ShowFrame(frame, additive, tint);
        if (!proxy.SpriteQueued)
        {
            proxy.SpriteQueued = true;
            _spritesToOrient.Add(proxy);
        }
    }

    /// <summary>
    /// r_sprites.c R_Model_Sprite_Draw_TransparentCallback, the part before the quad: each sprite submitted
    /// this frame is turned by its type against the view that RenderScene has just fixed. The two label types
    /// (sized in screen pixels, tracked along the screen edge) are drawn as plain view-plane sprites.
    /// </summary>
    private void OrientSprites()
    {
        if (_spritesToOrient.Count == 0) return;
        QcCoreBuiltins.AngleVectors(View.Angles, out QcVector forward, out QcVector right, out QcVector up);
        NVec3 viewOrigin = N(View.Origin), viewForward = N(forward), viewLeft = -N(right), viewUp = N(up);
        bool viewOk = Finite(View.Origin) && Finite(forward) && Finite(right) && Finite(up);
        foreach (Proxy proxy in _spritesToOrient)
        {
            proxy.SpriteQueued = false;
            if (!viewOk || proxy.Sprite is not { } sprite || !GodotObject.IsInstanceValid(sprite) || !proxy.HasPlacement) continue;
            BoneMatrix m = proxy.Placement;
            float scale = m.Fwd.Length();
            if (!(scale > 0) || !float.IsFinite(scale)) continue;
            SpriteType type = sprite.Data.SpriteType;
            SpriteOrientation.Axes(type, m.Origin, scale, m.Left, m.Up, viewOrigin, viewForward, viewLeft, viewUp, out NVec3 left, out NVec3 spriteUp);
            NVec3 origin = SpriteOrientation.Nudge(m.Origin, viewForward);
            if (type == SpriteType.Overhead)
            {
                // "offset (move nearer to player...)": r_overheadsprites_pushback, 15 by default.
                NVec3 middle = origin - viewOrigin;
                if (middle.LengthSquared() > 0) origin -= NVec3.Normalize(middle) * 15f;
            }
            if (!Finite(left) || !Finite(spriteUp)) continue;
            sprite.Orient(G(origin), G(-left), G(spriteUp));
        }
        _spritesToOrient.Clear();
    }

    // ---- the engine's sprite effects (the "effect" builtin, TE_ sprite explosions) ------------------------

    /// <summary>cl_main.c CL_Effect: a model shown at a point, stepping through frames at a rate, then gone.</summary>
    private void AddSpriteEffect(QcVector origin, string model, int startFrame, int frameCount, float frameRate)
    {
        // "CL_Effect: framerate %f is < 1" / "framecount %i is < 1"
        if (!(frameRate >= 1) || frameCount < 1 || string.IsNullOrEmpty(model) || !Finite(origin) || !LegacyQcHost.IsSafePath(model)) return;
        if (_spriteEffects.Count >= MaxSpriteEffects)
        {
            SpriteEffectsNotDrawn++;
            return;
        }
        _spriteEffects.Add(new SpriteEffect
        {
            Origin = origin, Model = model, StartTime = ClientTime, StartFrame = startFrame,
            EndFrame = (int)Math.Min((long)startFrame + frameCount, int.MaxValue), FrameRate = frameRate,
        });
    }

    /// <summary>cl_main.c CL_RelinkEffects: each live effect is submitted as an entity showing its current frame.</summary>
    private void SubmitSpriteEffects()
    {
        if (_spriteEffects.Count == 0) return;
        double time = ClientTime;
        for (int i = _spriteEffects.Count - 1; i >= 0; i--)
        {
            SpriteEffect effect = _spriteEffects[i];
            double frame = (time - effect.StartTime) * effect.FrameRate + effect.StartFrame;
            int intFrame = frame >= int.MaxValue ? int.MaxValue : frame <= int.MinValue ? int.MinValue : (int)frame;
            if (intFrame < 0 || intFrame >= effect.EndFrame)
            {
                _spriteEffects.RemoveAt(i);
                continue;
            }
            if (_oneOffs >= MaxOneOffsPerFrame) continue;
            _oneOffs++;
            _submittedThisFrame++;
            if (Touch(OneOffKey(effect.Model, 0), effect.Model, 0) is not { Node: not null } proxy) continue;
            LegacyModel? model = ModelData.Load(effect.Model);
            BoneMatrix placement = new(NVec3.UnitX, NVec3.UnitY, NVec3.UnitZ, N(effect.Origin));
            if (!PlaceProxy(proxy, placement)) continue;
            ApplyRenderState(proxy, 1f, proxy.Sprite is null ? unchecked((int)(model?.Effects ?? 0)) : EfNoShadow, 0);
            // The frame number is an animscene; a group's pictures are stepped by the effect's own clock.
            int pose = intFrame;
            if (model?.Scenes is { } scenes) pose = (uint)intFrame < (uint)scenes.Length ? scenes[intFrame].FirstFrame : 0;
            if (!proxy.Stale) ShowPoses(proxy, pose, pose, 0);
            UpdateMdlSkin(proxy, time - effect.StartTime);
            SubmitSprite(proxy, 1f, 0, 0, default);
        }
    }

    /// <summary>
    /// A proxy's placement: for a sprite it is only recorded (the quad is oriented against the view later,
    /// by <see cref="OrientSprites"/>); anything else is moved at once.
    /// </summary>
    private static bool PlaceProxy(Proxy proxy, in BoneMatrix placement)
    {
        if (proxy.Sprite is null) return ApplyPlacement(proxy, placement);
        if (proxy.Node is null) return false;
        if (!Finite(placement.Fwd) || !Finite(placement.Left) || !Finite(placement.Up) || !Finite(placement.Origin))
        {
            if (proxy.Shown)
            {
                proxy.Shown = false;
                proxy.Node.Visible = false;
            }
            return false;
        }
        proxy.HasPlacement = true;
        proxy.Placement = placement;
        return true;
    }

    // TODO(q1-model-light): a Quake 1 level has no light grid. DarkPlaces lights a model there from the
    // lightmap under it (model_brush.c Mod_Q1BSP_LightPoint: ambient = the sampled colour, no direction).
    // Until the map side provides that sample every model of such a level is drawn at full light (the skin
    // shader's "no grid" case). The hook: per submitted entity, ask the level for the light at the entity's
    // origin and hand it to ModelTint.ApplyGridLight(proxy.Node, true, ambient, diffuse, direction) - the
    // shader's second lobe, in display values - instead of the EnableGridLight(node, true) call in Build.
}
