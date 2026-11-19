// Client cosmetic-model manager: spawns/updates/removes the freezetag ICE BLOCK and the BUFF carrier GLOW
// from an entity's decoded status effects. Port companions:
//   - ice block      = QC freezetag_Freeze -> "models/ice/ice.md3" with one of 21 frames, the player's
//                       team color and a world-axis offset of '0 0 -16'.
//                       (freezetag.presentation.ice_model)
//   - buff carrier    = QC sv_buffs.qc buffs_BuffModel_Spawn: "models/relics/relic.md3"
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
    /// <summary>QC freezetag ice model ("models/ice/ice.md3"). Frames 0..20.</summary>
    private const string IceModel = "models/ice/ice.md3";
    private static readonly Vector3 IceOffset = Coords.ToGodot(new System.Numerics.Vector3(0f, 0f, -16f));
    private const int IceFrameCount = 21;

    // ---- BUFF GLOW (buffs.carrier_model) ----
    /// <summary>QC buff carrier glow model ("models/relics/relic.md3").</summary>
    private const string BuffModel = "models/relics/relic.md3";

    internal static bool IsBuffRelic(string? model) =>
        string.Equals(model, BuffModel, StringComparison.OrdinalIgnoreCase);
    private static readonly Vector3 BuffOffsetQuake = new(0f, 0f, 40f);
    /// <summary>QC buff carrier glow scale (0.7).</summary>
    private const float BuffScale = 0.7f;

    // Per-entity state. Keyed by Entity.Index (the engine slot).
    private readonly Dictionary<int, AttachedCosmeticModel> _ice = new();
    private readonly Dictionary<int, (AttachedCosmeticModel model, int buffDefId)> _buffGlow = new();
    /// <summary>The cached random ice frame per entity — ONE pick per freeze, held until the entity thaws.</summary>
    private readonly Dictionary<int, int> _iceFrame = new();

    private readonly Dictionary<int, (AttachedCosmeticModel Model, VortexArena.Net.CountdownIndicatorState State)> _digits = new();
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

        DriveCountdown(e, attachRoot);
        DriveIce(e, idx, attachRoot);
        DriveBuffGlow(e, idx, attachRoot);
    }

    private void DriveCountdown(Entity entity, Node3D root)
    {
        var state = entity.CountdownIndicator;
        if (_digits.TryGetValue(entity.Index, out var previous))
        {
            if (GodotObject.IsInstanceValid(previous.Model))
            {
                if (previous.State == state && !previous.Model.IsQueuedForDeletion() && previous.Model.IsInsideTree())
                {
                    PlaceCountdown(previous.Model, root);
                    return;
                }
                previous.Model.QueueFree();
            }
            _digits.Remove(entity.Index);
        }
        if (state.Digit is < 1 or > 10) return;
        var rgb = Teams.ColorRgb(state.TargetTeam);
        var tint = state.TargetTeam == -2 ? new Color(0.5f, 0.5f, 0.5f) : new Color(rgb.X, rgb.Y, rgb.Z);
        var digit = AttachedCosmeticModel.Create(_assets(), $"models/sprites/{state.Digit}.spr32",
            colormod: tint,
            name: "KillIndicator", modelLoader: _modelLoader);
        if (digit is null) return;
        root.AddChild(digit);
        digit.TopLevel = true;
        PlaceCountdown(digit, root);
        _digits[entity.Index] = (digit, state);
    }

    private static void PlaceCountdown(AttachedCosmeticModel digit, Node3D root)
    {
        // The stock digits face the view plane. Orient the entire sprite so its
        // off-center frame origin is applied in the same view plane as the quad.
        Camera3D? camera = root.GetViewport().GetCamera3D();
        Basis axes = camera is null ? Basis.Identity : camera.GlobalBasis.Orthonormalized();
        digit.GlobalTransform = new Transform3D(axes.Scaled(Vector3.One * 0.5f),
            root.GlobalPosition + Vector3.Up * 52f);
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
                if (!_iceFrame.TryGetValue(idx, out int frame))
                    _iceFrame[idx] = frame = Random.Shared.Next(IceFrameCount);
                ice = AttachedCosmeticModel.Create(
                    _assets(), IceModel, skin: 0, rawFrame: frame,
                    colormod: teamColor,
                    md3Loader: _md3Loader, modelLoader: _modelLoader);
                if (ice != null)
                {
                    attachRoot.AddChild(ice);
                    ice.TopLevel = true;
                    _ice[idx] = ice;
                }
            }
            else
            {
                // Already iced — refresh the team tint only when the team actually changed (gated; avoids
                // re-pushing the colormod into the shader every frame).
                if (!ColorsEqual(_iceColor.TryGetValue(idx, out var prev) ? prev : default, teamColor))
                    ice.SetColormod(teamColor);
            }
            if (ice != null)
                ice.GlobalTransform = new Transform3D(
                    Basis.Identity.Scaled(attachRoot.GlobalBasis.Scale), attachRoot.GlobalPosition + IceOffset);
            _iceColor[idx] = teamColor;
        }
        else
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
                    localOffsetQuake: BuffOffsetQuake, scale: BuffScale, colormod: colormod,
                    md3Loader: _md3Loader, modelLoader: _modelLoader);
                if (model != null)
                {
                    attachRoot.AddChild(model);
                    ModelTint.SetRelicLighting(model, fullbright: true);
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
        if (_digits.Remove(entityIndex, out var digit) && GodotObject.IsInstanceValid(digit.Model))
            digit.Model.QueueFree();
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
        foreach (var entry in _digits.Values)
            if (GodotObject.IsInstanceValid(entry.Model)) entry.Model.QueueFree();
        _digits.Clear();
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
