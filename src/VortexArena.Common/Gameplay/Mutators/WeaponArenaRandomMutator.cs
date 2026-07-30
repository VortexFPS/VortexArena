// Port of common/mutators/mutator/weaponarena_random/sv_weaponarena_random.qc
// (+ W_RandomWeapons from common/weapons/all.qc:178)

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Random Weapon Arena mutator — port of
/// common/mutators/mutator/weaponarena_random/sv_weaponarena_random.qc. A variant of the weapon arena
/// (<c>g_weaponarena</c>): instead of spawning with the WHOLE configured arena set, each player spawns with a
/// RANDOM subset of <c>g_weaponarena_random</c> weapons drawn from it, and after every frag the weapon that got
/// the kill is swapped out for another random weapon — so the loadout keeps churning. Active only while a weapon
/// arena is configured (QC reads <c>g_weaponarena_random</c> off the <c>g_weaponarena_random</c> cvar in
/// SetStartItems, and zeroes it when <c>g_weaponarena</c> is off).
///
/// Ported faithfully:
///  - SetStartItems: latch <c>g_weaponarena_random</c> (gated by <c>g_weaponarena</c>) and
///    <c>g_weaponarena_random_with_blaster</c>;
///  - PlayerSpawn: replace the player's owned set with <see cref="RandomWeapons"/> of N drawn from it (keeping the
///    blaster aside when with_blaster is set, then re-adding it);
///  - GiveFragsForKill: identify the culprit weapon (the deathtype's weapon, or the attacker's current weapon),
///    then — unless it's the kept blaster — pick one new random weapon NOT already owned and NOT the culprit
///    (drawn from the warmup or live arena set per <c>warmup_stage</c>), add it, drop the culprit, and finally
///    switch the attacker to their best weapon if the held one is gone;
///  - <see cref="RandomWeapons"/>: the ported <c>W_RandomWeapons(e, remaining, n)</c> reservoir pick (without
///    replacement) over a candidate <see cref="WepSet"/>.
///
/// Per-spawn the owned set is rewritten directly on <see cref="Entity.OwnedWeaponSet"/> (the authority
/// <see cref="Inventory"/> reads), mirroring QC's <c>STAT(WEAPONS, player) = ...</c>.
/// </summary>
[Mutator]
public sealed class WeaponArenaRandomMutator : MutatorBase
{
    /// <summary>QC g_weaponarena_random — number of random weapons per spawn (0 = inactive).</summary>
    public int RandomCount;

    /// <summary>QC g_weaponarena_random_with_blaster — always keep the blaster on top of the random set.</summary>
    public bool WithBlaster;

    public WeaponArenaRandomMutator() => NetName = "weaponarena_random";

    // QC: REGISTER_MUTATOR(weaponarena_random, true) — always registered; the SetStartItems hook decides whether
    // it does anything (g_weaponarena_random is only non-zero when a weapon arena is configured). The port gates
    // IsEnabled on g_weaponarena_random being set so the hooks aren't subscribed when it's a plain weapon arena.
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_weaponarena") != 0f
        && Api.Cvars.GetFloat("g_weaponarena_random") != 0f;

    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;
    private HookHandler<MutatorHooks.GiveFragsForKillArgs>? _onGiveFrags;

    public override void Hook()
    {
        _onSetStartItems ??= OnSetStartItems;
        _onPlayerSpawn ??= OnPlayerSpawn;
        _onGiveFrags ??= OnGiveFragsForKill;

        MutatorHooks.SetStartItems.Add(_onSetStartItems);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
        MutatorHooks.GiveFragsForKill.Add(_onGiveFrags);

        ReadCvars();
    }

    public override void Unhook()
    {
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
        if (_onGiveFrags is not null) MutatorHooks.GiveFragsForKill.Remove(_onGiveFrags);
    }

    private void ReadCvars()
    {
        if (Api.Services is null) return;
        // QC SetStartItems: g_weaponarena_random = g_weaponarena ? cvar("g_weaponarena_random") : 0.
        RandomCount = Api.Cvars.GetFloat("g_weaponarena") != 0f
            ? (int)Api.Cvars.GetFloat("g_weaponarena_random") : 0;
        WithBlaster = Api.Cvars.GetFloat("g_weaponarena_random_with_blaster") != 0f;
    }

    // MUTATOR_HOOKFUNCTION(weaponarena_random, SetStartItems)
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        // QC latches the two cvars here; the loadout itself is left to the weapon-arena config (start_weapons is
        // the arena set), and PlayerSpawn does the per-spawn randomization. Just refresh our cached cvars.
        ReadCvars();
        return false;
    }

    // MUTATOR_HOOKFUNCTION(weaponarena_random, PlayerSpawn)
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        if (RandomCount <= 0) return false; // QC: if(!g_weaponarena_random) return;
        Entity player = args.Player;

        Weapon? blaster = Weapons.ByName("blaster");

        // QC: if(with_blaster) STAT(WEAPONS) &= ~WEPSET(BLASTER);
        WepSet have = player.OwnedWeaponSet;
        if (WithBlaster && blaster is not null) have.Remove(blaster);

        // QC: STAT(WEAPONS) = W_RandomWeapons(player, STAT(WEAPONS), g_weaponarena_random);
        WepSet chosen = RandomWeapons(have, RandomCount);

        // QC: if(with_blaster) STAT(WEAPONS) |= WEPSET(BLASTER);
        if (WithBlaster && blaster is not null) chosen.Add(blaster);

        player.OwnedWeaponSet = chosen;
        Inventory.SwitchToBest(player);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(weaponarena_random, GiveFragsForKill)
    private bool OnGiveFragsForKill(ref MutatorHooks.GiveFragsForKillArgs args)
    {
        if (RandomCount <= 0) return false; // QC: if(!g_weaponarena_random) return;
        Entity attacker = args.Attacker;
        Entity targ = args.Target;
        if (ReferenceEquals(targ, attacker)) return false; // QC: not for suicides

        Weapon? blaster = Weapons.ByName("blaster");

        // QC: Weapon culprit = DEATH_WEAPONOF(deathtype); if(!culprit) culprit = wep_ent.m_weapon;
        //     else if(!(STAT(WEAPONS, attacker) & culprit.m_wepset)) culprit = wep_ent.m_weapon;
        // The scoring path carries no weapon entity (M_ARGV(4)=null), so the fallback is the attacker's current
        // weapon (QC wep_ent.m_weapon — the held weapon).
        Weapon? culprit = Weapons.ByName(DeathTypes.WeaponNetNameOf(args.DeathType));
        Weapon? held = Inventory.CurrentWeapon(attacker);
        if (culprit is null) culprit = held;
        else if (!attacker.OwnedWeaponSet.Has(culprit)) culprit = held;

        // QC: if(with_blaster && culprit == WEP_BLASTER) { /* no exchange */ } else { ... }
        if (!(WithBlaster && blaster is not null && culprit == blaster))
        {
            // QC builds a scratch set of the arena weapons, removes the attacker's owned set and the culprit,
            // picks one at random among the remaining, and ORs it into the attacker (dropping the culprit). QC
            // chooses the pool by match phase: scratch = warmup_stage ? WARMUP_START_WEAPONS : start_weapons.
            // Both are the arena weapon set (world.qc:2130 sets warmup_start_weapons = start_weapons, and the
            // arena setup at world.qc:1948-1979 makes them equal), so the port resolves each to ArenaWepSet();
            // we still branch on WarmupStage to stay faithful to the QC control flow (and to the allguns warmup
            // case below, which differs only outside an arena — where this mutator never runs).
            WepSet pool = NotificationSystem.WarmupStage ? WarmupArenaWepSet() : ArenaWepSet();
            pool = Difference(pool, attacker.OwnedWeaponSet);
            if (culprit is not null) pool.Remove(culprit);

            WepSet pick = RandomWeapons(pool, 1);
            if (!pick.IsEmpty)
            {
                foreach (Weapon w in pick.Weapons()) attacker.OwnedWeaponSet.Add(w);
                if (culprit is not null) attacker.OwnedWeaponSet.Remove(culprit);
            }
        }

        // QC: if(!(STAT(WEAPONS, attacker) & WepSet_FromWeapon(wep_ent.m_weapon)))
        //         W_SwitchWeapon_Force(attacker, w_getbestweapon(attacker, weaponentity), weaponentity);
        if (held is null || !attacker.OwnedWeaponSet.Has(held))
            Inventory.SwitchToBest(attacker);
        return false;
    }

    /// <summary>
    /// The arena weapon set (QC <c>start_weapons</c>): the weapons the random subset is drawn from. Computed from
    /// the SetStartItems loadout (the arena config writes start_weapons there), resolving each NetName to a Weapon.
    /// </summary>
    private static WepSet ArenaWepSet()
    {
        var set = new WepSet();
        StartLoadout loadout = SpawnSystem.ComputeStartItems();
        foreach (string n in loadout.Weapons)
            if (Weapons.ByName(n) is { } w) set.Add(w);
        return set;
    }

    /// <summary>
    /// QC <c>WARMUP_START_WEAPONS</c> (server/world.qh:101) — the swap pool while <c>warmup_stage</c> is set.
    /// With a weapon arena in force (the only state this mutator runs in), QC keeps
    /// <c>warmup_start_weapons = start_weapons</c> (world.qc:2130 + the arena setup world.qc:1948-1979), so the
    /// warmup pool equals the live arena set: resolve it the same way as <see cref="ArenaWepSet"/>. The
    /// allguns-DM warmup variant (<c>g_warmup_allguns</c> → all non-hidden weapons) is honoured here for
    /// completeness, but it only applies outside an arena, where weaponarena_random is inactive.
    /// </summary>
    private static WepSet WarmupArenaWepSet()
    {
        WepSet arena = ArenaWepSet();
        if (!arena.IsEmpty) return arena;

        // No arena set (degenerate: only reachable if an arena is configured but expands to nothing). Mirror the
        // warmup loadout's allguns expansion — every non-hidden, non-mutator-blocked weapon — as QC does for
        // WARMUP_START_WEAPONS = weapons_all() (world.qc:1953); else fall back to the arena/start set.
        if (Api.Cvars.GetFloat("g_warmup_allguns") != 0f)
        {
            var all = new WepSet();
            foreach (Weapon w in Weapons.All)
            {
                if ((w.SpawnFlags & WeaponFlags.Hidden) != 0) continue;
                if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
                all.Add(w);
            }
            return all;
        }
        return arena;
    }

    /// <summary>QC <c>a &amp; ~b</c> over weapon sets: the weapons in <paramref name="a"/> not in <paramref name="b"/>.</summary>
    private static WepSet Difference(WepSet a, WepSet b)
    {
        foreach (Weapon w in b.Weapons()) a.Remove(w);
        return a;
    }

    /// <summary>
    /// Port of <c>W_RandomWeapons(e, remaining, n)</c> (common/weapons/all.qc:178): pick <paramref name="n"/>
    /// weapons at random (without replacement) from the <paramref name="remaining"/> set and return the chosen
    /// subset. Each draw is a uniform reservoir pick over the still-eligible weapons (QC RandomSelection_AddEnt
    /// with weight 1); deterministic via <see cref="Prandom"/> (ADR-0010 — no nondeterministic random()).
    /// </summary>
    public static WepSet RandomWeapons(WepSet remaining, int n)
    {
        var result = new WepSet();
        for (int j = 0; j < n; j++)
        {
            // Gather the candidates still in `remaining` and pick one uniformly (weights all 1).
            Weapon? chosen = null;
            int count = 0;
            foreach (Weapon w in remaining.Weapons())
            {
                count++;
                // Reservoir: keep candidate #count with probability 1/count → uniform overall.
                if (Prandom.RangeInt(0, count) == 0) chosen = w;
            }
            if (chosen is null) break; // QC: chosen_ent == NULL → nothing left
            result.Add(chosen);
            remaining.Remove(chosen);
        }
        return result;
    }
}
