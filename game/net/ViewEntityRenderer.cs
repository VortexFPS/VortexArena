using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Client;
using VortexArena.Net;

namespace VortexArena.Game.Net;

/// <summary>
/// Renders the held-weapon view-entities of networked players — the C# successor to CSQC's
/// <c>CL_SpawnWeaponentity</c> / the <c>wepent</c> weapon-entity channel (lib/csqcmodel + weapons/weapons.qc).
/// A remote player carries its active weapon id in <see cref="NetEntityState.Weapon"/>; this renderer attaches
/// (and swaps, on weapon change) that weapon's world model to the player, co-located with the player's
/// interpolated pose at a hand offset. A standalone <see cref="NetEntityKind.ViewModel"/> entity (a thrown gun
/// / detached weapon model) renders its own <see cref="NetEntityState.ModelIndex"/> the same way.
///
/// It owns its weapon nodes as children of the <see cref="ClientWorld"/> (so they live in the render tree and
/// are freed when it is). The actual mesh is host-injected via <see cref="WeaponModelFactory"/> (wired to the
/// asset pipeline's weapon world models); without it, a small placeholder stands in so the attachment is visible.
/// </summary>
public sealed class ViewEntityRenderer
{
    private readonly ClientWorld _render;

    /// <summary>Builds the world model for a weapon registry id (host-wired to the asset pipeline). Null → placeholder.</summary>
    public Func<int, Node3D?>? WeaponModelFactory { get; set; }

    /// <summary>The hand offset (Godot local space, relative to the player's posed frame) the weapon sits at.</summary>
    public Vector3 HandOffset { get; set; } = new(6f, 36f, -18f);

    // [W14a QW5] Weapon-switch raise/lower timing — the third-person counterpart of ViewModel.RaiseTime /
    // HolsterTime (view.qc viewmodel_draw raise/drop). On a switch the held world weapon dips down out of the hand
    // (WS_DROP) and the new one rises back into place (WS_RAISE) instead of the model popping in/out.
    private const float SwitchRaiseTime = 0.15f;   // seconds to raise the new gun into place (WS_RAISE)
    private const float SwitchDropTime = 0.10f;    // seconds to lower the gun out (WS_DROP)
    private const float SwitchLowerDistance = 22f; // Godot units the gun dips below the hand when fully lowered

    private sealed class Held
    {
        public Node3D Holder = null!;  // posed to the player frame each Update
        public Node3D? Model;          // the built weapon model child (offset by HandOffset + the switch dip)
        public int WeaponId = int.MinValue;
        public float LastAlpha = float.NaN; // last applied render alpha (QC exteriorweaponentity.alpha)

        // [#34] Hand-tag attachment (QC CL_ExteriorWeaponentity_Think: gettagindex "tag_weapon" else
        // setattachment "bip01 r hand", weaponsystem.qc:158-165). When the owner's skeletal model exposes its
        // weapon-bone marker (PlayerModel.TagWeaponMarker via ClientWorld.GetAttachmentMarker), the holder is
        // reparented UNDER that marker with an identity transform — the bone pose IS the gun pose — and
        // RestOffset goes to zero. While the owner's model is still streaming (no marker yet) the holder falls
        // back to the old origin+yaw pose with the fixed HandOffset; every Update re-checks, so the gun snaps
        // onto the hand the frame the skeleton lands.
        public Node3D? AttachedTo;     // the hand marker we're parented under (null = origin+HandOffset fallback)
        public Vector3 RestOffset;     // the model's rest position under the holder (Zero attached, HandOffset not)

        // [wepentconsumers] Exterior vortex charge-glow (weapon-vortex.fx.charge_glow): a per-INSTANCE emission
        // override on the carrier gun so EVERY observer sees the held weapon brighten with charge — the wr_glow
        // the registry flagged as first-person/host-only. The v_*.md3 models are cached + shared, so we can't
        // touch their material; we duplicate a StandardMaterial3D once per Held and drive its emission.
        public StandardMaterial3D? GlowMaterial; // per-instance duplicated material carrying the charge emission
        public Color LastGlow = new(float.NaN, float.NaN, float.NaN); // change gate (mirrors LastAlpha)

        // [W14a QW5] Remote weapon-switch render state (decoded from the wepent block onto the proxy Entity).
        public byte SwitchPhase;       // last seen WepPhase (0 ready, 1 = WS_RAISE, 2 = WS_DROP)
        public float SwitchOffset;     // [0,1] raise/lower lerp: 0 = fully raised (rest), 1 = fully lowered

        // [#36] Owner colormap last pushed onto the weapon's skin-shader instance uniforms (Base
        // weaponsystem.qc:180: the weaponentity inherits owner.colormap — the _shirt/_pants panels tint with
        // the carrier's colors). MinValue forces a (re)apply after a model rebuild.
        public int LastColormap = int.MinValue;
    }

    private readonly Dictionary<int, Held> _held = new();

    public ViewEntityRenderer(ClientWorld render) => _render = render;

    /// <summary>
    /// Update (or create) the weapon view-entity for <paramref name="e"/>: a player shows its
    /// <see cref="NetEntityState.Weapon"/>; a ViewModel entity shows its own model. Re-poses the holder to the
    /// entity's interpolated transform and rebuilds the mesh only when the weapon id actually changes.
    /// </summary>
    public void Update(Entity e, in NetEntityState s)
    {
        // A dead player or a player with no weapon shows nothing.
        int weaponId = s.Kind == NetEntityKind.ViewModel ? -2 /* sentinel: use model index */ : s.Weapon;
        bool wantWeapon = (s.Flags & NetEntityFlags.Dead) == 0
                          && (s.Kind == NetEntityKind.ViewModel || weaponId >= 0);
        if (!wantWeapon)
        {
            Remove(e.Index);
            return;
        }

        if (!_held.TryGetValue(e.Index, out Held? h))
        {
            h = new Held { Holder = new Node3D { Name = $"wepent#{e.Index}" }, RestOffset = HandOffset };
            if (GodotObject.IsInstanceValid(_render))
                _render.AddChild(h.Holder);
            _held[e.Index] = h;
        }

        // [#34] Attach a player's held weapon to the owner's HAND marker when available (QC setattachment to
        // tag_weapon / "bip01 r hand"); fall back to the origin+yaw fixed-offset pose while the skeletal model
        // is still streaming. Re-checked every Update so the gun migrates onto the hand as soon as it exists —
        // the old unconditional fallback left the gun hovering at roughly eye height, yaw-aligned to the view.
        if (GodotObject.IsInstanceValid(h.Holder))
        {
            Node3D? marker = s.Kind == NetEntityKind.Player ? _render.GetAttachmentMarker(e.Index, "tag_weapon") : null;
            // Only a REAL hand/tag marker counts — the entity-root fallback GetAttachmentMarker returns for a
            // model-less owner would just re-create the wrong-anchor bug with extra reparenting.
            bool markerUsable = marker is not null && GodotObject.IsInstanceValid(marker)
                && !ReferenceEquals(marker, _render) && marker.Name == "tag_weapon";
            if (markerUsable)
            {
                if (!ReferenceEquals(h.AttachedTo, marker) || h.Holder.GetParent() != marker)
                {
                    h.Holder.Reparent(marker!, keepGlobalTransform: false);
                    h.Holder.Transform = Transform3D.Identity; // the bone pose IS the gun pose
                    h.AttachedTo = marker;
                    h.RestOffset = Vector3.Zero;
                    if (h.Model is not null && GodotObject.IsInstanceValid(h.Model))
                        h.Model.Position = h.RestOffset; // re-seat immediately (AdvanceSwitch keeps it fresh)
                }
            }
            else
            {
                if (h.AttachedTo is not null)
                {
                    // Marker went away (model rebuild / respawn) — fall back to the render-root pose.
                    if (GodotObject.IsInstanceValid(_render) && h.Holder.GetParent() != _render)
                        h.Holder.Reparent(_render, keepGlobalTransform: false);
                    h.AttachedTo = null;
                    h.RestOffset = HandOffset;
                }
                // Pose the holder to the player's interpolated frame (same Quake→Godot mapping as EntityNode).
                h.Holder.Position = Coords.ToGodot(e.Origin);
                h.Holder.Rotation = new Vector3(0f, -Mathf.DegToRad(e.Angles.Y), 0f);
            }
        }

        // [W14a QW5] Pick which model to show during a switch, faithful to view.qc viewmodel_draw: WS_DROP
        // (phase 2) lowers the OLD weapon out of the hand, then at the bottom the model swaps and WS_RAISE
        // (phase 1) raises the NEW weapon (.m_switchingweapon) into place. Outside a transition (phase 0) show the
        // settled active weapon. Standalone ViewModel entities ignore the wepent block (model index only).
        bool playerHeld = s.Kind == NetEntityKind.Player;
        int displayWeaponId = (playerHeld && e.WepPhase == 1 && e.SwitchingWeapon >= 0)
            ? e.SwitchingWeapon
            : weaponId;

        // Rebuild the mesh only when the weapon changed (CSQCMODEL: a weapon swap respawns the wepent).
        int key = s.Kind == NetEntityKind.ViewModel ? -1 - s.ModelIndex : displayWeaponId;
        if (h.WeaponId != key)
        {
            h.WeaponId = key;
            FreeChildren(h.Holder);
            Node3D? model = WeaponModelFactory?.Invoke(s.Kind == NetEntityKind.ViewModel ? weaponId : displayWeaponId);
            model ??= Placeholder();
            h.Model = model;
            if (GodotObject.IsInstanceValid(h.Holder))
            {
                model.Position = HandOffset;
                h.Holder.AddChild(model);
            }
            h.LastAlpha = float.NaN; // fresh meshes start opaque — force a re-apply of the current alpha below
            // The freshly-built (or swapped) model carries no glow override yet; drop the cached material + gate
            // so the charge-glow block below re-builds and re-applies for the new mesh.
            // [crash fix 2026-07-26] Dispose the per-instance duplicated glow material instead of just
            // dropping the reference — it leaked to the finalizer thread on every vortex-holder swap.
            h.GlowMaterial?.Dispose();
            h.GlowMaterial = null;
            h.LastGlow = new Color(float.NaN, float.NaN, float.NaN);
            h.LastColormap = int.MinValue; // [#36] fresh mesh → re-push the owner colormap below
        }

        // [#36] Tint the held weapon's _shirt/_pants skin panels with the OWNER's resolved colormap — Base's
        // weaponentity inherits owner.colormap (weaponsystem.qc:180), so the gun panels match the body. Uses
        // the same resolution the player-body appearance pass uses (forcecolors + normalization). Without
        // this the skin shader's black no-colormap default rendered every panel desaturated gray forever.
        if (playerHeld && h.Model is not null && GodotObject.IsInstanceValid(h.Model)
            && GodotObject.IsInstanceValid(_render))
        {
            int cmap = _render.PlayerColormap(e);
            if (cmap != h.LastColormap)
            {
                h.LastColormap = cmap;
                ModelTint.ApplyColormap(h.Model, cmap);
            }
        }

        // [W14a QW5] Drive the raise/lower dip off the networked WepPhase (1 = WS_RAISE, 2 = WS_DROP); phase 0
        // (ready) settles the gun back to rest. The offset advances per frame in Process so it animates smoothly
        // regardless of snapshot cadence.
        h.SwitchPhase = playerHeld ? e.WepPhase : (byte)0;

        // QC CL_ExteriorWeaponentity_Think (server/weapons/weaponsystem.qc:170-175): the exterior weapon entity
        // does NOT simply copy the owner's alpha — it resolves through the three-way default rule:
        //   if (owner.alpha == default_player_alpha) alpha = default_weapon_alpha;  // owner at the spawn default
        //   else if (owner.alpha != 0)               alpha = owner.alpha;           // custom fade → match owner
        //   else                                     alpha = 1;                     // owner alpha 0 → opaque
        // The FIRST branch is what makes Running Guns work: the player spawns at default_player_alpha = -1
        // (hidden) but the gun takes default_weapon_alpha = +1 (visible) — a floating visible gun on an invisible
        // player. Copying owner.alpha unconditionally would hide the gun too, defeating the mutator. Cloaked's
        // 0.25 also routes through branch 1 (default_weapon_alpha == default_player_alpha), so the gun fades with
        // the player; a per-player Invisibility powerup fade routes through branch 2 (owner != default → match).
        // [W14a QW5] Prefer the dedicated exterior-weapon alpha (QC wepent .alpha, networked independently of the
        // body so Running Guns can hide the body but keep the gun visible). The server sends 1 (the opaque
        // default sentinel) when it has no explicit exterior alpha; in that case fall back to the legacy
        // owner-alpha resolution below so existing Cloaked/Invisibility behaviour is unchanged.
        float weaponAlpha = playerHeld && e.WepAlpha != 1f
            ? e.WepAlpha
            : ResolveExteriorWeaponAlpha(e.Alpha);
        if (GodotObject.IsInstanceValid(h.Holder) && weaponAlpha != h.LastAlpha)
        {
            h.LastAlpha = weaponAlpha;
            // Per-INSTANCE transparency (not a material edit): the weapon world models are CACHED + SHARED by
            // the asset pipeline, so mutating their materials would fade every player's weapon (the same lesson
            // PlayerModel.ApplyAlpha / CsqcModelEffects document). Godot Transparency is the inverse of QC alpha.
            float clamped = weaponAlpha < 0f ? 0f : (weaponAlpha > 1f ? 1f : weaponAlpha);
            ApplyTransparency(h.Holder, 1f - clamped);
        }

        // [wepentconsumers] Exterior vortex charge GLOW (weapon-vortex.fx.charge_glow / wr_glow): brighten the
        // carrier weapon for EVERY observer as its owner charges the Vortex, closing the registry gap where the
        // glow was only seen first-person / on the listen host. The charge fraction is decoded onto the proxy
        // (Entity.WepentView.VortexCharge); the color uses the SAME team-palette ramp as the first-person
        // viewmodel glow (NetGame.UpdateViewModelTeamGlow → VortexGlowColor). Players-only and team-only (the QC
        // weaponentity_glowmod tints from the team/pants color); a non-vortex weapon or zero charge clears it.
        if (playerHeld)
            UpdateChargeGlow(h, (int)e.Team, displayWeaponId, e.WepentView.VortexCharge);
    }

    /// <summary>
    /// [wepentconsumers] Apply the exterior Vortex charge glow to the carrier weapon as a per-INSTANCE emission
    /// (weapon-vortex.fx.charge_glow). Resolves the team color from the proxy colormap (low nibble — the same
    /// source <see cref="ModelTint.ApplyAppearance"/> tints the player body with) and, while the held weapon is a
    /// Vortex/Nex with charge, drives a duplicated <see cref="StandardMaterial3D"/> override's emission off the
    /// shared <see cref="NetGame"/> charge ramp. The v_*.md3 world models are cached + shared by the asset
    /// pipeline, so the glow MUST NOT mutate their material (every player's gun would light up — the same lesson
    /// <see cref="ApplyTransparency"/> documents); we cache one duplicated material per <see cref="Held"/> and edit
    /// only its emission. <see cref="Held.LastGlow"/> gates the re-apply (mirrors the alpha gate), and a
    /// zero-charge / non-vortex / teamless gun disables the emission.
    /// </summary>
    private static void UpdateChargeGlow(Held h, int colormap, int displayWeaponId, float vortexCharge)
    {
        if (h.Model is null || !GodotObject.IsInstanceValid(h.Model))
            return;

        // Team color drives the glow (DP weaponentity_glowmod tints from the pants/team color); FFA / no team
        // leaves the carrier with no charge glow, matching the first-person fall-through.
        Color teamColor = ModelTint.TeamColor(colormap, out bool hasTeam);

        // Only a Vortex-family weapon (vortex / vaporizer / *nex*) with live charge glows; everything else clears.
        bool isVortexWeapon = false;
        if (displayWeaponId >= 0 && displayWeaponId < VortexArena.Common.Gameplay.Weapons.Count)
        {
            VortexArena.Common.Gameplay.Weapon w = VortexArena.Common.Gameplay.Weapons.ById(displayWeaponId);
            string net = w?.NetName ?? "";
            isVortexWeapon = net is "vortex" or "vaporizer" || net.Contains("nex");
        }

        bool glowing = hasTeam && isVortexWeapon && vortexCharge > 0f;
        Color glow = glowing
            ? VortexGlowColor(teamColor, Mathf.Max(0.25f, vortexCharge))
            : Black;

        if (glow == h.LastGlow)
            return;
        h.LastGlow = glow;

        // Lazily build (and cache) the per-instance duplicated material override the first time we need it; reuse
        // it thereafter so we never re-duplicate or touch the shared model material.
        StandardMaterial3D mat = h.GlowMaterial ??= BuildGlowMaterial(h.Model);
        if (glowing)
        {
            mat.EmissionEnabled = true;
            mat.Emission = glow;
            mat.EmissionEnergyMultiplier = 1f;
        }
        else
        {
            mat.EmissionEnabled = false;
        }
    }

    /// <summary>Duplicate the carrier mesh's material into a per-instance <see cref="StandardMaterial3D"/> override
    /// (so the charge glow can't leak onto the shared cached v_*.md3), apply it to every mesh under the held
    /// weapon, and return it for the <see cref="Held"/> to drive. Falls back to a fresh material when the cached
    /// model has none.</summary>
    private static StandardMaterial3D BuildGlowMaterial(Node3D model)
    {
        // Seed the override from the first mesh's active material so the gun keeps its real albedo/texture while we
        // layer the emission on top; a plain StandardMaterial3D is the safe fallback when none is resolvable.
        StandardMaterial3D mat = ResolveSeedMaterial(model)?.Duplicate() as StandardMaterial3D
            ?? new StandardMaterial3D();
        ApplyMaterialOverride(model, mat);
        return mat;
    }

    /// <summary>Find a surface material to seed the per-instance glow override from (the active override, then the
    /// mesh's surface-0 material) on the first mesh under <paramref name="node"/>.</summary>
    private static StandardMaterial3D? ResolveSeedMaterial(Node node)
    {
        if (node is MeshInstance3D mi && GodotObject.IsInstanceValid(mi))
        {
            if (mi.MaterialOverride is StandardMaterial3D ov)
                return ov;
            if (mi.GetSurfaceOverrideMaterialCount() > 0 && mi.GetActiveMaterial(0) is StandardMaterial3D active)
                return active;
        }
        foreach (Node child in node.GetChildren())
        {
            StandardMaterial3D? found = ResolveSeedMaterial(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>Set <paramref name="mat"/> as the per-instance <see cref="GeometryInstance3D.MaterialOverride"/> on
    /// every mesh under the held weapon, so the charge emission affects only this carrier (never the shared
    /// cached model material).</summary>
    private static void ApplyMaterialOverride(Node node, Material mat)
    {
        if (node is MeshInstance3D mi && GodotObject.IsInstanceValid(mi))
            mi.MaterialOverride = mat;
        foreach (Node child in node.GetChildren())
            ApplyMaterialOverride(child, mat);
    }

    /// <summary>Port of <c>vortex_glowcolor</c> (weapon/vortex.qc:7), duplicating <c>NetGame.VortexGlowColor</c>
    /// EXACTLY: a charge-scaled blend of the team color ramping 0→0.3× up to the anim limit (0.5) then 0.3→1.0×
    /// above it. <paramref name="charge"/> is the [0,1] charge fraction.</summary>
    private static Color VortexGlowColor(Color teamColor, float charge)
    {
        const float animlimit = 0.5f; // g_balance_vortex_charge_animlimit default
        float f = Mathf.Min(1f, charge / animlimit);
        Color g = teamColor * (f * 0.3f);
        if (charge > animlimit)
        {
            f = (charge - animlimit) / (1f - animlimit);
            g += teamColor * (f * 0.7f);
        }
        return g;
    }

    /// <summary>Opaque-black sentinel for "no charge glow" (emission disabled).</summary>
    private static readonly Color Black = new(0f, 0f, 0f);

    /// <summary>
    /// Resolve the exterior weapon entity's render alpha from its owner's networked alpha, porting the QC
    /// three-way rule in <c>CL_ExteriorWeaponentity_Think</c> (weaponsystem.qc:170-175):
    /// owner at <c>default_player_alpha</c> ⇒ <c>default_weapon_alpha</c>; owner at a custom non-zero fade ⇒
    /// match the owner; owner alpha 0 ⇒ opaque. <paramref name="ownerAlpha"/> is the decoded owner alpha
    /// (-1 = the QC "hidden default" sentinel, i.e. owner == default_player_alpha under Running Guns).
    /// <see cref="MutatorHooks.DefaultPlayerAlpha"/>/<see cref="MutatorHooks.DefaultWeaponAlpha"/> carry the
    /// worldspawn seed on a listen server; on a pure remote client they default to 1 (opaque), which is the
    /// correct Running-Guns weapon default — so the gun stays visible either way.
    /// </summary>
    private static float ResolveExteriorWeaponAlpha(float ownerAlpha)
    {
        float defaultPlayerAlpha = MutatorHooks.DefaultPlayerAlpha;
        float defaultWeaponAlpha = MutatorHooks.DefaultWeaponAlpha;
        // Branch 1 — owner is at the spawn default (e.g. Running Guns -1 hidden, or Cloaked 0.25): the gun
        // takes the weapon default. The decoded -1 sentinel is treated as the hidden default_player_alpha.
        if (ownerAlpha == defaultPlayerAlpha || (ownerAlpha < 0f && defaultPlayerAlpha < 0f))
            return defaultWeaponAlpha;
        // Branch 2 — owner has a custom non-zero alpha (an Invisibility-powerup fade): the gun matches it.
        if (ownerAlpha != 0f)
            return ownerAlpha;
        // Branch 3 — owner alpha 0: opaque.
        return 1f;
    }

    /// <summary>Render the owner's QC <c>.alpha</c> as a per-instance transparency on every mesh under the held
    /// weapon (0 = opaque .. 1 = invisible), matching <c>exteriorweaponentity.alpha</c>.</summary>
    private static void ApplyTransparency(Node node, float transparency)
    {
        if (node is GeometryInstance3D gi && GodotObject.IsInstanceValid(gi))
            gi.Transparency = transparency;
        foreach (Node child in node.GetChildren())
            ApplyTransparency(child, transparency);
    }

    /// <summary>Drop the weapon view-entity for a departed/removed entity id.</summary>
    public void Remove(int id)
    {
        if (_held.Remove(id, out Held? h))
        {
            h.GlowMaterial?.Dispose();
            h.GlowMaterial = null;
            if (GodotObject.IsInstanceValid(h.Holder))
            {
                DisposeOwnedMeshes(h.Holder);
                h.Holder.QueueFree();
            }
        }
    }

    /// <summary>
    /// [W14a QW5] Per-frame hook: advance each held weapon's switch raise/lower dip toward the target set by its
    /// networked <see cref="Held.SwitchPhase"/> and re-place the model. Phase 2 (WS_DROP) lowers the gun out of
    /// the hand; phase 1 (WS_RAISE) and phase 0 (ready) raise it back into place. Uses the engine frame delta so
    /// the tween is smooth regardless of snapshot cadence (the holder pose itself is refreshed in Update).
    /// </summary>
    public void Process()
    {
        float dt = (float)(GodotObject.IsInstanceValid(_render) ? _render.GetProcessDeltaTime() : 0.0);
        foreach (Held h in _held.Values)
            AdvanceSwitch(h, dt);
    }

    /// <summary>
    /// Move a held weapon's dip toward its phase target (down on WS_DROP, up otherwise) and apply it as a Y-offset
    /// below the hand. The drop and raise use distinct rates (Base switchdelay_drop / switchdelay_raise).
    /// </summary>
    private void AdvanceSwitch(Held h, float dt)
    {
        float target = h.SwitchPhase == 2 ? 1f : 0f;            // WS_DROP lowers; ready/raise settle to rest
        float rate = h.SwitchPhase == 2 ? SwitchDropTime : SwitchRaiseTime;
        h.SwitchOffset = Mathf.MoveToward(h.SwitchOffset, target, dt / Mathf.Max(rate, 1e-3f));

        if (h.Model is null || !GodotObject.IsInstanceValid(h.Model))
            return;
        // The model rests at RestOffset (#34: Zero when parented to the hand marker — the bone pose IS the gun
        // pose — else the legacy HandOffset fallback); the dip lowers it (Godot −Y) and tucks it slightly back
        // toward the body (−Z) so it reads as stowed rather than sinking through the hand. A zero dip
        // (SwitchOffset 0) leaves the gun exactly at rest.
        float dip = h.SwitchOffset * SwitchLowerDistance;
        h.Model.Position = h.RestOffset + new Vector3(0f, -dip, -dip * 0.25f);
    }

    /// <summary>Free every weapon view-entity (client teardown).</summary>
    public void Clear()
    {
        foreach (Held h in _held.Values)
        {
            h.GlowMaterial?.Dispose();
            h.GlowMaterial = null;
            if (GodotObject.IsInstanceValid(h.Holder))
            {
                DisposeOwnedMeshes(h.Holder);
                h.Holder.QueueFree();
            }
        }
        _held.Clear();
    }

    // [crash fix 2026-07-26] The held-weapon world models are fresh per-build Md3Builder trees (the
    // comment further up claiming CACHED + SHARED is true of the MATERIALS only — the ArrayMesh is
    // per-build). Dispose those meshes on the MAIN thread before the QueueFree, instead of leaving
    // them to the .NET finalizer thread whose RenderingServer::free races the render loop (the
    // 0xC0000374 family). Materials are the shared AssetSystem cache and are never touched.
    private static void DisposeOwnedMeshes(Node n)
    {
        if (n is MeshInstance3D mi && mi.Mesh is Mesh m)
        {
            mi.Mesh = null;
            m.Dispose();
        }
        foreach (Node c in n.GetChildren())
            DisposeOwnedMeshes(c);
    }

    private static void FreeChildren(Node n)
    {
        if (!GodotObject.IsInstanceValid(n)) return;
        foreach (Node c in n.GetChildren())
        {
            DisposeOwnedMeshes(c);
            c.QueueFree();
        }
    }

    private static Node3D Placeholder()
    {
        // A thin "gun" box so the attachment point is visible without the real weapon asset.
        var box = new MeshInstance3D
        {
            Name = "WeaponPlaceholder",
            Mesh = new BoxMesh { Size = new Vector3(6f, 6f, 28f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.25f, 0.3f) },
        };
        return box;
    }
}
