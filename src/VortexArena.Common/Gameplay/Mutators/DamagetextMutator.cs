// Port of common/mutators/mutator/damagetext/sv_damagetext.qc + damagetext.qh (the SVQC producer).

using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Damage-text wire constants — port of common/mutators/mutator/damagetext/damagetext.qh. The flag bits and
/// the precision multiplier the server attaches to each floating-damage-number event.
/// </summary>
public static class DamageTextWire
{
    /// <summary>QC DAMAGETEXT_PRECISION_MULTIPLIER — fixed-point scale applied to the wire health/armor/potential.</summary>
    public const int PrecisionMultiplier = 128;
    /// <summary>QC DAMAGETEXT_SHORT_LIMIT — at/above this the value needs an int24 instead of a short (the BIG_* flags).</summary>
    public const float ShortLimit = 256f;

    public const int FlagSameTeam        = 1 << 0; // DTFLAG_SAMETEAM
    public const int FlagBigHealth       = 1 << 1; // DTFLAG_BIG_HEALTH
    public const int FlagBigArmor        = 1 << 2; // DTFLAG_BIG_ARMOR
    public const int FlagBigPotential    = 1 << 3; // DTFLAG_BIG_POTENTIAL
    public const int FlagNoArmor         = 1 << 4; // DTFLAG_NO_ARMOR
    public const int FlagNoPotential     = 1 << 5; // DTFLAG_NO_POTENTIAL
    public const int FlagStopAccumulation = 1 << 6; // DTFLAG_STOP_ACCUMULATION
}

/// <summary>
/// One floating-damage-number event ready to deliver to the client (the C# successor to the
/// <c>net_damagetext</c> temp entity QC writes in <c>write_damagetext</c>). Carries the hit target, the
/// deathtype, the computed flags, and the (un-multiplied) health/armor/potential damage. The client
/// <c>DamageTextLayer</c> consumes these (DrainPending) and draws the number at the target's location.
/// </summary>
public readonly struct DamageTextEvent
{
    public readonly Entity Attacker;      // QC realowner
    public readonly Entity Target;        // QC enemy / hit
    public readonly int Flags;            // QC dent_net_flags (DTFLAG_*)
    public readonly string DeathType;     // QC dent_net_deathtype (string tag in this port)
    public readonly float Health;         // QC dent_net_health (un-multiplied)
    public readonly float Armor;          // QC dent_net_armor
    public readonly float Potential;      // QC dent_net_potential

    public DamageTextEvent(Entity attacker, Entity target, int flags, string deathType,
        float health, float armor, float potential)
    {
        Attacker = attacker; Target = target; Flags = flags; DeathType = deathType;
        Health = health; Armor = armor; Potential = potential;
    }
}

/// <summary>
/// The Damage Text mutator (server producer) — port of the SVQC half of
/// common/mutators/mutator/damagetext/sv_damagetext.qc. On every damage tick it builds a floating-number
/// event (the actual health/armor removed + the pre-split potential damage), computes the wire flags, folds
/// same-frame repeated hits (shotgun pellets) onto the previous event, and queues it for the client
/// <c>DamageTextLayer</c>. Gated by <c>sv_damagetext</c> (xonotic-server.cfg default <b>2</b>; 0 disables).
///
/// Ported faithfully (the PlayerDamaged handler): the skips (sv_damagetext &lt;= 0; hit == attacker;
/// potential_damage == 0; instagib-vaporizer one-shot suppression), the same-frame accumulation onto the
/// previous event (when the SAME attacker+target+deathtype hit again this frame), the flag computation
/// (SAMETEAM / BIG_HEALTH|ARMOR|POTENTIAL at DAMAGETEXT_SHORT_LIMIT / NO_ARMOR when armor==0 / NO_POTENTIAL
/// when armor+health ≈ potential within 5 / STOP_ACCUMULATION on the first hit from an attacker after
/// respawn via the dent_attackers set), and the per-spawn / disconnect bitset clear. The wire encoding (the
/// PRECISION_MULTIPLIER fixed-point + int24/short selection) lives in the client layer; the server passes the
/// un-multiplied amounts in <see cref="DamageTextEvent"/>.
///
/// Visibility tiers (parity §11): QC's <c>write_damagetext</c> filters by sv_damagetext 1/2/3
/// (spectators / +attacker / all); in this host port that reduces to "show to the local attacker"
/// (the default tier 2). The queued events are the attacker-credited ones; the client gate applies.
///
/// ClientDisconnect dent_attackers clear (sv_damagetext.qc:138-148): QC's
/// MUTATOR_HOOKFUNCTION(damagetext, ClientDisconnect) walks every player and clears the leaving
/// client's bit from their <c>dent_attackers</c> set, so a freed/reused edict slot can't mis-fire the
/// STOP_ACCUMULATION first-hit gate. This port subscribes <see cref="MutatorHooks.ClientDisconnect"/>
/// (fired from ClientManager.ClientDisconnect after the leaver is declassified) and removes the leaver from
/// every remaining live player's
/// <c>DentAttackers</c> set — the C# successor to QC's <c>FOREACH_CLIENT(true, dent_attackers[…] &amp;= ~BIT)</c>.
/// </summary>
[Mutator]
public sealed class DamagetextMutator : MutatorBase
{
    public DamagetextMutator() => NetName = "damagetext";

    // QC: REGISTER_MUTATOR(damagetext, true) — always registered; the PlayerDamaged handler early-outs when
    // sv_damagetext <= 0. Modeling IsEnabled on the cvar is equivalent (the handler is the only behavior).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("sv_damagetext") > 0f;

    private HookHandler<MutatorHooks.PlayerDamagedArgs>? _onDamaged;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.ClientDisconnectArgs>? _onDisconnect;

    public override void Hook()
    {
        _onDamaged ??= OnPlayerDamaged;
        _onSpawn ??= OnPlayerSpawn;
        _onDisconnect ??= OnClientDisconnect;
        MutatorHooks.PlayerDamaged.Add(_onDamaged);
        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.ClientDisconnect.Add(_onDisconnect);
    }

    public override void Unhook()
    {
        if (_onDamaged is not null) MutatorHooks.PlayerDamaged.Remove(_onDamaged);
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onDisconnect is not null) MutatorHooks.ClientDisconnect.Remove(_onDisconnect);
        _pending.Clear();
        _prev = null;
    }

    // The queued events for the client to draw, plus the QC same-frame accumulation state (static entity
    // net_text_prev / static float net_damagetext_prev_time). _prev points at the last queued event's index.
    private readonly List<DamageTextEvent> _pending = new();
    private float _prevTime = float.NegativeInfinity;
    private int? _prev; // index into _pending of the event to accumulate onto this frame
    private Entity? _prevAttacker, _prevTarget;
    private string _prevDeathType = "";

    /// <summary>
    /// Drain the queued floating-damage-number events for the client to draw this frame (the host/net layer
    /// calls this each frame and feeds them to <c>DamageTextLayer</c>). Empties the queue.
    /// </summary>
    public IReadOnlyList<DamageTextEvent> DrainPending()
    {
        if (_pending.Count == 0) return System.Array.Empty<DamageTextEvent>();
        var copy = _pending.ToArray();
        _pending.Clear();
        // The accumulation pointer is per-frame; drop it so the next frame starts a fresh group (QC keys
        // accumulation on net_damagetext_prev_time == time).
        _prev = null;
        return copy;
    }

    // MUTATOR_HOOKFUNCTION(damagetext, PlayerDamaged)
    private bool OnPlayerDamaged(ref MutatorHooks.PlayerDamagedArgs args)
    {
        if (Api.Services is null) return false;
        if (Api.Cvars.GetFloat("sv_damagetext") <= 0f) return false;

        Entity? attacker = args.Attacker;
        Entity hit = args.Target;
        if (attacker is not null && ReferenceEquals(hit, attacker)) return false; // hit == attacker
        float health = args.Health;
        float armor = args.Armor;
        string deathType = args.DeathType;
        float potential = args.PotentialDamage;
        if (potential == 0f) return false;

        // QC: instagib + DEATH_WEAPONOF == WEP_VAPORIZER → suppress (the one-shot kill text).
        if (Mutators.ByName("instagib") is { IsEnabled: true }
            && Damage.DeathTypes.WeaponNetNameOf(deathType) == "vaporizer")
            return false;

        float time = Api.Clock.Time;

        // QC same-frame accumulation: if the SAME attacker+hit+deathtype already produced an event THIS frame,
        // add this damage onto it (shotgun pellets / multi-hit accumulate into one number).
        bool multiple = _prevTime == time && _prev is not null
            && ReferenceEquals(_prevAttacker, attacker) && ReferenceEquals(_prevTarget, hit)
            && _prevDeathType == deathType;

        if (multiple)
        {
            DamageTextEvent p = _pending[_prev!.Value];
            health += p.Health;
            armor += p.Armor;
            potential += p.Potential;
        }

        int flags = 0;
        if (attacker is not null && Teams.SameTeam(hit, attacker)) flags |= DamageTextWire.FlagSameTeam;
        if (health >= DamageTextWire.ShortLimit) flags |= DamageTextWire.FlagBigHealth;
        if (armor >= DamageTextWire.ShortLimit) flags |= DamageTextWire.FlagBigArmor;
        if (potential >= DamageTextWire.ShortLimit) flags |= DamageTextWire.FlagBigPotential;
        if (armor == 0f) flags |= DamageTextWire.FlagNoArmor;
        if (AlmostEqualsEps(armor + health, potential, 5f)) flags |= DamageTextWire.FlagNoPotential;

        if (multiple)
        {
            // Update the previous event in place (QC rewrites net_text_prev's fields and returns).
            _pending[_prev!.Value] = new DamageTextEvent(attacker!, hit, flags, deathType, health, armor, potential);
            return false;
        }

        // First hit from this attacker since the target last respawned → force a fresh client accumulation group.
        if (attacker is not null && hit.DentAttackers.Add(attacker))
            flags |= DamageTextWire.FlagStopAccumulation;

        var ev = new DamageTextEvent(attacker ?? hit, hit, flags, deathType, health, armor, potential);
        _pending.Add(ev);

        _prev = _pending.Count - 1;
        _prevTime = time;
        _prevAttacker = attacker;
        _prevTarget = hit;
        _prevDeathType = deathType;
        return false;
    }

    // MUTATOR_HOOKFUNCTION(damagetext, PlayerSpawn) — clear the per-victim first-hit bitset.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        args.Player.DentAttackers.Clear();
        return false;
    }

    // MUTATOR_HOOKFUNCTION(damagetext, ClientDisconnect) — QC FOREACH_CLIENT(true, dent_attackers[etof(leaver)-1
    // bit] &= ~BIT): drop the leaving client from every player's first-hit set so a freed/reused slot can't
    // mis-fire STOP_ACCUMULATION. Keyed on the live Entity ref here, so we remove the leaver object from each set.
    private bool OnClientDisconnect(ref MutatorHooks.ClientDisconnectArgs args)
    {
        if (Api.Services is null) return false;
        Entity leaver = args.Player;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (it.IsFreed || (it.Flags & EntFlags.Client) == 0) continue;
            it.DentAttackers.Remove(leaver);
        }
        return false;
    }

    /// <summary>QC almost_equals_eps(a, b, eps): |a - b| &lt; eps (the NO_POTENTIAL test).</summary>
    private static bool AlmostEqualsEps(float a, float b, float eps) => MathF.Abs(a - b) < eps;
}
