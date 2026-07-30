// Client cosmetic-model manager: spawns/updates/removes the freezetag ICE BLOCK and the BUFF carrier GLOW
// from an entity's decoded status effects. Port companions:
//   - ice block      = QC freezetag.qc Freeze() -> the "models/ice/ice.md3" attachment with one of 20 random
//                       looks (the per-freeze cached frame), tinted by the carrier's team color, offset '0 0 16'.
//                       (freezetag.presentation.ice_model)
//   - buff carrier    = QC sv_buffs.qc buff_Effect / the relic glow worn by a buff carrier: "models/relics/relic.md3"
//                       at scale 0.7, wearing the buff's per-type skin (StatusEffectDef.Skin) and tinted by the
//                       buff's per-type color (StatusEffectDef.Color). (buffs.carrier_model)
//
// This subsystem networks NOTHING new: it consumes the already-networked StatusEffects blob (the Frozen state +
// the held buff_* effect) plus the standard entity delta. It is the single per-entity owner of "which cosmetic
// does this entity need this frame", so the host can drive it from one place.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Client;

/// <summary>
/// Per-entity cosmetic-model manager driven by decoded status effects. For each entity it decides which
/// cosmetic attachment it needs THIS frame — the freezetag ICE block while <see cref="StatusEffectsCatalog.Frozen"/>
/// is held, and the BUFF carrier GLOW while any <see cref="StatusEffectDef.IsBuff"/> effect is held — and
/// spawns/updates/removes the matching <see cref="AttachedCosmeticModel"/> under the entity's attach root.
/// One clear owner so there is no per-call-site duplication of the "spawn the ice / spawn the relic" logic.
/// </summary>
public sealed class CosmeticModelLayer
{
    // ---- ICE (freezetag.presentation.ice_model) ----
    /// <summary>QC freezetag ice model ("models/ice/ice.md3"). 20 looks (frames 0..20).</summary>
    private const string IceModel = "models/ice/ice.md3";
    /// <summary>QC ice attach offset '0 0 16' (Quake coords; AttachedCosmeticModel converts to Godot).</summary>
    private static readonly Vector3 IceOffsetQuake = new(0f, 0f, 16f);
    /// <summary>The number of ice looks (frames) — QC picks one of 20 (floor(random()*21) -> 0..20).</summary>
    private const int IceLooks = 20;
    /// <summary>Salt for the deterministic per-entity ice-frame RNG (keeps the look stable per freeze, varied per id).</summary>
    private const int IceFrameSalt = 0x1CE_F2AE;

    // ---- BUFF GLOW (buffs.carrier_model) ----
    /// <summary>QC buff carrier glow model ("models/relics/relic.md3").</summary>
    private const string BuffModel = "models/relics/relic.md3";
    /// <summary>QC buff carrier glow scale (0.7).</summary>
    private const float BuffScale = 0.7f;

    // Per-entity state. Keyed by Entity.Index (the engine slot).
    private readonly Dictionary<int, AttachedCosmeticModel> _ice = new();
    private readonly Dictionary<int, (AttachedCosmeticModel model, int buffDefId)> _buffGlow = new();
    /// <summary>The cached random ice frame per entity — ONE pick per freeze, held until the entity thaws.</summary>
    private readonly Dictionary<int, int> _iceFrame = new();

    private readonly Func<AssetSystem?> _assets;
    private readonly Func<string, VortexArena.Formats.Md3.Md3Data?>? _md3Loader;
    private readonly Func<string, int, Node3D?>? _modelLoader;

    /// <summary>
    /// Construct the layer. <paramref name="assets"/> supplies the live <see cref="AssetSystem"/> (or null in
    /// headless/teardown) so the attached models can texture their built meshes; it is read lazily each spawn.
    /// <paramref name="md3Loader"/> / <paramref name="modelLoader"/> are the host-wired asset readers
    /// (<c>AssetLoader.LoadMd3</c> / <c>AssetLoader.LoadModel</c>) that actually parse the cosmetic model files;
    /// when null the layer resolves nothing (headless).
    /// </summary>
    public CosmeticModelLayer(
        Func<AssetSystem?> assets,
        Func<string, VortexArena.Formats.Md3.Md3Data?>? md3Loader = null,
        Func<string, int, Node3D?>? modelLoader = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _md3Loader = md3Loader;
        _modelLoader = modelLoader;
    }

    /// <summary>
    /// Reconcile <paramref name="e"/>'s cosmetic attachments against its current status effects, spawning new
    /// ones under <paramref name="attachRoot"/> and freeing stale ones. Call once per frame per visible entity.
    /// (A) FROZEN -> the ice block (team-tinted, one cached random look). (B) any BUFF -> the relic carrier glow
    /// (per-buff skin + color); the first held buff effect wins, and the glow is rebuilt if the buff type changes.
    /// </summary>
    public void Drive(Entity e, Node3D attachRoot)
    {
        if (e is null || attachRoot is null) return;
        int idx = e.Index;

        DriveIce(e, idx, attachRoot);
        DriveBuffGlow(e, idx, attachRoot);
    }

    // ---- (A) FROZEN ice block ----
    private void DriveIce(Entity e, int idx, Node3D attachRoot)
    {
        var frozenDef = StatusEffectsCatalog.Frozen;
        bool frozen = frozenDef != null && StatusEffectsCatalog.Has(e, frozenDef);
        bool exists = _ice.TryGetValue(idx, out AttachedCosmeticModel? ice);

        if (frozen)
        {
            Color teamColor = TeamColor(e);
            if (!exists || ice is null || !ice.IsValid)
            {
                // First time frozen this id: pick + cache ONE of the 20 ice looks (stable for this freeze,
                // deterministic per id so two players don't share a look). QC floor(random()*21) -> 0..20.
                int frame = PickIceFrame(idx);
                var model = AttachedCosmeticModel.Create(
                    _assets(), IceModel, skin: 0, rawFrame: frame,
                    localOffsetQuake: IceOffsetQuake, scale: 1f, colormod: teamColor,
                    md3Loader: _md3Loader, modelLoader: _modelLoader);
                if (model != null)
                {
                    attachRoot.AddChild(model);
                    _ice[idx] = model;
                }
            }
            else
            {
                // Already iced — refresh the team tint only when the team actually changed (gated; avoids
                // re-pushing the colormod into the shader every frame).
                if (!ColorsEqual(_iceColor.TryGetValue(idx, out var prev) ? prev : default, teamColor))
                    ice.SetColormod(teamColor);
            }
            _iceColor[idx] = teamColor;
        }
        else if (exists)
        {
            // Thaw: drop the ice block and the cached frame so the NEXT freeze re-rolls a fresh look.
            ice?.QueueFree();
            _ice.Remove(idx);
            _iceFrame.Remove(idx);
            _iceColor.Remove(idx);
        }
    }

    // Last-applied ice tint per id, so the team-change refresh is gated (no field churn in the spec, kept
    // local to the ice path — purely a redundant-write guard).
    private readonly Dictionary<int, Color> _iceColor = new();

    /// <summary>Pick (and cache) the per-entity ice look. Deterministic per id so the look is stable for the
    /// whole freeze yet varied between players. QC: <c>(int)floor(random()*21)</c> clamped 0..20.</summary>
    private int PickIceFrame(int idx)
    {
        if (_iceFrame.TryGetValue(idx, out int cached)) return cached;
        var rng = new Random(idx ^ IceFrameSalt);
        int frame = (int)Math.Floor(rng.NextDouble() * (IceLooks + 1));
        if (frame < 0) frame = 0;
        if (frame > IceLooks) frame = IceLooks;
        _iceFrame[idx] = frame;
        return frame;
    }

    // ---- (B) BUFF carrier glow ----
    private void DriveBuffGlow(Entity e, int idx, Node3D attachRoot)
    {
        // Scan the held effects for the first one that is a buff (StatusEffectDef.IsBuff).
        int buffDefId = -1;
        StatusEffectDef? buffDef = null;
        var all = StatusEffectsCatalog.All;
        foreach (var s in e.StatusEffects)
        {
            if (s.DefId < 0 || s.DefId >= all.Count) continue;
            var def = all[s.DefId];
            if (def != null && def.IsBuff)
            {
                buffDefId = s.DefId;
                buffDef = def;
                break;
            }
        }

        bool exists = _buffGlow.TryGetValue(idx, out var entry);

        if (buffDef != null)
        {
            // Held a buff. Spawn the glow if missing, or rebuild it if the carried buff TYPE changed (a
            // swapper/replacement) so the skin + tint match the new buff.
            bool needsRebuild = !exists || entry.model is null || !entry.model.IsValid || entry.buffDefId != buffDefId;
            if (needsRebuild)
            {
                if (exists) entry.model?.QueueFree();
                _buffGlow.Remove(idx);

                Color colormod = BuffColor(buffDef);
                var model = AttachedCosmeticModel.Create(
                    _assets(), BuffModel, skin: buffDef.Skin, rawFrame: -1,
                    localOffsetQuake: default, scale: BuffScale, colormod: colormod,
                    md3Loader: _md3Loader, modelLoader: _modelLoader);
                if (model != null)
                {
                    attachRoot.AddChild(model);
                    _buffGlow[idx] = (model, buffDefId);
                }
            }
        }
        else if (exists)
        {
            // No buff held — drop the glow.
            entry.model?.QueueFree();
            _buffGlow.Remove(idx);
        }
    }

    /// <summary>
    /// Remove every cosmetic attachment for one entity (it left the PVS / despawned). Frees the live nodes and
    /// drops all three per-entity maps so a later respawn re-rolls a fresh ice look.
    /// </summary>
    public void Remove(int entityIndex)
    {
        if (_ice.TryGetValue(entityIndex, out var ice))
        {
            ice?.QueueFree();
            _ice.Remove(entityIndex);
        }
        if (_buffGlow.TryGetValue(entityIndex, out var entry))
        {
            entry.model?.QueueFree();
            _buffGlow.Remove(entityIndex);
        }
        _iceFrame.Remove(entityIndex);
        _iceColor.Remove(entityIndex);
    }

    /// <summary>Free every cosmetic attachment and reset all per-entity state (map reset / disconnect).</summary>
    public void Clear()
    {
        foreach (var ice in _ice.Values) ice?.QueueFree();
        foreach (var entry in _buffGlow.Values) entry.model?.QueueFree();
        _ice.Clear();
        _buffGlow.Clear();
        _iceFrame.Clear();
        _iceColor.Clear();
    }

    // ---- helpers ----

    /// <summary>The carrier's team tint for the ice block (QC <c>Team_ColorRGB(self.team)</c>), opaque.</summary>
    private static Color TeamColor(Entity e)
    {
        var rgb = Teams.ColorRgb((int)e.Team);
        return new Color(rgb.X, rgb.Y, rgb.Z, 1f);
    }

    /// <summary>The buff's per-type tint (QC <c>buff.m_color</c>), opaque.</summary>
    private static Color BuffColor(StatusEffectDef def)
        => new(def.Color.R, def.Color.G, def.Color.B);

    private static bool ColorsEqual(Color a, Color b)
        => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
}
