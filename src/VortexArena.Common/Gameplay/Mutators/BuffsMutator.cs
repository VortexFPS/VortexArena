using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Gameplay.Waypoints;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Buffs mutator — port of common/mutators/mutator/buffs/sv_buffs.qc + buff/*.qc, built on the shared
/// <see cref="StatusEffectsCatalog"/>. Buff pickups spawn around the map; touching one applies a buff
/// status effect (a <see cref="StatusEffectDef"/> with <see cref="StatusEffectDef.IsBuff"/>) for a timed
/// duration, replacing any buff already held. Each buff's effect is driven through the gameplay hooks and a
/// per-frame tick:
/// <list type="bullet">
/// <item>resistance — reduces damage taken (Damage_Calculate)</item>
/// <item>medic — boosts health regen + raises max + slows rot + can survive a fatal hit (PlayerRegen / Damage_Calculate)</item>
/// <item>vampire — converts dealt damage into health (PlayerDamage_SplitHealthArmor)</item>
/// <item>jump — higher jump velocity + fall-damage immunity (PlayerPhysics / Damage_Calculate)</item>
/// <item>flight — crouch in midair flips gravity (PlayerPreThink)</item>
/// <item>bash — more knockback dealt, immune to knockback (Damage_Calculate)</item>
/// <item>disability — attacks slow the victim (Damage_Calculate + the Stunned effect)</item>
/// <item>vengeance — reflects a share of damage taken (Damage_Calculate)</item>
/// <item>luck — chance of a critical hit (Damage_Calculate)</item>
/// <item>ammo — unlimited ammo while held (apply/remove)</item>
/// <item>magnet — auto-collects nearby items (tick)</item>
/// </list>
/// Enabled by the <c>g_buffs</c> cvar.
/// </summary>
[Mutator]
public sealed class BuffsMutator : MutatorBase
{
    public BuffsMutator() => NetName = "buffs";

    // QC REGISTER_MUTATOR(buffs, autocvar_g_buffs).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_buffs") != 0f;

    // ---- cvar-backed balance (defaults from bal/defaults; see *_time + the per-buff cvars) ----
    private float _resistanceBlock = 0.5f;       // g_buffs_resistance_blockpercent
    private float _medicSurviveChance = 0.6f;    // g_buffs_medic_survive_chance
    private float _medicSurviveHealth = 5f;      // g_buffs_medic_survive_health
    private float _medicRegen = 1.7f;            // g_buffs_medic_regen
    private float _medicMax = 1.5f;              // g_buffs_medic_max (limit_mod = max_mod)
    private float _medicRot = 0.2f;              // g_buffs_medic_rot (rot_mod — slower rot above stable)
    private float _vampireSteal = 0.4f;          // g_buffs_vampire_damage_steal
    private float _jumpVelocity = 600f;          // g_buffs_jump_velocity
    private float _bashForce = 2f;               // g_buffs_bash_force
    private float _bashForceSelf = 1.2f;         // g_buffs_bash_force_self
    private float _disabilitySlowTime = 3f;      // g_buffs_disability_slowtime
    private float _disabilitySpeed = 0.7f;       // g_buffs_disability_speed
    private float _disabilityAttackTime = 1.5f;  // g_buffs_disability_attack_time_multiplier (weapon fire-time slow)
    private float _vengeanceMult = 0.4f;         // g_buffs_vengeance_damage_multiplier
    private float _luckChance = 0.15f;           // g_buffs_luck_chance
    private float _luckMult = 2f;                // g_buffs_luck_damagemultiplier
    private float _magnetRangeItem = 250f;       // g_buffs_magnet_range_item
    private float _magnetRangeBuff = 100f;       // g_buffs_magnet_range_buff
    private float _pickupDelay = 0.7f;           // g_buffs_pickup_delay
    private float _waypointDistance = 1024f;     // g_buffs_waypoint_distance (radar/HUD waypoint max view distance)

    // ---- inferno (burn-on-hit + fire/lava resistance) ----
    private float _infernoDamageMult = 0.3f;     // g_buffs_inferno_damagemultiplier
    private float _infernoBurnMinTime = 0.5f;    // g_buffs_inferno_burntime_min_time
    private float _infernoBurnTargetDamage = 150f; // g_buffs_inferno_burntime_target_damage
    private float _infernoBurnTargetTime = 5f;   // g_buffs_inferno_burntime_target_time
    private float _infernoBurnFactor = 2f;       // g_buffs_inferno_burntime_factor

    // ---- swapper (swap places with the nearest enemy on drop-weapon) ----
    private float _swapperRange = 1500f;         // g_buffs_swapper_range

    // ---- hook handlers ----
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<GameHooks.PlayerDamageArgs>? _onSplit;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _onRegen;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.WeaponRateFactorArgs>? _onRate;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.PlayerUseKeyArgs>? _onUseKey;
    private HookHandler<MutatorHooks.FilterItemArgs>? _onFilterItem;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onSplit ??= OnSplitHealthArmor;
        _onRegen ??= OnPlayerRegen;
        _onPhysics ??= OnPlayerPhysics;
        _onPreThink ??= OnPlayerPreThink;
        _onRate ??= OnWeaponRateFactor;
        _onForbidThrow ??= OnForbidThrowCurrentWeapon;
        _onUseKey ??= OnPlayerUseKey;
        _onFilterItem ??= OnFilterItem;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onSplit);
        MutatorHooks.PlayerRegen.Add(_onRegen);
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        MutatorHooks.WeaponRateFactor.Add(_onRate);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.PlayerUseKey.Add(_onUseKey);
        MutatorHooks.FilterItem.Add(_onFilterItem);

        // Wire the buff-specific removal cleanup onto the buff status-effect defs so it runs on EVERY removal path
        // — timed expiry (StatusEffects.Tick → Remove(Timeout)) and death (RemoveAll), not just the explicit
        // buff_RemoveAll (pickup/drop) path. Without this the ammo buff's IT_UNLIMITED_AMMO bit and the flight
        // buff's gravity (a crouch-toggled Gravity = -1, which has no spawn-time reset) leaked past the buff's
        // lifetime (parity: each buff's m_remove side effect). Cleared in Unhook.
        if (StatusEffectsCatalog.ByName("buff_ammo") is { } ammoDef) ammoDef.OnRemove = OnBuffAmmoRemove;
        if (StatusEffectsCatalog.ByName("buff_flight") is { } flightDef) flightDef.OnRemove = OnBuffFlightRemove;

        if (Api.Services is not null)
        {
            R(ref _resistanceBlock, "g_buffs_resistance_blockpercent");
            R(ref _medicSurviveChance, "g_buffs_medic_survive_chance");
            R(ref _medicSurviveHealth, "g_buffs_medic_survive_health");
            R(ref _medicRegen, "g_buffs_medic_regen");
            R(ref _medicMax, "g_buffs_medic_max");
            R(ref _medicRot, "g_buffs_medic_rot");
            R(ref _vampireSteal, "g_buffs_vampire_damage_steal");
            R(ref _jumpVelocity, "g_buffs_jump_velocity");
            R(ref _bashForce, "g_buffs_bash_force");
            R(ref _bashForceSelf, "g_buffs_bash_force_self");
            R(ref _disabilitySlowTime, "g_buffs_disability_slowtime");
            R(ref _disabilitySpeed, "g_buffs_disability_speed");
            R(ref _disabilityAttackTime, "g_buffs_disability_attack_time_multiplier");
            R(ref _vengeanceMult, "g_buffs_vengeance_damage_multiplier");
            R(ref _luckChance, "g_buffs_luck_chance");
            R(ref _luckMult, "g_buffs_luck_damagemultiplier");
            R(ref _magnetRangeItem, "g_buffs_magnet_range_item");
            R(ref _magnetRangeBuff, "g_buffs_magnet_range_buff");
            R(ref _pickupDelay, "g_buffs_pickup_delay");
            R(ref _waypointDistance, "g_buffs_waypoint_distance");
            R(ref _infernoDamageMult, "g_buffs_inferno_damagemultiplier");
            R(ref _infernoBurnMinTime, "g_buffs_inferno_burntime_min_time");
            R(ref _infernoBurnTargetDamage, "g_buffs_inferno_burntime_target_damage");
            R(ref _infernoBurnTargetTime, "g_buffs_inferno_burntime_target_time");
            R(ref _infernoBurnFactor, "g_buffs_inferno_burntime_factor");
            R(ref _swapperRange, "g_buffs_swapper_range");

            // QC buffs_Initialize: spawn the configured number of buffs if none exist yet.
            SpawnInitialBuffs();
        }
    }

    // QC MUTATOR_HOOKFUNCTION(buffs, BuildMutatorsString / BuildMutatorsPrettyString): only report Buffs as an
    // active mutator when g_buffs > 0 (the auto-spawn/replace-powerups mode), matching Base exactly.
    public override string BuildMutatorsString(string s)
        => (Api.Services is not null && Api.Cvars.GetFloat("g_buffs") > 0f) ? s + ":Buffs" : s;

    public override string BuildMutatorsPrettyString(string s)
        => (Api.Services is not null && Api.Cvars.GetFloat("g_buffs") > 0f) ? s + ", Buffs" : s;

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onSplit is not null) GameHooks.PlayerDamageSplitHealthArmor.Remove(_onSplit);
        if (_onRegen is not null) MutatorHooks.PlayerRegen.Remove(_onRegen);
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onRate is not null) MutatorHooks.WeaponRateFactor.Remove(_onRate);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onUseKey is not null) MutatorHooks.PlayerUseKey.Remove(_onUseKey);
        if (_onFilterItem is not null) MutatorHooks.FilterItem.Remove(_onFilterItem);
        // Detach the buff-removal cleanup (mirrors the Hook wiring) so a deactivated Buffs mutator doesn't leave a
        // handler bound on the shared status-effect defs.
        if (StatusEffectsCatalog.ByName("buff_ammo") is { } ammoDef && ammoDef.OnRemove == OnBuffAmmoRemove) ammoDef.OnRemove = null;
        if (StatusEffectsCatalog.ByName("buff_flight") is { } flightDef && flightDef.OnRemove == OnBuffFlightRemove) flightDef.OnRemove = null;
    }

    private static void R(ref float field, string cvar)
    {
        float v = Api.Cvars.GetFloat(cvar);
        if (v != 0f) field = v;
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    private static bool IsFrozen(Entity e)
    {
        var fz = StatusEffectsCatalog.Frozen;
        return fz is not null && StatusEffectsCatalog.Has(e, fz);
    }

    // Resolve a buff def by its short name (e.g. "resistance" -> the "buff_resistance" StatusEffectDef).
    // The full name is memoized: Active() runs five times per player per tick from the PreThink handler, and
    // a per-call "buff_" + shortName concat was a steady ~2.5 KB/tick of GC churn at 12 players.
    private static readonly Dictionary<string, string> _buffNameCache = new(StringComparer.Ordinal);

    private static StatusEffectDef? Buff(string shortName)
    {
        if (!_buffNameCache.TryGetValue(shortName, out string? full))
            _buffNameCache[shortName] = full = "buff_" + shortName;
        return StatusEffectsCatalog.ByName(full);
    }

    private static bool Active(Entity? e, string shortName)
    {
        if (e is null) return false;
        var def = Buff(shortName);
        return def is not null && StatusEffectsCatalog.Has(e, def);
    }

    // =====================================================================================
    //  Pickup / spawning
    // =====================================================================================

    /// <summary>
    /// QC buff_Available: a buff can be offered when its g_buffs_&lt;name&gt; cvar is set, ammo is excluded
    /// when ammo is already unlimited or in melee_only, and vampire is excluded when the vampire mutator runs.
    /// </summary>
    private static bool BuffAvailable(StatusEffectDef buff)
    {
        if (Api.Services is null) return false;
        string n = buff.Name.StartsWith("buff_", StringComparison.Ordinal) ? buff.Name[5..] : buff.Name;
        // QC: ammo is excluded when start_items already grants IT_UNLIMITED_AMMO (the dominant source of that
        // start flag is g_use_ammunition 0 — readplayerstartcvars) OR in g_melee_only.
        if (n == "ammo" && (StartItemsHasUnlimitedAmmo() || Api.Cvars.GetFloat("g_melee_only") != 0f)) return false;
        if (n == "vampire" && Api.Cvars.GetFloat("g_vampire") != 0f) return false;
        return Api.Cvars.GetFloat("g_buffs_" + n) != 0f;
    }

    private static IReadOnlyList<StatusEffectDef> AvailableBuffs()
    {
        List<StatusEffectDef> list = new();
        foreach (var d in StatusEffectsCatalog.All)
            if (d.IsBuff && BuffAvailable(d))
                list.Add(d);
        return list;
    }

    // QC buff_seencount (a server-local fairness counter, keyed by buff RegistryId): each pick increments the
    // chosen buff's seencount so recently-seen buffs are weighted lower next time. Not networked.
    private static readonly Dictionary<int, int> _buffSeenCount = new();

    // void buff_NewType(entity) — QC weighted random over available buffs via RandomSelection with weight
    // max(0.2, 1/seencount), then ++seencount on the chosen buff (so the same type recurs less often).
    private static StatusEffectDef? RandomBuff()
    {
        var avail = AvailableBuffs();
        if (avail.Count == 0) return null;

        // QC RandomSelection weighted pick: total weight, then a uniform draw across the cumulative weights.
        float total = 0f;
        foreach (var d in avail)
        {
            int seen = _buffSeenCount.TryGetValue(d.RegistryId, out int c) && c > 0 ? c : 1;
            total += MathF.Max(0.2f, 1f / seen);
        }
        float pick = Prandom.Float() * total;
        StatusEffectDef chosen = avail[avail.Count - 1];
        foreach (var d in avail)
        {
            int seen = _buffSeenCount.TryGetValue(d.RegistryId, out int c) && c > 0 ? c : 1;
            pick -= MathF.Max(0.2f, 1f / seen);
            if (pick <= 0f) { chosen = d; break; }
        }

        // QC: ++newbuff.buff_seencount — lower the chance of seeing this buff again soon.
        _buffSeenCount[chosen.RegistryId] = (_buffSeenCount.TryGetValue(chosen.RegistryId, out int cur) ? cur : 0) + 1;
        return chosen;
    }

    // void buffs_DelayedInit / buff_Init — spawn g_buffs_spawn_count buff items if none exist.
    private void SpawnInitialBuffs()
    {
        if (Api.Cvars.GetFloat("g_buffs") <= 0f) return;
        int count = (int)Api.Cvars.GetFloat("g_buffs_spawn_count");
        if (count <= 0) return;

        // QC: only seed if no item_buff already exists.
        foreach (Entity _ in Api.Entities.FindByClass("item_buff"))
            return;

        for (int i = 0; i < count; i++)
        {
            // QC buffs_DelayedInit: each auto-seeded buff gets spawnflags|=64 (always randomize/relocate) BEFORE
            // buff_Init, a random launch velocity, then buff_Init (here ConfigureBuffEntity via SpawnBuff).
            Entity e = Api.Entities.Spawn();
            e.BuffAlwaysRelocate = true;
            e.Velocity = Prandom.Vec() * 250f;
            ConfigureBuffEntity(e, null); // null type -> randomize (QC buff_Init's buff_NewType)
        }
    }

    // QC MDL_BUFF — the spinning relic the buff item (and the carrier glow) uses.
    private const string MdlBuff = "models/relics/relic.md3";

    /// <summary>
    /// Spawn a buff pickup entity (QC buff_Init): a touchable item carrying a buff type, launched a little so
    /// it scatters. The buff type randomizes on respawn. Returns the spawned entity.
    /// </summary>
    public Entity SpawnBuff(StatusEffectDef? type)
    {
        Entity e = Api.Entities.Spawn();
        e.Velocity = Prandom.Vec() * 250f; // QC buffs_DelayedInit (reset if random location works)
        ConfigureBuffEntity(e, type ?? RandomBuff());
        return e;
    }

    // QC buff_Init: turn a freshly-spawned (or replacement) edict into a live, touchable buff item — assign the
    // model/box/movetype/effects, validate (randomize if no/unavailable type), arm the activate cooldown, and
    // wire the per-frame think + touch. Shared by SpawnBuff (auto-seed) and SpawnMapBuff (map spawnfunc). Returns
    // false (and frees the edict) when g_buffs is off or no buff type is available, matching buff_Init's deletes.
    private bool ConfigureBuffEntity(Entity e, StatusEffectDef? type)
    {
        // QC buff_Init: if (!autocvar_g_buffs) { delete(this); return; }
        if (Api.Cvars.GetFloat("g_buffs") == 0f) { RemoveBuffEntity(e); return false; }

        StatusEffectDef? buff = type;
        // QC: a null type (item_buff_random) or an unavailable type with g_buffs_replace_available re-rolls.
        if (buff is null || (Api.Cvars.GetFloat("g_buffs_replace_available") != 0f && !BuffAvailable(buff)))
            buff = RandomBuff();
        // QC: still invalid/unavailable -> delete the item.
        if (buff is null || !BuffAvailable(buff)) { RemoveBuffEntity(e); return false; }

        e.ClassName = "item_buff";
        e.Solid = Solid.Trigger;
        e.Flags |= EntFlags.Item;
        e.MoveType = MoveType.Toss;
        e.Gravity = 1f;
        e.Effects |= EffectFlags.FullBright | EffectFlags.Stardust | EffectFlags.NoShadow;
        e.BuffDef = buff;
        Api.Entities.SetModel(e, buff.Model ?? MdlBuff);
        // QC buff_Init/buff_Think (sv_buffs.qc:305-307): this.skin = buff.m_skin — the relic.md3 ships one skin
        // per buff type, so the picked buff renders in its own colour. (this.color/this.glowmod = buff.m_color
        // are item-tint fields the port does not network for world items yet — see buffs.item.spawnfunc.)
        e.Skin = buff.Skin;
        // QC: setsize(this, ITEM_D_MINS, ITEM_L_MAXS) = '-30 -30 0'..'30 30 70'.
        Api.Entities.SetSize(e, ItemBoxes.DefaultMins, ItemBoxes.LargeMaxs);

        // QC buff_Init: buff_SetCooldown(this, g_buffs_cooldown_activate + max(0, game_starttime - time));
        //               this.buff_active = !this.buff_activetime;
        float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
        float activate = Cvar("g_buffs_cooldown_activate", 5f) + MathF.Max(0f, gameStart - Api.Clock.Time);
        e.BuffLifetime = 0f;
        e.NextThink = Api.Clock.Time + activate;
        e.BuffActive = false;
        e.Owner = null;
        e.Touch = (self, toucher) => BuffTouch(self, toucher);
        // After the activate cooldown the item turns active (QC buff_Think activate-cooldown branch), then the
        // steady lifetime think takes over.
        e.Think = self => ActivateBuff(self);
        return true;
    }

    // QC buff_Think activate-cooldown elapse: the item becomes touchable, plays SND_STRENGTH_RESPAWN, then runs
    // its steady lifetime think.
    private void ActivateBuff(Entity item)
    {
        item.BuffActive = true;
        // QC buff_Think reactivation: SND_STRENGTH_RESPAWN + Send_Effect(EFFECT_ITEM_RESPAWN, CENTER_OR_VIEWOFS).
        SoundSystem.PlayOn(item, "STRENGTH_RESPAWN");
        EffectEmitter.Emit("ITEM_RESPAWN", Center(item));
        // QC buff_Init/buff_Think: attach the WP_Buff radar/HUD waypoint once the item is live (idempotent).
        SpawnBuffWaypoint(item);
        MaybeRelocate(item); // QC buff_Reset: relocate on (re)activation if random_location / spawnflag 64
        float lifetime = Api.Cvars.GetFloat("g_buffs_random_lifetime");
        if (item.BuffLifetime == 0f && lifetime > 0f && (item.BuffAlwaysRelocate || Api.Cvars.GetFloat("g_buffs_random_location") != 0f))
            item.BuffLifetime = Api.Clock.Time + lifetime;
        BuffThink(item);
    }

    /// <summary>
    /// QC buff_Init via the item_buff_&lt;type&gt; map spawnfuncs (buffs.qh BUFF_SPAWNFUNCS) + the Q3/QL/WOP
    /// compat classnames (BUFF_SPAWNFUNC_Q3COMPAT). Configures a map-placed edict into a live buff item carrying
    /// the named buff (null = item_buff_random), honoring the teamplay-only team_forced. Static so the spawnfunc
    /// table (ItemSpawnFuncs) can call it without a live mutator instance; the per-frame think/touch close over
    /// the static helpers (no instance state). Frees the edict when g_buffs is off / no type is available.
    /// </summary>
    public static void SpawnMapBuff(Entity e, string? buffShortName, int teamForced)
    {
        if (Api.Services is null) return; // no live world to configure into (test harness without services)
        // QC buff_Init_Compat / BUFF_SPAWNFUNC: team_forced only applies in teamplay.
        e.TeamForced = GameScores.Teamplay ? teamForced : 0;
        StatusEffectDef? type = buffShortName is null ? null : Buff(buffShortName);
        // Use a transient instance only to reach the (instance) ConfigureBuffEntity/think closures — it carries no
        // per-spawn state, so any instance configures identically.
        new BuffsMutator().ConfigureBuffEntity(e, type);
    }

    // QC ITEM_TOUCH_NEEDKILL(): the buff settled in lava/slime (NODROP) or on a sky surface; a zero-length trace
    // at the item reports the surface it's resting on. NoDropContents (DPCONTENTS_NODROP) covers lava brushes.
    private const int NoDropContents = unchecked((int)0x80000000);
    private const int Q3SurfaceFlagSky = 0x4;

    private static bool BuffInNeedKill(Entity item)
    {
        if (Api.Services is null) return false;
        TraceResult tr = Api.Trace.Trace(item.Origin, Vector3.Zero, Vector3.Zero, item.Origin, MoveFilter.Normal, item);
        return (tr.DpHitContents & NoDropContents) != 0 || (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0;
    }

    // void buff_Touch(entity this, entity toucher) — apply the buff to a touching player.
    private void BuffTouch(Entity item, Entity toucher)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return;

        // QC buff_Touch: a buff item that touches lava/slime/void/sky relocates instead of being picked up.
        if (BuffInNeedKill(item))
        {
            BuffRespawn(item);
            return;
        }

        if (!item.BuffActive || item.BuffDef is null) return;
        if (!IsPlayer(toucher) || toucher.DeadState != DeadFlag.No) return;
        // QC MUTATOR_HOOKFUNCTION(ft, BuffTouch) (sv_freezetag.qc): a frozen toucher does NOT collect
        // buffs — mirrors the ItemTouch gate. A frozen body can be pushed or teleported into a buff pickup;
        // without this guard it would collect the buff silently. IsFrozen reads the shared Frozen status effect
        // that FreezeTag.Freeze applies, so this works without a FreezeTag type-cast.
        if (IsFrozen(toucher)) return;
        // QC buff_Touch: a team-forced buff item can only be picked up by its team; a vehicle occupant can't.
        if ((item.TeamForced != 0 && (int)toucher.Team != item.TeamForced) || toucher.Vehicle is not null) return;
        if (Api.Clock.Time < toucher.BuffShield) return; // recently dropped/replaced a buff

        StatusEffectDef thebuff = item.BuffDef;

        // QC: if the player already holds a buff, replace it ONLY when cl_buffs_autoreplace is set AND it's a
        // *different* buff; otherwise do nothing (keep the held buff). cl_buffs_autoreplace is REPLICATE'd to the
        // server with default 1 (mutators.cfg); the port reads it as the server-side value (no per-client store).
        StatusEffectDef? held = HeldBuff(toucher);
        if (held is not null)
        {
            bool autoreplace = CvarBool("cl_buffs_autoreplace", true);
            if (!autoreplace || held == thebuff) return; // do nothing

            // QC: notify the replaced buff is lost (to others, MSG_INFO INFO_ITEM_BUFF_LOST), guarded by
            // !IS_INDEPENDENT_PLAYER. (SND_BUFF_LOST on a replace is commented out in QC — intentionally omitted.)
            if (!toucher.IsIndependentPlayer)
                NotificationSystem.Send(NotifBroadcast.AllExcept, toucher, MsgType.Info,
                    "ITEM_BUFF_LOST", toucher.NetName, held.RegistryId);
            RemoveAllBuffs(toucher);
        }

        item.BuffActive = false;
        item.BuffLifetime = 0f; // QC: this.lifetime = 0 (a picked-up item no longer auto-relocates)
        item.Owner = toucher;
        // QC buff_Touch: the collected item's waypoint goes away while it's on its respawn cooldown.
        WaypointSprites.Kill(item.BuffWaypoint);
        item.BuffWaypoint = null;

        // QC: bufftime = this.buffs_finished ? ... : thebuff.m_time(thebuff); fall back to 999 / cvar time.
        float bufftime = BuffTime(thebuff);
        ApplyBuff(toucher, thebuff, bufftime);

        // QC: Send_Effect(EFFECT_ITEM_PICKUP, CENTER_OR_VIEWOFS(this), '0 0 0', 1).
        EffectEmitter.Emit("ITEM_PICKUP", Center(item));
        Api.Sound.Play(toucher, SoundChannel.Item, "misc/shield_respawn.wav");
        // QC Send_Notification(ITEM_BUFF_GOT, thebuff.m_id). The CENTER variant takes one float (the buff id)
        // and tells the picker "You got the <buff> buff!". (The buff id is the status-effect registry id.)
        NotificationSystem.Center(toucher, "ITEM_BUFF_GOT", thebuff.RegistryId);
        // QC: INFO_ITEM_BUFF to everyone else — "<name> got the <buff> buff!" (guarded by !IS_INDEPENDENT_PLAYER).
        if (!toucher.IsIndependentPlayer)
            NotificationSystem.Send(NotifBroadcast.AllExcept, toucher, MsgType.Info,
                "ITEM_BUFF", toucher.NetName, thebuff.RegistryId);

        // QC: the item goes on cooldown then respawns (re-randomizing its type). Keep the entity alive.
        ScheduleRespawn(item);
    }

    // float Buff.m_time — cvar g_buffs_<name>_time, default 60s; 0 → "permanent-ish" 999.
    private static float BuffTime(StatusEffectDef buff)
    {
        if (Api.Services is null) return 60f;
        string n = buff.Name.StartsWith("buff_", StringComparison.Ordinal) ? buff.Name[5..] : buff.Name;
        float t = Api.Cvars.GetFloat("g_buffs_" + n + "_time");
        return t > 0f ? t : 999f;
    }

    private void ScheduleRespawn(Entity item)
    {
        // QC buff_Think respawn branch: after the respawn cooldown, clear the owner, optionally re-randomize the
        // type (only when g_buffs_randomize is on for this gametype), optionally relocate (g_buffs_random_location
        // or spawnflags&64), and re-activate. The cooldown is counted by a recurring per-frame think so the
        // lifetime timer (buff_Respawn) can also fire while the buff sits untouched.
        float cooldown = Cvar("g_buffs_cooldown_respawn", 3f);
        float ready = Api.Clock.Time + cooldown;
        item.Owner = null;
        item.NextThink = ready;
        item.Think = self =>
        {
            // Cooldown elapsed: re-arm the buff (QC buff_Think + buff_Reset gating).
            MaybeRandomize(self);
            MaybeRelocate(self);
            Api.Entities.SetModel(self, self.BuffDef?.Model ?? MdlBuff);
            // QC buff_Think type-change retint: this.skin = buff.m_skin after a re-randomize.
            self.Skin = self.BuffDef?.Skin ?? 0;
            self.BuffActive = true;
            // QC buff_Think activate-cooldown elapse: SND_STRENGTH_RESPAWN + EFFECT_ITEM_RESPAWN on reactivation.
            SoundSystem.PlayOn(self, "STRENGTH_RESPAWN");
            EffectEmitter.Emit("ITEM_RESPAWN", Center(self));
            // QC buff_Think: the picked-up item's waypoint was killed on pickup — re-attach it for the fresh
            // (possibly re-randomized) buff type. Kill any stale marker first so the colour/customize re-bind.
            WaypointSprites.Kill(self.BuffWaypoint);
            self.BuffWaypoint = null;
            SpawnBuffWaypoint(self);
            BuffThink(self);
        };
    }

    // QC buff_Think (the steady per-frame think on an active buff): re-randomize/relocate the buff when its
    // random_lifetime elapses without anyone touching it. Recurs every frame (QC sets nextthink = time).
    private void BuffThink(Entity item)
    {
        item.NextThink = Api.Clock.Time;
        item.Think = self =>
        {
            if (self.BuffActive && self.BuffLifetime != 0f && Api.Clock.Time >= self.BuffLifetime)
                BuffRespawn(self);
            BuffThink(self);
        };
    }

    // QC buff_Reset randomize gate: re-roll the buff type only when g_buffs_randomize is enabled for the current
    // gametype (bit 1 = non-team, bit 2 = team). Default 0 → never randomize (the port previously always did).
    private static void MaybeRandomize(Entity item)
    {
        if (Api.Services is null) return;
        int randomize = (int)Api.Cvars.GetFloat("g_buffs_randomize");
        int gate = GameScores.Teamplay ? 2 : 1;
        if ((randomize & gate) != 0)
            item.BuffDef = RandomBuff();
    }

    // QC buff_Reset / buff_Think relocate gate: relocate only when g_buffs_random_location is set or the buff
    // entity carries the always-relocate spawnflag (64). buffs_DelayedInit sets spawnflag 64 on each auto-seeded
    // buff; the port marks auto-spawned buffs the same way via BuffActive seeding (see SpawnBuff).
    private void MaybeRelocate(Entity item)
    {
        if (Api.Services is null) return;
        bool alwaysRelocate = item.BuffAlwaysRelocate;
        if (Api.Cvars.GetFloat("g_buffs_random_location") != 0f || alwaysRelocate)
            BuffRespawn(item);
    }

    // QC buff_Respawn: relocate an untouched/expired buff to a fresh random map spot (with the SelectSpawnPoint
    // fallback), launch it upward, unstick it, arm the lifetime timer, fire the two electro-combo bursts (old +
    // new origin), play the relocation sound, and ping/re-tint the buff's radar waypoint at its new spot.
    private void BuffRespawn(Entity item)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return;

        Vector3 oldOrigin = item.Origin; // QC: vector oldbufforigin = this.origin

        // QC: velocity = '0 0 200'; MoveToRandomMapLocation, else SelectSpawnPoint fallback (randomvec*100 + up).
        // RandomMapLocation already folds in the SelectSpawnPoint fallback (it samples a player spawnpoint, or
        // jitters around the home origin when the map has none).
        Vector3 dest = BallEntity.RandomMapLocation(item.Origin);
        Api.Entities.SetOrigin(item, dest);
        item.Velocity = (Prandom.Vec() * 100f) + new Vector3(0f, 0f, 200f);

        // QC: tracebox(origin, mins*1.5, maxs*1.5, origin, MOVE_NOMONSTERS, this) → setorigin(trace_endpos) to unstick.
        TraceResult tr = Api.Trace.Trace(dest, item.Mins * 1.5f, item.Maxs * 1.5f, dest, MoveFilter.NoMonsters, item);
        Api.Entities.SetOrigin(item, tr.EndPos);

        item.MoveType = MoveType.Toss;
        item.Angles = Vector3.Zero;

        // QC: lifetime = time + g_buffs_random_lifetime (re-respawn if still untouched after this).
        float lifetime = Api.Cvars.GetFloat("g_buffs_random_lifetime");
        item.BuffLifetime = lifetime > 0f ? Api.Clock.Time + lifetime : 0f;

        // QC: Send_Effect(EFFECT_ELECTRO_COMBO, oldbufforigin + center, ...) + Send_Effect(... CENTER_OR_VIEWOFS(this)).
        Vector3 half = (item.Mins + item.Maxs) * 0.5f;
        EffectEmitter.Emit("ELECTRO_COMBO", oldOrigin + half);
        EffectEmitter.Emit("ELECTRO_COMBO", Center(item));

        // QC: sound(this, CH_TRIGGER, SND_KA_RESPAWN, VOL_BASE, ATTEN_NONE).
        SoundSystem.PlayOn(item, "KA_RESPAWN");

        // QC buff_Respawn: WaypointSprite_Ping(this.buff_waypoint) — pulse the radar marker at the new spot, then
        // resync its icon/colour to the (possibly re-randomized) buff type (QC re-tints the waypoint on a change).
        WaypointSprites.Ping(item.BuffWaypoint);
        if (item.BuffWaypoint is not null && item.BuffDef is not null)
            WaypointSprites.UpdateTeamRadar(item.BuffWaypoint, 1, BuffColor(item.BuffDef));
    }

    // QC delete(this) on a buff item: tear down its radar/HUD waypoint before freeing the edict (QC's waypoint is
    // owned by the item and would otherwise dangle on a freed reference).
    private static void RemoveBuffEntity(Entity e)
    {
        WaypointSprites.Kill(e.BuffWaypoint);
        e.BuffWaypoint = null;
        Api.Entities.Remove(e);
    }

    // QC CENTER_OR_VIEWOFS(e): the entity-centre point used for effect emission (origin + (mins+maxs)*0.5).
    private static Vector3 Center(Entity e) => e.Origin + (e.Mins + e.Maxs) * 0.5f;

    /// <summary>
    /// QC buff_Waypoint_Spawn (sv_buffs.qc): attach the WP_Buff radar/HUD waypoint to a live buff item so the
    /// buff shows on the radar + as a 3D sprite. The waypoint rides the item (reference), tinted by the buff's
    /// colour, with the per-buff radar icon. QC's WaypointSprite_Spawn customize hides the marker from any viewer
    /// who already holds that buff type, which the port mirrors via <see cref="WaypointSprite.VisibleForPlayer"/>.
    /// Returns null (no waypoint) when buffs are off or the item carries no buff type. Idempotent per item via
    /// <see cref="Entity.BuffWaypoint"/> — a second call returns the existing marker.
    /// </summary>
    public WaypointSprite? SpawnBuffWaypoint(Entity buffItem)
    {
        if (Api.Cvars.GetFloat("g_buffs") == 0f || buffItem.BuffDef is null) return null;
        // QC: one waypoint per item — don't double-spawn (buff_Init only attaches once).
        if (buffItem.BuffWaypoint is not null) return buffItem.BuffWaypoint;

        StatusEffectDef def = buffItem.BuffDef;
        WaypointSprite wp = WaypointSprites.Spawn(
            "Buff",
            lifetime: 0f,
            maxDistance: Cvar("g_buffs_waypoint_distance", 1024f),
            reference: buffItem,
            offset: new Vector3(0f, 0f, 64f),
            origin: default,
            team: buffItem.TeamForced,
            color: BuffColor(def),
            radarIcon: 1,
            rule: SpriteRule.Default,
            hideable: true);

        // QC buff_Waypoint_Spawn customize (WaypointSprite_FilterForLocalPlayer-style hide): a player already
        // holding this buff type doesn't see its waypoint (no point chasing a buff you can't pick up anyway).
        string shortName = ShortName(def);
        wp.VisibleForPlayer = viewer => viewer is null || !Active(viewer, shortName);

        buffItem.BuffWaypoint = wp;
        return wp;
    }

    // QC Buff.m_color ('1 0.62 0' style triples) → the Vector3 the waypoint/radar tint expects.
    private static Vector3 BuffColor(StatusEffectDef def)
        => new(def.Color.R, def.Color.G, def.Color.B);

    // =====================================================================================
    //  Apply / remove (the buff-specific m_apply/m_remove side effects)
    // =====================================================================================

    private void ApplyBuff(Entity actor, StatusEffectDef buff, float duration)
    {
        string n = ShortName(buff);

        // QC AmmoBuff.m_apply: grant unlimited ammo, remembering the previous state, and force every reloadable
        // slot's clip to full (saving the previous clip load) so reload weapons never run dry while held.
        if (n == "ammo")
        {
            actor.BuffAmmoPrevInfItems = (actor.Items & ItUnlimitedAmmo) != 0;
            actor.Items |= ItUnlimitedAmmo;
            for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; slot++)
            {
                var st = actor.WeaponState(new WeaponSlot(slot));
                if (st.ClipLoad != 0) st.BuffAmmoPrevClipLoad = st.ClipLoad;
                FillSlotClip(st);
            }
        }
        // QC FlightBuff.m_apply: remember gravity (so flipping it back works), default it if zero.
        if (n == "flight")
        {
            actor.BuffFlightOldGravity = actor.Gravity;
            if (actor.Gravity == 0f) actor.Gravity = 1f;
        }
        // QC Buff.m_apply: EF_NOSHADOW while a buff is held (so the buff icon reads cleanly).
        actor.Effects |= EffectFlags.NoShadow;

        StatusEffectsCatalog.Apply(actor, buff, duration);
    }

    // void buff_RemoveAll(actor) — strip every buff, then set the pickup delay. The per-buff m_remove side effects
    // (ammo bit / clip loads, flight gravity) now live in each buff def's OnRemove (wired in Hook), so they run on
    // EVERY removal path via StatusEffectsCatalog.Remove — here, on timed expiry, and on death — not just here.
    private void RemoveAllBuffs(Entity actor)
    {
        foreach (var d in StatusEffectsCatalog.All)
        {
            if (!d.IsBuff || !StatusEffectsCatalog.Has(actor, d)) continue;
            StatusEffectsCatalog.Remove(actor, d);   // fires OnBuffAmmoRemove / OnBuffFlightRemove
        }
        actor.Effects &= ~EffectFlags.NoShadow;
        if (Api.Services is not null)
            actor.BuffShield = Api.Clock.Time + MathF.Max(0f, _pickupDelay);
    }

    // QC AmmoBuff.m_remove: restore the pre-buff unlimited-ammo bit + the saved clip loads. Wired onto the
    // buff_ammo def so it runs on ANY removal (timed expiry / death / explicit strip); without it the
    // IT_UNLIMITED_AMMO bit leaked for the rest of the player's life (bounded only by the next respawn's item wipe).
    private static void OnBuffAmmoRemove(Entity actor, ActiveStatusEffect _, StatusEffectRemoval __)
    {
        actor.Items = BitSet(actor.Items, ItUnlimitedAmmo, actor.BuffAmmoPrevInfItems);
        RestoreAmmoClips(actor);
        actor.BuffAmmoPrevInfItems = false;
    }

    // QC FlightBuff.m_remove: if the player is still standing in a gravity zone, restore THAT zone's gravity
    // (trigger_gravity_check.enemy.gravity); otherwise restore the saved pre-buff gravity. Wired onto the
    // buff_flight def so it runs on timed expiry / death too — without it a crouch-toggled Gravity = -1 persisted
    // across the respawn (there is no spawn-time gravity reset), leaving the player floating upward.
    private static void OnBuffFlightRemove(Entity actor, ActiveStatusEffect _, StatusEffectRemoval __)
    {
        actor.Gravity = actor.GravityCheck?.Enemy is { } zone
            ? zone.Gravity
            : actor.BuffFlightOldGravity;
        actor.BuffFlightOldGravity = 0f;
    }

    private static StatusEffectDef? HeldBuff(Entity actor)
    {
        foreach (var d in StatusEffectsCatalog.All)
            if (d.IsBuff && StatusEffectsCatalog.Has(actor, d)) return d;
        return null;
    }

    private static string ShortName(StatusEffectDef d)
        => d.Name.StartsWith("buff_", StringComparison.Ordinal) ? d.Name[5..] : d.Name;

    // =====================================================================================
    //  Damage_Calculate — resistance / medic / jump / vengeance / bash / disability / luck
    // =====================================================================================

    // MUTATOR_HOOKFUNCTION(buffs, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity? attacker = args.Attacker;
        Entity target = args.Target;
        string dt = args.DeathType;
        float damage = args.Damage;
        Vector3 force = args.Force;

        // QC: no recurrence for inferno's or vengeance's own dealt damage.
        if (dt == DeathTypes.BuffInferno || dt == DeathTypes.BuffVengeance) { return false; }

        bool needKill = IsNeedKill(dt);

        // resistance — flat damage reduction (also on self-damage).
        if (Active(target, "resistance"))
            damage = QMath.Bound(0f, damage * (1f - _resistanceBlock), damage);

        // medic — survive a fatal hit with a sliver of health, by chance. QC guards with !ITEM_DAMAGE_NEEDKILL
        // so lava/slime/swamp/void hits are never wrongly survived.
        if (Active(target, "medic") && attacker is not null && !needKill
            && target.GetResource(ResourceType.Health) - damage <= 0f)
        {
            if (Prandom.Float() <= _medicSurviveChance)
                damage = MathF.Max(5f, target.GetResource(ResourceType.Health) - _medicSurviveHealth);
        }

        // jump — immune to fall damage.
        if (Active(target, "jump") && dt == DeathTypes.Fall)
            damage = 0f;

        // vengeance — reflect a share back at the attacker (after a short delay, like QC). QC guards with
        // !ITEM_DAMAGE_NEEDKILL so a lava/slime/void kill doesn't reflect.
        if (Active(target, "vengeance") && attacker is not null && !ReferenceEquals(attacker, target) && !needKill)
        {
            float reflected = damage * _vengeanceMult;
            ScheduleVengeance(target, attacker, reflected);
        }

        // bash — target takes no knockback; attacker deals scaled knockback.
        if (Active(target, "bash") && !ReferenceEquals(attacker, target))
            force = Vector3.Zero;
        if (Active(attacker, "bash") && force != Vector3.Zero)
            force *= ReferenceEquals(attacker, target) ? _bashForceSelf : _bashForce;

        // disability — the attacker's hit slows the target down for a while.
        if (Active(attacker, "disability") && attacker is not null && !ReferenceEquals(attacker, target))
        {
            var stunned = StatusEffectsCatalog.ByName("stunned");
            if (stunned is not null)
                StatusEffectsCatalog.Apply(target, stunned, _disabilitySlowTime);
        }

        // inferno (carrier as target) — immune to plain fire, half damage from lava.
        if (Active(target, "inferno"))
        {
            if (dt == DeathTypes.Fire) damage = 0f;
            if (dt == DeathTypes.Lava) damage *= 0.5f; // QC: TODO cvarize
        }

        if (!ReferenceEquals(attacker, target))
        {
            // luck — chance to crit for a damage multiplier.
            if (Active(attacker, "luck") && _luckMult > 0f)
            {
                if (Prandom.Float() <= _luckChance)
                    damage *= _luckMult;
            }

            // inferno (carrier as attacker) — set the target alight, burning for a log-curve time.
            if (Active(attacker, "inferno"))
            {
                float btime = InfernoBurnTime(damage);
                MonsterFramework.AddFireDamage(target, attacker, damage * _infernoDamageMult, btime,
                    DeathTypes.BuffInferno);
            }
        }

        args.Damage = damage;
        args.Force = force;
        return false;
    }

    // float buff_Inferno_CalculateTime(dmg) — burn duration grows logarithmically with the hit's damage:
    //   offset_y + (target_time - offset_y) * logn((dmg) * ((factor-1)/target_damage) + 1, factor)
    private float InfernoBurnTime(float dmg)
    {
        float offsetY = _infernoBurnMinTime;
        float intersectX = _infernoBurnTargetDamage;
        float intersectY = _infernoBurnTargetTime;
        float b = _infernoBurnFactor;
        if (b <= 0f || b == 1f || intersectX == 0f) return offsetY;
        // logn(x, base) = log(x) / log(base)
        float arg = dmg * ((b - 1f) / intersectX) + 1f;
        if (arg <= 0f) return offsetY;
        float logn = MathF.Log(arg) / MathF.Log(b);
        return offsetY + (intersectY - offsetY) * logn;
    }

    // QC ITEM_DAMAGE_NEEDKILL(dt): DEATH_HURTTRIGGER (= void) / DEATH_SLIME / DEATH_LAVA / DEATH_SWAMP.
    private static bool IsNeedKill(string dt)
        => dt == DeathTypes.Void || dt == DeathTypes.Slime || dt == DeathTypes.Lava || dt == DeathTypes.Swamp;

    // buff_Vengeance_DelayedDamage — reflect after 0.1s so it doesn't recurse into this hook.
    private static void ScheduleVengeance(Entity owner, Entity enemy, float dmg)
    {
        if (Api.Services is null || dmg <= 0f) return;
        Entity e = Api.Entities.Spawn();
        e.ClassName = "buff_vengeance_dmg";
        e.NextThink = Api.Clock.Time + 0.1f;
        e.Think = self =>
        {
            if (!enemy.IsFreed && enemy.DeadState == DeadFlag.No)
                Combat.Damage(enemy, owner, owner, dmg, "buff_vengeance", enemy.Origin, Vector3.Zero);
            Api.Entities.Remove(self);
        };
    }

    // =====================================================================================
    //  PlayerDamage_SplitHealthArmor — vampire heal-on-damage
    // =====================================================================================

    private bool OnSplitHealthArmor(ref GameHooks.PlayerDamageArgs args)
    {
        Entity attacker = args.Attacker;
        Entity target = args.Target;
        if (!Active(attacker, "vampire")) return false;

        float healthTake = QMath.Bound(0f, args.DamageTake, target.GetResource(ResourceType.Health));
        if (!ReferenceEquals(target, attacker) && IsPlayer(attacker)
            && target.DeadState == DeadFlag.No && !IsFrozen(target))
        {
            attacker.GiveResource(ResourceType.Health, _vampireSteal * healthTake);
        }
        return false;
    }

    // =====================================================================================
    //  PlayerRegen — medic boosts health regen
    // =====================================================================================

    // QC MUTATOR_HOOKFUNCTION(buffs, PlayerRegen) (buff/medic.qc:4-14): a medic carrier scales the regen tuning
    // factors the shared regen/rot loop reads — rot_mod = g_buffs_medic_rot (0.2, slower health rot above stable),
    // limit_mod = max_mod = g_buffs_medic_max (1.5, raised ceiling), regen_mod = g_buffs_medic_regen (1.7, faster
    // heal). The port's PlayerRegen loop (PlayerFrameLogic.Regen) consumes MaxMod/RegenMod/RotMod/LimitMod exactly
    // like QC's M_ARGV(1..4) slots, so setting them here reproduces the QC curve (no ad-hoc per-frame heal needed).
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args)
    {
        if (!Active(args.Player, "medic")) return false;
        args.RotMod = _medicRot;                          // M_ARGV(3) rot_mod
        args.LimitMod = args.MaxMod = _medicMax;          // M_ARGV(4) limit_mod = M_ARGV(1) max_mod
        args.RegenMod = _medicRegen;                      // M_ARGV(2) regen_mod
        return false;
    }

    // =====================================================================================
    //  PlayerPhysics — jump height, speed, disability slow
    // =====================================================================================

    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;

        // QC movement stats auto-reset each frame; reset the overrides first so they stay frame-local.
        player.JumpVelocityOverride = 0f;
        player.SpeedMultiplier = 1f;

        // QC JumpBuff PlayerPhysics: raise the jump velocity while held.
        if (Active(player, "jump"))
            player.JumpVelocityOverride = _jumpVelocity;

        // disability (Stunned) — slow the affected player down.
        var stunned = StatusEffectsCatalog.ByName("stunned");
        if (stunned is not null && StatusEffectsCatalog.Has(player, stunned))
            player.SpeedMultiplier *= _disabilitySpeed;

        return false;
    }

    // =====================================================================================
    //  WeaponRateFactor — disability (Stunned) slows the victim's weapon fire-rate
    // =====================================================================================

    // QC disability WeaponRateFactor: a stunned player's refire/animtime factor is multiplied by
    // g_buffs_disability_attack_time_multiplier (1.5 → slower firing).
    private bool OnWeaponRateFactor(ref MutatorHooks.WeaponRateFactorArgs args)
    {
        var stunned = StatusEffectsCatalog.ByName("stunned");
        if (stunned is not null && StatusEffectsCatalog.Has(args.Player, stunned))
            args.Factor *= _disabilityAttackTime;
        return false;
    }

    // =====================================================================================
    //  ForbidThrowCurrentWeapon — swapper buff: drop-weapon swaps you with the nearest enemy
    // =====================================================================================

    // QC swapper ForbidThrowCurrentWeapon: pressing drop-weapon while the swapper buff is held teleports you to
    // swap places with the nearest enemy in range (origin/velocity/angles exchanged), consuming the buff.
    // Returning true suppresses the normal weapon drop.
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return false;
        Entity player = args.Player;
        if (!Active(player, "swapper")) return false;

        // Find the nearest valid enemy within g_buffs_swapper_range.
        Entity? closest = null;
        float bestDist2 = _swapperRange * _swapperRange;
        foreach (Entity head in Api.Entities.FindInRadius(player.Origin, _swapperRange))
        {
            if (!IsPlayer(head) || ReferenceEquals(head, player)) continue;
            if (Teams.SameTeam(head, player)) continue;       // QC DIFF_TEAM
            if (head.DeadState != DeadFlag.No || IsFrozen(head) || head.Vehicle is not null) continue;
            float d2 = (head.Origin - player.Origin).LengthSquared();
            if (d2 < bestDist2) { bestDist2 = d2; closest = head; }
        }
        if (closest is null) return false;

        Vector3 myOrg = player.Origin, myVel = player.Velocity, myAng = player.Angles;
        Vector3 theirOrg = closest.Origin, theirVel = closest.Velocity, theirAng = closest.Angles;

        // QC: Drop_Special_Items(closest) + MUTATOR_CALLHOOK(PortalTeleport, player) — both omitted here (the
        // DropSpecialItems / PortalTeleport hooks are not ported; see todos). Core position/velocity swap:
        Api.Entities.SetOrigin(player, theirOrg);
        Api.Entities.SetOrigin(closest, myOrg);
        player.Velocity = theirVel;
        player.Angles = theirAng;
        player.OldOrigin = theirOrg;
        closest.Velocity = myVel;
        closest.Angles = myAng;
        closest.OldOrigin = myOrg;

        // QC: SND_KA_RESPAWN + EFFECT_ELECTRO_COMBO at both endpoints.
        SoundSystem.PlayOn(player, "KA_RESPAWN");
        SoundSystem.PlayOn(closest, "KA_RESPAWN");
        EffectEmitter.Emit("ELECTRO_COMBO", Center(player));
        EffectEmitter.Emit("ELECTRO_COMBO", Center(closest));

        // QC: buff_RemoveAll(player) — the swap consumes the buff.
        RemoveAllBuffs(player);
        return true;
    }

    // =====================================================================================
    //  PlayerUseKey — drop the held buff (g_buffs_drop)
    // =====================================================================================

    // QC MUTATOR_HOOKFUNCTION(buffs, PlayerUseKey, CBC_ORDER_FIRST): pressing +use with g_buffs_drop on drops the
    // held buff — notify the picker (ITEM_BUFF_DROP) + others (INFO_ITEM_BUFF_LOST), strip the buff, arm the
    // pickup-shield delay, and play SND_BUFF_LOST. Returns true to consume the press.
    private bool OnPlayerUseKey(ref MutatorHooks.PlayerUseKeyArgs args)
    {
        if (Api.Services is null || VehicleCommon.GameStopped) return false;
        if (Api.Cvars.GetFloat("g_buffs_drop") == 0f) return false;

        Entity player = args.Player;
        StatusEffectDef? held = HeldBuff(player);
        if (held is null) return false;

        NotificationSystem.Center(player, "ITEM_BUFF_DROP", held.RegistryId);
        // QC: INFO_ITEM_BUFF_LOST to others, guarded by !IS_INDEPENDENT_PLAYER.
        if (!player.IsIndependentPlayer)
            NotificationSystem.Send(NotifBroadcast.AllExcept, player, MsgType.Info,
                "ITEM_BUFF_LOST", player.NetName, held.RegistryId);

        RemoveAllBuffs(player); // sets buff_shield = time + g_buffs_pickup_delay
        player.BuffShield = Api.Clock.Time + MathF.Max(0f, _pickupDelay);
        SoundSystem.PlayOn(player, "BUFF_LOST");
        return true;
    }

    // =====================================================================================
    //  FilterItem — replace map powerups with buffs (g_buffs_replace_powerups)
    // =====================================================================================

    // QC MUTATOR_HOOKFUNCTION(buffs, FilterItem) (sv_buffs.qc:652) + buff_SpawnReplacement (507): in the g_buffs > 0
    // ("replace") mode, every powerup item on the map is swapped for a random buff. A negative g_buffs (the default
    // -1, "on but no auto-replace") leaves items alone. We spawn a fresh buff item at the powerup's origin/angles
    // (keeping its suspended/noalign state) and return true so the powerup edict is deleted by the spawn driver.
    private bool OnFilterItem(ref MutatorHooks.FilterItemArgs args)
    {
        if (Api.Services is null) return false;
        // QC: if (autocvar_g_buffs < 0) return false; — no auto-replacing in the "-1" mode.
        if (Api.Cvars.GetFloat("g_buffs") < 0f) return false;
        if (Api.Cvars.GetFloat("g_buffs_replace_powerups") == 0f) return false;

        Entity item = args.Item;
        // QC: item.itemdef.instanceOfPowerup. The port's item def exposes IsPowerup on the resolved pickup.
        if (item.Pickup?.IsPowerup != true) return false;

        // QC buff_SpawnReplacement(spawn(), item): setorigin(ent, old.origin); ent.angles = old.angles;
        // ent.noalign = ITEM_SHOULD_KEEP_POSITION(old); buff_Init(ent).
        Entity e = Api.Entities.Spawn();
        Api.Entities.SetOrigin(e, item.Origin);
        e.Angles = item.Angles;
        e.NoAlign = item.NoAlign;
        ConfigureBuffEntity(e, null); // null type -> buff_NewType random pick (QC buff_Init)
        return true; // delete the original powerup item
    }

    // =====================================================================================
    //  PlayerPreThink — flight gravity flip, per-frame buff ticks
    // =====================================================================================

    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null || VehicleCommon.GameStopped) return false;
        if (!IsPlayer(player) || player.DeadState != DeadFlag.No) return false;

        // QC FlightBuff PlayerPreThink: crouch in midair flips gravity once per press (toggle flight).
        if (Active(player, "flight"))
        {
            if (!player.ButtonCrouch)
                player.BuffFlightCrouchHeld = false;
            else if (!player.BuffFlightCrouchHeld)
            {
                player.BuffFlightCrouchHeld = true;
                player.Gravity *= -1f;
            }
        }

        // Per-frame buff ticks (QC StatusEffect m_tick equivalents). NOTE: medic regen is NOT a tick — it is the
        // PlayerRegen hook (OnPlayerRegen) scaling the shared regen/rot/limit factors, exactly like QC.
        if (Active(player, "magnet")) MagnetTick(player);
        if (Active(player, "ammo")) AmmoTick(player);

        return false;
    }

    // MagnetBuff.m_tick: pull nearby items in. QC uses boxesoverlap(player.absmin/absmax expanded by the reach,
    // item.absmin/absmax) — a box-vs-box test (so large/off-centre items are caught), with the reach selected by
    // whether the item is a buff (range_buff) or a normal item (range_item). The FindInBox is the broadphase:
    // the player's box expanded by the LARGER of the two reaches (an exact superset of both per-reach gates, so
    // the old origin-sphere + 256u item-extent pad is gone); the per-item gate below still applies the precise
    // item-vs-buff reach.
    private void MagnetTick(Entity player)
    {
        float broad = MathF.Max(_magnetRangeItem, _magnetRangeBuff);
        Vector3 pad = new(broad, broad, broad);
        // Degenerate-bounds fallback (headless: AbsMin/AbsMax never linked): center the query on the origin,
        // matching BoxesOverlapExpanded's own origin-distance fallback below (reach <= broad keeps the superset).
        Vector3 aMin = player.AbsMin, aMax = player.AbsMax;
        if (aMin == aMax) { aMin = player.Origin; aMax = player.Origin; }
        Api.Entities.FindInBox(aMin - pad, aMax + pad, _magnetScratch);
        foreach (Entity it in _magnetScratch)
        {
            if ((it.Flags & EntFlags.Item) == 0 || it.IsFreed || ReferenceEquals(it, player)) continue;
            float reach = it.ClassName == "item_buff" ? _magnetRangeBuff : _magnetRangeItem;
            if (!BoxesOverlapExpanded(player, it, reach)) continue;
            it.Touch?.Invoke(it, player);
        }
    }

    /// <summary>Scratch buffer reused for the per-tick <see cref="MagnetTick"/> item scan (replaces the
    /// per-call list the enumerator overload materialized). Iterated as a SNAPSHOT: <c>it.Touch</c> (item
    /// pickup) can free/relink entities mid-loop, which mutates the GRID but not this buffer — and no pickup
    /// path re-enters MagnetTick (it runs only from the per-player buff tick), so one shared static is safe;
    /// freed entries are skipped by the <c>it.IsFreed</c> check above.</summary>
    private static readonly List<Entity> _magnetScratch = new();

    // QC boxesoverlap(a.absmin - r, a.absmax + r, b.absmin, b.absmax). Falls back to an origin distance test when
    // the bounds are degenerate (headless, no physics link maintaining AbsMin/AbsMax).
    private static bool BoxesOverlapExpanded(Entity a, Entity b, float r)
    {
        Vector3 e = new(r, r, r);
        Vector3 aMin = a.AbsMin - e, aMax = a.AbsMax + e;
        Vector3 bMin = b.AbsMin, bMax = b.AbsMax;
        if (aMin == aMax && bMin == bMax)
            return Vector3.Distance(a.Origin, b.Origin) <= r;
        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    // AmmoBuff.m_tick: the unlimited-ammo flag (set on apply) covers the resource pools; QC's per-tick work only
    // tops up each reloadable slot's clip so reload weapons (rifle / OK machinegun) never need reloading.
    private static void AmmoTick(Entity player)
    {
        if (!IsPlayer(player)) return;
        for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; slot++)
            FillSlotClip(player.WeaponState(new WeaponSlot(slot)));
    }

    // QC: actor.(weaponentity).clip_load = actor.(weaponentity).(weapon_load[switchweapon]) = clip_size — fill the
    // slot's live magazine AND its persistent per-weapon store for the weapon currently selected in that slot.
    private static void FillSlotClip(WeaponSlotState st)
    {
        if (st.ClipSize == 0) return;
        st.ClipLoad = st.ClipSize;
        if (st.SwitchWeaponId >= 0)
            Weapon.SetWeaponLoad(st, st.SwitchWeaponId, st.ClipSize);
    }

    // QC AmmoBuff.m_remove: restore each slot's pre-buff clip load (saved at apply), then clear the saved value.
    private static void RestoreAmmoClips(Entity actor)
    {
        for (int slot = 0; slot < MutatorConstants.MaxWeaponSlots; slot++)
        {
            var st = actor.WeaponState(new WeaponSlot(slot));
            if (st.BuffAmmoPrevClipLoad != 0)
            {
                st.ClipLoad = st.BuffAmmoPrevClipLoad;
                st.BuffAmmoPrevClipLoad = 0;
            }
        }
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    private const int ItUnlimitedAmmo = 1 << 0; // QC IT_UNLIMITED_AMMO = BIT(0) (common/items/item.qh)

    private static int BitSet(int bits, int mask, bool set) => set ? bits | mask : bits & ~mask;

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // QC: start_items & IT_UNLIMITED_AMMO. The dominant (gametype-independent) source of that start flag is
    // g_use_ammunition 0 (readplayerstartcvars). Read it default-1-aware so an unset cvar means "ammo used".
    private static bool StartItemsHasUnlimitedAmmo() => !CvarBool("g_use_ammunition", true);

    // Read a boolean cvar default-aware: an unset cvar uses the fallback (distinguishes "unset" from explicit 0,
    // unlike the float Cvar helper which treats 0 as "use the fallback").
    private static bool CvarBool(string name, bool fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}
