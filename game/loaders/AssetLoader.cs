using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Formats.Mdl;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Formats.Sprites;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Game.Loaders.Models; // IqmBuilder / DpmBuilder / Md3Builder

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// The unified, host-side asset entry point for the Godot port: one object that owns a
/// <see cref="VirtualFileSystem"/> and an <see cref="AssetSystem"/> (the material/texture resolver) and turns
/// a virtual path into a ready Godot scene node or resource.
///
/// <para>It is the single place that knows the mapping from a file to the right importer + builder:
/// <list type="bullet">
///   <item><b>Models</b> are dispatched by their on-disk magic, NOT by extension, exactly as Darkplaces does
///         (<c>INTERQUAKEMODEL</c> → IQM, <c>DARKPLACESMODEL</c> → DPM, <c>IDP3</c> → MD3). The matching
///         sidecars are loaded through the VFS and passed to the builder: a <c>.framegroups</c> animation
///         table for every format, and a <c>_0.skin</c> material override for MD3.</item>
///   <item><b>Maps</b> go BspReader → <see cref="MapLoader.BuildMap"/> (render geometry) with collision built
///         on the side via <see cref="MapLoader.BuildCollision"/>.</item>
///   <item><b>Sprites</b> go SpriteReader → <see cref="SpriteBuilder.Build"/>.</item>
///   <item><b>Textures / materials</b> delegate straight to the owned <see cref="AssetSystem"/>.</item>
/// </list></para>
///
/// <para>The expensive part of a model/sprite load — reading the file and decoding the binary format plus
/// its sidecars — is cached per normalized vpath as a build factory; each call invokes it to produce a fresh,
/// independent scene node (Godot nodes and their script-backed state cannot be shared across two tree
/// positions, and a parsed POCO is cheap to re-build). Maps are not cached (a level is loaded once).</para>
///
/// All file reads go through the VFS, so a missing or malformed asset throws an
/// <see cref="XonoticGodot.Formats.AssetParseException"/> that callers can catch to skip the asset rather than crash.
/// </summary>
public sealed class AssetLoader
{
    // Model file magics (first bytes). MD3/MDL are 4-byte tags; IQM/DPM are 16-byte (NUL-padded) tags.
    private const string MagicIqm = "INTERQUAKEMODEL";
    private const string MagicDpm = "DARKPLACESMODEL";
    private const string MagicMd3 = "IDP3";
    private const string MagicMdl = "IDPO"; // Quake1 MDL (alias) — casings, gib chunk, legacy projectile props

    private readonly VirtualFileSystem _vfs;
    private readonly AssetSystem _assets;
    private readonly FontLoader _fonts;

    // We cache the *parsed* inputs (the expensive binary decode + sidecar parse), NOT the built Godot node:
    // the builders return nodes backed by C# script fields (Md3Morph, Skeleton3D-driven IQM/DPM) that
    // Node.Duplicate() would not deep-copy correctly, and Godot resources/nodes can't be shared across two
    // tree positions anyway. So each LoadModel/LoadSprite call rebuilds a fresh node from the cached parse —
    // cheap relative to re-reading and re-decoding the file. Null caches a known parse failure.
    private readonly Dictionary<string, Func<Node3D?>?> _modelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Node3D?>?> _spriteCache = new(StringComparer.Ordinal);
    // Parsed MD3 data cache for the client's per-entity ModelResolver (world items/gibs/monsters render via the
    // MD3 morph/snapshot path). Keyed by normalized vpath; null caches a miss / non-MD3 model.
    private readonly Dictionary<string, Md3Data?> _md3Cache = new(StringComparer.Ordinal);
    // Per-weapon shot-origin (QC movedir, Quake model-local coords), keyed by the "v|h" vpath pair (or just v
    // when no hand rig); null caches "neither model has a shot tag".
    private readonly Dictionary<string, System.Numerics.Vector3?> _muzzleOffsetCache = new(StringComparer.Ordinal);
    // Per-h_-rig first-person render bucket: true = invisible-hand (render v_ attached to "weapon" bone),
    // false = full-model (render the h_ rig itself), null = missing/unparsable. Keyed by normalized vpath.
    private readonly Dictionary<string, bool?> _rigBucketCache = new(StringComparer.Ordinal);
    // Resolved sample -> decoded AudioStream (or null when the sample resolves to nothing). The stream is
    // immutable and safe to share across many AudioStreamPlayer3D, so unlike models we cache the built resource.
    private readonly Dictionary<string, AudioStream?> _soundCache = new(StringComparer.Ordinal);

    // (perf 2026-07-03) Grow-only per-thread FILE buffer for model reads — the same pooling AssetSystem's
    // texture path uses (LoadImageFromVpath). Every LoadModel/ParseSkeletalModel/LoadMd3 used to allocate a
    // fresh full-file byte[] (player IQMs ~0.5-1.2 MB, all > the 85 KB LOH threshold), which at the bot-join
    // window meant one LOH array per roster model inside a single frame. The parsed output never aliases the
    // input (every reader copies into its own arrays), so the buffer is safe to reuse across reads on the
    // same thread. Touched from the main thread and the streamer's dedicated lane workers (a small FIXED
    // thread set — see BackgroundAssetStreamer — so per-thread growth is bounded and converges).
    [ThreadStatic] private static byte[]? _fileScratch;

    /// <summary>Read a model file into the per-thread scratch and return the valid window. The span is only
    /// valid until the next read on this thread — parse it fully before reading anything else.</summary>
    private ReadOnlySpan<byte> ReadModelBytes(string key)
    {
        int length = _vfs.ReadBytesInto(key, ref _fileScratch);
        return new ReadOnlySpan<byte>(_fileScratch, 0, length);
    }

    // (hitch-fix 2026-06-14) Skeletal-IQM parse cache keyed by normalized vpath (model only — none of these
    // depend on skinIndex; the skin only selects materials, loaded separately per call). The AnimationLibrary
    // build is the 100-360 ms / ~50-100 MB worker burst behind the bot-spawn hitch storm (PERFORMANCE_REPORT
    // §13.3 backlog #1): every bot wearing the same model re-ran it. The parsed IqmData is immutable post-read and
    // the AnimationLibrary is attached verbatim (IqmBuilder.Build -> AddAnimationLibrary, no per-instance
    // mutation; bone tracks are "Skeleton3D:bone" paths identical across instances), so both are safe to share.
    // Touched from BackgroundAssetStreamer worker threads -> guarded by _skeletalCacheGate.
    private readonly Dictionary<string, (IqmData Iqm, IReadOnlyList<FrameGroup>? Groups, AnimationLibrary Anims, string? DefaultClip)>
        _skeletalParseCache = new(StringComparer.Ordinal);
    private readonly object _skeletalCacheGate = new();

    /// <summary>The virtual filesystem this loader reads from (mounted gamedirs/pk3s).</summary>
    public VirtualFileSystem Vfs => _vfs;

    /// <summary>The material/texture resolver shared with the model/map builders.</summary>
    public AssetSystem Assets => _assets;

    /// <summary>The font cache/loader over the same VFS.</summary>
    public FontLoader Fonts => _fonts;

    /// <summary>
    /// Build a loader over an already-mounted <paramref name="vfs"/>. The <see cref="AssetSystem"/> is
    /// constructed over the same VFS so shader/texture resolution sees the same mounts.
    /// </summary>
    public AssetLoader(VirtualFileSystem vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _assets = new AssetSystem(_vfs);
        _fonts = new FontLoader(_vfs);
    }

    /// <summary>Build a loader over an existing <see cref="AssetSystem"/> (when the host already made one).</summary>
    public AssetLoader(VirtualFileSystem vfs, AssetSystem assets)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _fonts = new FontLoader(_vfs);
    }

    // =============================================================================================
    //  Models  (dispatch by magic)
    // =============================================================================================

    /// <summary>
    /// Load a model from the VFS at <paramref name="vpath"/>, dispatching by the file's magic (not its
    /// extension) to the right importer + Godot builder, and passing any sibling <c>.framegroups</c> /
    /// <c>_N.skin</c> sidecars. Returns an independent scene node (a duplicate of the cached template), or
    /// <c>null</c> when the file is missing or cannot be parsed.
    /// </summary>
    /// <param name="vpath">Virtual path, e.g. <c>"models/player/erebus.iqm"</c>.</param>
    /// <param name="skinIndex">Which <c>_N.skin</c> variant to apply for skinned formats (MD3). Default 0.</param>
    public Node3D? LoadModel(string vpath, int skinIndex = 0)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            return null;

        // [#48] Quake SPRITE models (.spr — the chat bubble models/misc/chatbubble.spr is the stock user): route
        // through the real sprite pipeline (SpriteReader parses IDSP/IDS2 incl. the Half-Life embedded-palette
        // chatbubble; SpriteBuilder sizes the quad from the frame, picks the billboard mode from the sprite type,
        // applies the frame origin, animates multi-frame sprites, and caches). SpriteBuilder's DP external-frame
        // override still prefers the shipped `<name>.spr_0.tga` when present. This replaced a hand-rolled 16×16
        // quad that bypassed all of the above; LoadSprite returns null only when the container can't be parsed.
        if (key.EndsWith(".spr", StringComparison.OrdinalIgnoreCase))
        {
            Node3D? sprite = LoadSprite(key);
            if (sprite is not null)
                return sprite;
            // Unparseable container → fall through to the normal (placeholder) path.
        }

        // Cache key includes the skin variant (different skins build different nodes).
        string cacheKey = skinIndex == 0 ? key : $"{key}#skin{skinIndex}";
        if (!_modelCache.TryGetValue(cacheKey, out Func<Node3D?>? factory))
        {
            factory = BuildModelFactory(key, skinIndex);
            _modelCache[cacheKey] = factory;
        }
        return factory?.Invoke();
    }

    /// <summary>
    /// Parse a model as raw <see cref="Md3Data"/> for the client's per-entity <c>ClientWorld.ModelResolver</c>
    /// (world items/gibs/monsters that render via the MD3 morph/snapshot path). Returns null for a missing file
    /// or a non-MD3 model (IQM/DPM go through <see cref="LoadModel"/>); cached per normalized vpath.
    /// </summary>
    public Md3Data? LoadMd3(string vpath)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            return null;
        if (_md3Cache.TryGetValue(key, out Md3Data? cached))
            return cached;

        Md3Data? data = null;
        try
        {
            ReadOnlySpan<byte> bytes = ReadModelBytes(key);
            if (ReadMagic(bytes).StartsWith(MagicMd3, StringComparison.Ordinal))
                data = Md3Reader.Read(bytes);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] md3 '{key}' read/parse failed: {ex.Message}");
        }
        _md3Cache[key] = data;
        return data;
    }

    /// <summary>
    /// Compute a weapon's per-model shot origin (QC <c>movedir</c>) in MODEL-LOCAL Quake coords (x=fwd, y=+left,
    /// z=up), faithful to <c>CL_WeaponEntity_SetModel</c>'s full v_-then-h_ selection (all.qc:367-424): given the
    /// <c>v_*</c> VISUAL model path and the sibling <c>h_*</c> HAND RIG path, prefer the v_ model's own
    /// <c>shot</c>/<c>tag_shot</c> tag transformed THROUGH the h_ rig's <c>weapon</c>/<c>tag_weapon</c> attach
    /// bone; else fall back to the h_ rig's OWN shot tag; else null. Both files are magic-dispatched (extensions
    /// lie: v_*.md3 are often IQM, h_*.iqm are DPM) onto the Godot-free
    /// <see cref="XonoticGodot.Formats.MuzzleTag.ComputeShotOrigin"/>. (In stock content every v_ model is
    /// tag-less, so this always resolves to the h_ rig's own shot tag — but the composition is here and correct
    /// for any future weapon that ships a v_ shot tag.) Returns null when neither model carries a shot tag — the
    /// caller then keeps the generic muzzle fallback. Cached per normalized (v,h) vpath pair.
    /// </summary>
    /// <param name="vModelPath">The <c>v_*</c> visual-model vpath (e.g. <c>models/weapons/v_rl.md3</c>).</param>
    /// <param name="hRigPath">The sibling <c>h_*</c> hand-rig vpath, or null/missing to use only the v_ model.</param>
    public System.Numerics.Vector3? LoadMuzzleOffset(string vModelPath, string? hRigPath)
    {
        string vKey = AssetPaths.Normalize(vModelPath);
        if (vKey.Length == 0)
            return null;
        string hKey = string.IsNullOrEmpty(hRigPath) ? "" : AssetPaths.Normalize(hRigPath!);
        string cacheKey = hKey.Length == 0 ? vKey : $"{vKey}|{hKey}";
        if (_muzzleOffsetCache.TryGetValue(cacheKey, out System.Numerics.Vector3? cached))
            return cached;

        // Load each model INDEPENDENTLY (try-read, NO _vfs.Exists pre-gate). A stock weapon's v_ model is
        // tag-less, so movedir comes from the h_ rig's own shot tag (ComputeShotOrigin branch 4) — if a flaky
        // Exists() (path-normalization mismatch) blocked the h_ load, the whole result collapsed to null and the
        // weapon silently fell back to the generic CENTERED muzzle (the "blaster fires from screen center, not
        // the gun" regression — the old single-arg path read the h_ rig directly with no Exists check). A
        // genuinely-absent file just yields Model.None here.
        XonoticGodot.Formats.MuzzleTag.Model vModel = TryLoadMuzzleModel(vKey);
        XonoticGodot.Formats.MuzzleTag.Model hRig = TryLoadMuzzleModel(hKey);
        System.Numerics.Vector3? offset = XonoticGodot.Formats.MuzzleTag.ComputeShotOrigin(vModel, hRig);
        _muzzleOffsetCache[cacheKey] = offset;
        return offset;
    }

    /// <summary>Parse a model into a <see cref="XonoticGodot.Formats.MuzzleTag.Model"/>, or <c>None</c> on any
    /// failure (empty key, missing file, unparsable) — so one model's absence never nulls the muzzle result.</summary>
    private XonoticGodot.Formats.MuzzleTag.Model TryLoadMuzzleModel(string key)
    {
        if (key.Length == 0)
            return XonoticGodot.Formats.MuzzleTag.Model.None;
        try { return LoadMuzzleModel(key); }
        catch { return XonoticGodot.Formats.MuzzleTag.Model.None; }
    }

    /// <summary>
    /// Classify a weapon's first-person render bucket from its <c>h_*</c> HAND RIG (Base
    /// <c>CL_WeaponEntity_SetModel</c>, all.qc:381-400): does the rig expose a <c>weapon</c>/<c>tag_weapon</c>
    /// attach bone?
    /// <list type="bullet">
    ///   <item><c>true</c> — <b>invisible-hand</b> (the IQM rigs h_arc/h_nex/h_shotgun/…): Base creates a
    ///   <c>weaponchild</c> and renders the <c>v_</c> visual model attached to that bone.</item>
    ///   <item><c>false</c> — <b>full-model</b> (the DPM rigs h_rl/h_crylink/h_electro/h_gl/h_hagar/ok_*): Base
    ///   leaves <c>weaponchild</c> NULL and renders the <b>h_ rig itself</b> (its own gun+hand mesh).</item>
    ///   <item><c>null</c> — no rig file at this path, or it could not be parsed (caller treats as invisible-hand
    ///   so it keeps the legacy v_ path rather than rendering nothing).</item>
    /// </list>
    /// Dispatch is by on-disk magic (extensions lie: h_*.iqm are often DPM). Cached per normalized vpath.
    /// </summary>
    /// <param name="hRigPath">The <c>h_*</c> hand-rig vpath (e.g. <c>models/weapons/h_rl.iqm</c>).</param>
    public bool? WeaponRigIsInvisibleHand(string hRigPath)
    {
        string key = AssetPaths.Normalize(hRigPath);
        if (key.Length == 0 || !_vfs.Exists(key))
            return null;
        if (_rigBucketCache.TryGetValue(key, out bool? cached))
            return cached;

        bool? result = null;
        try
        {
            XonoticGodot.Formats.MuzzleTag.Model rig = LoadMuzzleModel(key);
            result = XonoticGodot.Formats.MuzzleTag.IsInvisibleHandRig(rig);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] rig classify '{key}' read/parse failed: {ex.Message}");
        }
        _rigBucketCache[key] = result;
        return result;
    }

    /// <summary>Read+magic-dispatch a single model file into a Godot-free <see cref="XonoticGodot.Formats.MuzzleTag.Model"/>
    /// (or the empty model on a missing/unknown file). Extensions lie, so dispatch is by on-disk magic.</summary>
    private XonoticGodot.Formats.MuzzleTag.Model LoadMuzzleModel(string key)
    {
        ReadOnlySpan<byte> bytes = ReadModelBytes(key);
        string magic = ReadMagic(bytes);
        if (magic.StartsWith(MagicIqm, StringComparison.Ordinal))
            return XonoticGodot.Formats.MuzzleTag.Model.Of(IqmReader.Read(bytes));
        if (magic.StartsWith(MagicDpm, StringComparison.Ordinal))
            return XonoticGodot.Formats.MuzzleTag.Model.Of(DpmReader.Read(bytes));
        if (magic.StartsWith(MagicMd3, StringComparison.Ordinal))
            return XonoticGodot.Formats.MuzzleTag.Model.Of(Md3Reader.Read(bytes));
        return XonoticGodot.Formats.MuzzleTag.Model.None;
    }

    /// <summary>
    /// Read+parse the model and its sidecars once and return a factory that builds a fresh Godot node from
    /// the cached parse on each call. Returns null (cached) if the file is missing or not a known model.
    /// </summary>
    private Func<Node3D?>? BuildModelFactory(string key, int skinIndex)
    {
        ReadOnlySpan<byte> bytes;
        try
        {
            bytes = ReadModelBytes(key);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] model '{key}' read failed: {ex.Message}");
            return null;
        }

        string magic = ReadMagic(bytes);
        try
        {
            if (magic.StartsWith(MagicIqm, StringComparison.Ordinal))
            {
                IqmData iqm = IqmReader.Read(bytes);
                IReadOnlyList<FrameGroup>? groups = LoadFrameGroups(key);
                SkinFile? skin = LoadSkin(key, skinIndex);
                return () => IqmBuilder.Build(iqm, _assets, groups, skin);
            }
            if (magic.StartsWith(MagicDpm, StringComparison.Ordinal))
            {
                DpmData dpm = DpmReader.Read(bytes);
                IReadOnlyList<FrameGroup>? groups = LoadFrameGroups(key);
                return () => DpmBuilder.Build(dpm, _assets, groups);
            }
            if (magic.StartsWith(MagicMd3, StringComparison.Ordinal))
            {
                Md3Data md3 = Md3Reader.Read(bytes);
                SkinFile? skin = LoadSkin(key, skinIndex);
                IReadOnlyList<FrameGroup>? groups = LoadFrameGroups(key);
                return () => Md3Builder.Build(md3, _assets, skin, groups);
            }
            if (magic.StartsWith(MagicMdl, StringComparison.Ordinal))
            {
                // Quake1 MDL ("IDPO"). The files that actually reach here are static single-frame props
                // (models/casing_shell.mdl + casing_steel.mdl for the shotgun casing, models/gibs/chunk.mdl for
                // the fast gib chunk). Build the shared geometry + palette-decoded skin material ONCE, then hand
                // out cheap MeshInstance3D instances — the same "cache the parse, rebuild the node" contract the
                // IQM/DPM/MD3 factories use, so a per-casing/per-gib spawn is a node alloc, not a re-decode.
                MdlData mdl = MdlReader.Read(bytes);
                MdlBuilder.Prepared prep = MdlBuilder.Prepare(mdl, 0);
                return () => MdlBuilder.Instantiate(prep);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] model '{key}' parse failed: {ex.Message}");
            return null;
        }

        // ── Not-yet-implemented importers: MD2 / ZYM / PSK  (TODO T72 — NOT a deliberate cut) ──────────
        // Darkplaces dispatches model loads by leading magic (model_shared.c:45-65): "IDPO"→MDL, "IDP2"→MD2,
        // "ZYMOTICMODEL"→ZYM, "ACTRHEAD"→PSK, plus "IDP3"→MD3 / "INTERQUAKEMODEL"→IQM / "DARKPLACESMODEL"→DPM.
        // This port implements IQM, DPM, MD3 and MDL (the clause above; MdlReader/MdlBuilder, added 2026-07 —
        // casing_shell/casing_steel/gibs-chunk are genuine Quake1 MDLs that used to spam "not a known model"
        // every boot). MD2/ZYM/PSK are simply NOT PORTED YET (tracked as TODO T72); a file in one of them falls
        // through to the null+log below and the caller's placeholder. Content impact:
        //   • ZYM ("ZYMOTICMODEL", DP Mod_ZYMOTICMODEL_Load): 2 real map props (models/pomp/pomp.zym,
        //     models/train.zym in xonotic-maps.pk3dir) render as placeholders until a reader lands.
        //   • MD2 ("IDP2", Mod_IDP2_Load) / PSK ("ACTRHEAD", Mod_PSKMODEL_Load): no shipped Base content today —
        //     mod / custom-map compat only.
        // Implement any of them by mirroring the MDL importer (src/XonoticGodot.Formats/Mdl + models/MdlBuilder).
        GD.PrintErr($"[AssetLoader] '{key}' is not a known model (magic \"{Printable(magic)}\").");
        return null;
    }

    /// <summary>
    /// Load + parse a player model's <c>_N.txt</c> model-info sidecar (e.g. <c>erebus.iqm_0.txt</c>): the
    /// per-skin skeletal parameters (bone_upperbody / bone_aim* / fixbone) the upper/lower split + aim need.
    /// Returns null when the model has no such sidecar.
    /// </summary>
    public ModelInfo? LoadModelInfo(string vpath, int skinIndex = 0)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0) return null;
        string sidecar = $"{key}_{skinIndex}.txt";
        if (!_vfs.Exists(sidecar)) return null;
        try { return ModelInfoParser.Parse(_vfs.ReadText(sidecar)); }
        catch (Exception ex) { GD.PrintErr($"[AssetLoader] modelinfo '{sidecar}' failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// The pieces a <c>PlayerModel</c> needs to drive a skeletal IQM at runtime: the parsed
    /// <see cref="IqmData"/> (bones + per-frame poses), a freshly built Godot scene root
    /// (<see cref="Skeleton3D"/> + skinned mesh + <see cref="AnimationPlayer"/>), the animation clip ranges,
    /// and the model-info skeletal parameters.
    /// </summary>
    public sealed record SkeletalModelParts(
        IqmData Iqm, Node3D Root, IReadOnlyList<FrameGroup>? Groups, ModelInfo? Info);

    /// <summary>
    /// Load a skeletal (IQM) model and its sidecars for runtime CPU posing (the player upper/lower split +
    /// aim). Returns null for a missing file or a non-IQM model (only IQM carries the skeleton + per-frame
    /// poses Xonotic players animate with). The caller wraps the result in a <c>PlayerModel</c>.
    /// </summary>
    public SkeletalModelParts? LoadSkeletalModel(string vpath, int skinIndex = 0)
    {
        SkeletalModelParse? parse = ParseSkeletalModel(vpath, skinIndex);
        return parse is null ? null : BuildSkeletalModel(parse);
    }

    /// <summary>The off-thread product of <see cref="ParseSkeletalModel"/>: the parsed IQM + its sidecars
    /// (pure data) plus the pre-built <see cref="AnimationLibrary"/> (§12.3-1 — a Resource built on the worker
    /// and handed to the main thread once, the supported Godot threading pattern; ~130 ms of track-key work a
    /// player-model build no longer pays on the main thread). <see cref="BuildSkeletalModel"/> turns it into
    /// the scene parts on the main thread. This is the parse/build split the background asset streamer drives.</summary>
    public sealed record SkeletalModelParse(
        IqmData Iqm, IReadOnlyList<FrameGroup>? Groups, ModelInfo? Info, SkinFile? Skin,
        AnimationLibrary? Anims = null, string? DefaultClip = null);

    /// <summary>
    /// OFF-THREAD phase of a skeletal-model load (S1): read + parse the IQM and its <c>_N.txt</c>/skin/frame-group
    /// sidecars (pure C# over thread-safe VFS reads), then pre-build the animation library (worker-safe, see
    /// <see cref="IqmBuilder.BuildAnimationLibrary"/>). Returns null for a missing or non-IQM model. Pair with
    /// <see cref="BuildSkeletalModel"/> on the main thread.
    /// </summary>
    public SkeletalModelParse? ParseSkeletalModel(string vpath, int skinIndex = 0)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0) return null;

        // (hitch-fix) Cache hit: reuse the shared parsed IqmData + AnimationLibrary (the expensive ~360 ms /
        // ~96 MB build); only the per-skin sidecars (Info, Skin) are loaded fresh. This is what collapses the
        // bot-spawn storm — N bots on one model now pay the build ONCE, not N times.
        lock (_skeletalCacheGate)
        {
            if (_skeletalParseCache.TryGetValue(key, out var hit))
            {
                Prof.Event($"anim cache HIT {key}");
                return new SkeletalModelParse(hit.Iqm, hit.Groups, LoadModelInfo(vpath, skinIndex),
                    LoadSkin(key, skinIndex), hit.Anims, hit.DefaultClip);
            }
        }

        ReadOnlySpan<byte> bytes;
        try { bytes = ReadModelBytes(key); }
        catch (Exception ex) { GD.PrintErr($"[AssetLoader] skeletal model '{key}' read failed: {ex.Message}"); return null; }

        if (!ReadMagic(bytes).StartsWith(MagicIqm, StringComparison.Ordinal))
            return null;
        try
        {
            IqmData iqm = IqmReader.Read(bytes);
            IReadOnlyList<FrameGroup>? groups = LoadFrameGroups(key);
            AnimationLibrary anims;
            string? defaultClip;
            using (Prof.Sample("iqm.anims"))   // attribution: this now costs the WORKER, not the frame
                anims = IqmBuilder.BuildAnimationLibrary(iqm, groups, out defaultClip);

            // Publish to the cache (double-build race is harmless — last writer wins, all readers get a valid
            // shared library). Build happened OUTSIDE the lock so a slow build never blocks other models' parses.
            lock (_skeletalCacheGate)
            {
                if (_skeletalParseCache.TryGetValue(key, out var raced))
                    (iqm, groups, anims, defaultClip) = (raced.Iqm, raced.Groups, raced.Anims, raced.DefaultClip);
                else
                    _skeletalParseCache[key] = (iqm, groups, anims, defaultClip);
            }
            Prof.Event($"anim cache MISS {key}");
            return new SkeletalModelParse(iqm, groups, LoadModelInfo(vpath, skinIndex), LoadSkin(key, skinIndex),
                anims, defaultClip);
        }
        catch (Exception ex) { GD.PrintErr($"[AssetLoader] skeletal model '{key}' parse failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// The material names a parsed model's build will resolve (post skin-remap, nodraw meshes skipped) —
    /// the work-list for the staged texture predecode/upload jobs (§12.3-1). Off-thread-safe (pure data).
    /// </summary>
    public static List<string> EffectiveMaterials(SkeletalModelParse parse)
    {
        var mats = new List<string>(4);
        foreach (XonoticGodot.Formats.Iqm.IqmMesh sub in parse.Iqm.Meshes)
        {
            string? name = IqmBuilder.EffectiveMaterialName(sub, parse.Skin, out _);
            if (name is not null && !mats.Contains(name))
                mats.Add(name);
        }
        return mats;
    }

    /// <summary>
    /// MAIN-THREAD phase of a skeletal-model load (S1): turn an off-thread <see cref="ParseSkeletalModel"/> bundle
    /// into the Godot scene parts (<see cref="IqmBuilder.Build"/> creates the Skeleton3D + skinned mesh +
    /// materials, which is RenderingServer-backed and must stay on the main thread). The animation library
    /// arrives pre-built from the parse.
    /// </summary>
    public SkeletalModelParts? BuildSkeletalModel(SkeletalModelParse parse)
    {
        if (parse is null) return null;
        try
        {
            Node3D root = IqmBuilder.Build(parse.Iqm, _assets, parse.Groups, parse.Skin,
                parse.Anims, parse.DefaultClip);
            return new SkeletalModelParts(parse.Iqm, root, parse.Groups, parse.Info);
        }
        catch (Exception ex) { GD.PrintErr($"[AssetLoader] skeletal model build failed: {ex.Message}"); return null; }
    }

    // =============================================================================================
    //  Maps
    // =============================================================================================

    /// <summary>
    /// Load a BSP map from the VFS into Godot render geometry. Reads the bytes, parses with
    /// <see cref="BspReader"/>, and builds the textured mesh via <see cref="MapLoader.BuildMap"/> (which
    /// resolves materials through the shared <see cref="AssetSystem"/>). Returns the map root node or null.
    /// Use <see cref="ReadBsp"/> + <see cref="MapLoader.BuildCollision"/> for the matching collision world
    /// (parse once, build both render geometry and collision).
    /// </summary>
    public Node3D? LoadMap(string vpath)
    {
        BspData? bsp = ReadBsp(vpath);
        if (bsp is null)
            return null;
        try
        {
            // Pass the vpath so external lightmaps (maps/<name>/lm_NNNN.jpg) resolve — stock Xonotic maps
            // carry no internal lightmap lump, so without a name the world renders with no baked lighting.
            return MapLoader.BuildMap(bsp, _assets, vpath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] map '{vpath}' build failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse a BSP from the VFS and return its raw <see cref="BspData"/> (so a caller can build both the
    /// render geometry and the collision world from one parse without reading the file twice). Null on miss.
    /// </summary>
    public BspData? ReadBsp(string vpath)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            return null;
        try
        {
            byte[] bytes = _vfs.ReadBytes(key);
            return BspReader.Read(bytes);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] map '{key}' read/parse failed: {ex.Message}");
            return null;
        }
    }

    // =============================================================================================
    //  Sprites
    // =============================================================================================

    /// <summary>
    /// Load a Quake-family sprite (spr/sprhl/spr32/sp2) from the VFS into a billboarded Godot node via
    /// <see cref="SpriteBuilder.Build"/>. Returns an independent node (duplicate of the cached template), or
    /// null on miss/parse failure. Dispatch is by the parsed sprite <see cref="SpriteData.Format"/>, not the
    /// file extension (SpriteReader reads the magic).
    /// </summary>
    public Node3D? LoadSprite(string vpath)
    {
        string key = AssetPaths.Normalize(vpath);
        if (key.Length == 0)
            return null;

        if (!_spriteCache.TryGetValue(key, out Func<Node3D?>? factory))
        {
            factory = BuildSpriteFactory(key);
            _spriteCache[key] = factory;
        }
        return factory?.Invoke();
    }

    /// <summary>Read+parse the sprite once; return a factory that builds a fresh node from the cached parse.</summary>
    private Func<Node3D?>? BuildSpriteFactory(string key)
    {
        SpriteData spr;
        try
        {
            spr = SpriteReader.Read(ReadModelBytes(key));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] sprite '{key}' read/parse failed: {ex.Message}");
            return null;
        }
        return () => SpriteBuilder.Build(spr, _assets, key);
    }

    // =============================================================================================
    //  Textures / materials  (delegate to AssetSystem)
    // =============================================================================================

    /// <summary>Resolve a texture base name (extension-agnostic) to a Godot <see cref="Texture2D"/>, or null.</summary>
    public Texture2D? LoadTexture(string name)
    {
        try
        {
            return _assets.LoadTexture(name);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] texture '{name}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Resolve a Q3/DP shader name to a Godot <see cref="Material"/> through the shared compiler.</summary>
    public Material ResolveMaterial(string shaderName) => _assets.ResolveMaterial(shaderName);

    /// <summary>Get a UI/HUD font by logical name (e.g. "xolonium"), loaded from the font packs. Null on miss.</summary>
    public FontFile? GetFont(string name) => _fonts.GetFont(name);

    // =============================================================================================
    //  Sounds  (VFS → Godot AudioStream)
    // =============================================================================================

    /// <summary>
    /// Resolve a QC sound sample (e.g. <c>"weapons/rocket_impact"</c>, with or without a leading
    /// <c>sound/</c> and with or without an extension) to a decoded Godot <see cref="AudioStream"/> read
    /// from the mounted VFS — the audio equivalent of <see cref="LoadTexture"/>. Reproduces Darkplaces'
    /// <c>S_PrecacheSound</c> path search: the bare sample is rooted under <c>sound/</c> and probed as
    /// <c>.ogg</c> then <c>.wav</c>. Returns a cached stream (shared, immutable) or <c>null</c> when the
    /// sample resolves to no file / fails to decode, so callers can fall back or stay silent.
    /// </summary>
    public AudioStream? LoadSound(string sample)
    {
        if (string.IsNullOrWhiteSpace(sample))
            return null;

        string cacheKey = AssetPaths.Normalize(sample);
        if (cacheKey.Length == 0)
            return null;
        if (_soundCache.TryGetValue(cacheKey, out AudioStream? cached))
            return cached;

        AudioStream? stream = null;
        string? vpath = ResolveSoundVpath(cacheKey);
        if (vpath is not null)
        {
            try
            {
                stream = BuildAudioStream(_vfs.ReadBytes(vpath), vpath);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AssetLoader] sound '{vpath}' load failed: {ex.Message}");
            }
        }

        _soundCache[cacheKey] = stream; // cache misses too, to avoid re-probing a known-absent sample
        return stream;
    }

    /// <summary>The ordered VFS vpaths a sample is probed at: under <c>sound/</c> (DP convention) and verbatim,
    /// each as <c>.ogg</c> then <c>.wav</c>. Returns the first that exists, or null.</summary>
    private string? ResolveSoundVpath(string normalizedSample)
    {
        // Strip a trailing audio extension to get the stem, then re-probe both extensions.
        string stem = normalizedSample;
        string ext = AssetPaths.GetExtension(stem);
        if (ext is "ogg" or "wav")
            stem = AssetPaths.StripExtension(stem);

        // Root under sound/ unless the caller already did (some QC samples are pre-rooted).
        string underSound = stem.StartsWith("sound/", StringComparison.Ordinal) ? stem : "sound/" + stem;

        foreach (string root in underSound == stem ? new[] { stem } : new[] { underSound, stem })
        {
            if (_vfs.Exists(root + ".ogg")) return root + ".ogg";
            if (_vfs.Exists(root + ".wav")) return root + ".wav";
        }
        return null;
    }

    /// <summary>
    /// Decode raw audio bytes into a Godot <see cref="AudioStream"/>: <c>.ogg</c> via
    /// <see cref="AudioStreamOggVorbis.LoadFromBuffer"/>, <c>.wav</c> via a minimal RIFF/PCM parse into an
    /// <see cref="AudioStreamWav"/> (8/16-bit, mono/stereo — the formats Xonotic ships). Null on unsupported.
    /// </summary>
    private static AudioStream? BuildAudioStream(byte[] bytes, string vpath)
    {
        string ext = AssetPaths.GetExtension(vpath);
        return ext switch
        {
            "ogg" => AudioStreamOggVorbis.LoadFromBuffer(bytes),
            "wav" => DecodeWav(bytes),
            _ => null,
        };
    }

    /// <summary>Minimal canonical-WAV (RIFF/PCM) → <see cref="AudioStreamWav"/>: reads the <c>fmt </c> and
    /// <c>data</c> chunks, supporting 8-bit (unsigned→signed) and 16-bit LE PCM, mono or stereo.</summary>
    private static AudioStreamWav? DecodeWav(byte[] b)
    {
        if (b.Length < 12 ||
            b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
            b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            return null;

        int channels = 0, sampleRate = 0, bits = 0;
        byte[]? data = null;

        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > b.Length)
                size = b.Length - body; // tolerate a truncated/over-stated size

            if (id == "fmt " && body + 16 <= b.Length)
            {
                channels = BitConverter.ToUInt16(b, body + 2);
                sampleRate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
            }
            else if (id == "data")
            {
                data = new byte[size];
                Array.Copy(b, body, data, 0, size);
            }
            pos = body + size + (size & 1); // chunks are word-aligned
        }

        if (data is null || channels is < 1 or > 2 || sampleRate <= 0)
            return null;

        var wav = new AudioStreamWav { MixRate = sampleRate, Stereo = channels == 2 };
        if (bits == 16)
        {
            wav.Format = AudioStreamWav.FormatEnum.Format16Bits;
            wav.Data = data;
        }
        else if (bits == 8)
        {
            // WAV 8-bit is unsigned; Godot's Format8Bits expects signed — rebias by 128.
            var signed = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                signed[i] = (byte)(data[i] - 128);
            wav.Format = AudioStreamWav.FormatEnum.Format8Bits;
            wav.Data = signed;
        }
        else
        {
            return null; // 24/32-bit PCM not used by Xonotic SFX
        }
        return wav;
    }

    // =============================================================================================
    //  Sidecars
    // =============================================================================================

    /// <summary>
    /// Load and parse the model's <c>.framegroups</c> sidecar (e.g. <c>player.iqm.framegroups</c>): the named
    /// animation ranges that carve the model's flat pose stack into clips. Returns null when there is no
    /// sidecar (the builder then uses the model's own anims / a single default clip). Weapon hand rigs
    /// (<c>models/weapons/h_*</c>) get Base's slot-index clip names stamped onto their nameless groups
    /// (all.qc:373-376: 0=fire, 1=fire2, 2=idle, 3=reload) so the ViewModel's idle/fire/reload lookups —
    /// and the builders' idle autoplay — address the ranges Base plays by index.
    /// </summary>
    private List<FrameGroup>? LoadFrameGroups(string modelKey)
    {
        // DP convention: "<modelname>.framegroups" sits next to the model (full filename + suffix).
        string sidecar = modelKey + ".framegroups";
        if (!_vfs.Exists(sidecar))
            return null;
        try
        {
            return WeaponRigAnims.NameGroups(modelKey, FrameGroups.Parse(_vfs.ReadText(sidecar)));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] framegroups '{sidecar}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load and parse the model's <c>_N.skin</c> sidecar (e.g. <c>player.iqm_0.skin</c>): per-mesh material
    /// overrides for the requested skin variant. Returns null when there is no matching skin file.
    /// </summary>
    private SkinFile? LoadSkin(string modelKey, int skinIndex)
    {
        // DP convention: "<modelname>_<index>.skin" (the index after the full filename, including extension).
        string sidecar = $"{modelKey}_{skinIndex}.skin";
        if (!_vfs.Exists(sidecar))
            return null;
        try
        {
            return SkinFile.Parse(_vfs.ReadText(sidecar));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AssetLoader] skin '{sidecar}' failed: {ex.Message}");
            return null;
        }
    }

    // =============================================================================================
    //  Enumeration helpers (used by the offline converter)
    // =============================================================================================

    /// <summary>Enumerate every model vpath under <c>models/</c> (by extension: iqm/dpm/md3).</summary>
    public IEnumerable<string> EnumerateModels()
    {
        foreach (string p in _vfs.Find("models/", "iqm")) yield return p;
        foreach (string p in _vfs.Find("models/", "dpm")) yield return p;
        foreach (string p in _vfs.Find("models/", "md3")) yield return p;
    }

    /// <summary>Enumerate every map vpath under <c>maps/</c> (.bsp).</summary>
    public IEnumerable<string> EnumerateMaps() => _vfs.Find("maps/", "bsp");

    /// <summary>Enumerate every sprite vpath (spr/spr32/sp2) anywhere in the mounts.</summary>
    public IEnumerable<string> EnumerateSprites()
    {
        foreach (string p in _vfs.Find("", "spr")) yield return p;
        foreach (string p in _vfs.Find("", "spr32")) yield return p;
        foreach (string p in _vfs.Find("", "sp2")) yield return p;
    }

    // =============================================================================================
    //  Low-level
    // =============================================================================================

    /// <summary>Read up to the first 16 bytes as an ASCII tag (trimming at the first NUL) for magic dispatch.</summary>
    private static string ReadMagic(ReadOnlySpan<byte> data)
    {
        int n = Math.Min(16, data.Length);
        int end = 0;
        while (end < n && data[end] != 0)
            end++;
        return System.Text.Encoding.ASCII.GetString(data.Slice(0, end));
    }

    private static string Printable(string magic)
    {
        var sb = new System.Text.StringBuilder(magic.Length);
        foreach (char c in magic)
            sb.Append(c >= 32 && c < 127 ? c : '?');
        return sb.ToString();
    }
}
