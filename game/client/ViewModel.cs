using System;
using Godot;
using XonoticGodot.Formats.Md3;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The first-person weapon view-model — the Godot successor to CSQC's <c>viewmodel</c> / <c>weaponentity</c>
/// (qcsrc/client/view.qc), including the muzzle-flash attachment the libs flag with
/// <c>TODO(port,client): EFFECT_*_MUZZLEFLASH</c>. It renders the held weapon model in front of the camera
/// and, on each shot, fires the weapon's muzzle effect (from <see cref="EffectSystem"/>) at the model's
/// muzzle <see cref="Md3Tag"/> ("tag_shot" / "tag_weapon"), with a brief flash light and a small kick/bob.
///
/// <para><b>Placement (faithful to Xonotic+DarkPlaces).</b> Xonotic's weapon models (<c>v_*.md3</c>) are
/// authored directly in <i>view space</i> in Quake units: the geometry already sits forward/right/down of the
/// eye (e.g. <c>v_nex</c> spans X≈-12..46), and DP renders the viewmodel entity at the view origin with
/// <c>cl_viewmodel_scale</c> (=1) — there is NO per-model shrink or hand-tuned offset. So we attach this node
/// to the player <see cref="Camera3D"/> and place the model at the camera origin under a fixed Quake→camera
/// basis (the model is NOT scaled). We add <c>cl_gunoffset</c> (default 0) and the right/center/left
/// <c>cl_gunalign</c> nudge, matching <c>CL_WeaponEntity_SetModel</c> + <c>viewmodel_draw</c>.</para>
///
/// <para><b>Sway (cl_followmodel / cl_leanmodel / cl_bobmodel).</b> Ported from <c>viewmodel_animate</c> in
/// view.qc: the gun lags/pulls when accelerating (follow), tilts when turning (lean), and bobs while running
/// on the ground (bob). These produce a view-space <c>gunorg</c> offset and <c>gunangles</c> rotation applied
/// each frame on top of the rest placement. State is read from the player each frame via
/// <see cref="ViewStateProvider"/>.</para>
///
/// Coordinates: the model + sway math live in Quake view space (X fwd, Y left, Z up) and are mapped once into
/// the camera's local Godot frame (<see cref="QuakeViewToCamera"/>). The muzzle effect is still emitted in
/// WORLD space (the muzzle marker's global transform converted back through Godot).
/// </summary>
public partial class ViewModel : Node3D
{
    /// <summary>The live view state the sway math reads (player velocity, view angles, onground).</summary>
    public readonly struct ViewState
    {
        /// <summary>Player velocity in Quake space (units/s).</summary>
        public NVec3 VelocityQuake { get; init; }
        /// <summary>View angles in degrees, Quake convention (X = pitch down-positive, Y = yaw, Z = roll).</summary>
        public NVec3 ViewAnglesQuake { get; init; }
        /// <summary>Whether the player is standing on the ground (gates the run-bob).</summary>
        public bool OnGround { get; init; }
    }

    /// <summary>
    /// Supplies the per-frame <see cref="ViewState"/> for sway. Injected by the host (the
    /// <see cref="PlayerController"/>); when null the weapon simply holds its rest pose with no sway.
    /// </summary>
    public Func<ViewState>? ViewStateProvider { get; set; }

    /// <summary>The effect system that renders muzzle flashes (injected by the client world).</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>EFFECT_* (or effectinfo) name for this weapon's muzzle flash, e.g. "VORTEX_MUZZLEFLASH".</summary>
    [Export] public string MuzzleEffect { get; set; } = "BLASTER_MUZZLEFLASH";

    /// <summary>
    /// The weapon's muzzle-flash MODEL vpath (QC <c>m_muzzlemodel</c>), e.g. <c>"models/flash.md3"</c> for the
    /// Devastator/MineLayer or <c>"models/uziflash.md3"</c> for the Machinegun/Shotgun. Empty = <c>MDL_Null</c>
    /// (most weapons attach NO model flash — only the four that set a non-null <c>m_muzzlemodel</c> do). Set per
    /// equip alongside <see cref="MuzzleEffect"/>; <see cref="Fire"/> only spawns a flash model when this is set.
    /// </summary>
    [Export] public string MuzzleModelPath { get; set; } = "";

    /// <summary>Tag name on the weapon model where shots originate (Quake/Xonotic convention).</summary>
    [Export] public string MuzzleTagName { get; set; } = "tag_shot";

    /// <summary>
    /// QC <c>wepent.movedir</c> — the equipped weapon's model-local <c>tag_shot</c> offset in Quake coords
    /// (X = forward, Y = +left, Z = up), the SAME registered value the server's <c>SetupShot</c> spawns shots
    /// from (<c>WeaponFiring.RegisterMuzzleOffset</c>). Set per equip by the host; <see cref="Fire"/> rotates it
    /// into the live view basis (with the <c>cl_gunalign</c> mirror — Base <c>movedir_aligned</c>) to place the
    /// first-person muzzle-flash burst at the REAL muzzle point, independent of how the viewmodel renders.
    /// Default = <c>WeaponFiring.DefaultMuzzleOffset</c> (the tag-less/dedicated fallback).
    /// </summary>
    public Vector3 MuzzleMovedir { get; set; } = new Vector3(12f, 0f, -8f);

    /// <summary>
    /// <c>cl_viewmodel_scale</c> — uniform scale DarkPlaces applies to the viewmodel matrix. Xonotic's default
    /// is 1: the <c>v_*</c> models are authored at the right size in view space, so we do NOT shrink them.
    /// </summary>
    [Export] public float ViewModelScale { get; set; } = 1f;

    /// <summary>
    /// <c>cl_gunoffset</c> — extra view-space offset (Quake: X fwd, Y left, Z up) applied to the gun on top of
    /// the alignment nudge. Xonotic default is "0 0 0". Exposed for tuning / a future cvar binding.
    /// </summary>
    [Export] public Vector3 GunOffset { get; set; } = Vector3.Zero;

    /// <summary>
    /// <c>cl_gunalign</c> — 1/2 = center, 3 = right (Xonotic default), 4 = left. With shoot-from-eye servers
    /// this is purely the visual gun position. We apply the same small nudge <c>shotorg_adjustfromclient</c>
    /// does so the gun reads as held in the right hand by default.
    /// </summary>
    [Export] public int GunAlign { get; set; } = 3;

    /// <summary>How far the weapon kicks back per shot (view-space Quake units), and how fast it recovers.</summary>
    [Export] public float RecoilKick { get; set; } = 2.0f;
    [Export] public float RecoilRecovery { get; set; } = 8f;

    // --- weapon-switch raise/lower animation (the faithful stand-in for Xonotic's viewmodel_draw raise/drop;
    //     see view.qc:339-362). Instead of popping the new model in, the gun lowers off the bottom of the view
    //     and the new one rises up. -----------------------------------------------------------------------------
    /// <summary>How far (view-space Quake units, straight down) the gun drops when fully holstered — enough to
    /// clear the bottom of the screen for a ~25u model.</summary>
    [Export] public float SwitchLowerDistance { get; set; } = 40f;
    /// <summary>Seconds to lower the gun off-screen (the drop half — Xonotic <c>switchdelay_drop</c>-ish).</summary>
    [Export] public float HolsterTime { get; set; } = 0.10f;
    /// <summary>Seconds to raise the new gun into view (the raise half — Xonotic <c>switchdelay_raise</c>-ish).</summary>
    [Export] public float RaiseTime { get; set; } = 0.15f;

    /// <summary>
    /// The most recent <c>calc_followmodel_ofs</c> result (view-space fwd/right/up, QC <c>cl_followmodel_ofs</c>),
    /// published each frame so the HUD's <c>hud_dynamic_follow</c> effect (QC <c>Hud_Dynamic_Frame</c>) can sway
    /// the whole HUD with the viewmodel. Zero when <see cref="FollowModel"/> is off (the gun isn't swaying). QC
    /// recomputes the offset independently in the HUD; here we share the viewmodel's already-computed value,
    /// matching the cvar's "effect shared with cl_followmodel" semantics.
    /// </summary>
    public Vector3 LastFollowOffset { get; private set; } = Vector3.Zero;

    // --- gun sway cvars (Xonotic xonotic-client.cfg + DP defaults; see qcsrc/client/view.qc) ---------------
    [Export] public bool FollowModel { get; set; } = true;
    [Export] public float FollowSpeed { get; set; } = 0.3f;
    [Export] public float FollowLimit { get; set; } = 135f;
    [Export] public float FollowVelocityLowpass { get; set; } = 0.05f;
    [Export] public float FollowHighpass { get; set; } = 0.05f;
    [Export] public float FollowLowpass { get; set; } = 0.03f;

    /// <summary>
    /// <c>cl_followmodel_velocity_absolute</c> (default 0). When 0 (Base default) the follow sway uses the
    /// <i>view-relative</i> velocity (forward/right/up projection) so the gun lags the direction you actually move.
    /// When 1 it uses the raw world velocity components directly — Base's "ignore velocity direction changes" mode
    /// (xonotic-client.cfg:162: "SIDE-EFFECT: causes a glitch when teleporting / passing through a warpzone").
    /// Read live from the cvar in <see cref="RefreshCvars"/> and branched in <see cref="FollowModelOffset"/>.
    /// </summary>
    [Export] public bool FollowVelocityAbsolute { get; set; } = false;

    [Export] public bool LeanModel { get; set; } = true;
    [Export] public float LeanSpeed { get; set; } = 0.3f;
    [Export] public float LeanLimit { get; set; } = 30f;
    [Export] public float LeanHighpass1 { get; set; } = 0.2f;
    [Export] public float LeanHighpass { get; set; } = 0.2f;
    [Export] public float LeanLowpass { get; set; } = 0.05f;

    [Export] public bool BobModel { get; set; } = true;
    [Export] public float BobModelSide { get; set; } = 0.2f;
    [Export] public float BobModelUp { get; set; } = 0.1f;
    [Export] public float BobModelSpeed { get; set; } = 10f;

    // The fixed model → camera-local basis. The model builders (IqmBuilder/Md3Builder/DpmBuilder/ModelLoader)
    // already convert vertices Quake->Godot WORLD via Coords.ToGodot, so a built gun points Godot +X (= Quake
    // forward), with Godot +Y = up and Godot -Z = the gun's left (Quake +Y). This basis re-aims those built-in
    // axes into the camera's local frame (camera: +X right, +Y up, -Z forward):
    //   model fwd  (Godot +X) -> camera -Z (forward)
    //   model up   (Godot +Y) -> camera +Y (up)
    //   model left (Godot -Z) -> camera -X (left)   i.e. Godot +Z -> camera +X
    // (Basis(x,y,z) takes the COLUMN images of the model's local axes.)
    private static readonly Basis ViewBasis = new(
        new Vector3(0f, 0f, -1f),   // col 0: image of model +X (forward) = camera -Z
        new Vector3(0f, 1f, 0f),    // col 1: image of model +Y (up)      = camera +Y
        new Vector3(1f, 0f, 0f));   // col 2: image of model +Z           = camera +X

    /// <summary>
    /// Host loader for a muzzle-flash model node, keyed by vpath (Base <c>m_muzzlemodel</c>, e.g.
    /// <c>models/flash.md3</c> for the Devastator, <c>models/uziflash.md3</c> for the Machinegun/Shotgun). When
    /// set AND the active weapon's <see cref="MuzzleModelPath"/> is non-empty, <see cref="Fire"/> spawns a fresh
    /// instance attached to the muzzle socket and fades/shrinks it out over three 0.05-second ticks — faithful to
    /// Base <c>W_MuzzleFlash_Model_Think</c> (scale ×0.5, alpha −0.25 each tick; EF_ADDITIVE|EF_FULLBRIGHT).
    /// Injected by the host (e.g. NetGame sets it to <c>path => assets.LoadModel(path)</c>). May return null when
    /// the file is missing (gracefully degrades to no model flash). Null factory = no flash model.
    /// </summary>
    public Func<string, Node3D?>? FlashModelFactory { get; set; }

    private ModelAnimator? _animator;           // optional MD3 vertex-morph animated weapon model
    private AnimationPlayer? _iqmAnimPlayer;    // optional IQM/DPM skeletal AnimationPlayer (h_* rigs); mutually exclusive with _animator — only one is set at a time
    private Node3D _modelRoot = null!;     // holds the weapon mesh (+ its tags), under the Quake->camera basis
    private Marker3D? _muzzleMarker;       // muzzle socket on the model (from tags)
    private bool _surfaceFxApplied;        // viewmodel surface state (short-depth-range / legacy fallback) applied to the current model
    private float _modelAlpha = 1f;        // last alpha applied to the model materials (so we only re-touch on change)
    private OmniLight3D _flashLight = null!;
    private OmniLight3D _fillLight = null!; // soft constant light so the view-model is never a black silhouette
    private float _flashTime;              // remaining flash-light time
    private float _recoil;                 // current recoil displacement (Quake units, decays to 0)
    private Node3D? _muzzleFlashNode;      // live flash-model node (single slot; replaced each Fire call)

    // Player colormap + per-weapon glowmod (Base viewmodel_draw lines 302-321: e.colormap = 256 + c where c =
    // entcs_GetClientColors — the packed shirt/pants profile colors in EVERY mode, #43; e.glowmod =
    // weaponentity_glowmod(wep, c)). _teamGlow is the resolved glow color (per-weapon wr_glow override or the
    // pants palette color); _shirtColor/_pantsColor tint the gun's _shirt/_pants masks so the held weapon reads
    // as "yours". _hasTeam ("has color") is false only when no colors resolved (untinted, native glow).
    // _glowDirty tracks whether the current model's materials already carry the tint so we only re-walk on a
    // change (mirrors _surfaceFxApplied).
    private Color _teamGlow = new(1f, 1f, 1f);
    private bool _hasTeam;
    private bool _glowDirty = true;        // force a material re-walk after a model swap or a color change

    // Weapon-switch raise/lower state. _switchOffset in [0,1]: 0 = fully raised (rest), 1 = fully lowered (off
    // the bottom of the view). _switchDir: -1 raising (→0), +1 lowering (→1), 0 idle. _holsterGrace recovers a
    // keypress-predicted holster that the server never confirms (a denied switch) so the gun can't stay stuck down.
    private float _switchOffset;
    private int _switchDir;
    private float _holsterGrace;

    // The current model's authored shot-tag side offset (Quake view +Y = left), read from the resolved muzzle
    // marker — this is Base's `vecs.y` (the per-model `tag_shot` Y that shotorg_adjustfromclient mirrors/zeroes).
    // Null until a marker resolves; GunAlignOffset falls back to a representative value so left/right still differ
    // on placeholder/early frames. Captured per model swap so cl_gunalign moves each gun by its OWN Base amount.
    private float? _authoredShotSideY;

    // Sway running state (the QC static locals: follow/lean low/high-pass accumulators + bob phase).
    private SwayState _sway;

    // One-shot teleport flag (Base csqcmodel_teleported): set by NotifyTeleported() on the predicted teleport tick,
    // consumed in LeanModelOffset to re-seed the lean prev-angle so the gun doesn't snap-lean across a warpzone.
    private bool _leanTeleported;

    // =================================================================================================
    //  Construction / weapon swap
    // =================================================================================================

    public override void _Ready()
    {
        _modelRoot = new Node3D { Name = "WeaponModel" };
        AddChild(_modelRoot);

        _flashLight = new OmniLight3D
        {
            Name = "MuzzleLight",
            LightColor = new Color(1f, 0.9f, 0.6f),
            LightEnergy = 0f,
            OmniRange = 120f,
            ShadowEnabled = false,
        };
        AddChild(_flashLight);

        // A fill light that travels with the weapon so the first-person model stays legible regardless of how
        // the map's lighting falls near the camera (view weapons otherwise read as a black silhouette). Placed
        // up/right and slightly behind the camera, aimed at the gun which sits forward (camera -Z). Energy/range
        // are generous (Quake-unit range covering the ~25u gun) so the textured surface reads in any map light.
        _fillLight = new OmniLight3D
        {
            Name = "ViewFill",
            LightColor = new Color(1f, 0.98f, 0.95f),
            LightEnergy = 6.0f,
            OmniRange = 96f,
            OmniAttenuation = 0.5f,
            ShadowEnabled = false,
            Position = new Vector3(8f, 14f, 6f), // camera-local: right, up, behind (+Z) — Quake units
            // The gun meshes live on the dedicated viewmodel render layer; restricting the fill to that layer
            // stops it spilling onto nearby world geometry through the camera (it exists only for the gun).
            LightCullMask = ViewModelRenderFx.RenderLayerBit,
        };
        AddChild(_fillLight);

        ApplyRestPlacement();
    }

    /// <summary>
    /// Place the model root at the resting view-space transform: the Quake→camera basis (no shrink — the model
    /// is authored in view space), with <c>cl_gunoffset</c> + the <c>cl_gunalign</c> nudge as the origin. Sway
    /// and recoil are layered on top of this each frame in <see cref="_Process"/>.
    /// </summary>
    private void ApplyRestPlacement()
    {
        Vector3 alignOfs = GunAlignOffset();                       // Quake view space
        Vector3 restQuake = alignOfs + GunOffset;                  // + cl_gunoffset
        _modelRoot.Transform = new Transform3D(
            ViewBasis.Scaled(new Vector3(ViewModelScale, ViewModelScale, ViewModelScale)),
            QuakeViewToCamera(restQuake));
        // The ViewModel node itself carries no extra transform at rest (sway resets it each frame).
        Transform = Transform3D.Identity;
    }

    /// <summary>
    /// Swap in a weapon model from a parsed MD3, with its muzzle effect/tag. The previous model is freed.
    /// If the MD3 has multiple frames a <see cref="ModelAnimator"/> is created and its "idle" clip played;
    /// otherwise a static <see cref="ModelLoader.BuildModel"/> snapshot is used. The muzzle marker is taken
    /// from the model's tags (falling back to <see cref="MuzzleTagName"/> alternatives).
    /// </summary>
    public void SetWeapon(Md3Data md3, string muzzleEffect, string? muzzleTag = null, string muzzleModel = "")
    {
        MuzzleEffect = muzzleEffect;
        MuzzleModelPath = muzzleModel;
        if (!string.IsNullOrEmpty(muzzleTag))
            MuzzleTagName = muzzleTag!;

        // Clear old model.
        foreach (Node c in _modelRoot.GetChildren())
            c.QueueFree();
        _animator = null;
        _iqmAnimPlayer = null;
        _muzzleMarker = null;
        ResetModelFx();

        ApplyRestPlacement();

        if (md3.FrameCount > 1)
        {
            _animator = ModelAnimator.Create(md3, "WeaponAnim");
            _modelRoot.AddChild(_animator);
            _muzzleMarker = ResolveMuzzleMarker(_animator.GetTag);
        }
        else
        {
            MeshInstance3D mesh = ModelLoader.BuildModel(md3, 0);
            _modelRoot.AddChild(mesh);
            Node3D tags = ModelLoader.BuildTags(md3, 0);
            _modelRoot.AddChild(tags);
            _muzzleMarker = ResolveMuzzleMarker(name =>
            {
                foreach (Node c in tags.GetChildren())
                    if (c is Marker3D m && m.Name == name) return m;
                return null;
            });
        }
        ViewModelRenderFx.Apply(_modelRoot); // viewmodel layer + no shadows + no occlusion cull + short-depth-range materials
        ReseedInstanceTint(); // AFTER attach — the new meshes start untinted (playtest #36)
        CaptureAuthoredShotSide();
    }

    /// <summary>Set a weapon that has no model yet (uses a placeholder bar) but still flashes correctly.</summary>
    public void SetWeaponPlaceholder(string muzzleEffect, string? muzzleTag = null, string muzzleModel = "")
    {
        MuzzleEffect = muzzleEffect;
        MuzzleModelPath = muzzleModel;
        if (!string.IsNullOrEmpty(muzzleTag)) MuzzleTagName = muzzleTag!;

        foreach (Node c in _modelRoot.GetChildren())
            c.QueueFree();
        _animator = null;
        _iqmAnimPlayer = null;
        ResetModelFx();

        // The placeholder is authored in Quake view space too (a barrel a few units long forward of the eye),
        // so it shares the same Quake->camera basis as a real model.
        ApplyRestPlacement();

        var body = new MeshInstance3D
        {
            Name = "Placeholder",
            // Quake units: ~24 long down the barrel (+X forward), thin.
            Mesh = new BoxMesh { Size = new Vector3(24f, 4f, 4f) },
            Position = new Vector3(12f, 0f, 0f), // pushed forward so it's in front of the eye
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.17f) },
        };
        _modelRoot.AddChild(body);

        // Muzzle at the front tip of the barrel (+X forward in Quake view space).
        var muzzle = new Marker3D { Name = "tag_shot", Position = new Vector3(24f, 0f, 0f) };
        _modelRoot.AddChild(muzzle);
        _muzzleMarker = muzzle;
        ViewModelRenderFx.Apply(_modelRoot); // layer + no shadows (+ NoDepthTest on the override bar)
        // The placeholder is centered (no authored side); keep the representative-fallback so cl_gunalign
        // left/right still read distinctly on the placeholder bar.
        _authoredShotSideY = null;
        ApplyRestPlacement();
    }

    /// <summary>
    /// Equip a weapon from a prebuilt model node — any format the asset pipeline produces (MD3/IQM/DPM,
    /// skinned + animated), e.g. from <see cref="AssetLoader.LoadModel"/>. The node is placed in view space
    /// (the model is authored there; no shrink); the muzzle socket is resolved by searching the node's
    /// descendants for an MD3 tag marker (<c>tag_shot</c>/<c>tag_weapon</c>), falling back to the model front.
    /// A <c>null</c> model installs the placeholder bar.
    /// </summary>
    /// <param name="attach">
    /// The <c>weapon</c>-tag attachment transform (in the model's built Godot space) that offsets the gun to
    /// the held position — Xonotic attaches the <c>v_*</c> model to the <c>h_*</c> hand model's <c>weapon</c>
    /// bone (≈7u right, ≈15-18u down). Pass <see cref="Transform3D.Identity"/> to place the model at the eye.
    /// </param>
    public void SetWeaponModel(Node3D? model, string muzzleEffect, string? muzzleTag = null, Transform3D? attach = null, string muzzleModel = "")
    {
        if (model is null)
        {
            SetWeaponPlaceholder(muzzleEffect, muzzleTag, muzzleModel);
            return;
        }

        MuzzleEffect = muzzleEffect;
        MuzzleModelPath = muzzleModel;
        if (!string.IsNullOrEmpty(muzzleTag))
            MuzzleTagName = muzzleTag!;

        foreach (Node c in _modelRoot.GetChildren())
            c.QueueFree();
        _animator = null;
        _iqmAnimPlayer = null;
        _lastNetAnimFrame = -1;  // reset so the first networked frame fires on a rising edge after the model swap
        ResetModelFx();

        ApplyRestPlacement();
        // The attachment transform lives in the model's built (Godot) space, so it is applied AS the model's
        // local transform — _modelRoot's ViewBasis then re-aims the whole assembly into the camera frame.
        model.Transform = attach ?? Transform3D.Identity;
        _modelRoot.AddChild(model);
        ViewModelRenderFx.Apply(_modelRoot); // viewmodel layer + no shadows + no occlusion cull + short-depth-range materials
        ReseedInstanceTint(); // AFTER attach — the new meshes start untinted (playtest #36)

        _muzzleMarker = ResolveMuzzleMarker(name => FindMarkerByName(model, name))
                        // Skeletal viewmodels (the h_ HAND RIG rendered for full-model DPM weapons) carry the
                        // muzzle as a `tag_shot` BONE, not a Marker3D — FindMarkerByName won't see it. Fall back
                        // to the skeleton's bone rest and pin a Marker3D there so the flash/light park at the
                        // barrel, in the SAME model space as the rendered gun (so flash + shot origin coincide).
                        ?? ResolveMuzzleMarker(name => MarkerFromSkeletonBone(model, name));

        // Capture the IQM/DPM AnimationPlayer (h_* skeletal rigs) so PlayIdle/PlayReload/SetNetAnimFrame can
        // drive the live weapon animation. IqmBuilder and DpmBuilder always name it "AnimationPlayer" (verified
        // in IqmBuilder.cs:134 and DpmBuilder.cs:140). Null on plain v_*.md3 static models — those have no rig.
        // (r9) The tree can now hold TWO AnimationPlayers: the live-rig equip parents the v_ VISUAL model under
        // the rig's weapon BoneAttachment, and a skeletal v_ (several are 4-bone IQMs with ZERO animations)
        // brings its own EMPTY player — FindChild's first match grabbed that one and every clip lookup found an
        // empty list ([dbg9]-probe confirmed). Prefer the player that actually HAS clips.
        _iqmAnimPlayer = FindAnimationPlayerWithClips(model);

        // Capture the resolved shot tag's authored side offset (Base `vecs.y`) so cl_gunalign moves THIS gun by
        // its own per-model amount. CaptureAuthoredShotSide composes the marker relative to _modelRoot, so it
        // works for both a nested v_ Marker3D and a skeleton bone-rest marker.
        CaptureAuthoredShotSide();
    }

    /// <summary>
    /// Resolve a named muzzle socket (e.g. <c>tag_shot</c>) from a built skeletal model's <see cref="Skeleton3D"/>,
    /// creating a child <see cref="Marker3D"/> under a live <see cref="BoneAttachment3D"/> so the marker FOLLOWS
    /// the bone's animated pose (the flash/light ride the rig's idle sway and fire recoil, matching Base where
    /// the shot tag is part of the animated model). Returns null when the model has no skeleton or no such bone.
    /// With no animation playing the attachment sits at the bone rest — the old static-rest placement.
    /// </summary>
    private static Marker3D? MarkerFromSkeletonBone(Node3D model, string boneName)
    {
        Skeleton3D? skel = XonoticGodot.Game.Loaders.Models.IqmBuilder.FindSkeleton(model);
        if (skel is null)
            return null;
        // Bones may be stored under a sanitized name (IqmBuilder replaces ':','/','@','%','.' with '_'); the
        // muzzle tag names (tag_shot/shot/…) contain none of those, so a direct find suffices, but fall back to
        // a sanitized find for safety.
        int idx = skel.FindBone(boneName);
        if (idx < 0)
            idx = skel.FindBone(boneName.Replace(':', '_').Replace('/', '_').Replace('@', '_').Replace('%', '_').Replace('.', '_'));
        if (idx < 0)
            return null;
        var follow = new BoneAttachment3D { Name = boneName + "_follow" };
        // Parent under the skeleton FIRST so the attachment resolves it, then bind the bone by name.
        skel.AddChild(follow);
        follow.BoneName = skel.GetBoneName(idx);
        var marker = new Marker3D { Name = boneName };
        follow.AddChild(marker);
        return marker;
    }

    /// <summary>
    /// Capture the model's authored shot-tag side offset (Base <c>vecs.y</c>, the value
    /// <c>shotorg_adjustfromclient</c> mirrors for left / zeroes for center) from the resolved muzzle marker, so
    /// <c>cl_gunalign</c> moves THIS gun by its own per-model amount rather than a fixed representative value. The
    /// marker sits in built-Godot model space where Quake +Y (left) maps to Godot −Z (see <see cref="ViewBasis"/>),
    /// so the Quake side Y of the shot tag is the marker's local −Z. Rebuilds the rest placement so the change
    /// takes effect immediately. Cleared (back to the representative fallback) when no marker resolves.
    /// </summary>
    private void CaptureAuthoredShotSide()
    {
        if (_muzzleMarker is not null && GodotObject.IsInstanceValid(_muzzleMarker)
            && _muzzleMarker.IsInsideTree() && _modelRoot is not null && GodotObject.IsInstanceValid(_modelRoot)
            && _modelRoot.IsInsideTree())
        {
            // The marker lives somewhere under _modelRoot (possibly nested through an attach transform / skeleton).
            // Compose its transform relative to _modelRoot so we read the shot tag in the SAME built-model space the
            // ViewBasis maps from. Quake +Y (left) maps to Godot −Z (see ViewBasis), so the side Y is the local −Z.
            Transform3D rel = _modelRoot.GlobalTransform.AffineInverse() * _muzzleMarker.GlobalTransform;
            _authoredShotSideY = -rel.Origin.Z;
        }
        else
        {
            _authoredShotSideY = null;
        }
        ApplyRestPlacement();
    }

    private Marker3D? ResolveMuzzleMarker(Func<string, Marker3D?> lookup)
    {
        // Try the configured name, then the common Quake/Xonotic muzzle tag aliases.
        foreach (string candidate in new[] { MuzzleTagName, "tag_shot", "tag_weapon", "shot", "muzzle", "tag_barrel" })
        {
            Marker3D? m = lookup(candidate);
            if (m is not null)
                return m;
        }
        return null;
    }

    /// <summary>Depth-first search for a <see cref="Marker3D"/> descendant whose name matches (case-insensitive).</summary>
    private static Marker3D? FindMarkerByName(Node root, string name)
    {
        foreach (Node c in root.GetChildren())
        {
            if (c is Marker3D m && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                return m;
            if (FindMarkerByName(c, name) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Set the held weapon's colormap tint + glowmod, port of the per-child loop in Base
    /// <c>viewmodel_draw</c> (view.qc:302-321): <c>e.colormap = 256 + c</c> where <c>c</c> is
    /// <c>entcs_GetClientColors(current_player)</c> — the local player's packed shirt/pants colors,
    /// UNCONDITIONALLY (the FFA profile color; teamplay's team color falls out because the server forces
    /// clientcolors to the team) — and <c>e.glowmod = weaponentity_glowmod(wep, c)</c> (the per-weapon
    /// <c>wr_glow</c> override, falling back to the pants palette color). So the first-person gun reads as
    /// <i>yours</i> in every mode (#43). <paramref name="hasColor"/> false (colorless — colors never
    /// networked / spectating nobody) leaves the gun untinted with its native glow.
    ///
    /// <para>Applied two ways to cover both view-model rig kinds: full-rig <c>h_*</c> hand models render through
    /// <see cref="PlayerSkinShader"/> (instance-uniform colormod/glow via <see cref="ModelTint"/>); invisible-hand
    /// <c>v_*</c> models render with a plain <see cref="StandardMaterial3D"/>, so the tint is layered onto the
    /// surface materials in <see cref="ApplyMaterialFx"/>. Idempotent — only re-walks when the color changes.</para>
    /// </summary>
    public void SetPlayerColors(Color glow, Color shirt, Color pants, bool hasColor)
    {
        if (_hasTeam == hasColor && _teamGlow == glow && _shirtColor == shirt && _pantsColor == pants)
            return;
        _teamGlow = glow;
        _shirtColor = shirt;
        _pantsColor = pants;
        _hasTeam = hasColor;
        _glowDirty = true;
        ReapplyInstanceTint();
    }

    private Color _shirtColor = new(0f, 0f, 0f);
    private Color _pantsColor = new(0f, 0f, 0f);

    /// <summary>
    /// The lightgrid-sampled model light at the player's position (#41, upgraded r14 to real DP grid
    /// SHADING): DP lights every model from the BSP lightgrid (<c>Mod_Q3BSP_LightPoint</c>) — ambient +
    /// a directed lobe along the baked light direction + a colored specular, all position-varying. The
    /// host samples the grid at the camera each frame and pushes the terms here; they ride the skin
    /// shader's per-instance <c>grid_*</c> uniforms (see <c>PlayerSkinShader</c>), which switch the gun
    /// onto the DP-formula branch and off Godot's scene lights. <paramref name="on"/> false = no grid on
    /// the map → the PBR + fill-light fallback (the pre-#41 look). Values are DP scale (1.0 ≈ grid byte
    /// 128, may exceed 1); <paramref name="dir"/> is normalized, GODOT world axes, pointing AT the light.
    /// Epsilon-gated: the grid varies smoothly, so most frames skip the tree-walk.
    /// </summary>
    public void SetGridLight(bool on, Vector3 ambient, Vector3 diffuse, Vector3 dir)
    {
        if (on == _gridOn
            && (ambient - _gridAmbient).LengthSquared() < 1e-4f
            && (diffuse - _gridDiffuse).LengthSquared() < 1e-4f
            && (dir - _gridDir).LengthSquared() < 1e-4f)
            return;
        _gridOn = on;
        _gridAmbient = ambient;
        _gridDiffuse = diffuse;
        _gridDir = dir;
        // The always-on fill exists to keep the PBR-lit gun legible; the grid-lit branch ignores scene
        // lights entirely (EMISSION output), so leaving the omni on would only spill onto nearby world
        // geometry through the camera.
        if (_fillLight is not null && GodotObject.IsInstanceValid(_fillLight))
            _fillLight.Visible = !on;
        PushGridLight();
    }

    private bool _gridOn;
    private Vector3 _gridAmbient = Vector3.One;
    private Vector3 _gridDiffuse = Vector3.Zero;
    private Vector3 _gridDir = Vector3.Up;

    /// <summary>
    /// Push the grid-light uniforms, folding in the muzzle-flash pop while <see cref="_flashTime"/> runs:
    /// the grid branch ignores the flash OmniLight (EMISSION path), but in DP the muzzle dlight DOES light
    /// the viewmodel (r_shadow_realtime_dlight 1), so a warm ambient boost stands in for it.
    /// </summary>
    private void PushGridLight()
    {
        if (_modelRoot is null || !GodotObject.IsInstanceValid(_modelRoot))
            return;
        Vector3 ambient = _gridAmbient;
        if (_gridOn && _flashTime > 0f)
        {
            float f = Mathf.Clamp(_flashTime / 0.06f, 0f, 1f);
            ambient += new Vector3(1f, 0.93f, 0.8f) * (0.5f * f);
        }
        ModelTint.ApplyGridLight(_modelRoot, _gridOn, ambient, _gridDiffuse, _gridDir);
    }

    /// <summary>Push the current team tint onto the model's skin-shader instance uniforms (shirt/pants =
    /// the team color), plus the grid-light terms. Harmless on plain-material v_ models (no such uniforms —
    /// the StandardMaterial tint in ApplyMaterialFx handles those instead).</summary>
    private void ReapplyInstanceTint()
    {
        if (_modelRoot is null || !GodotObject.IsInstanceValid(_modelRoot))
            return;
        Color shirt = _hasTeam ? _shirtColor : ModelTint.Black;
        Color pants = _hasTeam ? _pantsColor : ModelTint.Black;
        Color glowU = _hasTeam ? _teamGlow : ModelTint.White;
        ModelTint.Apply(_modelRoot, ModelTint.White, glowU, shirt, pants);
        PushGridLight();
    }

    // =================================================================================================
    //  Firing
    // =================================================================================================

    /// <summary>
    /// Play one shot: spawn the muzzle flash at the muzzle socket (world space), pop the flash light, and
    /// apply recoil. Call this from the client when the local player's weapon fires (the CSQC
    /// <c>W_MuzzleFlash</c> / weapon attack hook). <paramref name="effectOverride"/> lets a specific shot use
    /// a different effect (e.g. secondary fire).
    /// </summary>
    public void Fire(string? effectOverride = null)
    {
        string effect = string.IsNullOrEmpty(effectOverride) ? MuzzleEffect : effectOverride!;

        // Muzzle flash: the REAL per-weapon effectinfo burst, via the same Spawn path a networked W_MuzzleFlash
        // from another player takes (parsed effectinfo blocks, the block dlight, the faithful/modern router).
        // Origin/direction are the port of Base NET_HANDLE(w_muzzleflash)'s first-person branch (all.qc:742-775):
        //   org = eye + the weapon's movedir_aligned rotated into the VIEW basis (forward*md.x + right*-md.y + up*md.z)
        //   dir = view forward * 1000
        // NOT the rendered tag_shot's world transform: the viewmodel draws in compressed view space hung off the
        // camera, so the tag's world position sits short of the real muzzle and its basis is authored/bone-rest
        // arbitrary — both read visibly wrong on screen (playtest follow-up to #49). This node is a direct child
        // of the camera at identity, so its GlobalPosition IS the render eye.
        if (ViewStateProvider is not null)
        {
            AngleVectorsQuake(ViewStateProvider().ViewAnglesQuake, out NVec3 fwd, out NVec3 right, out NVec3 up);
            Vector3 md = MovedirAligned();
            NVec3 eye = Coords.ToQuake(GlobalPosition);
            NVec3 org = eye + fwd * md.X + right * -md.Y + up * md.Z; // QC right*-vecs.y: model Y is +left
            Effects?.MuzzleFlash(effect, org, fwd * 1000f);
        }
        else
        {
            // No view state wired (tests/tools): fall back to the muzzle socket's world transform.
            Transform3D muzzleXf = MuzzleGlobalTransform();
            Effects?.MuzzleFlash(effect, Coords.ToQuake(muzzleXf.Origin), Coords.ToQuake(-muzzleXf.Basis.Z) * 1000f);
        }

        // Flash light + recoil (+ the grid-lit ambient pop standing in for the flash light — see PushGridLight).
        _flashTime = 0.06f;
        _flashLight.LightEnergy = 3.0f;
        if (_gridOn)
            PushGridLight();
        _recoil = Mathf.Min(_recoil + RecoilKick, RecoilKick * 2f);

        // Muzzle-flash MODEL (Base W_MuzzleFlash_Model — models/flash.md3 MDL_DEVASTATOR_MUZZLEFLASH and
        // every weapon that passes a non-null m_muzzlemodel to W_MuzzleFlash). Spawns a tiny additive mesh
        // at the muzzle socket and fades/shrinks it out over three 0.05-second ticks, faithfully matching
        // W_MuzzleFlash_Model_Think (scale*=0.5, alpha-=0.25, nextthink+=0.05 — 3 ticks → alpha 0 → free).
        // The previous flash node (if still alive) is freed first (QC: wepent.muzzle_flash reused).
        SpawnMuzzleFlashModel();

        // If the weapon has a "fire"/"attack" clip, play it once. FindFireClip() plays the clip as a side-effect
        // on whichever backend is active (IQM AnimationPlayer or the MD3 ModelAnimator), so a bare call suffices.
        FindFireClip();
    }

    /// <summary>
    /// Spawn the muzzle-flash model node at the muzzle socket, port of QC <c>W_MuzzleFlash_Model</c> /
    /// <c>W_MuzzleFlash_Model_Think</c>. The node inherits the socket's global transform (for world-correct
    /// placement) but is attached to the muzzle marker so it rides the viewmodel sway. A random roll is applied
    /// to give each shot a distinct orientation, matching the QC <c>flash.angles.z = random() * 180</c>.
    /// Fades out over 3 × 0.05 s via a Tween: scale halved and alpha decremented by 0.25 per step.
    /// </summary>
    private void SpawnMuzzleFlashModel()
    {
        // Per-weapon m_muzzlemodel gate: only the weapons that set a non-null m_muzzlemodel (Devastator/MineLayer
        // → flash.md3, Machinegun/Shotgun → uziflash.md3) attach a model flash; every other weapon is MDL_Null and
        // attaches NONE. QC: `if (thiswep.m_muzzlemodel != MDL_Null)` in W_MuzzleFlash.
        if (FlashModelFactory is null || string.IsNullOrEmpty(MuzzleModelPath)) return;
        if (_muzzleMarker is null || !GodotObject.IsInstanceValid(_muzzleMarker)) return;

        // Free the previous flash (QC: reuses the wepent.muzzle_flash slot).
        if (_muzzleFlashNode is not null && GodotObject.IsInstanceValid(_muzzleFlashNode))
            _muzzleFlashNode.QueueFree();
        _muzzleFlashNode = null;

        Node3D? flashNode = FlashModelFactory(MuzzleModelPath);
        if (flashNode is null) return;

        // QC W_MuzzleFlash_Model: scale=0.75, alpha=0.75, EF_ADDITIVE|EF_FULLBRIGHT. The CSQC viewmodel path then
        // overwrites the roll in W_MuzzleFlash_Model_AttachToShotorg with `angles.z = random()*360` (the muzzle
        // marker is the viewmodel-side analog), so the effective roll is a full 0-360°.
        flashNode.Scale = Vector3.One * 0.75f;
        flashNode.RotationDegrees = new Vector3(0f, 0f, (float)GD.RandRange(0, 360));

        // EF_ADDITIVE | EF_FULLBRIGHT: make every surface on the model unshaded + additive blend.
        ApplyFlashMaterialFx(flashNode);
        // Viewmodel render state for the flash too (inherited RENDER_VIEWMODEL in DP): the dedicated render
        // layer (hidden from portal cameras), no shadow casting, no occlusion cull, and the short-depth-range
        // uniform on GPU-morph flash materials (the additive BaseMaterial3D surfaces got NoDepthTest above).
        ViewModelRenderFx.Apply(flashNode);

        _muzzleMarker.AddChild(flashNode);
        _muzzleFlashNode = flashNode;

        // QC W_MuzzleFlash_Model_Think: three ticks at 0.05 s intervals — scale*=0.5, alpha-=0.25 each.
        // After the third tick alpha reaches 0 and the entity is freed (QC uses nextthink + SUB_Remove).
        // Use a Tween for exact per-step control (not a smooth Lerp — the QC does discrete steps).
        // In Godot 4, TweenCallback.SetDelay adds a RELATIVE delay before THAT step (sequential tween),
        // so each step fires 0.05s after the previous one — cumulative total 0.05, 0.10, 0.15 s.
        // QC W_MuzzleFlash_Model sets flash.frame = 2 initially; W_MuzzleFlash_Model_Think does frame += 2 each tick.
        // When FlashModelFactory returns a ModelAnimator (multi-frame MD3), seed it at frame 2 immediately.
        if (_muzzleFlashNode is ModelAnimator seedAnim)
            seedAnim.SetRawFrame(2);

        Tween tween = CreateTween();
        tween.SetProcessMode(Tween.TweenProcessMode.Idle);
        // Step 1 (t=+0.05 s): scale 0.75 → 0.375, alpha 0.75 → 0.50, frame 2 → 4
        // Step 2 (t=+0.05 s): scale 0.375 → 0.1875, alpha 0.50 → 0.25, frame 4 → 6
        // Step 3 (t=+0.05 s): scale 0.1875 → 0.09375, alpha 0.25 → 0 → free
        for (int step = 1; step <= 3; step++)
        {
            float targetScale = 0.75f * MathF.Pow(0.5f, step);
            float targetAlpha = 0.75f - step * 0.25f; // 0.50, 0.25, 0
            int capturedStep = step;
            int targetFrame = 2 + step * 2; // 4, 6, 8 (QC: flash.frame += 2 each think)
            tween.TweenCallback(Callable.From(() =>
            {
                if (_muzzleFlashNode is null || !GodotObject.IsInstanceValid(_muzzleFlashNode)) return;
                _muzzleFlashNode.Scale = Vector3.One * targetScale;
                SetFlashAlpha(_muzzleFlashNode, targetAlpha);
                // Advance MD3 flash frame: flash.md3=30 frames, uziflash.md3=14; SetRawFrame clamps safely.
                if (_muzzleFlashNode is ModelAnimator flashAnim)
                    flashAnim.SetRawFrame(targetFrame);
                if (capturedStep >= 3)
                {
                    _muzzleFlashNode.QueueFree();
                    _muzzleFlashNode = null;
                }
            })).SetDelay(0.05f);
        }
    }

    /// <summary>
    /// Apply EF_ADDITIVE (blend mode additive) + EF_FULLBRIGHT (unshaded) to every surface of the flash node —
    /// faithful to Base <c>EF_ADDITIVE | EF_FULLBRIGHT</c> on the muzzle flash model entity. Walks all
    /// <see cref="MeshInstance3D"/> descendants, duplicating surface materials so the shared mesh is not mutated.
    /// </summary>
    private static void ApplyFlashMaterialFx(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh is { } mesh)
        {
            int surfaces = mesh.GetSurfaceCount();
            for (int i = 0; i < surfaces; i++)
            {
                var ov = mi.GetSurfaceOverrideMaterial(i) as BaseMaterial3D;
                if (ov is null)
                {
                    if (mesh.SurfaceGetMaterial(i) is not BaseMaterial3D bm)
                        continue;
                    ov = (BaseMaterial3D)bm.Duplicate();
                    mi.SetSurfaceOverrideMaterial(i, ov);
                }
                ov.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;  // EF_FULLBRIGHT
                ov.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                ov.BlendMode = BaseMaterial3D.BlendModeEnum.Add;            // EF_ADDITIVE
                // The flash is setattachment'd to the viewmodel, so in DP it inherits RENDER_VIEWMODEL
                // (cl_main.c:1138) and its depth hack. For this transparent additive material the no-depth-test
                // flag is the working equivalent (DP itself converts SHORTDEPTHRANGE to NODEPTHTEST for
                // sprite-class attachments, r_sprites.c:407-408): drawn after the opaque world, it can never
                // be clipped by the wall you are firing into point-blank.
                ov.NoDepthTest = true;
            }
        }
        foreach (Node child in node.GetChildren())
            ApplyFlashMaterialFx(child);
    }

    /// <summary>
    /// Set the alpha on every surface material of the flash node (already additive/unshaded from
    /// <see cref="ApplyFlashMaterialFx"/>). Walks <see cref="MeshInstance3D"/> descendants in place.
    /// </summary>
    private static void SetFlashAlpha(Node node, float alpha)
    {
        if (node is MeshInstance3D mi && mi.Mesh is { } mesh)
        {
            int surfaces = mesh.GetSurfaceCount();
            for (int i = 0; i < surfaces; i++)
            {
                if (mi.GetSurfaceOverrideMaterial(i) is BaseMaterial3D ov)
                {
                    Color c = ov.AlbedoColor;
                    ov.AlbedoColor = new Color(c.R, c.G, c.B, alpha);
                }
            }
        }
        foreach (Node child in node.GetChildren())
            SetFlashAlpha(child, alpha);
    }

    /// <summary>
    /// The first <see cref="AnimationPlayer"/> under <paramref name="root"/> that carries at least one clip —
    /// the h_ rig's real animation driver. A clipless player (a skeletal but animation-less v_ visual model
    /// nested under the rig's weapon bone) is only returned when NO clip-carrying player exists. (r9)
    /// </summary>
    private static AnimationPlayer? FindAnimationPlayerWithClips(Node root)
    {
        AnimationPlayer? empty = null;
        foreach (Node child in root.GetChildren())
        {
            if (child is AnimationPlayer ap)
            {
                if (ap.GetAnimationList().Length > 0)
                    return ap;
                empty ??= ap;
            }
            AnimationPlayer? nested = FindAnimationPlayerWithClips(child);
            if (nested is not null)
            {
                if (nested.GetAnimationList().Length > 0)
                    return nested;
                empty ??= nested;
            }
        }
        return empty;
    }

    /// <summary>
    /// Restart the FIRE clip once — the per-shot server edge (the listen host derives it from the slot's
    /// ATTACK_FINISHED bump; Base <c>weapon_thinkf(WFRAME_FIRE1)</c> re-sets the frame on EVERY shot). Unlike
    /// the networked selector's rising-edge path, an already-playing fire clip is REWOUND here so sustained
    /// rapid fire re-triggers visibly (Godot <c>Play()</c> on the current animation does not restart it).
    /// (playtest r8 #3)
    /// </summary>
    public void PlayFireClip()
    {
        string n = FindFireClip(); // plays the clip when found (IQM/DPM or MD3 path)
        if (_iqmAnimPlayer is not null && GodotObject.IsInstanceValid(_iqmAnimPlayer)
            && !string.IsNullOrEmpty(n) && _iqmAnimPlayer.CurrentAnimation == n)
            _iqmAnimPlayer.Seek(0.0, update: true);
    }

    /// <summary>
    /// Drop a still-looping FIRE clip back to idle — Base's weapon state machine asserts WFRAME_IDLE
    /// explicitly when the attack window closes (w_ready → weapon_thinkf), it never waits for clip end.
    /// Needed because some fire clips are authored LOOPING (h_hagar's fire is loop=1): the end-of-clip idle
    /// recovery in <see cref="_Process"/> never triggers on those, so the gun pumped forever after the last
    /// shot (playtest r12). No-op unless the CURRENT clip is a looping fire/fire2.
    /// </summary>
    public void StopLoopingFire()
    {
        if (_iqmAnimPlayer is null || !GodotObject.IsInstanceValid(_iqmAnimPlayer))
            return;
        string cur = _iqmAnimPlayer.CurrentAnimation;
        if (cur is not ("fire" or "fire2" or "shoot" or "attack" or "fire1"))
            return;
        Animation? clip = _iqmAnimPlayer.HasAnimation(cur) ? _iqmAnimPlayer.GetAnimation(cur) : null;
        if (clip is null || clip.LoopMode == Animation.LoopModeEnum.None)
            return; // non-looping fire ends on its own (the _Process idle recovery handles it)
        PlayIdle();
    }

    private string FindFireClip()
    {
        // IQM/DPM path (h_* rigs with an AnimationPlayer — the live equip path).
        if (_iqmAnimPlayer is not null && GodotObject.IsInstanceValid(_iqmAnimPlayer))
        {
            foreach (string n in new[] { "fire", "shoot", "attack", "fire1" })
            {
                if (_iqmAnimPlayer.HasAnimation(n))
                {
                    _iqmAnimPlayer.Play(n);
                    return n;
                }
            }
            return _iqmAnimPlayer.CurrentAnimation;
        }
        // MD3 vertex-morph path (legacy SetWeapon(Md3Data) path).
        if (_animator is not null)
        {
            foreach (string n in new[] { "fire", "shoot", "attack", "fire1" })
                if (_animator.PlayIfChanged(n)) return n;
            return _animator.CurrentClip;
        }
        return "";
    }

    /// <summary>
    /// Play the weapon's <b>reload</b> animation once — the visible analog of Base's networked
    /// <c>WFRAME_RELOAD</c> (all.qc <c>NET_HANDLE(wframe)</c> sets <c>anim_reload</c> when the server reports a
    /// reload). The port does not network the <c>wframe</c> temp entity, so the host derives the reload edge from
    /// the already-networked clip state (<c>clip_load == -1</c>, the QC "scheduled for reload" sentinel) and calls
    /// this once per reload. Plays the model's "reload" clip (best-effort: only multi-frame v_/h_ rigs that ship a
    /// reload clip animate; single-frame guns no-op gracefully). The clip is non-looping, so
    /// <see cref="_Process"/> drops back to the idle clip when it finishes, matching <c>viewmodel_draw</c> 335-337
    /// (<c>anim_set(this, anim_idle, ...)</c> once the override clip ends).
    /// </summary>
    public void PlayReload()
    {
        // IQM/DPM path (h_* rigs — the live equip path).
        if (_iqmAnimPlayer is not null && GodotObject.IsInstanceValid(_iqmAnimPlayer))
        {
            foreach (string n in new[] { "reload", "reload1", "load" })
            {
                if (_iqmAnimPlayer.HasAnimation(n))
                {
                    _iqmAnimPlayer.Play(n);
                    return;
                }
            }
            return; // no reload clip on this rig — keep idling (faithful degenerate)
        }
        // MD3 vertex-morph path. Best-effort: only models that ship a reload clip animate the reload (Base picks
        // anim_reload; a model without that frame group simply keeps idling, so a no-op here is the faithful
        // degenerate case — we do NOT borrow the fire clip, which would read as the gun firing mid-reload).
        if (_animator is null)
            return;
        foreach (string n in new[] { "reload", "reload1", "load" })
            if (_animator.PlayIfChanged(n))
                return;
    }

    /// <summary>Play the model's looping idle clip (Base <c>viewmodel_draw</c> 335-337: re-asserts
    /// <c>anim_idle</c> when no fire/reload override is running). No-op when the model has no idle/anim.</summary>
    private void PlayIdle()
    {
        // IQM/DPM path (h_* rigs — the live equip path).
        if (_iqmAnimPlayer is not null && GodotObject.IsInstanceValid(_iqmAnimPlayer))
        {
            foreach (string n in new[] { "idle", "still", "stand" })
            {
                if (_iqmAnimPlayer.HasAnimation(n))
                {
                    if (_iqmAnimPlayer.CurrentAnimation != n)
                        _iqmAnimPlayer.Play(n); // switch to idle only when not already playing it
                    return;
                }
            }
            return;
        }
        // MD3 vertex-morph path.
        if (_animator is null)
            return;
        foreach (string n in new[] { "idle", "still", "stand" })
            if (_animator.PlayIfChanged(n))
                return;
    }

    // The last networked viewmodel frame selector seen (so SetNetAnimFrame only restarts a clip on a rising
    // edge, staying idempotent when called every frame). -1 = nothing networked yet.
    private int _lastNetAnimFrame = -1;

    /// <summary>
    /// Drive the local viewmodel anim from the networked frame selector (Base CSQC <c>wframe</c> /
    /// <c>WepentView.ViewmodelFrame</c>): 0 idle, 1 fire, 2 reload, 3 raise, 4 drop. Plays the matching clip
    /// ONCE on a rising edge (when the value differs from the last frame seen) so it is safe to call every
    /// frame — a repeated value is a no-op rather than a continually-restarted clip. This is the source for the
    /// pure-remote-client / spectated-player branch, where there is no local weapon state to derive fire/reload
    /// edges from; it COMPLEMENTS (does not replace) the host's <c>UpdateViewModelReloadAnim</c> / best-effort
    /// <see cref="Fire"/> path for the local player.
    ///
    /// <para>Mapping: 0 → <see cref="PlayIdle"/>; 1 → the fire clip once (<see cref="FindFireClip"/>); 2 →
    /// <see cref="PlayReload"/>; 3/4 (raise/drop) → no-op for the local viewmodel, since the raise/drop slide is
    /// already handled by <see cref="PlayRaise"/> / <see cref="PlayHolster"/> on the weapon switch. No effect on
    /// sway or weapon-switch logic.</para>
    /// </summary>
    public void SetNetAnimFrame(int frame)
    {
        if (frame == _lastNetAnimFrame)
            return; // idempotent: only act on a rising edge (value change)
        _lastNetAnimFrame = frame;

        switch (frame)
        {
            case 0: // WFRAME_IDLE
                PlayIdle();
                break;
            case 1: // WFRAME_FIRE1 — play the fire clip once (non-looping; _Process drops back to idle when done).
                // FindFireClip() plays the clip as a side-effect on whichever backend is active.
                FindFireClip();
                break;
            case 2: // WFRAME_RELOAD
                PlayReload();
                break;
            // case 3 (raise) / case 4 (drop): the PlayRaise/PlayHolster slide already covers the local viewmodel,
            // so the networked raise/drop selector is a no-op here (no clip to start).
            default:
                break;
        }
    }

    // =================================================================================================
    //  Weapon switch raise/lower (Xonotic viewmodel_draw raise/drop — view.qc:339-362)
    // =================================================================================================

    /// <summary>
    /// Begin lowering the current gun off the bottom of the view — the <b>drop</b> half of a weapon switch.
    /// Called on the switch-key press for instant local feedback while the actual switch round-trips to the
    /// server (the gun starts dropping the moment you press the key, masking the latency, exactly as Xonotic's
    /// drop animation does). If the server never confirms the switch (e.g. denied — out of ammo / not owned),
    /// the gun auto-recovers and raises the same weapon back up after a short grace, so it can never stay stuck
    /// off-screen. Purely cosmetic — firing/usability stays server-authoritative.
    /// </summary>
    public void PlayHolster()
    {
        _switchDir = +1;        // animate toward fully lowered
        _holsterGrace = 0.45f;  // recover if no raise confirms by then
    }

    /// <summary>
    /// Snap the gun to fully lowered and raise it into view — the <b>raise</b> half of a weapon switch, called
    /// right after the model is swapped to the new weapon on a server-confirmed change, so the new gun rises up
    /// instead of popping in. Cancels any pending holster auto-recovery.
    /// </summary>
    public void PlayRaise()
    {
        _switchOffset = 1f;     // the new gun starts off-screen…
        _switchDir = -1;        // …and rises to rest
        _holsterGrace = 0f;     // confirmed — no auto-recovery needed
    }

    /// <summary>
    /// Reset the lean-sway accumulator to the current view angles so the gun does NOT snap-lean after a teleport /
    /// warpzone view-angle jump — port of Base <c>viewmodel_animate</c>'s <c>csqcmodel_teleported</c> guard
    /// (<c>gunangles_prev = view_angles</c> when the entity teleported this frame). Without this, the highpass in
    /// <see cref="LeanModelOffset"/> sees the destination facing as one enormous angular delta and flings the gun.
    /// Called from the host on the predicted teleport tick (NetGame.ConsumePredictedFixAngle). One-shot: the flag
    /// is consumed on the next <see cref="LeanModelOffset"/> so the reset uses that frame's actual view angles.
    /// </summary>
    public void NotifyTeleported()
    {
        _leanTeleported = true;
    }

    /// <summary>Advance the switch raise/lower animation one frame and recover a never-confirmed holster.</summary>
    private void UpdateSwitch(float dt)
    {
        if (_switchDir > 0)         // lowering
            _switchOffset = Mathf.MoveToward(_switchOffset, 1f, dt / Mathf.Max(HolsterTime, 1e-3f));
        else if (_switchDir < 0)    // raising
        {
            _switchOffset = Mathf.MoveToward(_switchOffset, 0f, dt / Mathf.Max(RaiseTime, 1e-3f));
            if (_switchOffset <= 0f)
                _switchDir = 0;
        }

        // Auto-recover a keypress-predicted holster the server never confirmed: once fully lowered, count down
        // the grace and then raise the (unchanged) weapon back up rather than leave it stuck off-screen.
        if (_holsterGrace > 0f && _switchDir >= 0 && _switchOffset >= 1f)
        {
            _holsterGrace -= dt;
            if (_holsterGrace <= 0f)
                _switchDir = -1; // recover
        }
    }

    /// <summary>The muzzle socket's global transform (marker if present, else the model front).</summary>
    private Transform3D MuzzleGlobalTransform()
    {
        if (_muzzleMarker is not null && GodotObject.IsInstanceValid(_muzzleMarker))
            return _muzzleMarker.GlobalTransform;
        // Fall back to a point in front of the weapon root (camera -Z forward).
        return new Transform3D(GlobalBasis, ToGlobal(new Vector3(0f, 0f, -16f)));
    }

    // =================================================================================================
    //  Per-frame: decay the flash, recover recoil, drive follow/lean/bob sway
    // =================================================================================================

    public override void _Process(double delta)
    {
        using var _vmScope = XonoticGodot.Game.Client.FrameProfiler.Scope("viewmodel"); // [profiling] viewmodel sway/anim
        // #30 slowmo/pause: the whole viewmodel animation set (flash decay, recoil recovery, switch slide, sway,
        // fire/reload clips) advances on the slowmo-scaled delta — Base's viewmodel_draw runs on CSQC frametime,
        // which freezes/slows with the sim. The two self-advancing clip drivers are speed-scaled the same way.
        float dt = XonoticGodot.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
        if (_animator is not null && GodotObject.IsInstanceValid(_animator))
            _animator.TimeScale = XonoticGodot.Game.Client.ClientRenderTime.Scale; // Advance() multiplies by this
        if (_iqmAnimPlayer is not null && GodotObject.IsInstanceValid(_iqmAnimPlayer))
            _iqmAnimPlayer.SpeedScale = XonoticGodot.Game.Client.ClientRenderTime.Scale;

        RefreshCvars();

        if (_flashTime > 0f)
        {
            _flashTime -= dt;
            // Ease the light out over the flash window.
            _flashLight.LightEnergy = Mathf.Max(0f, _flashLight.LightEnergy - 3.0f * (dt / 0.06f));
            if (_flashTime <= 0f)
                _flashLight.LightEnergy = 0f;
            // Keep the flash light parked at the muzzle.
            if (_muzzleMarker is not null && GodotObject.IsInstanceValid(_muzzleMarker))
                _flashLight.GlobalPosition = _muzzleMarker.GlobalPosition;
            // Grid-lit guns don't see the flash OmniLight — decay the stand-in ambient boost with it.
            if (_gridOn)
                PushGridLight();
        }

        // Recoil recovers along view forward (+X Quake). It and the sway both feed the gunorg below.
        _recoil = Mathf.MoveToward(_recoil, 0f, RecoilRecovery * RecoilKick * dt);

        UpdateSwitch(dt);
        UpdateSway(dt);

        // Idle recovery (Base viewmodel_draw 335-337: re-assert anim_idle once the fire/reload override clip ends).
        // A one-shot fire/reload clip clears the animator's playing flag when its playhead reaches the end; drop
        // back to the looping idle so the gun keeps subtly cycling rather than freezing on the last fire frame.
        if (_animator is not null && !_animator.IsPlaying)
            PlayIdle();
        // IQM/DPM path: AnimationPlayer.IsPlaying() returns false once a non-looping clip (fire/reload) finishes;
        // recover to idle, mirroring the _animator guard above.
        if (_iqmAnimPlayer is not null && GodotObject.IsInstanceValid(_iqmAnimPlayer) && !_iqmAnimPlayer.IsPlaying())
            PlayIdle();
    }

    /// <summary>
    /// Pull the live menu/console viewmodel cvars into the export fields each frame so the menu sliders/radio
    /// buttons actually drive the gun (Base reads <c>autocvar_*</c> directly in <c>viewmodel_draw</c>):
    /// cl_gunalign (1/2/3/4), cl_gunoffset ("x y z" Quake view), cl_followmodel/cl_leanmodel/cl_bobmodel toggles,
    /// and cl_viewmodel_alpha / cl_viewmodel_alpha_min (continuous opacity, applied to the model materials). All
    /// are cheap dictionary reads from the shared client cvar store (the same store the in-game console writes).
    /// </summary>
    private void RefreshCvars()
    {
        XonoticGodot.Common.Services.ICvarService cv = XonoticGodot.Game.Menu.MenuState.Cvars;

        // cl_gunalign: 3 = right (default), 4 = left, 1/2 = center. Rebuild the rest placement when it changes.
        int align = (int)CvarFloat(cv, "cl_gunalign", GunAlign);
        Vector3 offset = ParseVec(cv.GetString("cl_gunoffset"), GunOffset);
        if (align != GunAlign || offset != GunOffset)
        {
            GunAlign = align;
            GunOffset = offset;
            ApplyRestPlacement();
        }

        // Sway toggles (cl_followmodel / cl_leanmodel / cl_bobmodel — default 1).
        FollowModel = CvarFloat(cv, "cl_followmodel", FollowModel ? 1f : 0f) != 0f;
        FollowVelocityAbsolute = CvarFloat(cv, "cl_followmodel_velocity_absolute", FollowVelocityAbsolute ? 1f : 0f) != 0f;
        LeanModel = CvarFloat(cv, "cl_leanmodel", LeanModel ? 1f : 0f) != 0f;
        BobModel = CvarFloat(cv, "cl_bobmodel", BobModel ? 1f : 0f) != 0f;

        // cl_viewmodel_alpha (max opacity, default 1) / cl_viewmodel_alpha_min (default 0). Base:
        //   amax = (alpha != 0) ? alpha : 1;   amin = (alpha_min != 0) ? alpha_min : -1;
        //   a = (amin >= amax) ? amax : bound(amin, m_alpha, amax);
        // m_alpha is the model's nominal opacity (1 when shown); the gun is otherwise drawn fully opaque, so the
        // continuous result is amax (clamped to [amin,amax]). Apply that to the model materials.
        float clA = CvarFloat(cv, "cl_viewmodel_alpha", 1f);
        float clAMin = CvarFloat(cv, "cl_viewmodel_alpha_min", 0f);
        float amax = clA != 0f ? clA : 1f;
        float amin = clAMin != 0f ? clAMin : -1f;
        float a = (amin >= amax) ? amax : Mathf.Clamp(1f, amin, amax);
        ApplyModelAlpha(Mathf.Clamp(a, 0f, 1f));
    }

    private static float CvarFloat(XonoticGodot.Common.Services.ICvarService cv, string name, float fallback)
    {
        string s = cv.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : cv.GetFloat(name);
    }

    /// <summary>Parse a "x y z" cvar vector (Quake view space), falling back to the current value on a blank/parse miss.</summary>
    private static Vector3 ParseVec(string s, Vector3 fallback)
    {
        if (string.IsNullOrWhiteSpace(s))
            return fallback;
        string[] p = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3
            || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            return fallback;
        return new Vector3(x, y, z);
    }

    /// <summary>
    /// Apply the viewmodel surface state + continuous opacity to every mesh in the current weapon model,
    /// faithful to Base <c>viewmodel_draw</c> (the per-child loop sets <c>csqcmodel_effects |=
    /// EF_NODEPTHTEST</c> and <c>e.alpha = a</c>; the engine turns RENDER_VIEWMODEL/EF_NODEPTHTEST into
    /// <c>MATERIALFLAG_SHORTDEPTHRANGE</c> — see <see cref="ViewModelRenderFx"/>). At the default opaque
    /// alpha every surface rides the faithful short-depth-range shader (converting plain StandardMaterial3D
    /// surfaces); with <c>cl_viewmodel_alpha</c> &lt; 1 the plain surfaces fall back to the legacy
    /// translucent BaseMaterial3D path (NoDepthTest + alpha — the skin shader is deliberately opaque-only).
    /// Walks every <see cref="MeshInstance3D"/> descendant; only re-touches on a change.
    /// </summary>
    private void ApplyModelAlpha(float a)
    {
        bool alphaChanged = !Mathf.IsEqualApprox(a, _modelAlpha);
        bool needDepth = !_surfaceFxApplied;
        if (!alphaChanged && !needDepth && !_glowDirty)
            return;
        _modelAlpha = a;
        _surfaceFxApplied = true;
        _glowDirty = false;
        // Player colormod (subtle albedo tint toward the PANTS color — the dominant personal color, and exactly
        // the team color in teamplay) + glowmod (emission) — only reachable on the legacy BaseMaterial3D
        // fallback surfaces; converted/skin surfaces take their tint from the instance uniforms
        // (ReapplyInstanceTint), which is DP's actual model: no _shirt/_pants/_glow map → no team color.
        ApplyMaterialFx(_modelRoot, a, _hasTeam ? _pantsColor : (Color?)null, _hasTeam ? _teamGlow : (Color?)null,
            _muzzleFlashNode);
    }

    /// <summary>Forget the applied material fx so the NEXT frame re-walks the freshly-built model (depth state +
    /// alpha must be re-applied to the new model's materials). Called by every model swap. Also clears any
    /// still-live muzzle-flash model node (the QC wepent.muzzle_flash slot is implicitly cleared on model change
    /// since the exterior weapon entity is rebuilt; we QueueFree so there is no orphan node).</summary>
    private void ResetModelFx()
    {
        _surfaceFxApplied = false;
        _modelAlpha = 1f;
        _glowDirty = true; // the freshly-built model's materials need the team colormod/glow re-applied
        if (_muzzleFlashNode is not null && GodotObject.IsInstanceValid(_muzzleFlashNode))
            _muzzleFlashNode.QueueFree();
        _muzzleFlashNode = null;
    }

    /// <summary>
    /// Re-push the PlayerSkinShader per-INSTANCE team tint + lightgrid modulate onto the CURRENT model
    /// meshes. Must run AFTER a freshly-built model is attached: the old swap-time re-seed lived in
    /// <see cref="ResetModelFx"/>, which every equip path calls BEFORE adding the new model — it walked the
    /// outgoing (freed) meshes and the new ones kept the shader defaults, silently dropping the team tint on
    /// every weapon switch (playtest #36).
    /// </summary>
    private void ReseedInstanceTint() => ReapplyInstanceTint();

    private static void ApplyMaterialFx(Node node, float a, Color? colormod = null, Color? glow = null, Node? skip = null)
    {
        if (node == skip)
            return; // the muzzle-flash model runs its own material FX + fade (ApplyFlashMaterialFx/SetFlashAlpha)
        if (node is MeshInstance3D mi && mi.Mesh is { } mesh)
        {
            int surfaces = mesh.GetSurfaceCount();
            for (int i = 0; i < surfaces; i++)
            {
                // OPAQUE default (cl_viewmodel_alpha 1): the faithful short-depth-range state — plain
                // StandardMaterial3D surfaces are swapped to the equivalent skin-shader material and every
                // shader surface rides the viewmodel_depth_range instance uniform (set at equip). Tint/glow
                // ride the instance uniforms there (ReapplyInstanceTint); nothing else to touch per surface.
                if (a >= 1f && ViewModelRenderFx.EnsureOpaqueSurfaceFx(mi, i))
                    continue;

                // Legacy BaseMaterial3D path: a translucent gun (cl_viewmodel_alpha < 1 — the skin shader is
                // deliberately opaque-only) or a surface the shader can't reproduce (untextured / tinted /
                // transparent source). Edit a per-instance override so we never mutate a shared mesh material
                // (other instances / the world pickup share it). Reuse our own override if present (idempotent
                // re-touch on alpha change) — but a ShaderMaterial override left by a previous opaque frame is
                // replaced from the pristine mesh source; otherwise duplicate the surface's base material
                // (the GpuWarmPass per-instance pattern).
                BaseMaterial3D? ov = mi.GetSurfaceOverrideMaterial(i) as BaseMaterial3D;
                if (ov is null)
                {
                    if (mesh.SurfaceGetMaterial(i) is not BaseMaterial3D bm)
                        continue;
                    ov = (BaseMaterial3D)bm.Duplicate();
                    mi.SetSurfaceOverrideMaterial(i, ov);
                }
                ov.NoDepthTest = true; // EF_NODEPTHTEST approximation — draw the gun over the world.

                // Team colormod (Base e.colormap = 256 + c): tint the gun's albedo toward the player's team color
                // so the held weapon reads as yours. Captured base RGB once (in AlbedoColor.A's sibling channels)
                // is not tracked, so we MULTIPLY the duplicated base material's albedo — the duplicate starts at
                // the model's authored color, and a re-walk reuses the same override, so we must reset to white-
                // multiplied each pass. Use a NeutralBase via metadata to stay idempotent: blend authored*team.
                // FFA (colormod null) leaves albedo untouched.
                if (colormod is { } cm)
                {
                    Color baseAlbedo = GetOrCaptureBaseAlbedo(ov);
                    // Mild tint so the texture detail survives (DP colormap is a shirt/pants palette swap on a
                    // small region; on a single-material gun we approximate with a subtle whole-model team tint).
                    Color tinted = new(
                        baseAlbedo.R * (0.6f + 0.4f * cm.R),
                        baseAlbedo.G * (0.6f + 0.4f * cm.G),
                        baseAlbedo.B * (0.6f + 0.4f * cm.B),
                        baseAlbedo.A);
                    ov.AlbedoColor = new Color(tinted.R, tinted.G, tinted.B, ov.AlbedoColor.A);
                }

                // Glowmod (Base e.glowmod): a team-colored emission so the gun glows with the team color (and, for
                // weapons with a wr_glow override like the Vortex, that glow). Drives the StandardMaterial3D
                // emission. White/native glow (FFA) → no added emission.
                if (glow is { } gm && (gm.R + gm.G + gm.B) > 0.01f && (gm.R < 0.99f || gm.G < 0.99f || gm.B < 0.99f))
                {
                    ov.EmissionEnabled = true;
                    ov.Emission = gm;
                    ov.EmissionEnergyMultiplier = 0.35f;
                }

                if (a < 1f)
                {
                    ov.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    Color c = ov.AlbedoColor;
                    ov.AlbedoColor = new Color(c.R, c.G, c.B, a);
                }
                else if (ov.Transparency == BaseMaterial3D.TransparencyEnum.Alpha)
                {
                    Color c = ov.AlbedoColor;
                    ov.AlbedoColor = new Color(c.R, c.G, c.B, 1f);
                }
            }
        }
        foreach (Node child in node.GetChildren())
            ApplyMaterialFx(child, a, colormod, glow, skip);
    }

    /// <summary>
    /// The material's authored (pre-tint) albedo, captured on first touch into a meta field so repeated team-tint
    /// re-walks stay idempotent (a tint is always applied to the authored color, never compounded). Without this
    /// the second pass would tint the already-tinted color and the gun would darken every frame.
    /// </summary>
    private static Color GetOrCaptureBaseAlbedo(BaseMaterial3D ov)
    {
        const string key = "vm_base_albedo";
        if (ov.HasMeta(key))
            return (Color)ov.GetMeta(key);
        Color a = ov.AlbedoColor;
        ov.SetMeta(key, a);
        return a;
    }

    /// <summary>
    /// Drive the per-frame weapon sway (Xonotic <c>viewmodel_animate</c>): follow (origin lag on accel), lean
    /// (angle tilt on turn), bob (run cycle), plus the firing recoil. Produces a Quake view-space
    /// <c>gunorg</c> / <c>gunangles</c> applied as this node's local transform on top of the rest placement.
    /// </summary>
    private void UpdateSway(float dt)
    {
        // gunorg (view space: X fwd, Y right(+)/left, Z up) and gunangles (deg) accumulate the effects.
        Vector3 gunorg = new(-_recoil, 0f, 0f); // recoil pulls the gun back along -fwd
        Vector3 gunangles = Vector3.Zero;

        ViewState vs = ViewStateProvider is not null ? ViewStateProvider() : default;
        // Guard against a zero/oversized dt (paused frame, first frame); the QC avg_factor needs frametime>0.
        float frametime = Mathf.Clamp(dt, 0f, 0.1f);

        if (frametime > 0f)
        {
            if (FollowModel)
            {
                Vector3 follow = FollowModelOffset(vs, frametime);
                LastFollowOffset = follow; // expose for hud_dynamic_follow (HUD sway with the viewmodel)
                gunorg += follow;
            }
            else
            {
                LastFollowOffset = Vector3.Zero;
            }
            if (LeanModel)
                gunangles += LeanModelOffset(vs, frametime);
            if (BobModel)
                gunorg += BobModelOffset(vs, frametime);
        }

        // Weapon-switch raise/lower: drop the gun straight down by the lowered fraction (and tuck it back a
        // little along -forward so it reads as stowed rather than sinking through the floor). Z is up in this
        // view-space gunorg, so lowering = negative Z. Independent of the frametime>0 sway guard above.
        if (_switchOffset > 0f)
        {
            gunorg.Z -= _switchOffset * SwitchLowerDistance;
            gunorg.X -= _switchOffset * (SwitchLowerDistance * 0.25f);
        }

        // Map the view-space gunorg/gunangles into the camera-local Godot frame and apply.
        // Translation: the gunorg components are (fwd, right, up) in Quake view space. Note bob/follow build
        // gunorg.y as a RIGHT-positive side value (matching the QC, which works in a right-handed view), so we
        // negate y to Quake's left-positive before the basis map.
        Vector3 ofsQuake = new(gunorg.X, -gunorg.Y, gunorg.Z);
        Vector3 localOfs = QuakeViewToCamera(ofsQuake);

        // Rotation: gunangles is (pitch, yaw, roll) in degrees, view space. Build the small rotation in the
        // camera frame: pitch about the view right axis, yaw about view up, roll about view forward.
        Basis rot = Basis.Identity;
        if (gunangles != Vector3.Zero)
            rot = LeanRotation(gunangles);

        Transform = new Transform3D(rot, localOfs);
    }

    /// <summary>
    /// Port of <c>calc_followmodel_ofs</c> (view.qc): turns the player's view-relative velocity into a lagging
    /// origin offset via a velocity-lowpass then a highpass→lowpass (velocity→acceleration) chain, so the gun
    /// pulls back when you accelerate and settles when you cruise. Returns a view-space (fwd,right,up) offset.
    /// </summary>
    private Vector3 FollowModelOffset(ViewState vs, float frametime)
    {
        // velocity for the follow lag. Base calc_followmodel_ofs:
        //   if (cl_followmodel_velocity_absolute)  vel = view_velocity;        // raw world velocity, no projection
        //   else { vel.x = view_velocity*v_forward; vel.y = -view_velocity*v_right; vel.z = view_velocity*v_up; }
        // Absolute mode ignores look direction (the documented teleport/warpzone-glitch mode); the default 0
        // path projects the world velocity into view space so the gun lags the way you actually move.
        Vector3 vel;
        if (FollowVelocityAbsolute)
        {
            // raw world velocity components used directly (Quake X fwd, Y left, Z up).
            vel = new Vector3(vs.VelocityQuake.X, vs.VelocityQuake.Y, vs.VelocityQuake.Z);
        }
        else
        {
            // view-relative velocity: x = along forward, y = along right*-1, z = along up (QC convention).
            AngleVectorsQuake(vs.ViewAnglesQuake, out NVec3 f, out NVec3 r, out NVec3 u);
            vel = new Vector3(
                Dot(vs.VelocityQuake, f),
                -Dot(vs.VelocityQuake, r),
                Dot(vs.VelocityQuake, u));
        }

        // bound around the running average so a spike can't yank the gun across the screen.
        vel.X = Bound(_sway.FollowVelAvg.X - FollowLimit, vel.X, _sway.FollowVelAvg.X + FollowLimit);
        vel.Y = Bound(_sway.FollowVelAvg.Y - FollowLimit, vel.Y, _sway.FollowVelAvg.Y + FollowLimit);
        vel.Z = Bound(_sway.FollowVelAvg.Z - FollowLimit, vel.Z, _sway.FollowVelAvg.Z + FollowLimit);

        float frac = AvgFactor(FollowVelocityLowpass, frametime);
        Vector3 gunorg = Lowpass3(vel, frac, ref _sway.FollowVelAvg);

        gunorg *= -FollowSpeed * 0.042f;

        // highpass THEN lowpass (the QC trick: lowpass last so the stored vector IS the result).
        frac = AvgFactor(FollowHighpass, frametime);
        gunorg = Highpass3(gunorg, frac, ref _sway.FollowAdjHighpass);
        frac = AvgFactor(FollowLowpass, frametime);
        gunorg = Lowpass3(gunorg, frac, ref _sway.FollowAdjLowpass);

        return gunorg;
    }

    /// <summary>
    /// Port of <c>leanmodel_ofs</c> (view.qc): a highpass on the view angles (the difference between the actual
    /// and a smoothed angle) tilts the gun when you turn, scaled by <c>cl_leanmodel_speed</c> and run through a
    /// further high/low-pass for smoothness. Returns view-space (pitch, yaw, roll) degrees.
    /// </summary>
    private Vector3 LeanModelOffset(ViewState vs, float frametime)
    {
        Vector3 view = new(vs.ViewAnglesQuake.X, vs.ViewAnglesQuake.Y, vs.ViewAnglesQuake.Z);

        // Teleport reset (Base csqcmodel_teleported -> gunangles_prev = view_angles): on the frame we teleported,
        // re-seed the lean prev-angle + highpass store to THIS view so the angular delta below is zero and the gun
        // doesn't fling toward the destination facing. Consume the one-shot.
        if (_leanTeleported)
        {
            _leanTeleported = false;
            _sway.LeanPrev = view;
            _sway.LeanHighpass = Vector3.Zero;
            _sway.LeanAdjHighpass = Vector3.Zero;
            _sway.LeanAdjLowpass = Vector3.Zero;
        }

        // store the DIFFERENCE to the actual view angles, unwrapped across the 360° seam (pitch+yaw).
        _sway.LeanHighpass += _sway.LeanPrev;
        _sway.LeanHighpass.X += 360f * Mathf.Floor((view.X - _sway.LeanHighpass.X) / 360f + 0.5f);
        _sway.LeanHighpass.Y += 360f * Mathf.Floor((view.Y - _sway.LeanHighpass.Y) / 360f + 0.5f);
        float frac = AvgFactor(LeanHighpass1, frametime);
        Vector3 gunangles = Vector3.Zero;
        gunangles.X = Highpass1Limited(view.X, frac, LeanLimit, ref _sway.LeanHighpass.X);
        gunangles.Y = Highpass1Limited(view.Y, frac, LeanLimit, ref _sway.LeanHighpass.Y);
        _sway.LeanPrev = view;
        _sway.LeanHighpass -= _sway.LeanPrev;

        gunangles.X *= -LeanSpeed; // pitch
        gunangles.Y *= -LeanSpeed; // yaw

        // PITCH = x, YAW = y: highpass then lowpass.
        frac = AvgFactor(LeanHighpass, frametime);
        gunangles.X = Highpass1(gunangles.X, frac, ref _sway.LeanAdjHighpass.X);
        gunangles.Y = Highpass1(gunangles.Y, frac, ref _sway.LeanAdjHighpass.Y);
        frac = AvgFactor(LeanLowpass, frametime);
        gunangles.X = Lowpass1(gunangles.X, frac, ref _sway.LeanAdjLowpass.X);
        gunangles.Y = Lowpass1(gunangles.Y, frac, ref _sway.LeanAdjLowpass.Y);

        gunangles.X = -gunangles.X; // pitch was inverted; matches the QC final negate.
        return gunangles;
    }

    /// <summary>
    /// Port of <c>bobmodel_ofs</c> (view.qc): a sinusoidal side/up sway scaled by horizontal speed, ramped in
    /// while running on the ground and out while airborne. Returns a view-space (fwd,right,up) offset (only the
    /// right + up components move).
    /// </summary>
    private Vector3 BobModelOffset(ViewState vs, float frametime)
    {
        bool onground = vs.OnGround;
        if (onground)
        {
            if (!_sway.BobOldOnGround)
                _sway.BobHitGroundTime = _sway.BobTime;
        }
        _sway.BobOldOnGround = onground;
        // advance the bob clock (QC uses global 'time'; we keep our own monotonic accumulator).
        _sway.BobTime += frametime;

        if (onground)
        {
            if (_sway.BobTime - _sway.BobHitGroundTime > 0.05f)
                _sway.BobScale = Mathf.Min(1f, _sway.BobScale + frametime * 5f);
        }
        else
            _sway.BobScale = Mathf.Max(0f, _sway.BobScale - frametime * 5f);

        Vector3 gunorg = Vector3.Zero;
        if (_sway.BobScale > 0f)
        {
            float xyspeed = Bound(0f, Length2(vs.VelocityQuake), 400f);
            const float avgTime = 0.1f;
            float frac = 1f - Mathf.Exp(-frametime / Mathf.Max(0.001f, avgTime));
            _sway.BobAvgXySpeed = frac * xyspeed + (1f - frac) * _sway.BobAvgXySpeed;
            if (_sway.BobAvgXySpeed < 1f)
                _sway.BobTimeOfs = _sway.BobTime;
            else
            {
                if (_sway.BobAvgXySpeed < 400f) // reduce bob frequency when crouch-walking / slow
                    _sway.BobTimeOfs += frametime * MapBoundRanges(_sway.BobAvgXySpeed, 150f, 400f, 0.08f, 0f);

                float bspeed = _sway.BobAvgXySpeed * 0.01f * _sway.BobScale;
                float s = (_sway.BobTime - _sway.BobTimeOfs) * BobModelSpeed;
                gunorg.Y = bspeed * BobModelSide * Mathf.Sin(s);       // right
                gunorg.Z = bspeed * BobModelUp * Mathf.Cos(s * 2f);    // up
            }
        }
        else
            _sway.BobTimeOfs = _sway.BobTime;

        return gunorg;
    }

    /// <summary>Build a small camera-local rotation from view-space (pitch,yaw,roll) degrees: pitch about the
    /// view right axis (camera +X), yaw about the view up (camera +Y), roll about view forward (camera -Z).</summary>
    private static Basis LeanRotation(Vector3 gunanglesDeg)
    {
        float pitch = Mathf.DegToRad(gunanglesDeg.X);
        float yaw = Mathf.DegToRad(gunanglesDeg.Y);
        float roll = Mathf.DegToRad(gunanglesDeg.Z);
        // Quake yaw turns left (+); in the camera frame that's a +Y rotation. Quake pitch is down-positive;
        // tilting the gun down is a negative rotation about camera +X. Roll about forward (camera -Z).
        Basis b = new Basis(Vector3.Up, yaw)
                  * new Basis(Vector3.Right, -pitch)
                  * new Basis(Vector3.Back, -roll);
        return b;
    }

    // =================================================================================================
    //  View-space mapping + alignment
    // =================================================================================================

    /// <summary>Map a Quake view-space point (X fwd, Y left, Z up) into the camera's local Godot frame.</summary>
    private static Vector3 QuakeViewToCamera(Vector3 q) => new(-q.Y, q.Z, -q.X);

    /// <summary>
    /// Base <c>movedir_aligned</c> (all.qc:483 — <c>shotorg_adjust(v, false, visual:true, algn)</c> =
    /// <c>shotorg_adjustfromclient</c>): the weapon's <see cref="MuzzleMovedir"/> with the <c>cl_gunalign</c>
    /// adjustment — right (3) = authored, left (4) mirrors Y, center (1/2) zeroes Y and drops 2u. This is the
    /// visual shot origin the first-person muzzle flash spawns from (unaffected by shootfromeye).
    /// </summary>
    private Vector3 MovedirAligned()
    {
        Vector3 md = MuzzleMovedir;
        switch (GunAlign)
        {
            case 4: md.Y = -md.Y; break;                 // left: mirror across the centre line
            case 1: case 2: md.Y = 0f; md.Z -= 2f; break; // center: middle + 2u drop
            default: break;                               // 3 right: authored
        }
        return md;
    }

    /// <summary>
    /// The <c>cl_gunalign</c> view-space nudge (Quake: X fwd, Y left, Z up). Right alignment (3) is the model's
    /// authored position (no change); center (1/2) pulls it to the middle and drops it 2u; left (4) mirrors the
    /// side. This mirrors <c>shotorg_adjustfromclient</c>, applied to a representative right-hand offset so the
    /// gun reads as held even though the v_ model is authored roughly centered.
    /// </summary>
    private Vector3 GunAlignOffset()
    {
        // The v_ models are authored in the RIGHT hand: the gun sits at a positive side offset from the view
        // centre. Base's shotorg_adjustfromclient transforms the model's authored shot-tag offset (`vecs`), but
        // the port keeps the gun at its authored position, so to make left/centre actually move the gun we apply
        // the alignment as a DELTA off the authored (align 3) position. The side magnitude is the model's own
        // authored shot-tag Y (Quake +Y = left); falling back to a representative hand offset when no marker has
        // resolved yet so right/left still differ on placeholder/early frames.
        //   3 (right)  = authored position (no delta)
        //   4 (left)   = mirror across the centre line  → delta = -2*side (vecs.y = -vecs.y in Base)
        //   1/2(center)= pull to the centre + drop 2u   → delta = -side, z -= 2 (vecs.y = 0; vecs.z -= 2)
        float side = AuthoredShotSideY();
        switch (GunAlign)
        {
            default:
            case 3: // right — authored position
                return Vector3.Zero;
            case 4: // left — mirror the side across the centre
                return new Vector3(0f, -2f * side, 0f);
            case 1:
            case 2: // center — pull to the middle and drop 2 units
                return new Vector3(0f, -side, -2f);
        }
    }

    /// <summary>
    /// The authored shot-origin side offset (Quake view +Y = left) used as Base <c>vecs.y</c> for the cl_gunalign
    /// mirror/centre. When a muzzle marker has resolved this is the model's OWN <c>tag_shot</c> Y
    /// (<see cref="_authoredShotSideY"/>, captured in <see cref="CaptureAuthoredShotSide"/>) — faithful to Base
    /// where <c>shotorg_adjustfromclient</c> mirrors/zeroes the model's own shot-tag Y. Falls back to a
    /// representative side (~3.5u, the v_ models' typical shot-tag Y magnitude) on placeholder/early frames so
    /// left (mirror) and centre (zero) still read distinctly from right. The captured value is clamped to a sane
    /// band so a stray marker can't fling the gun off-screen.
    /// </summary>
    private float AuthoredShotSideY()
    {
        if (_authoredShotSideY is { } y && MathF.Abs(y) > 0.05f)
            return Mathf.Clamp(y, -16f, 16f);
        return -3.5f;
    }

    // =================================================================================================
    //  Sway state + math helpers (ports of the view.qc macros)
    // =================================================================================================

    /// <summary>The running accumulators the QC keeps as function-static locals (one set per local player).</summary>
    private struct SwayState
    {
        // follow
        public Vector3 FollowVelAvg;
        public Vector3 FollowAdjHighpass;
        public Vector3 FollowAdjLowpass;
        // lean
        public Vector3 LeanPrev;
        public Vector3 LeanHighpass;
        public Vector3 LeanAdjHighpass;
        public Vector3 LeanAdjLowpass;
        // bob
        public bool BobOldOnGround;
        public float BobHitGroundTime;
        public float BobScale;
        public float BobTimeOfs;
        public float BobAvgXySpeed;
        public float BobTime;
    }

    /// <summary><c>avg_factor</c>: 1 - exp(-frametime / max(0.001, avg_time)).</summary>
    private static float AvgFactor(float avgTime, float frametime)
        => 1f - Mathf.Exp(-frametime / Mathf.Max(0.001f, avgTime));

    // lowpass(value, frac, ref store) { store = store*(1-frac) + value*frac; return store; }
    private static float Lowpass1(float value, float frac, ref float store)
    {
        store = store * (1f - frac) + value * frac;
        return store;
    }

    private static Vector3 Lowpass3(Vector3 v, float frac, ref Vector3 store)
        => new(Lowpass1(v.X, frac, ref store.X), Lowpass1(v.Y, frac, ref store.Y), Lowpass1(v.Z, frac, ref store.Z));

    // highpass(value, frac, ref store) { float f = lowpass(value,frac,store); return value - f; }
    private static float Highpass1(float value, float frac, ref float store)
    {
        float f = Lowpass1(value, frac, ref store);
        return value - f;
    }

    private static Vector3 Highpass3(Vector3 v, float frac, ref Vector3 store)
        => new(Highpass1(v.X, frac, ref store.X), Highpass1(v.Y, frac, ref store.Y), Highpass1(v.Z, frac, ref store.Z));

    // highpass_limited: lowpass into store, clamp store to value±limit, return value - store.
    private static float Highpass1Limited(float value, float frac, float limit, ref float store)
    {
        Lowpass1(value, frac, ref store);
        store = Bound(value - limit, store, value + limit);
        return value - store;
    }

    private static float Bound(float lo, float v, float hi) => Mathf.Max(lo, Mathf.Min(v, hi));

    /// <summary>Length of the horizontal (X,Y) velocity components in Quake space.</summary>
    private static float Length2(NVec3 v) => Mathf.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float Dot(NVec3 a, NVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>QC <c>map_bound_ranges</c>: remap <paramref name="v"/> from [a0,a1] into [b0,b1], clamped.</summary>
    private static float MapBoundRanges(float v, float a0, float a1, float b0, float b1)
    {
        if (a1 == a0) return b0;
        float t = Mathf.Clamp((v - a0) / (a1 - a0), 0f, 1f);
        return b0 + (b1 - b0) * t;
    }

    /// <summary>
    /// Quake AngleVectors for the follow-model velocity projection (forward/right/up unit vectors from
    /// pitch/yaw/roll degrees). Standard Quake handedness (yaw CCW, pitch down-positive) — matching the sim's
    /// QMath so the projected velocity agrees with the camera the player actually sees.
    /// </summary>
    private static void AngleVectorsQuake(NVec3 anglesDeg, out NVec3 forward, out NVec3 right, out NVec3 up)
    {
        float pitch = Mathf.DegToRad(anglesDeg.X);
        float yaw = Mathf.DegToRad(anglesDeg.Y);
        float roll = Mathf.DegToRad(anglesDeg.Z);
        float sp = Mathf.Sin(pitch), cp = Mathf.Cos(pitch);
        float sy = Mathf.Sin(yaw), cy = Mathf.Cos(yaw);
        float sr = Mathf.Sin(roll), cr = Mathf.Cos(roll);

        forward = new NVec3(cp * cy, cp * sy, -sp);
        right = new NVec3(
            -sr * sp * cy + cr * sy,
            -sr * sp * sy - cr * cy,
            -sr * cp);
        up = new NVec3(
            cr * sp * cy + sr * sy,
            cr * sp * sy - sr * cy,
            cr * cp);
    }
}
