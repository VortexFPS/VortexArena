// Port of qcsrc/common/mutators/mutator/nades/sv_nades.qc — the REGISTER_MUTATOR(nades, …) wiring + its
// MUTATOR_HOOKFUNCTIONs (PlayerSpawn 801, PlayerPreThink 726, PlayerDies 836, MakePlayerObserver 922,
// ClientDisconnect 927, reset_map_global 932, PutClientInServer 470, Damage_Calculate 876).
//
// Offhand throw path: QC drives OFFHAND_NADE.offhand_think from PlayerPreThink (the +hook release-throw) and
// nades_CheckThrow from ForbidThrowCurrentWeapon (the weapon_drop second-press). The port's
// ForbidThrowCurrentWeapon chain has no Call site (only Instagib/Melee subscribe), and W_ThrowWeapon is
// unowned, so — exactly as the spec directs — the throw is driven entirely through the OFFHAND path:
// PlayerSpawn assigns the nade offhand, PlayerPreThink runs offhand_think each frame gated by the held
// alt-button (Entity.NadeAltButton / Entity.OffhandFirePressed), and tracks the held nade + accrues bonus.
//
// The per-type detonation is the boom-dispatch seam (NadeBoom / NadeBoomRegistry); part B's boom files plug
// in there. This file plus the Nade core (registry/throw/projectile/bonus) is everything that makes a nade
// thrown, charged, tracked, picked up, and detonated — no boom file edits needed.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades;

/// <summary>
/// The Nades mutator — gives every player offhand grenades (the g_nades arsenal). Enabled by the
/// <c>g_nades</c> cvar (default OFF). On enable it builds the nade catalog + the boom-dispatch registry.
/// </summary>
[Mutator]
public sealed class NadesMutator : MutatorBase
{
    public NadesMutator() => NetName = "nades";

    // QC REGISTER_MUTATOR(nades, autocvar_g_nades).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_nades") != 0f;

    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onDies;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.VehicleEnterArgs>? _onVehicleEnter;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.MakePlayerObserverArgs>? _onObserver;
    private HookHandler<MutatorHooks.ClientDisconnectArgs>? _onDisconnect;

    public override void Hook()
    {
        // Build the nade catalog + boom dispatch registry on enable (self-contained — no GameInit edit).
        NadeRegistry.RegisterAll();
        NadeBoomRegistry.EnsureScanned();

        _onSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        _onDies ??= OnPlayerDies;
        _onDamageCalc ??= OnDamageCalculate;
        _onVehicleEnter ??= OnVehicleEnter;
        _onForbidThrow ??= OnForbidThrowCurrentWeapon;
        _onObserver ??= OnMakePlayerObserver;
        _onDisconnect ??= OnClientDisconnect;

        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
        // QC PlayerDies hook is CBC_ORDER_LAST (it tosses the held nade + awards bonus after other handlers).
        MutatorHooks.PlayerDies.Add(_onDies, HookOrder.Last);
        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        // QC VehicleEnter (sv_nades.qc:667): boarding a vehicle tosses the held nade.
        MutatorHooks.VehicleEnter.Add(_onVehicleEnter);
        // QC ForbidThrowCurrentWeapon CBC_ORDER_LAST (sv_nades.qc:715): the weapon_drop second-press throw path.
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow, HookOrder.Last);
        // QC MakePlayerObserver (922) / ClientDisconnect (927): nades_RemovePlayer — drop a held nade, wipe the
        // banked bonus, and delete the spawn-loc marker so none of it leaks across observer/disconnect.
        MutatorHooks.MakePlayerObserver.Add(_onObserver);
        MutatorHooks.ClientDisconnect.Add(_onDisconnect);
    }

    // =====================================================================================
    //  BuildMutatorsString (sv_nades.qc:954) / BuildMutatorsPrettyString (sv_nades.qc:949)
    // =====================================================================================
    // QC appends ":Nades" to the machine token (server browser / g_mutatorblacklist) and ", Nades" to the
    // human-readable mutators list (scoreboard / map-info modifications line). Other mutators (BloodlossMutator
    // etc.) override these same virtuals.
    public override string BuildMutatorsString(string s) => s + ":Nades";
    public override string BuildMutatorsPrettyString(string s) => s + ", Nades";

    public override void Unhook()
    {
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
        if (_onDies is not null) MutatorHooks.PlayerDies.Remove(_onDies);
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onVehicleEnter is not null) MutatorHooks.VehicleEnter.Remove(_onVehicleEnter);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onObserver is not null) MutatorHooks.MakePlayerObserver.Remove(_onObserver);
        if (_onDisconnect is not null) MutatorHooks.ClientDisconnect.Remove(_onDisconnect);
    }

    // =====================================================================================
    //  ForbidThrowCurrentWeapon (sv_nades.qc:715, CBC_ORDER_LAST) — the weapon_drop second-press throw
    // =====================================================================================
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;

        // QC: if (offhand != OFFHAND_NADE || (WEAPONS & WEPSET(HOOK)) || g_nades_override_dropweapon)
        //         { nades_CheckThrow(player); return true; }
        // When the nade is NOT the player's offhand (the offhand is busy being the hook/blaster), or the player
        // carries the HOOK weapon, or the override cvar is set, the weapon-drop bind primes/throws the nade and
        // the actual weapon-drop is suppressed (return true). Otherwise the nade rides the +hook offhand path and
        // the weapon-drop is left to its normal behaviour.
        bool offhandIsNade = player.OffhandWeapon == "nade";
        bool hasHookWeapon = Weapons.ByName("hook") is { } hookWep && Inventory.HasWeapon(player, hookWep);
        if (!offhandIsNade || hasHookWeapon || Cvar("g_nades_override_dropweapon", 0f) != 0f)
        {
            NadeThrow.CheckThrow(player);
            return true;
        }
        return false;
    }

    // =====================================================================================
    //  PlayerSpawn (sv_nades.qc:801)
    // =====================================================================================
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;
        float now = Api.Clock.Time;

        // QC: nade_refire = spawnshield end (if shielded) else now; +refire unless g_nades_onspawn.
        var ss = StatusEffectsCatalog.SpawnShield;
        if (ss is not null && StatusEffectsCatalog.Has(player, ss))
            player.NadeRefire = player.SpawnShieldExpire; // the spawn-shield end time
        else
            player.NadeRefire = now;

        if (Cvar("g_nades_onspawn", 1f) == 0f)
            player.NadeRefire += Cvar("g_nades_nade_refire", 6f);

        player.NadeTimer = 0f;

        // QC: if (!player.offhand) player.offhand = OFFHAND_NADE; — give the nade offhand.
        if (string.IsNullOrEmpty(player.OffhandWeapon))
            player.OffhandWeapon = "nade";

        // small-nade option (STAT(NADES_SMALL)).
        player.NadesSmall = Cvar("g_nades_nade_small", 0f) != 0f;

        // QC spawn-nade relocate: if the player has a spawn-nade marker, move them there + set spawn health.
        if (player.NadeSpawnLoc is not null)
        {
            Api.Entities.SetOrigin(player, player.NadeSpawnLoc.Origin);
            --player.NadeSpawnLoc.NadeSpawnCount;

            // nade_spawn_SetSpawnHealth: override respawn health if configured.
            float spawnHp = Cvar("g_nades_spawn_health_respawn", 0f);
            if (spawnHp > 0f)
                player.SetResource(ResourceType.Health, spawnHp);

            if (player.NadeSpawnLoc.NadeSpawnCount <= 0)
            {
                Api.Entities.Remove(player.NadeSpawnLoc);
                player.NadeSpawnLoc = null;
            }
        }
        return false;
    }

    // =====================================================================================
    //  PlayerPreThink (sv_nades.qc:726)
    // =====================================================================================
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;
        if ((player.Flags & EntFlags.Client) == 0) return false;

        // QC: drive the offhand think while the alt-button is held (the +hook release path). The input layer
        // flags the held button via Entity.OffhandFirePressed / Entity.NadeAltButton.
        bool keyHeld = player.NadeAltButton || player.OffhandFirePressed;
        if (player.OffhandWeapon == "nade")
            NadeThrow.OffhandThink(player, keyHeld);

        // QC: track the held nade — update the HUD charge ring + keep it floating in front of the player, and
        // auto-toss it just before its fuse runs out.
        Entity? heldNade = player.Nade;
        if (heldNade is not null)
        {
            float lifetime = heldNade.NadeLifetime > 0f ? heldNade.NadeLifetime : 1f;
            player.NadeTimer = Bound(0f, (Api.Clock.Time - heldNade.NadeTimePrimed) / lifetime, 1f);
            // QC keeps the held nade at player.origin + view_ofs + v_forward*8 + v_right*-8; the position is
            // render-only here, but we DO sync its velocity so a thrown-at-the-last-moment nade inherits motion.
            heldNade.Velocity = player.Velocity;

            // QC: if (time + 0.1 >= held_nade.wait) toss_nade(player, false, '0 0 0', time + 0.05).
            if (Api.Clock.Time + 0.1f >= heldNade.NadeWait)
                NadeProjectile.Toss(player, false, System.Numerics.Vector3.Zero, Api.Clock.Time + 0.05f);
        }

        // QC: bonus type selection + per-second bonus accrual (when g_nades_bonus is on).
        if (player.DeadState == DeadFlag.No)
        {
            if (Cvar("g_nades_bonus", 0f) != 0f && Cvar("g_nades", 0f) != 0f)
            {
                // QC: STAT(NADE_BONUS_TYPE) = the configured bonus nade type (client-select deferred —
                // headless has no per-client cvar, so use the server g_nades_bonus_type).
                player.NadeBonusType = NadeRegistry.FromString(CvarStr("g_nades_bonus_type", "2")).Id;
                player.PokenadeType = CvarStr("g_nades_pokenade_monster_type", "zombie");

                float scoreMax = Cvar("g_nades_bonus_score_max", 120f);
                // QC sv_nades.qc:773-778: a flag/ball carrier (GameRules_scoring_is_vip) accrues at the
                // flagcarrier rate instead of the (decaying) base rate; a Key Hunt carrier accrues at the
                // flagcarrier rate scaled by the number of keys held (which overrides the vip branch).
                float timeScore = GametypeEntities.ScoringIsVip(player)
                    ? CvarRaw("g_nades_bonus_score_time_flagcarrier", 2f)
                    : CvarRaw("g_nades_bonus_score_time", -1f); // base rate can be negative (decay)
                int keyCount = GametypeEntities.KeyHuntKeyCount(player);
                if (keyCount != 0)
                    timeScore = CvarRaw("g_nades_bonus_score_time_flagcarrier", 2f) * keyCount;
                if (player.NadeBonusScore >= 0f && scoreMax != 0f)
                    NadeBonus.GiveBonus(player, timeScore / scoreMax);
            }
            else
            {
                player.NadeBonus = 0;
                player.NadeBonusScore = 0f;
            }

            // QC: nade_veil_Apply(player) — restore alpha when a veil orb's effect lapses.
            ApplyVeilLapse(player);
        }
        return false;
    }

    // =====================================================================================
    //  PlayerDies (sv_nades.qc:836, CBC_ORDER_LAST)
    // =====================================================================================
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        Entity victim = args.Target;
        Entity? attacker = args.Attacker;
        if (Api.Services is null) return false;

        // QC: toss the held nade on death (unless a freezetag revive-nade is mid-flight on a frozen target).
        if (victim.Nade is not null
            && (!IsFrozen(victim) || Cvar("g_freezetag_revive_nade", 0f) == 0f))
        {
            float boomAt = MathF.Max(victim.Nade.NadeWait, Api.Clock.Time + 0.05f);
            NadeProjectile.Toss(victim, true, new System.Numerics.Vector3(0f, 0f, 100f), boomAt);
        }

        // QC: the killcount/spree bonus award (+ teamkill/suicide wipe), then wipe the victim's bonus.
        NadeBonus.OnPlayerDies(attacker, victim);
        return false;
    }

    // =====================================================================================
    //  VehicleEnter (sv_nades.qc:667)
    // =====================================================================================
    private bool OnVehicleEnter(ref MutatorHooks.VehicleEnterArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;

        // QC: if (player.nade) toss_nade(player, true, '0 0 100', max(player.nade.wait, time + 0.05));
        // A player can't keep a primed nade while boarding a vehicle — toss it upward (same args as the
        // death-toss so the throw is owner-credited and detonates on its existing fuse).
        if (player.Nade is not null)
        {
            float boomAt = MathF.Max(player.Nade.NadeWait, Api.Clock.Time + 0.05f);
            NadeProjectile.Toss(player, true, new System.Numerics.Vector3(0f, 0f, 100f), boomAt);
        }
        return false;
    }

    // =====================================================================================
    //  MakePlayerObserver (sv_nades.qc:922) / ClientDisconnect (sv_nades.qc:927) — nades_RemovePlayer
    // =====================================================================================
    private bool OnMakePlayerObserver(ref MutatorHooks.MakePlayerObserverArgs args)
    {
        RemovePlayer(args.Player);
        return false;
    }

    private bool OnClientDisconnect(ref MutatorHooks.ClientDisconnectArgs args)
    {
        RemovePlayer(args.Player);
        return false;
    }

    /// <summary>
    /// Port of <c>nades_RemovePlayer(entity this)</c> (sv_nades.qc:914): clear the held/fake nade + HUD ring
    /// (nades_Clear), wipe the banked bonus (nades_RemoveBonus), and delete any planted spawn-loc marker — so a
    /// player who goes observer or disconnects leaks none of it.
    /// </summary>
    private static void RemovePlayer(Entity player)
    {
        if (Api.Services is null) return;
        NadeThrow.Clear(player);
        NadeBonus.RemoveBonus(player);
        if (player.NadeSpawnLoc is not null)
        {
            Api.Entities.Remove(player.NadeSpawnLoc);
            player.NadeSpawnLoc = null;
        }
    }

    // =====================================================================================
    //  reset_map_global (sv_nades.qc:932) + PutClientInServer (sv_nades.qc:470) — host seams
    // =====================================================================================
    // The port has no MutatorHooks chain for reset_map_global / PutClientInServer, so the host (GameWorld /
    // ClientManager) drives these directly, gated on the nades mutator being active. Both are no-ops when
    // g_nades is off, matching the REGISTER_MUTATOR(nades, autocvar_g_nades) gate on the QC hooks.

    /// <summary>True when the nades mutator's hooks are active (QC autocvar_g_nades gate).</summary>
    public static bool Active => Api.Services is not null && Api.Cvars.GetFloat("g_nades") != 0f;

    /// <summary>
    /// Port of <c>MUTATOR_HOOKFUNCTION(nades, reset_map_global)</c> (sv_nades.qc:932):
    /// <c>FOREACH_CLIENT(IS_PLAYER(it), nades_RemovePlayer(it))</c>. On a round/map reset, every player drops
    /// their held nade, banked bonus, and spawn-loc marker so none of it leaks across the reset. Called by the
    /// host from <c>GameWorld.ResetMap</c> (the port's reset_map seam) before the per-client respawn.
    /// </summary>
    public static void ResetMapGlobal(System.Collections.Generic.IReadOnlyList<Entity> players)
    {
        if (!Active) return;
        for (int i = 0; i < players.Count; i++)
        {
            Entity p = players[i];
            if ((p.Flags & EntFlags.Client) != 0)
                RemovePlayer(p);
        }
    }

    /// <summary>
    /// Port of <c>MUTATOR_HOOKFUNCTION(nades, PutClientInServer)</c> (sv_nades.qc:470):
    /// <c>nades_RemoveBonus(player)</c>. A (re)spawning player's banked bonus + accrual is wiped, so a bonus
    /// banked in a previous life doesn't carry into the new one. Called by the host from the respawn path.
    /// </summary>
    public static void OnPutClientInServer(Entity player)
    {
        if (!Active) return;
        NadeBonus.RemoveBonus(player);
    }

    /// <summary>
    /// Port of <c>MUTATOR_HOOKFUNCTION(nades, SpectateCopy)</c> (sv_nades.qc:937): mirror the spectatee's nade
    /// HUD/bonus stats (NADE_TIMER / NADE_BONUS_TYPE / pokenade_type / NADE_BONUS / NADE_BONUS_SCORE) onto a
    /// following observer so its nade charge ring + bonus readout track the watched player. Called by the host
    /// from <c>ClientManager.SpectateCopy</c>.
    /// </summary>
    public static void OnSpectateCopy(Entity spectator, Entity spectatee)
    {
        if (!Active) return;
        spectator.NadeTimer = spectatee.NadeTimer;
        spectator.NadeBonusType = spectatee.NadeBonusType;
        spectator.PokenadeType = spectatee.PokenadeType;
        spectator.NadeBonus = spectatee.NadeBonus;
        spectator.NadeBonusScore = spectatee.NadeBonusScore;
    }

    // =====================================================================================
    //  Damage_Calculate (sv_nades.qc:876) — freezetag revive-nade
    // =====================================================================================
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        // QC: a frozen player hit by their OWN nade (DEATH_NADE) within 0.1s of the toss is unfrozen instead
        // of damaged. FreezeTag's unfreeze path is owned by the gametype task; here we honour the no-damage +
        // no-force suppression so the revive-nade doesn't hurt the reviving (frozen) player. The actual
        // unfreeze + revive-health is a documented cross-task seam (FreezeTag owns freezetag_Unfreeze).
        if (Cvar("g_freezetag_revive_nade", 0f) == 0f) return false;
        Entity? attacker = args.Attacker;
        Entity target = args.Target;
        if (attacker is null || !ReferenceEquals(attacker, target)) return false;
        if (!IsFrozen(target)) return false;
        if (DeathIsNade(args.DeathType) && args.Inflictor is { } infl
            && Api.Clock.Time - infl.NadeTossTime <= 0.1f)
        {
            args.Damage = 0f;
            args.Force = System.Numerics.Vector3.Zero;
        }
        return false;
    }

    // =====================================================================================
    //  veil-lapse restore (sv_nades.qc nade_veil_Apply) — kept here so the veil boom file stays self-contained
    // =====================================================================================
    private static void ApplyVeilLapse(Entity player)
    {
        // QC nade_veil_Apply: when the veil time has lapsed, restore the player's previous alpha.
        if (player.NadeVeilTime != 0f && player.NadeVeilTime <= Api.Clock.Time)
        {
            player.NadeVeilTime = 0f;
            player.Alpha = player.NadeVeilPrevAlpha;
        }
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    /// <summary>QC DEATH_NADE — the nade deathtype tag (the throw + freezetag revive path use it).</summary>
    private static bool DeathIsNade(string? deathType) => Damage.DeathTypes.BaseOf(deathType) == NadeDeathTypes.Nade;

    private static bool IsFrozen(Entity e)
    {
        var fz = StatusEffectsCatalog.Frozen;
        return fz is not null && StatusEffectsCatalog.Has(e, fz);
    }

    private static float Bound(float lo, float v, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    // For cvars that can legitimately be negative (bonus_score_time = -1 decay): read raw, default only when unset.
    private static float CvarRaw(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    private static string CvarStr(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}
