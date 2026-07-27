using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Game.Client;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The networked entity → renderer bridge — the Godot successor to CSQC's <c>CSQC_Ent_Update</c> /
/// <c>CSQCModel_Hook_PreUpdate</c> dispatch loop (lib/csqcmodel). <see cref="ClientNet"/> decodes the
/// delta-compressed snapshot into a per-entity <see cref="NetEntityState"/> table and interpolates each
/// remote entity's pose; this node, once per frame, reads that table and drives the actual render
/// representation in <see cref="ClientWorld"/>:
///
/// <list type="bullet">
///   <item><b>Player / Generic / Gib</b> → an <c>EntityNode</c> + <see cref="ModelAnimator"/> whose animation
///         is <b>frame-driven</b> by the networked <see cref="NetEntityState.Frame"/> (CSQCMODEL_AUTOUPDATE),
///         which is the gap this closes — remote players/monsters now play their server-computed frame instead
///         of the local movement heuristic.</item>
///   <item><b>Item</b> → a static/bobbing model node (no frame playback).</item>
///   <item><b>Projectile</b> → the <see cref="ProjectileRenderer"/> (trail/scale/spin from the catalog).</item>
///   <item><b>ViewModel</b> → the held-weapon entity, attached to its owner (see <see cref="ViewEntityRenderer"/>).</item>
///   <item><b>Nameplate</b> → a lightweight pose-only entity (radar/nameplate; no world model).</item>
/// </list>
///
/// It keeps one reusable proxy <see cref="Entity"/> per net id (the sim is Godot-free, so the renderer wants
/// an <see cref="Entity"/> to bind to). The proxy is mutated each frame from the decoded state + the
/// interpolated pose, then handed to the renderer — exactly how the QC client filled a CSQCModel entity from
/// the read functions and let the draw path consume it.
///
/// Model identity: the wire carries a numeric <see cref="NetEntityState.ModelIndex"/> (a precache slot), not a
/// classname. <see cref="ResolveModelIndex"/> (host-provided, backed by the precache table) maps it to a
/// classname/model string so projectiles classify correctly and <c>ClientWorld.ModelResolver</c> can load the
/// mesh; without it, entities fall back to kind-derived names (still renders, just less specific).
/// </summary>
public sealed partial class ClientEntityView : Node
{
    private readonly ClientNet _net;
    private readonly ClientWorld _render;
    private readonly ViewEntityRenderer _viewEntities;

    // One reusable proxy entity per net id (mutated in place each frame, like a CSQCModel slot).
    private readonly Dictionary<int, Entity> _proxies = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _stale = new();

    /// <summary>
    /// Resolve a networked model index (precache slot) to a classname + model name, so the renderer can pick
    /// the right projectile type / load the right mesh. Host-provided (precache table). When null, entities
    /// use a generic kind-derived classname.
    /// </summary>
    public Func<int, (string ClassName, string Model)>? ResolveModelIndex { get; set; }

    /// <summary>
    /// Builds the world model for a third-person carried weapon (a weapon registry id → a fresh scene node),
    /// host-wired to the asset pipeline. Forwarded to the <see cref="ViewEntityRenderer"/>; when null the
    /// renderer falls back to a placeholder box.
    /// </summary>
    public Func<int, Node3D?>? WeaponModelFactory
    {
        get => _viewEntities.WeaponModelFactory;
        set => _viewEntities.WeaponModelFactory = value;
    }

    public ClientEntityView(ClientNet net, ClientWorld render)
    {
        _net = net ?? throw new ArgumentNullException(nameof(net));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _viewEntities = new ViewEntityRenderer(render);
    }

    public override void _Process(double delta)
    {
        if (!_net.Accepted)
            return;

        using var _prof = FrameProfiler.Scope("cev.process");

        // Interpolate remote entities at the latest server time (a one-snapshot delay falls out of the lerp).
        float now = _net.LatestServerTime;
        _seen.Clear();

        // (perf-investigation) the remote-entity count drives this whole loop's cost; surface it as a marker so
        // the profiler shows how many entities catharsis carries at the spawn (estimates diverged 20x).
        XonoticGodot.Common.Diagnostics.Prof.Mark("remote.ents", _net.RemoteIds.Count);

        foreach (int id in _net.RemoteIds)
        {
            if (id == _net.LocalNetId)
                continue;
            if (!_net.TryGetRemoteState(id, out NetEntityState s))
                continue;

            // Interpolated pose (origin + blended angles); fall back to the raw state if no interp yet.
            if (!_net.SampleRemote(id, now, out NVec3 origin, out NVec3 angles))
            {
                origin = s.Origin;
                angles = s.Angles;
            }

            _seen.Add(id);
            DriveEntity(id, s, origin, angles);
        }

        CullDeparted();
        _viewEntities.Process();
    }

    // =====================================================================================
    //  Per-entity drive
    // =====================================================================================

    private void DriveEntity(int id, in NetEntityState s, NVec3 origin, NVec3 angles)
    {
        Entity e = GetOrCreateProxy(id);

        // Fill the proxy from the decoded state + interpolated pose (the CSQCModel property copy).
        e.Origin = origin;
        e.OldOrigin = origin;
        // [#50] Player BODIES are yaw-only in Base: entcs networks angles_y alone (ent_cs.qc:144-146 forces
        // X/Z to 0) and CSQCPlayer only ever writes e.angles_y (cl_player.qc:659), so looking up/down NEVER
        // pitches the model — aim pitch belongs to the animdecide upper-body layer, not the entity basis.
        // The port networks the player's full view angles (the aim), which routed players onto EntityNode's
        // pitched full-basis path and tilted the whole body. Zero pitch/roll for player kinds here — the one
        // choke point both the pure client AND the listen host render players through. The pitch isn't lost: it
        // rides the separate render-only ViewPitch field, which PlayerModel's aim-bone layer reads (#7), so the
        // torso/arms still aim up/down while the legs stay upright — the body yaw-only, the aim layer pitched.
        if (s.Kind == NetEntityKind.Player)
        {
            e.Angles = new NVec3(0f, angles.Y, 0f);
            e.ViewPitch = angles.X;
        }
        else
        {
            e.Angles = angles;
            e.ViewPitch = 0f;
        }
        e.Velocity = s.Velocity;
        e.Frame = s.Frame;
        e.Skin = s.Skin;
        e.ModelIndex = s.ModelIndex;
        e.Effects = s.Effects;
        // [W5-cloaked] Decode the networked render alpha (QC csqcmodel m_alpha) onto the proxy so PlayerModel/
        // EntityNode renders the Cloaked / Invisibility / fade transparency. ServerNet.QuantizeAlpha sends 0 for
        // a fully-opaque entity (costs nothing on the wire), 1..254 for a real fade (= byte/255), and 255 for the
        // QC -1 "do not render" sentinel (Running Guns hides the player model). Mirror that mapping back to the
        // float Entity.Alpha (1 = opaque; -1 = hidden, which ApplyAlpha clamps to fully transparent).
        e.Alpha = s.Alpha switch { 0 => 1f, 255 => -1f, _ => s.Alpha / 255f };
        e.Health = s.Health;
        e.Team = s.Colormap;
        e.Colors = s.Colors; // [r15 #43] packed clientcolors → shirt/pants/glow on the body + held weapon
        // RENDER_COLORMAPPED entities (dropped weapons; colormapped props/monsters): the server networks its
        // authoritative Entity.ColorMapOverride verbatim (own delta field), so ClientWorld's item tint pass
        // paints the DROPPER's shirt/pants on the loot model. This is the ONLY path that fills it for
        // rendering — the listen host renders from these same decoded proxies (ClientWorld.OnEntityUpdate has
        // no caller outside this file), so there is no shared-edict shortcut to fall back on.
        e.ColorMapOverride = s.ColorMapOverride;
        // A weapon PICKUP's explicit identity (non-player Weapon field = RegistryId + 1, 0 = not a weapon
        // pickup): the render layer prefers this over matching the item model filename.
        e.ItemWeaponId = s.Kind != NetEntityKind.Player && s.Weapon > 0 ? s.Weapon - 1 : -1;
        // Players only: the raw held-weapon registry id. On non-player entities the wire Weapon field carries
        // the +1-BIASED pickup id (decoded into ItemWeaponId below) — letting it leak into ActiveWeaponId
        // would hand every raw-id consumer (Weapons.ById callers) an off-by-one weapon on item proxies.
        e.ActiveWeaponId = s.Kind == NetEntityKind.Player ? s.Weapon : -1;
        // [W14a-anim] decode the upper-body action overlay (QC csqcmodel animdecide getupperanim) onto the proxy so a
        // future torso-overlay render (LI3) plays the server-decided SHOOT/PAIN/DRAW/TAUNT/DEAD action over the
        // velocity-derived legs. RESERVED — no server producer yet, so these are 0/idle until LI1 lands.
        e.UpperAction = s.UpperAction;
        e.AnimActionTime = s.AnimActionTime;
        // [W14a-wepent] decode the exterior-weapon block (QC common/wepent.qh) onto the proxy: the remote third-person
        // held weapon's switch target / in-transition weapon / raise-drop phase / skin / align, plus the gun's own
        // alpha (mapped exactly like the body Alpha: 0 = opaque, 255 = hidden -1, else byte/255). Drives the remote
        // weapon switch raise/lower tween + exterior-weapon transparency (QW5; ViewEntityRenderer reads them).
        e.SwitchWeapon = s.SwitchWeapon;
        e.SwitchingWeapon = s.SwitchingWeapon;
        e.WepPhase = s.WepPhase;
        e.ViewmodelSkin = s.ViewmodelSkin;
        e.GunAlign = s.GunAlign;
        e.WepAlpha = s.WepAlpha switch { 0 => 1f, 255 => -1f, _ => s.WepAlpha / 255f };
        // [W-wepent-view] decode the per-player wepent HUD view-state (charge/clip/heat/beam) onto the proxy so a
        // spectator following this player and any third-person beam consumer can read the watched player's live
        // weapon state — the all-clients counterpart of the owner-block rings.
        e.WepentView = s.WepentView;
        // [W-vehicleview] decode the spectator-follow vehicle view-state (health/shield/energy/ammo/reload bars +
        // veh kind + weapon-2 mode + lock strength/flags) onto the proxy so a spectator FOLLOWING this player can
        // read the watched pilot's live vehicle HUD — the all-clients counterpart of the owner block. The LOCAL
        // pilot does not read this; it reads its own decoded slice off ClientNet.LocalState (captured wholesale in
        // ClientNet.HandleSnapshot). VehicleViewState.None (VehKind 0 = on-foot/observing) keeps the bit clear.
        e.VehicleView = s.VehicleView;
        // [W-nadeclient] decode the owner-only nade feedback fields (QC STAT(NADE_DARKNESS_TIME / NADE_BONUS /
        // NADE_BONUS_TYPE / NADE_BONUS_SCORE)) onto the proxy. These are 0 on every non-owner entity (the Feedback
        // bit stays clear for remotes), so they only carry data on the local player's own delta — NetGame reads them
        // off the local proxy to drive the darkness overlay + the HUD bonus-nade count/score ring.
        e.NadeDarknessTime = s.NadeDarknessTime;
        e.NadeBonus = s.NadeBonus;
        e.NadeBonusType = s.NadeBonusType;
        e.NadeBonusScore = s.NadeBonusScore;
        // [W-objstream] decode the turret-head + objective view-state onto the per-id proxy each frame (decode only;
        // no render work here). These feed the Phase-3 turret-head node (HeadWorldAngles = Entity.Angles +
        // (TurHeadPitch,TurHeadYaw,0); integrate TurHeadAVelYaw*dt for the idle head spin) and the objective HUD
        // healthbar/build-bar. ObjHealthFrac defaults to full (1f) unless the entity is an active/damaged objective.
        e.TurHeadPitch = s.TurHeadPitch;
        e.TurHeadYaw = s.TurHeadYaw;
        e.TurHeadAVelYaw = s.TurHeadAVelYaw;
        e.TurActive = (s.TurFlags & 1) != 0;
        e.ObjState = s.ObjState;
        e.ObjHealthFrac = (s.ObjState != 0 || s.ObjHealthByte != 0) ? s.ObjHealthByte / 255f : 1f;
        // QC ITS_EXPIRING (item snapshot flag): drives ClientWorld's loot despawn animation. Refreshed every
        // frame so it tracks the networked status (only ever set by the server on an expiring loot item).
        e.ItemExpiringFx = (s.Flags & NetEntityFlags.ItemExpiring) != 0;
        // QC !ITS_AVAILABLE (item snapshot flag): a picked-up item awaiting respawn → ClientWorld renders it as
        // the cl_ghost_items fade. Only ever set by the server on an unavailable item, so non-items stay available.
        e.ItemAvailable = (s.Flags & NetEntityFlags.ItemGhost) == 0;
        // QC ITS_ANIMATE1/2 (item bob+spin class): refreshed each frame from the snapshot. ANIMATE1 takes
        // priority (matches the client's if/else if). EntityNode reads this to drive the float + rotation.
        e.ItemAnimate = (s.Flags & NetEntityFlags.ItemAnimate1) != 0 ? (byte)1
                      : (s.Flags & NetEntityFlags.ItemAnimate2) != 0 ? (byte)2 : (byte)0;
        // QC FL_DUCKED: a remote player's crouch drives LocomotionBlend (duck legs) + the lowered hull/nameplate.
        e.IsDucked = (s.Flags & NetEntityFlags.Crouched) != 0;
        // QC IT_USING_JETPACK (common/physics/player.qc:878): a firing jetpack → the client derives
        // csqcmodel_modelflags |= MF_ROCKET, which drives the looping jetpack-fly sound + rocket trail. Carried on
        // the proxy so ClientWorld's effects pass can compose the per-player MF_ROCKET forced appearance.
        e.UsingJetpack = (s.Flags & NetEntityFlags.UsingJetpack) != 0;
        // QC FL_ONGROUND: the skeletal PlayerModel's LocomotionBlend.SelectLegs picks the JUMP clip whenever the
        // player is airborne — so without copying the networked on-ground flag onto the proxy (Entity.OnGround is
        // derived from EntFlags.OnGround), every remote player reads as in-air and is frozen in the jump pose
        // (the "bot doesn't animate" bug). Mirror it here so run/walk/idle/jump are selected correctly.
        if ((s.Flags & NetEntityFlags.OnGround) != 0) e.Flags |= EntFlags.OnGround;
        else e.Flags &= ~EntFlags.OnGround;
        // QC csqcmodel_isdead: a corpse plays the death animation (LocomotionBlend.Dead). PlayerModel.Pose also
        // checks Health, but the proxy carries no MaxHealth, so drive the dead state straight off the networked
        // flag so a killed remote player poses dead instead of idling.
        e.DeadState = (s.Flags & NetEntityFlags.Dead) != 0 ? DeadFlag.Dead : DeadFlag.No;
        // QC ENT_CLIENT_STATUSEFFECTS read: decode the networked status-effect bitmap onto the proxy so a remote
        // entity carries the same frozen/burning/buff set the server has — the data ClientWorld's overlay pass
        // (and any consumer of Entity.StatusEffects) reads to draw the burning particles / frozen tint.
        ApplyStatusEffects(e, s.StatusEffects);
        ApplyIdentity(e, s);

        switch (s.Kind)
        {
            case NetEntityKind.Projectile:
                // Client-side prediction (CSQC client-animated projectiles do NOT two-snapshot-interpolate):
                // hand the ProjectileRenderer the RAW authoritative origin, not the lerp-delayed interpolated
                // pose, so its predictor snaps to server truth and extrapolates locally by velocity. Using the
                // interpolated (one-snapshot-stale) origin would reintroduce the very lag the predictor removes.
                e.Origin = s.Origin;
                e.OldOrigin = s.Origin;
                // ClientWorld.IsProjectile routes this to the ProjectileRenderer (catalog-classified).
                _render.OnEntityUpdate(e);
                break;

            case NetEntityKind.ViewModel:
                // The local player's own held weapon is the first-person ViewModel, not a world entity — only
                // render OTHER players' carried weapons (third-person attachment).
                if (s.Owner != _net.LocalNetId)
                    _viewEntities.Update(e, s);
                break;

            case NetEntityKind.Nameplate:
                // Pose-only (radar/nameplate). No world model; the HUD/radar reads the state separately.
                break;

            case NetEntityKind.Player:
                // Animated networked actor + its held-weapon view-entity (wepent) attached third-person.
                _render.OnEntityUpdate(e, frameDriven: true);
                _viewEntities.Update(e, s);
                break;

            case NetEntityKind.Generic:
            case NetEntityKind.Gib:
                // Animated networked actors: drive the model frame straight from the network (autoupdate).
                _render.OnEntityUpdate(e, frameDriven: true);
                break;

            case NetEntityKind.NadeOrb:
                // [W-nadeclient] heal/ammo/entrap/veil/darkness orb effect entity. Map the networked orb block onto
                // the proxy and route it to the dedicated NadeOrbRenderer (wired through ClientWorld like the
                // ProjectileRenderer). The orb is otherwise static — its Origin rides the normal Origin field, so use
                // the RAW server origin (no two-snapshot interp) the same way projectiles do.
                e.NadeBonusType = s.OrbType;
                e.NadeOrbExpire = s.OrbExpire;
                e.OrbRadiusClient = s.OrbRadius;
                e.Origin = s.Origin;
                // NadeOrbs is wired by NetGame.SetupCameraAndHud, which runs right after SetupRender (where this
                // view is built); a stray orb delta before that wiring is a no-op rather than a null deref.
                _render.NadeOrbs?.OnSpawn(e);
                _render.NadeOrbs?.OnUpdate(e);
                break;

            case NetEntityKind.Item:
            default:
                // Static/bobbing pickups: render the model, no frame playback.
                _render.OnEntityUpdate(e, frameDriven: false);
                break;
        }
    }

    /// <summary>Set the proxy's classname/model from the precache resolver, or a kind-derived default.</summary>
    private void ApplyIdentity(Entity e, in NetEntityState s)
    {
        // Prefer the networked model NAME (QC .model): players/bots carry their playermodel path so the client
        // loads the skeletal IQM directly, without a shared precache-index table (which wouldn't survive a real
        // client/server split). ModelIndex stays available as the fallback below.
        if (!string.IsNullOrEmpty(s.Model))
            e.Model = s.Model;

        if (ResolveModelIndex is not null && s.ModelIndex > 0)
        {
            (string className, string model) = ResolveModelIndex(s.ModelIndex);
            if (!string.IsNullOrEmpty(className)) e.ClassName = className;
            if (!string.IsNullOrEmpty(model)) e.Model = model;
            if (!string.IsNullOrEmpty(e.ClassName)) return;
        }

        // Kind-derived fallback (enough for ClientWorld.IsProjectile routing + a placeholder model).
        e.ClassName = s.Kind switch
        {
            NetEntityKind.Projectile => "projectile",
            NetEntityKind.Player => "player",
            NetEntityKind.Item => "item",
            NetEntityKind.Gib => "gib",
            NetEntityKind.ViewModel => "weaponentity",
            NetEntityKind.Nameplate => "nameplate",
            _ => e.ClassName,
        };
    }

    /// <summary>
    /// Decode the networked status-effect bitmap (QC <c>ENT_CLIENT_STATUSEFFECTS</c>) onto a proxy entity: rebuild
    /// its <see cref="Entity.StatusEffects"/> list from the full snapshot the server packed. The blob is the full
    /// bitmap each time it's networked (delta-resent only on change), so a clear-and-refill is correct — a null or
    /// empty blob (the "no effects" / "effects cleared" case) leaves the list empty. The per-effect
    /// <c>Strength</c> isn't networked (the QC wire carries only time + flags), so it's defaulted; the overlays
    /// only need the effect's PRESENCE + timer, which round-trip exactly.
    /// </summary>
    internal static void ApplyStatusEffects(Entity e, byte[]? blob)
    {
        // The blob is delta-resent only on change, so its reference is stable while unchanged — skip the re-decode
        // (StatusEffectsCatalog.Read allocates a dictionary) when it's the same blob we last applied. This runs
        // every render frame per remote entity AND for the pure-client HUD mirror, so the skip is real gen0 relief.
        if (ReferenceEquals(blob, e.LastStatusBlob))
            return;
        e.LastStatusBlob = blob;

        List<ActiveStatusEffect> list = e.StatusEffects;
        if (list.Count == 0 && (blob is null || blob.Length == 0))
            return; // common case: no effects either side — nothing to do
        list.Clear();
        if (blob is null || blob.Length == 0)
            return;
        foreach (KeyValuePair<int, (float Time, StatusEffectFlags Flags)> kv in StatusEffectsCatalog.Read(blob))
            list.Add(new ActiveStatusEffect
            {
                DefId = kv.Key,
                ExpireTime = kv.Value.Time,
                Flags = kv.Value.Flags,
                Strength = 1f,
            });
    }

    private Entity GetOrCreateProxy(int id)
    {
        if (!_proxies.TryGetValue(id, out Entity? e))
        {
            e = new Entity { Index = id };
            _proxies[id] = e;
        }
        return e;
    }

    // [crash fix 2026-07-26] PVS-flicker grace: sv_cullentities_pvs removes an entity from the snapshot
    // the moment it leaves the client's PVS and re-adds it on return. Tearing the render node down on
    // FIRST absence rebuilt the same ModelAnimators over and over (measured: 107 full mesh+material
    // builds in 100 s of implosion fly-around), flooding the finalizer thread with RenderingServer
    // frees that race the main thread's surface updates — the 0xC0000374 heap corruption. Instead:
    // hide the node on first absence and only tear down after a sustained absence. Projectiles are
    // exempt (their removal carries impact semantics and must stay immediate).
    private const ulong CullGraceMs = 750;
    private readonly Dictionary<int, ulong> _unseenSince = new();
    private readonly List<int> _returned = new();

    /// <summary>Remove proxies (and their render nodes) for ids the server stopped sending this frame.</summary>
    private void CullDeparted()
    {
        ulong now = Time.GetTicksMsec();

        // Ids that came BACK inside the grace window: unhide, forget the absence.
        _returned.Clear();
        foreach (KeyValuePair<int, ulong> kv in _unseenSince)
            if (_seen.Contains(kv.Key))
                _returned.Add(kv.Key);
        for (int i = 0; i < _returned.Count; i++)
        {
            _unseenSince.Remove(_returned[i]);
            _render.SetEntityHidden(_returned[i], false);
        }

        _stale.Clear();
        foreach (KeyValuePair<int, Entity> kv in _proxies)
            if (!_seen.Contains(kv.Key))
                _stale.Add(kv.Key);

        for (int i = 0; i < _stale.Count; i++)
        {
            int id = _stale[i];
            Entity e = _proxies[id];
            // Grace path (non-projectiles): first absence hides; teardown only after CullGraceMs.
            if (!_render.Projectiles.IsTracking(id))
            {
                if (!_unseenSince.TryGetValue(id, out ulong since))
                {
                    _unseenSince[id] = now;
                    _render.SetEntityHidden(id, true);
                    continue;
                }
                if (now - since < CullGraceMs)
                    continue;
            }
            _unseenSince.Remove(id);
            _viewEntities.Remove(id);
            // [W-nadeclient] tear down the orb render state for a departed id. The NadeOrbRenderer only tracks
            // nade_orb ids, so this is a harmless no-op for any non-orb entity — same idempotent contract as
            // ProjectileRenderer's removal path.
            _render.NadeOrbs?.OnRemove(id);
            _render.OnEntityRemove(id, e.Origin);
            _proxies.Remove(id);
        }
    }

    public override void _ExitTree() => _viewEntities.Clear();
}
