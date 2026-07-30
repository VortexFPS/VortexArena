using System.Numerics;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// Per-weapon-slot weapon state — the C# successor to the pile of <c>.float</c>/<c>.entity</c> fields
/// QuakeC hangs off each <c>actor.(weaponentity)</c> (the weapon-entity component, common/wepent.qh).
/// In QC every weapon stores its scratch state (Vortex charge, Hagar load, Mortar bounce counters,
/// Crylink link group, Arc beam heat, Devastator rocket-release latch, …) on the weapon entity that
/// occupies the slot. Here those live in <see cref="WeaponSlotState"/>, looked up per (actor, slot) via
/// <see cref="Entity.WeaponState"/>.
///
/// This is a NEW file (declares <c>partial class Entity</c>); per the task constraints it only extends
/// the entity rather than editing an existing file. Field names are weapon-prefixed to mirror the QC
/// originals so the port reads 1:1 against the .qc sources.
/// </summary>
public partial class Entity
{
    /// <summary>
    /// QC <c>.porto_current</c> — the player's in-flight portal projectile (only one allowed at a time).
    /// </summary>
    public Entity? PortoCurrent;

    /// <summary>QC <c>.porto_forbidden</c> — a short cooldown (ticks) blocking new portal shots.</summary>
    public int PortoForbidden;

    /// <summary>
    /// QC <c>.cnt</c>-tracked previous weapon for W_LastWeapon (server/weapons/selection.qc). The
    /// weapon-switch system records the outgoing weapon's RegistryId here so the Blaster secondary (and any
    /// "last weapon" bind) can switch back to it. -1 = none recorded.
    /// </summary>
    public int LastWeaponId = -1;

    /// <summary>
    /// QC <c>.vortex_chargepool_pauseregen_finished</c> (common/weapons/weapon/vortex.qc:172) — an
    /// ACTOR-level field (not per weapon slot, unlike <c>vortex_chargepool_ammo</c>): the server time the
    /// secondary charge pool may resume regenerating after charging paused it. [T57]
    /// </summary>
    public float VortexChargepoolPauseRegenFinished;

    /// <summary>
    /// QC <c>.bouncefactor</c> — velocity retained per bounce for MOVETYPE_BOUNCE/BOUNCEMISSILE projectiles
    /// (mortar grenades, electro orbs). 0 = engine default. Read by the movement integrator.
    /// </summary>
    public float BounceFactor;

    /// <summary>QC <c>.bouncestop</c> — speed below which a bouncing projectile comes to rest.</summary>
    public float BounceStop;

    /// <summary>
    /// QC <c>.damagedbycontents</c> (server/damage.qh:45) — when true this entity is enrolled in the
    /// <c>g_damagedbycontents</c> per-frame sweep (server/main.qc CreatureFrame_All): a projectile resting in
    /// lava/slime (or a fully-submerged drowner) takes content damage. Set on an electro orb when
    /// <c>g_balance_electro_secondary_damagedbycontents</c> is on (default 1). Default false.
    /// </summary>
    public bool DamagedByContents;

    /// <summary>
    /// QC <c>.contents_damagetime</c> (server/main.qc CreatureFrame_hotliquids) — the next sim time this entity
    /// may take liquid content damage (rate-limited by <c>g_balance_contents_damagerate</c>). 0 = ready now.
    /// </summary>
    public float ContentsDamageTime;

    /// <summary>
    /// QC <c>.event_damage</c> for shootable PROJECTILES (rockets/grenades/mines/orbs/tags/bolts). When a
    /// projectile's HP drops to 0 from being shot, it explodes via this handler (QC W_*_Damage ->
    /// W_PrepareExplosionByDamage). The weapons install this when they spawn a damageable projectile; the
    /// damage pipeline invokes it after subtracting <see cref="Health"/>. (The pipeline-side dispatch — call
    /// <c>targ.ProjectileDamage?.Invoke(this, attacker)</c> when a non-player damageable entity is hit —
    /// lives in the damage system, which is owned outside the weapons folder.)
    /// </summary>
    public Action<Entity, Entity?>? ProjectileDamage;

    // QC stored these on actor.(weaponentity); here a tiny per-slot table on the actor entity.
    // Lazily created so non-player entities (projectiles) never pay for it.
    private Dictionary<int, WeaponSlotState>? _weaponSlots;

    /// <summary>
    /// Get (creating on first use) the mutable weapon-scratch state for <paramref name="slot"/>.
    /// Mirrors reaching <c>actor.(weaponentities[slot])</c> in QC.
    /// </summary>
    public WeaponSlotState WeaponState(WeaponSlot slot)
    {
        _weaponSlots ??= new Dictionary<int, WeaponSlotState>(2);
        if (!_weaponSlots.TryGetValue(slot.Index, out var s))
        {
            s = new WeaponSlotState();
            _weaponSlots[slot.Index] = s;
        }
        return s;
    }

    /// <summary>Run <paramref name="action"/> over every populated weapon slot (QC <c>for(slot…)</c> resets).</summary>
    public void ForEachWeaponSlot(Action<WeaponSlotState> action)
    {
        if (_weaponSlots is null) return;
        foreach (var s in _weaponSlots.Values) action(s);
    }

    // NOTE: weapon-SELECTION scratch (selectweapon / weaponcomplainindex / hasweapon_complain_spam) lives on the
    // per-slot WeaponSlotState (slot 0) — see that class — because QC stores selectweapon/weaponcomplainindex on
    // the weapon entity (this.(weaponentity)). The Inventory selection API reads them via WeaponState(slot 0).
}

/// <summary>
/// The per-slot weapon-fire driver state (QC the <c>.state</c> on <c>actor.(weaponentity)</c>,
/// common/weapons/all.qh WS_*). Drives the raise/ready/fire/drop lifecycle the weapon-system tick
/// (<see cref="XonoticGodot.Common.Gameplay.WeaponFireDriver"/>) advances each frame. (Named <c>WeaponFireState</c>
/// rather than <c>WeaponState</c> to stay clear of the <see cref="Entity.WeaponState"/> accessor method.)
/// </summary>
public enum WeaponFireState
{
    /// <summary>WS_CLEAR — no weapon equipped in this slot (empty hands).</summary>
    Clear = 0,
    /// <summary>WS_RAISE — the weapon is being raised (switch-in anim playing); can't fire yet.</summary>
    Raise = 1,
    /// <summary>WS_DROP — the weapon is being lowered (switch-out anim playing).</summary>
    Drop = 2,
    /// <summary>WS_INUSE — a fire/reload action is mid-animation (busy this frame).</summary>
    InUse = 3,
    /// <summary>WS_READY — idle and ready to fire / switch.</summary>
    Ready = 4,
}

/// <summary>
/// The mutable per-slot scratch state. One instance per weapon slot on an actor. Grouped by weapon so the
/// fields map directly to the QC <c>.field</c> names (e.g. <c>vortex_charge</c> -&gt; <see cref="VortexCharge"/>).
/// </summary>
public sealed class WeaponSlotState
{
    // --- weapon-fire driver state machine (QC .state / ATTACK_FINISHED / .weapon_nextthink, weaponsystem.qc) ---
    /// <summary>QC <c>.state</c> (WS_*): the raise/ready/fire/drop lifecycle phase of this slot.</summary>
    public WeaponFireState State = WeaponFireState.Clear;

    /// <summary>
    /// QC <c>ATTACK_FINISHED(actor, weaponentity)</c> (the per-slot attack-finished server time). A fire is
    /// only allowed once <c>time &gt;= AttackFinished</c>; <see cref="WeaponFireDriver"/> advances it by the
    /// weapon's refire on every shot (the refire gate). Starts at 0 (ready immediately).
    /// </summary>
    public float AttackFinished;

    /// <summary>
    /// QC <c>.weapon_nextthink</c>: server time the scheduled <see cref="WeaponThink"/> fires (the animtime
    /// timer — e.g. the return-to-READY after a shot's anim, or the end of a raise/drop). 0 = none pending.
    /// </summary>
    public float WeaponNextThink;

    /// <summary>
    /// QC <c>.weapon_think</c>: the function the slot runs when <see cref="WeaponNextThink"/> elapses (the
    /// scheduled state transition — usually "become READY"). Null when nothing is scheduled.
    /// </summary>
    public Action<Entity, WeaponSlot>? WeaponThink;

    /// <summary>QC <c>.bulletcounter</c>: total shots fired with this slot since it was raised (burst logic).</summary>
    public int BulletCounter;

    /// <summary>
    /// QC <c>PHYS_INPUT_BUTTON_ATCK(actor)</c> for THIS slot's frame, set by <see cref="WeaponFireDriver"/>
    /// around each <c>WrThink</c> call so <c>Weapon.PrepareAttack</c> can require the primary button be held.
    /// </summary>
    public bool ButtonAttack;

    /// <summary>QC <c>PHYS_INPUT_BUTTON_ATCK2(actor)</c> for THIS slot's frame (see <see cref="ButtonAttack"/>).</summary>
    public bool ButtonAttack2;

    /// <summary>QC <c>PHYS_INPUT_BUTTON_ZOOM(actor) | PHYS_INPUT_BUTTON_ZOOMSCRIPT(actor)</c> for THIS slot's
    /// frame (see <see cref="ButtonAttack"/>). The rifle reads it inside its fire path to re-aim a scoped shot
    /// straight from the eye (rifle.qc:16-20).</summary>
    public bool ButtonZoom;

    /// <summary>QC <c>.prevdryfire</c> — last server time the dry-fire CLICK played (throttles it to ~1/s).</summary>
    public float PrevDryFire;

    /// <summary>
    /// QC <c>.m_weapon</c> / <c>.m_switchweapon</c> mirror for this slot's driver: the RegistryId the slot is
    /// currently holding (-1 = none). Set by the driver as the raise completes; the slot fires this weapon.
    /// </summary>
    public int CurrentWeaponId = -1;

    /// <summary>
    /// QC the slot's <c>.m_switchweapon</c>: the RegistryId the slot is switching TO (-1 = none/keep). When it
    /// differs from <see cref="CurrentWeaponId"/> the driver runs the drop→clear→raise switch sequence.
    /// </summary>
    public int SwitchWeaponId = -1;

    /// <summary>
    /// QC <c>.m_switchingweapon</c> (wepent.qh): the weapon currently being switched-to mid raise/drop (latched
    /// when the drop/clear phase commits). Distinct from <see cref="SwitchWeaponId"/> (the *requested* target):
    /// while WS_DROP this tracks the in-progress switch so a re-request during the drop is honored (QC the WS_DROP
    /// case sets <c>m_switchingweapon = m_switchweapon</c> every frame). -1 = none.
    /// </summary>
    public int SwitchingWeaponId = -1;

    // --- reload / clip system (QC server/weapons/weaponsystem.qc W_Reload, common/wepent.qh clip_load/clip_size) ---

    /// <summary>
    /// QC <c>.clip_load</c> (wepent.qh): the live magazine counter for the weapon currently in this slot —
    /// rounds remaining before a reload is needed. <c>-1</c> is the QC sentinel "scheduled for reload" (set while
    /// a reload think is pending, and persisted into <see cref="WeaponLoad"/> so the next access reloads). 0 for a
    /// non-reloadable weapon. Networked to the HUD in QC (this port keeps it server-side).
    /// </summary>
    public int ClipLoad;

    /// <summary>
    /// QC <c>.clip_size</c> (wepent.qh): the magazine capacity of the weapon in this slot (== the weapon's
    /// <c>reloading_ammo</c>), seeded when switching into a reloadable weapon. 0 for a non-reloadable weapon.
    /// </summary>
    public int ClipSize;

    /// <summary>
    /// QC <c>.old_clip_load</c> (weaponsystem.qh): the magazine counter captured at the *start* of a reload, so
    /// <c>W_ReloadedAndReady</c> can restore it before topping up (a reload interrupted by a weapon switch must
    /// not lose the rounds that were already in the gun).
    /// </summary>
    public int OldClipLoad;

    /// <summary>
    /// QC <c>.buff_ammo_prev_clipload</c> (buffs/buff/ammo.qc): the slot's clip load saved when the ammo buff was
    /// applied (which then force-fills the clip so reload weapons never run dry). Restored on the buff's removal.
    /// 0 = nothing saved.
    /// </summary>
    public int BuffAmmoPrevClipLoad;

    /// <summary>
    /// QC <c>.weapon_load[REGISTRY_MAX(Weapons)]</c> (weaponsystem.qh): the per-weapon persistent magazine store,
    /// indexed by weapon RegistryId, kept on the slot so each weapon's clip survives switching away and back.
    /// Seeded to <c>reloading_ammo</c> (full clip) at spawn for every reloadable weapon (QC PutPlayerInServer),
    /// copied into <see cref="ClipLoad"/> on switch-in, and set to <c>-1</c> ("needs reload") while a reload runs
    /// or when the weapon is thrown. Lazily sized to the weapon registry.
    /// </summary>
    public int[] WeaponLoad = System.Array.Empty<int>();

    /// <summary>
    /// Tracks which weapon ids have had their <see cref="WeaponLoad"/> entry seeded, so the magazine can start
    /// FULL on first equip — QC seeds <c>weapon_load[id] = reloading_ammo</c> for every reloadable weapon at
    /// PutPlayerInServer (server/client.qc:809). The port can't observe that spawn pass from the weapon folder, so
    /// the driver lazily seeds a reloadable weapon's clip to full the first time it is raised; this set prevents a
    /// re-seed (which would refill the magazine for free) on subsequent switches. Null until first use.
    /// </summary>
    public HashSet<int>? WeaponLoadSeeded;

    /// <summary>
    /// QC <c>.wframe</c> compared against <c>WFRAME_RELOAD</c> in <c>W_Reload</c>: true while this slot is mid
    /// reload animation, so a second reload request is ignored until the first completes (QC
    /// <c>if (this.wframe == WFRAME_RELOAD) return;</c>).
    /// </summary>
    public bool Reloading;

    /// <summary>QC the reload pipeline globals captured on the slot by <c>W_Reload</c>: the per-shot ammo floor
    /// (<c>reload_ammo_min</c> — below this the weapon can't even fire one shot, so don't reload, switch away).</summary>
    public float ReloadAmmoMin;

    /// <summary>QC <c>.reload_ammo_amount</c> — the clip capacity to top up to (== the weapon's reloading_ammo).</summary>
    public float ReloadAmmoAmount;

    /// <summary>QC <c>.reload_time</c> — how long the reload think takes for the weapon in this slot.</summary>
    public float ReloadTime;

    /// <summary>QC <c>actor.reload_complain</c> (weaponsystem.qc): server time until which the "not enough ammo to
    /// reload" sprint+sound is suppressed (a 1 s throttle).</summary>
    public float ReloadComplain;

    /// <summary>QC <c>.prevwarntime</c> (weaponsystem.qc): server time of the last out-of-ammo "use the other fire
    /// mode" notification, throttled to once a second.</summary>
    public float PrevWarnTime;

    /// <summary>QC <c>.cnt</c> on the weapon entity (selection.qc <c>W_SwitchWeapon_Force</c>): the id of the
    /// weapon that was equipped *before* the current switch, so <c>W_LastWeapon</c> ("switch back") works. -1 = none.</summary>
    public int PrevWeaponId = -1;

    // --- weapon-selection scratch (QC .selectweapon / .weaponcomplainindex on the weapon entity; selection.qh) ---

    /// <summary>
    /// QC <c>.selectweapon</c> (selection.qh): the last weapon id the player *selected* (the cycling cursor),
    /// which can differ from the equipped weapon while a complain/switch is mid-flight. QC uses 0 (the Null
    /// weapon) as "none selected yet" / "use m_switchweapon"; this port registers no Null weapon (id 0 is a real
    /// weapon — Arc), so "none" is <c>-1</c> here. Set by <c>W_SwitchWeapon_Force</c> / <c>W_SwitchWeapon</c>.
    /// </summary>
    public int SelectWeapon = -1;

    /// <summary>
    /// QC <c>.weaponcomplainindex</c> (selection.qh): rotates which weapon on a shared impulse button gets the
    /// "you don't have it / it's on the map" complaint, so pressing a group key repeatedly cycles the complaint
    /// through that group's weapons rather than always complaining about the first.
    /// </summary>
    public int WeaponComplainIndex;

    /// <summary>
    /// QC <c>CS(this).hasweapon_complain_spam</c> (selection.qh): server time until which weapon-ownership
    /// complaints are suppressed (a 0.2 s throttle), so a rapidly-pressed bind doesn't spam the unavailable feedback.
    /// (QC keeps this per-player; this single-active-slot port homes it on slot 0's state.)
    /// </summary>
    public float HasWeaponComplainSpam;

    // --- shared refire / counters (QC .misc_bulletcounter, .jump_interval) ---
    /// <summary>QC <c>.misc_bulletcounter</c> — generic per-burst shot counter (MachineGun/HLAC/Arc/Seeker).</summary>
    public int MiscBulletCounter;
    /// <summary>QC <c>.jump_interval</c> — manual refire gate (Vaporizer secondary laser).</summary>
    public float JumpInterval;
    /// <summary>QC <c>.jump_interval2</c> — rapid-laser refire gate (Vaporizer rocket-minsta).</summary>
    public float JumpInterval2;
    // --- Porto (porto.qc) per-slot portal state ---
    public XonoticGodot.Common.Framework.Entity? PortoCurrent;
    public int PortoForbidden;

    // --- Vortex (vortex.qc) ---
    /// <summary>QC <c>.vortex_charge</c> — current charge level [0,1].</summary>
    public float VortexCharge;
    /// <summary>QC <c>.vortex_chargepool_ammo</c> — secondary charge-pool reserve.</summary>
    public float VortexChargePoolAmmo = 1f;
    public float VortexChargeRotTime;

    // --- Machinegun (machinegun.qc) ---
    /// <summary>QC <c>.machinegun_spread_accumulation</c> — accumulated spread that decays over time.</summary>
    public float MachinegunSpreadAccumulation;
    public float SpreadUpdateTime;

    // --- HLAC (hlac.qc) uses MiscBulletCounter for spread growth ---

    // --- Hagar load (hagar.qc) ---
    /// <summary>QC <c>.hagar_load</c> — number of rockets currently loaded in the secondary.</summary>
    public int HagarLoad;
    public float HagarLoadStep;
    public bool HagarLoadBlock;
    public bool HagarLoadBeep;
    public bool HagarWarning;

    // --- Devastator (devastator.qc) ---
    /// <summary>QC <c>.rl_release</c> — primary must be re-pressed between launches (1 = released).</summary>
    public bool RlRelease = true;
    /// <summary>QC <c>actor.(weaponentity).lastrocket</c> — the rocket currently being guided.</summary>
    public Entity? LastRocket;

    // --- Crylink link-join (crylink.qc) ---
    /// <summary>QC <c>.crylink_lastgroup</c> — head of the last-fired spike queue.</summary>
    public Entity? CrylinkLastGroup;
    /// <summary>QC <c>.crylink_waitrelease</c> — 0 none, 1 primary held, 2 secondary held.</summary>
    public int CrylinkWaitRelease;

    // --- Electro (electro.qc) ---
    /// <summary>QC <c>.electro_count</c> — remaining orbs to fire in the secondary burst.</summary>
    public int ElectroCount;
    public float ElectroSecondaryTime;

    // --- Arc (arc.qc) ---
    /// <summary>QC <c>actor.(weaponentity).arc_beam</c> — the live beam entity (null when not firing).</summary>
    public Entity? ArcBeam;
    public float BeamPrev;
    /// <summary>QC <c>.beam_heat</c> — accumulated barrel heat (toward overheat_max).</summary>
    public float BeamHeat;
    /// <summary>QC <c>.arc_overheat</c> — server time the overheat jam lasts until.</summary>
    public float ArcOverheat;
    /// <summary>QC <c>.arc_cooldown</c> — the cooldown speed latched when the beam ended (HUD heat-% scale).</summary>
    public float ArcCooldown;
    /// <summary>QC <c>.beam_bursting</c> — latched once the burst beam starts; keeps bursting until the beam ends.</summary>
    public bool BeamBursting;
    /// <summary>QC <c>.beam_dir</c> — the current beam direction (lags the aim, curving toward it).</summary>
    public Vector3 BeamDir;
    /// <summary>Whether <see cref="BeamDir"/> has been seeded yet (QC beam_initialized).</summary>
    public bool BeamInitialized;
    /// <summary>QC <c>actor.arc_smoke_sound</c> — whether the overheat-fire loop (SND_ARC_LOOP_OVERHEAT) is playing.</summary>
    public bool ArcSmokeSound;

    // --- Rifle (rifle.qc) ---
    /// <summary>QC <c>.rifle_accumulator</c> — burst-cost accumulator gating the bullethail.</summary>
    public float RifleAccumulator;

    // --- Shotgun (shotgun.qc) ---
    public float ShotgunPrimaryTime;

    // --- Tuba (tuba.qc) ---
    /// <summary>QC <c>.tuba_instrument</c> — 0 tuba, 1 accordion, 2 klein bottle (cycled on reload).</summary>
    public int TubaInstrument;
    /// <summary>QC <c>actor.(weaponentity).tuba_note</c> — the sustained note entity while a key is held.</summary>
    public Entity? TubaNote;
    public float TubaSmokeTime;

    // QC tuba.qc MAX_TUBANOTES = 32: the melody-recognition ring buffer (per weapon slot, like QC stores
    // tuba_lastnotes[] / tuba_lastnotes_last / tuba_lastnotes_cnt on actor.(weaponentity)). Each entry is the
    // just-ended note as vec3(on=start time, off=end time, pitch=note); W_Tuba_HasPlayed walks it backwards.
    /// <summary>QC <c>.tuba_lastnotes_last</c> — index of the most-recently recorded note in the ring.</summary>
    public int TubaLastNotesLast;
    /// <summary>QC <c>.tuba_lastnotes_cnt</c> — how many notes are valid in the ring (capped at MAX_TUBANOTES).</summary>
    public int TubaLastNotesCount;
    /// <summary>QC <c>.tuba_lastnotes[MAX_TUBANOTES]</c> — ring of (on, off, pitch) note records.</summary>
    public Vector3[] TubaLastNotes = new Vector3[Tuba.MaxTubaNotes];

    // --- Hook grapple (server/hook.qc) ---
    /// <summary>QC <c>actor.(weaponentity).hook</c> — the live grapple chain entity.</summary>
    public Entity? Hook;
    /// <summary>QC <c>.hook_state</c> — FIRING/REMOVING/PULLING/WAITING_FOR_RELEASE bitset.</summary>
    public HookState HookState;
    public float HookRefire;
    public float HookTimeHooked;
    public float HookTimeFuelDecrease;

    // --- Porto (porto.qc) ---
    public Vector3 PortoVAngle;
    public bool PortoVAngleHeld;

    // --- BallStealer / Nexball (sv_weapon.qc) ---
    /// <summary>QC <c>NB_METERSTART(actor)</c> — server time the basketball power-meter charge started; 0 = not
    /// charging. Set when the primary button is first pressed while carrying; cleared on shot or steal.</summary>
    public float NbMeterStart;
}

/// <summary>QC hook_state bitset (mutators/mutator/hook + server/hook.qh).</summary>
[Flags]
public enum HookState
{
    None = 0,
    Firing = 1,             // HOOK_FIRING
    Removing = 2,           // HOOK_REMOVING
    Pulling = 4,            // HOOK_PULLING
    Releasing = 8,          // HOOK_RELEASING
    WaitingForRelease = 16, // HOOK_WAITING_FOR_RELEASE
}
