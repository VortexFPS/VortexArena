using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Loaders.Models;
using SN = System.Numerics;
using L = XonoticGodot.Engine.Simulation.LocomotionBlend.DirLocomotion;

namespace XonoticGodot.Game.Client;

/// <summary>
/// A networked player's rendered model: the IQM <see cref="Skeleton3D"/> scene (built by
/// <see cref="IqmBuilder"/>) driven each frame by the CPU skeletal poser <see cref="PlayerSkeleton"/> — the
/// upper/lower-body split (the torso plays an aim pose while the legs run/strafe) plus the view-pitch AIM
/// bones, exactly as <c>player_skeleton.qc</c> does. Replaces the morph/placeholder path for players.
///
/// Each frame we synthesize the four-pose <see cref="SkeletonAnim"/> from the entity's movement state
/// (<see cref="LocomotionBlend"/>), run <see cref="PlayerSkeleton.FromFrames"/>, then push every bone's posed
/// model-space transform onto the <see cref="Skeleton3D"/> — conjugated from Quake space into Godot space via
/// <see cref="IqmBuilder.ConjugateQuakeWorldToGodot"/> and localized against its parent, so the posed mesh
/// stays consistent with the world-conjugated rests + skin. The built-in <see cref="AnimationPlayer"/> is
/// stopped so it can't fight these per-frame writes.
/// </summary>
public partial class PlayerModel : Node3D
{
    private Skeleton3D _skeleton = null!;
    private SkeletonManager _mgr = null!;
    private PlayerSkeleton _player = null!;
    private int _skel;

    // legs locomotion clips by directional intent (the faithful animdecide set), plus a stable torso clip;
    // resolved once from the model's framegroups. Indexed by LocomotionBlend.DirLocomotion.
    private readonly FrameGroup[] _legClips = new FrameGroup[21];
    private FrameGroup _torsoClip;

    // [W14b LI3] the upper-body ACTION clip table (SHOOT this wave; PAIN/DRAW/TAUNT/MELEE/DIE slot in at Stage 4),
    // resolved once from the model's framegroups with the same name-keyword + fallback discipline as the legs. The
    // SHOOT clip plays non-looping so SampleClip clamps it at the last frame (a held-but-expired shot holds its end
    // pose). Indexed by AnimDecide.AnimUpperAction; None (0) is unused (the static aim pose covers idle).
    private readonly FrameGroup[] _actionClips = new FrameGroup[9];

    private float _legsTime;     // seconds the legs clip has been playing (advances with the active locomotion)
    private LocomotionBlend.DirLocomotion _lastLoco = (LocomotionBlend.DirLocomotion)(-1);

    // 3.3: off-screen / distant pose-cull state (gated by cl_pose_cull at the call site). The notifier tracks
    // whether this model is on-screen; when culling is enabled we skip the interop pose PUSH for an off-screen
    // remote player and refresh distant on-screen players at half rate. The CPU pose (FromFrames) still runs
    // every frame, so a regain shows a fresh pose, never a stale one. NEVER active for the local player.
    private VisibleOnScreenNotifier3D? _visNotifier;
    private bool _onScreen = true;   // assume visible until the notifier says otherwise (no stale first frame)
    private bool _forcePush = true;  // push a fresh pose the next call (set on visibility regain + first frame)
    private int _farPhase;           // per-model parity bit for the distance half-rate stagger
    private int _frameTick;          // running Pose-call counter, parity-matched against _farPhase
    private static int _farPhaseSeq; // running counter to spread the phase across models

    // QW1: a child Node3D that tracks the resolved weapon bone ("tag_weapon"), so a remote skeletal player's
    // held weapon attaches to the HAND bone, not the body root. Parented under the Skeleton3D so its transform
    // is in the same space as the posed bones (worldGodot[]); PushBones sets its local transform from the
    // resolved BoneWeapon. Null when the weapon bone is unresolved (the caller falls back to old behavior).
    private Node3D? _tagWeapon;
    private int _boneWeaponIndex; // 0-based index into the Skeleton3D bones (BoneWeapon - 1); -1 = unresolved

    /// <summary>The marker tracking the resolved weapon bone, or null when the bone is unresolved / not skeletal.
    /// Used by <c>ClientWorld.GetAttachmentMarker</c> to attach a remote player's weapon to the hand bone.</summary>
    public Node3D? TagWeaponMarker => _tagWeapon;

    /// <summary>True once the skeleton + poser are wired (a non-skeletal IQM stays a static prop).</summary>
    public bool Active { get; private set; }

    /// <summary>The networked entity this model renders (set when attached; <c>ClientWorld._Process</c> poses it).</summary>
    public Entity? Bound { get; set; }

    /// <summary>
    /// [freezetag.presentation.frozen_anim] When set, hold the skeleton in the frozen standing/idle pose: the
    /// legs are pinned to the idle locomotion (the morph holds one frame, _legsTime is not advanced) and the
    /// upper-body action overlay is skipped (a frozen player plays no shoot/pain torso). The body still aims with
    /// view pitch (FromFrames runs unchanged), matching Base, which sets the frozen frame and stops the animation.
    /// </summary>
    public bool FrozenHold { get; set; }

    // --- streaming placeholder (perf §9.4 Wave 1) ---------------------------------------------------------
    // While the skeletal model parses on the thread pool, the player renders as this shared gray box (the
    // same one ClientWorld drops for an unresolvable model). Shared mesh+material: a per-shell
    // StandardMaterial3D would compile a fresh pipeline per player. The box also matters for correctness:
    // CsqcModelEffects caches the node's mesh list and re-collects when a cached mesh is FREED — clearing
    // the placeholder is what invalidates that cache once the real meshes arrive.
    private static readonly BoxMesh PlaceholderMesh = new() { Size = new Vector3(24f, 56f, 24f) };
    private static readonly StandardMaterial3D PlaceholderMaterial = new() { AlbedoColor = new Color(0.6f, 0.6f, 0.65f) };
    private MeshInstance3D? _placeholder;

    /// <summary>Show the streaming placeholder box (no-op if already shown). Cleared by <see cref="Setup"/>.</summary>
    public void ShowPlaceholder()
    {
        if (_placeholder is not null)
            return;
        _placeholder = new MeshInstance3D
        {
            Name = "Placeholder",
            Mesh = PlaceholderMesh,
            Position = new Vector3(0f, 28f, 0f),
            MaterialOverride = PlaceholderMaterial,
        };
        AddChild(_placeholder);
    }

    /// <summary>Drop the streaming placeholder (the freed mesh is what re-triggers the csqc mesh-list collect).</summary>
    public void ClearPlaceholder()
    {
        if (_placeholder is null)
            return;
        if (GodotObject.IsInstanceValid(_placeholder))
            _placeholder.QueueFree();
        _placeholder = null;
    }

    /// <summary>
    /// Build the poser over an already-built IQM scene (<paramref name="iqmRoot"/>, the <see cref="IqmBuilder"/>
    /// output) parsed from <paramref name="iqm"/>, using its animation clips and skeletal parameters.
    /// </summary>
    public void Setup(IqmData iqm, Node3D iqmRoot, IReadOnlyList<FrameGroup>? groups, ModelInfo? info)
    {
        ClearPlaceholder(); // the real model replaces the streaming box (even the non-skeletal static prop)

        // The CPU poser owns the bones — silence the AnimationPlayer so it can't overwrite our per-frame
        // poses. This must happen BEFORE the scene enters the tree: autoplay fires on tree entry, and setting
        // Autoplay afterwards is a no-op (Godot warns). On the streamed path Setup runs on a shell that is
        // ALREADY in the tree, so the AddChild below is the tree entry.
        foreach (Node child in iqmRoot.GetChildren())
            if (child is AnimationPlayer ap) { ap.Stop(); ap.Autoplay = ""; }

        AddChild(iqmRoot);
        Skeleton3D? skel = IqmBuilder.FindSkeleton(iqmRoot);
        if (skel is null || iqm.Joints.Length == 0)
            return; // not skeletal — render it as the static prop it is

        _skeleton = skel;
        var model = new IqmSkeletalModel(iqm);
        _mgr = new SkeletonManager();
        _skel = _mgr.Create(model);
        if (_skel == 0)
            return;

        PlayerSkeletonConfig cfg = info is not null
            ? PlayerSkeletonConfig.FromModelInfo(info, _mgr, _skel)
            : new PlayerSkeletonConfig();
        _player = new PlayerSkeleton(_mgr, model, cfg);

        BuildClipTable(iqm, groups);

        // QW1: wire the weapon-tag marker once the poser knows the resolved weapon bone. BoneWeapon is a 1-based
        // bonenum (0 = unresolved); the Skeleton3D bones are 0-based in the SAME order, so the index is
        // BoneWeapon - 1. Parent the marker under the Skeleton3D so PushBones can drive its LOCAL transform from
        // worldGodot[boneIdx] (already in the skeleton's pose space). Leave it null when the bone is unresolved so
        // GetAttachmentMarker falls back to the old entity-root behavior.
        _boneWeaponIndex = _player.BoneWeapon - 1;
        if (_boneWeaponIndex >= 0 && _boneWeaponIndex < _skeleton.GetBoneCount())
        {
            _tagWeapon = new Node3D { Name = "tag_weapon" };
            _skeleton.AddChild(_tagWeapon);
        }
        else
        {
            _boneWeaponIndex = -1;
        }

        Active = true;

        // 3.3: attach a visibility notifier so the off-screen pose-cull (cl_pose_cull) can skip the interop
        // push when this model isn't on-screen. Use a FIXED player-hull AABB in Godot space (do NOT use the
        // skinned mesh AABB — it's the rest-pose bind box and can be tiny/empty for a CPU-posed skeleton), sized
        // generously (ducked..standing with margin so a mid-air or limb-extended player isn't culled early).
        // In a headless/smoke context (no viewport) the notifier never fires ScreenExited, so _onScreen stays
        // true and nothing is skipped; combined with cl_pose_cull defaulting OFF this is doubly safe.
        var aabb = new Aabb(new Vector3(-32f, -8f, -32f), new Vector3(64f, 88f, 64f));
        _visNotifier = new VisibleOnScreenNotifier3D { Name = "PoseCull", Aabb = aabb };
        _visNotifier.ScreenEntered += () => { _onScreen = true; _forcePush = true; };
        _visNotifier.ScreenExited += () => { _onScreen = false; };
        _farPhase = (_farPhaseSeq++) & 1;
        AddChild(_visNotifier);
    }

    /// <summary>
    /// Pose the skeleton for this frame from the entity's movement + view pitch. <paramref name="dt"/> advances
    /// the legs clip. Call once per rendered frame from <c>ClientWorld._Process</c>.
    /// <para>
    /// 3.3 pose-cull: when <paramref name="cullEnabled"/> is set (cl_pose_cull) the interop bone PUSH is skipped
    /// for an off-screen REMOTE player and halved for distant on-screen players (<paramref name="distSqToView"/>
    /// beyond <paramref name="cullDistSq"/>). The CPU locomotion pose below ALWAYS runs every frame, so the
    /// model's logical pose stays current; only how often the <see cref="Skeleton3D"/> is refreshed changes.
    /// <paramref name="isLocal"/> forces a full push (the local player's own model is never culled). The hints
    /// default so existing callers (the <c>--skeleton-smoke</c> test) keep cull OFF and push every frame.
    /// </para>
    /// </summary>
    public void Pose(Entity e, float dt, bool cullEnabled = false, bool isLocal = false,
        float distSqToView = 0f, float cullDistSq = 0f, float serverNow = float.NaN)
    {
        // Render the networked per-entity alpha (Cloaked / fades) every frame, before the skeletal early-out so a
        // non-skeletal static prop / streaming placeholder also fades. Cheap when unchanged (idempotent).
        ApplyAlpha(e.Alpha);

        if (!Active) return;

        bool dead = e.DeadState is DeadFlag.Dying or DeadFlag.Dead || (e.Health <= 0f && e.MaxHealth > 0f);
        // Faithful animdecide locomotion: the 8-direction + duck-variant lower-body clip, inferred client-side
        // from the networked velocity + angles + onground + ducked (Base decides locomotion CSQC-side too — only
        // the upper-body ACTION overlays are networked, and those are still out of scope). Entity.Velocity/Angles
        // are System.Numerics.Vector3 already, so they feed SelectLegsDirectional directly.
        LocomotionBlend.DirLocomotion loco =
            LocomotionBlend.SelectLegsDirectional(e.Velocity, e.Angles, e.OnGround, e.IsDucked, dead);

        // [freezetag.presentation.frozen_anim] A frozen player holds the standing/idle pose: force the idle
        // locomotion and DO NOT advance _legsTime, so the legs morph is pinned to one frame (Base sets the frozen
        // frame and stops the animation). The body still aims with view pitch via FromFrames below. The upper-body
        // action overlay is skipped further down so a frozen player plays no shoot/pain torso.
        if (FrozenHold)
        {
            loco = L.Idle;
            _lastLoco = loco; // pin: equal-loco path increments _legsTime, so keep it consistent without advancing
        }
        else if (loco != _lastLoco) { _legsTime = 0f; _lastLoco = loco; }
        else _legsTime += dt;

        FrameGroup legs = _legClips[(int)loco];

        // [W14b LI3/Stage 4] the upper-body ACTION overlay (draw/pain/shoot/melee/taunt/die). When the server has an
        // active upper action for this player (the expiry-resolved NetEntityState.UpperAction networked onto
        // e.UpperAction) AND we have the server clock (serverNow; NaN on a pure demo / smoke harness), the torso plays
        // the action clip at its phase (now − start) instead of the static aim pose — the legs keep their
        // velocity-derived locomotion. The priority cascade lives in AnimDecide.GetUpperAnim (via SelectTorsoAction):
        // DIE1/DIE2 (DEAD) outrank live actions and never expire, so a dead player's death torso is NEVER stomped by a
        // late pain (and the server only ever networks DIE on a dead player). Falls back to the static aim path
        // otherwise, which is bit-identical to before this wave (FromFrames' static Lerp4 = 0).
        // Default to the static aim pose (bit-identical to pre-W14b); the overlay path replaces it when an
        // action is active. Initialized up front so the compiler sees `anim` definitely assigned regardless
        // of the action-clip branch (the static Split for an overlaid frame is a rare, brief-window cost).
        SkeletonAnim anim = LocomotionBlend.Split(legs, _legsTime, _torsoClip, 0f);
        if (!FrozenHold && !float.IsNaN(serverNow) && e.UpperAction != 0)
        {
            var (active, phase) = LocomotionBlend.SelectTorsoAction(e.UpperAction, e.AnimActionTime, serverNow);
            if (active)
            {
                FrameGroup actionClip = _actionClips[e.UpperAction < _actionClips.Length ? e.UpperAction : 0];
                if (actionClip.FrameCount > 0)
                    anim = LocomotionBlend.Split(legs, _legsTime, actionClip, phase, _actionTag: true);
            }
        }
        // Aim-bone pitch for the torso/arms. Remote players come through ClientEntityView, which renders the BODY
        // yaw-only (#50, e.Angles.X forced to 0) and parks the view pitch on the render-only ViewPitch field (#7)
        // — read that. The local player's own body (third-person crosshair-chase) is bound to a direct entity that
        // never passed through that choke point, so its pitch still rides Angles.X and ViewPitch is 0; fall back to
        // Angles.X there. The two are never both non-zero, so this can't double-count.
        float aimPitch = e.ViewPitch != 0f ? e.ViewPitch : e.Angles.X;
        _player.FromFrames(anim, aimPitch, dead);

        // 3.3: decide whether to PUSH the posed bones onto the Skeleton3D this frame. The synthesis above always
        // ran, so the skip never produces a stale logical pose — only a delayed visual refresh. Order matters:
        // OFF / local / forced always push; an off-screen remote skips (only when the notifier is wired);
        // a distant on-screen remote pushes on parity-matched frames; near players always push.
        bool doPush;
        if (!cullEnabled || isLocal) doPush = true;
        else if (_forcePush) doPush = true;
        else if (_visNotifier is not null && !_onScreen) doPush = false;
        else if (cullDistSq > 0f && distSqToView > cullDistSq) doPush = ((_frameTick & 1) == _farPhase);
        else doPush = true;
        _frameTick++;
        // Clear _forcePush only after a successful push, so a regain that lands on an off-screen-again frame
        // still pushes once it's actually drawn.
        if (doPush) { PushBones(); _forcePush = false; }
    }

    /// <summary>Release the CPU skeleton handle (QC <c>skel_delete</c>). Call before freeing the node.</summary>
    public void ReleaseSkeleton()
    {
        if (Active) { _player.Free(); Active = false; }
    }

    /// <summary>
    /// [crash fix 2026-07-26] Deterministic MAIN-THREAD release of this model's exclusively-owned
    /// native resources — the IqmBuilder-built body <see cref="ArrayMesh"/> and its <see cref="Skin"/>
    /// (one fresh pair per PlayerModel; the ownership audit verified there is NO pool and NO corpse
    /// handoff). Without this they ride GodotSharp finalizers on the .NET finalizer thread, whose
    /// RenderingServer::free races the render loop (the 0xC0000374 family). Everything SHARED is
    /// excluded: surface materials/textures (AssetSystem caches), the AnimationLibrary
    /// (AssetLoader parse cache), and the static placeholder mesh/material (by-reference check).
    /// </summary>
    public override void _ExitTree()
    {
        ReleaseSkeleton(); // idempotent (guarded by Active) — normal teardown already called it
        DisposeOwnedVisuals(this);
        _alphaMeshes.Clear();
        _alphaMeshesChildGen = -1;
    }

    private static void DisposeOwnedVisuals(Node n)
    {
        if (n is MeshInstance3D mi)
        {
            if (mi.Skin is Skin skin)
            {
                mi.Skin = null;
                skin.Dispose();
            }
            if (mi.Mesh is Mesh m && !ReferenceEquals(m, PlaceholderMesh))
            {
                mi.Mesh = null;
                m.Dispose();
            }
        }
        foreach (Node c in n.GetChildren())
            DisposeOwnedVisuals(c);
    }

    // --- per-entity alpha render (W1 alpha-net seam) -----------------------------------------------------
    // The networked Entity.Alpha (default 1 = opaque; the Cloaked mutator seeds default_player_alpha 0.25, fades
    // set < 1) is rendered as a per-instance transparency on every mesh under this model. We use
    // GeometryInstance3D.Transparency (a per-INSTANCE float, 0=opaque..1=fully transparent) — NOT a material edit
    // — because the resolved-model surface materials are CACHED + SHARED across entities by AssetSystem, so
    // mutating them would fade every player using that texture (the same RC3/RC4 lesson CsqcModelEffects documents).
    // Per-instance Transparency keeps each player independent and never touches shared materials.
    //
    // The mesh list is flattened once and cached, rebuilt when the model node tree changes (a swap / freed mesh /
    // the placeholder→real handoff) — detected by child count + validity, mirroring CsqcModelEffects.EnsureMeshCache.
    private readonly List<GeometryInstance3D> _alphaMeshes = new();
    private int _alphaMeshesChildGen = -1; // cheap staleness key: combined child-count fingerprint of the tree
    private float _lastAlphaApplied = float.NaN;

    /// <summary>
    /// Render this entity's networked <see cref="Entity.Alpha"/> as a per-instance transparency on every mesh
    /// under the model (Cloaked / fades / invisibility). <paramref name="alpha"/> is the QC render alpha: 1 = fully
    /// opaque (the default), 0..1 = translucent; ≤0 (gib / hidden) clamps to fully transparent. Idempotent — only
    /// re-walks the tree / re-applies when the value or the mesh set actually changed, so it is cheap per frame.
    /// </summary>
    public void ApplyAlpha(float alpha)
    {
        // Godot Transparency is the inverse of QC alpha: 0 = opaque, 1 = invisible. Clamp QC alpha to [0,1]
        // (a negative gib alpha → fully transparent here; EF_NODRAW hiding is handled separately by the csqc pass).
        float clamped = alpha < 0f ? 0f : (alpha > 1f ? 1f : alpha);
        float transparency = 1f - clamped;

        EnsureAlphaMeshCache();
        // Skip the per-mesh interop when nothing changed (value AND the cached set are unchanged).
        if (transparency == _lastAlphaApplied)
            return;
        _lastAlphaApplied = transparency;
        foreach (GeometryInstance3D mi in _alphaMeshes)
            if (GodotObject.IsInstanceValid(mi))
                mi.Transparency = transparency;
    }

    /// <summary>Rebuild the flattened mesh list when the model tree changed (swap / freed mesh / placeholder→real),
    /// keyed on a cheap child-fingerprint; otherwise reuse it. A rebuild also forces the next alpha re-apply (the
    /// new meshes start opaque and must pick up the current value).</summary>
    private void EnsureAlphaMeshCache()
    {
        int gen = ChildFingerprint();
        bool stale = gen != _alphaMeshesChildGen;
        if (!stale)
            for (int i = 0; i < _alphaMeshes.Count; i++)
                if (!GodotObject.IsInstanceValid(_alphaMeshes[i])) { stale = true; break; }
        if (!stale)
            return;
        _alphaMeshes.Clear();
        CollectGeometry(this, _alphaMeshes);
        _alphaMeshesChildGen = gen;
        _lastAlphaApplied = float.NaN; // fresh meshes are opaque — force a re-apply of the current alpha
    }

    /// <summary>A cheap fingerprint of the model subtree shape (so a model swap / placeholder drop invalidates the
    /// cache without a full tree walk every frame). Counts immediate children plus the skeleton's mesh children —
    /// enough to notice the placeholder→IQM handoff and any node add/remove.</summary>
    private int ChildFingerprint()
    {
        int n = GetChildCount();
        if (GodotObject.IsInstanceValid(_skeleton))
            n = n * 31 + _skeleton.GetChildCount();
        return n;
    }

    private static void CollectGeometry(Node node, List<GeometryInstance3D> into)
    {
        if (node is GeometryInstance3D gi)
            into.Add(gi);
        foreach (Node child in node.GetChildren())
            CollectGeometry(child, into);
    }

    // 3.3: per-bone flag — true when this bone currently has a NON-unit pose scale set. Player skeletons are
    // rigid (every bone is unit-scale), so SetBonePoseScale is a pure-overhead interop call ~50-60×/player/frame;
    // we skip it while the scale stays unit. The flag makes the skip safe for a model that DOES animate a bone's
    // scale: when such a bone returns to unit we set it back to one (instead of leaving the stale scale).
    private bool[]? _boneScaleNonUnit;

    /// <summary>Convert each CPU bone's posed model-space transform → Godot bone-local pose, parents first.</summary>
    private void PushBones()
    {
        int n = _skeleton.GetBoneCount();
        if (_boneScaleNonUnit is null || _boneScaleNonUnit.Length != n)
            _boneScaleNonUnit = new bool[n];
        Span<Transform3D> worldGodot = n <= 256 ? stackalloc Transform3D[n] : new Transform3D[n];
        for (int i = 0; i < n; i++)
        {
            BoneMatrix abs = _player.GetBoneAbs(i + 1); // PlayerSkeleton bones are 1-based; Skeleton3D 0-based, same order
            Transform3D quakeWorld = ToTransform(abs);
            Transform3D wg = IqmBuilder.ConjugateQuakeWorldToGodot(quakeWorld);
            worldGodot[i] = wg;

            int parent = _skeleton.GetBoneParent(i);
            Transform3D local = (parent >= 0 && parent < i) ? worldGodot[parent].AffineInverse() * wg : wg;

            _skeleton.SetBonePosePosition(i, local.Origin);
            _skeleton.SetBonePoseRotation(i, local.Basis.GetRotationQuaternion());

            // 3.3: skip the SetBonePoseScale interop while the scale is unit (the rigid-player common case);
            // only touch it when this bone's scale is non-unit, or to reset a bone that just returned to unit.
            Vector3 scale = local.Basis.Scale;
            bool unit = IsUnitScale(scale);
            if (!unit)
            {
                _skeleton.SetBonePoseScale(i, scale);
                _boneScaleNonUnit[i] = true;
            }
            else if (_boneScaleNonUnit[i])
            {
                _skeleton.SetBonePoseScale(i, Vector3.One);
                _boneScaleNonUnit[i] = false;
            }
        }

        // QW1: drive the weapon-tag marker from the resolved weapon bone. worldGodot[boneIdx] is the bone's
        // transform in the Skeleton3D's pose space (already conjugated Quake→Godot exactly like every body bone
        // above); the marker is a child of the Skeleton3D, so that transform IS its local transform. A remote
        // player's held weapon (reparented to this marker by ViewEntityRenderer) then tracks the hand bone.
        if (_tagWeapon is not null && (uint)_boneWeaponIndex < (uint)n)
            _tagWeapon.Transform = worldGodot[_boneWeaponIndex];
    }

    /// <summary>True when a bone scale is unit within a small epsilon (so the scale interop can be skipped).</summary>
    private static bool IsUnitScale(Vector3 s)
    {
        const float eps = 0.001f;
        return MathF.Abs(s.X - 1f) < eps && MathF.Abs(s.Y - 1f) < eps && MathF.Abs(s.Z - 1f) < eps;
    }

    // A Quake-space BoneMatrix (Fwd/Left/Up columns + Origin) as a Godot Transform3D, still in Quake coords
    // (the conjugation to Godot space happens afterward). Godot's Basis(x,y,z) takes the COLUMN vectors.
    private static Transform3D ToTransform(in BoneMatrix m)
        => new(new Basis(GV(m.Fwd), GV(m.Left), GV(m.Up)), GV(m.Origin));

    private static Vector3 GV(SN.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Resolve the legs' per-intent clips + a torso clip from the model's framegroups (by a name keyword, else
    /// a sensible fallback). Without framegroups, the whole pose range becomes one looping clip.
    /// </summary>
    // The Base animdecide FIXED-INDEX player animation registry (common/animdecide.qh REGISTER_ANIMATION
    // order). Xonotic player IQMs define their clips via NAMELESS `.framegroups` lines; the DP engine
    // auto-names those `groupified_<i>_anim` and Base's framenames tables match exactly those — i.e. the
    // slot INDEX is the contract, not a name. (playtest #32: every port clip used to fall back to
    // groups[0] = DIE1 because the name match found nothing on unnamed groups — remote players "played
    // no animation / held the wrong pose" while locomotion selection itself was correct.)
    private enum Slot
    {
        Die1 = 0, Die2 = 1, Draw = 2, Duck = 3, DuckWalk = 4, DuckJump = 5, DuckIdle = 6, Idle = 7,
        Jump = 8, Pain1 = 9, Pain2 = 10, Shoot = 11, Taunt = 12, Run = 13, RunBackwards = 14,
        StrafeLeft = 15, StrafeRight = 16, Dead1 = 17, Dead2 = 18, ForwardRight = 19, ForwardLeft = 20,
        BackRight = 21, BackLeft = 22, Melee = 23, DuckWalkBackwards = 24, DuckWalkStrafeLeft = 25,
        DuckWalkStrafeRight = 26, DuckWalkForwardRight = 27, DuckWalkForwardLeft = 28,
        DuckWalkBackRight = 29, DuckWalkBackLeft = 30,
    }

    private void BuildClipTable(IqmData iqm, IReadOnlyList<FrameGroup>? groups)
    {
        int total = Math.Max(1, iqm.Frames.Length);
        var whole = new FrameGroup(0, total, 20f, true);

        // A model with NAMED groups (IQM internal anims / community models) resolves by keyword like before;
        // an unnamed `.framegroups` set (every stock Xonotic player model) resolves by the Base slot index.
        bool named = false;
        if (groups is not null)
            foreach (FrameGroup g in groups)
                if (!string.IsNullOrEmpty(g.Name)) { named = true; break; }

        FrameGroup PickByName(string[] keys)
        {
            if (groups is not null)
                foreach (FrameGroup g in groups)
                    foreach (string k in keys)
                        if (!string.IsNullOrEmpty(g.Name) && g.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
                            return g;
            // fall back to the first framegroup, else the whole range.
            return groups is { Count: > 0 } ? groups[0] : whole;
        }

        // Base animfixfps(primary, fallback): a model missing the primary slot uses the fallback slot
        // (animdecide.qc:68-96 — forwardright→straferight, melee→shoot, duckwalk-dirs→duckwalk, …).
        FrameGroup BySlot(Slot idx, Slot fb)
        {
            if (groups is not null)
            {
                if ((int)idx < groups.Count) return groups[(int)idx];
                if (fb != idx && (int)fb < groups.Count) return groups[(int)fb];
            }
            return groups is { Count: > 0 } ? groups[0] : whole;
        }

        // One resolver for both worlds: keyword on named sets, Base slot index on unnamed .framegroups sets.
        FrameGroup Pick(Slot idx, Slot fb, params string[] keys)
            => named ? PickByName(keys) : BySlot(idx, fb);

        // The faithful animdecide locomotion set. Fallbacks mirror animdecide_load_if_needed (animdecide.qc:68-96):
        // the diagonal/back clips fall back to their straight strafe; the duckwalk directional set falls back to
        // plain duckwalk; idle/duckidle/jump/duckjump have their own clips. Named sets keep the keyword chains;
        // unnamed .framegroups sets take the Base slot index (see the Slot enum note).
        FrameGroup idle = Pick(Slot.Idle, Slot.Idle, "idle", "stand");
        FrameGroup run = Pick(Slot.Run, Slot.Run, "run", "forward", "walk");
        FrameGroup runback = Pick(Slot.RunBackwards, Slot.Run, "runbackwards", "run");
        FrameGroup strafeL = Pick(Slot.StrafeLeft, Slot.Run, "strafeleft", "run");
        FrameGroup strafeR = Pick(Slot.StrafeRight, Slot.Run, "straferight", "run");
        FrameGroup duckidle = Pick(Slot.DuckIdle, Slot.Idle, "duckidle", "duck", "crouch");
        FrameGroup duckwalk = Pick(Slot.DuckWalk, Slot.Run, "duckwalk", "duck", "crouch");

        // Dying/dead legs play DIE1 (slot 0) — its non-looping end holds the corpse pose, matching Base's
        // DEAD1 state (die1 frames while dying, then the settled dead1 hold).
        _legClips[(int)L.Dead] = Pick(Slot.Die1, Slot.Die1, "death", "dead", "die");
        _legClips[(int)L.Idle] = idle;
        _legClips[(int)L.Run] = run;
        _legClips[(int)L.RunBackwards] = runback;
        _legClips[(int)L.StrafeLeft] = strafeL;
        _legClips[(int)L.StrafeRight] = strafeR;
        _legClips[(int)L.ForwardLeft] = Pick(Slot.ForwardLeft, Slot.StrafeLeft, "forwardleft", "strafeleft", "run");
        _legClips[(int)L.ForwardRight] = Pick(Slot.ForwardRight, Slot.StrafeRight, "forwardright", "straferight", "run");
        _legClips[(int)L.BackLeft] = Pick(Slot.BackLeft, Slot.StrafeLeft, "backleft", "strafeleft", "run");
        _legClips[(int)L.BackRight] = Pick(Slot.BackRight, Slot.StrafeRight, "backright", "straferight", "run");
        _legClips[(int)L.Jump] = Pick(Slot.Jump, Slot.Jump, "jump");
        _legClips[(int)L.DuckIdle] = duckidle;
        _legClips[(int)L.DuckWalk] = duckwalk;
        _legClips[(int)L.DuckWalkBackwards] = Pick(Slot.DuckWalkBackwards, Slot.DuckWalk, "duckwalkbackwards", "duckwalk", "duck");
        _legClips[(int)L.DuckWalkStrafeLeft] = Pick(Slot.DuckWalkStrafeLeft, Slot.DuckWalk, "duckwalkstrafeleft", "duckwalk", "duck");
        _legClips[(int)L.DuckWalkStrafeRight] = Pick(Slot.DuckWalkStrafeRight, Slot.DuckWalk, "duckwalkstraferight", "duckwalk", "duck");
        _legClips[(int)L.DuckWalkForwardLeft] = Pick(Slot.DuckWalkForwardLeft, Slot.DuckWalk, "duckwalkforwardleft", "duckwalk", "duck");
        _legClips[(int)L.DuckWalkForwardRight] = Pick(Slot.DuckWalkForwardRight, Slot.DuckWalk, "duckwalkforwardright", "duckwalk", "duck");
        _legClips[(int)L.DuckWalkBackLeft] = Pick(Slot.DuckWalkBackLeft, Slot.DuckWalk, "duckwalkbackleft", "duckwalk", "duck");
        _legClips[(int)L.DuckWalkBackRight] = Pick(Slot.DuckWalkBackRight, Slot.DuckWalk, "duckwalkbackright", "duckwalk", "duck");
        _legClips[(int)L.DuckJump] = Pick(Slot.DuckJump, Slot.Jump, "duckjump", "jump");
        _torsoClip = Pick(Slot.Idle, Slot.Idle, "idle", "stand", "aim");

        // [W14b LI3/Stage 4] the upper-body ACTION clips: draw / pain1 / pain2 / shoot / melee / taunt / die1 / die2.
        // Force each clip NON-LOOPING and to the AnimDecide framerate (e.g. SHOOT = 5 fps / 0.2s, DRAW = 3 fps / 0.333s,
        // PAIN = 2 fps / 0.5s, DIE = 0.5 fps / 2.0s, TAUNT = 0.33 fps / ~3.03s) so the client play PHASE clamps at the
        // last frame exactly when the SERVER expiry window elapses — producer and consumer agree on the duration.
        // The Base fallback discipline (animdecide.qc:88 melee → shoot; the duckwalk-variant → duckwalk) is BAKED into
        // each Pick() keyword chain here, which SUBSUMES the cl-csqcmodel.fallbackframe.remap (no runtime frame-id remap).
        FrameGroup ActionClip(AnimDecide.AnimUpperAction a, Slot idx, Slot fb, params string[] keys)
        {
            FrameGroup g = Pick(idx, fb, keys);
            AnimDecide.AnimSpec spec = AnimDecide.SpecFor(a);
            float fps = spec.FrameRate > 0f ? spec.FrameRate : (g.Fps > 0f ? g.Fps : 20f);
            return new FrameGroup(g.FirstFrame, g.FrameCount, fps, loop: false, g.Name);
        }
        _actionClips[(int)AnimDecide.AnimUpperAction.Draw] = ActionClip(AnimDecide.AnimUpperAction.Draw, Slot.Draw, Slot.Draw, "draw", "raise");
        _actionClips[(int)AnimDecide.AnimUpperAction.Pain1] = ActionClip(AnimDecide.AnimUpperAction.Pain1, Slot.Pain1, Slot.Pain1, "pain1", "pain");
        _actionClips[(int)AnimDecide.AnimUpperAction.Pain2] = ActionClip(AnimDecide.AnimUpperAction.Pain2, Slot.Pain2, Slot.Pain1, "pain2", "pain");
        _actionClips[(int)AnimDecide.AnimUpperAction.Shoot] = ActionClip(AnimDecide.AnimUpperAction.Shoot, Slot.Shoot, Slot.Shoot, "shoot", "attack", "fire");
        // Base animdecide.qc:88 melee falls back to shoot — the keyword chain ends with the shoot keys so a model
        // lacking a "melee" framegroup reuses the SHOOT clip (subsumes the fallbackframe melee→shoot remap).
        _actionClips[(int)AnimDecide.AnimUpperAction.Melee] = ActionClip(AnimDecide.AnimUpperAction.Melee, Slot.Melee, Slot.Shoot, "melee", "shoot", "attack", "fire");
        _actionClips[(int)AnimDecide.AnimUpperAction.Taunt] = ActionClip(AnimDecide.AnimUpperAction.Taunt, Slot.Taunt, Slot.Taunt, "taunt");
        _actionClips[(int)AnimDecide.AnimUpperAction.Die1] = ActionClip(AnimDecide.AnimUpperAction.Die1, Slot.Die1, Slot.Die1, "die1", "death1", "death", "dead", "die");
        _actionClips[(int)AnimDecide.AnimUpperAction.Die2] = ActionClip(AnimDecide.AnimUpperAction.Die2, Slot.Die2, Slot.Die1, "die2", "death2", "death", "dead", "die");
    }
}
