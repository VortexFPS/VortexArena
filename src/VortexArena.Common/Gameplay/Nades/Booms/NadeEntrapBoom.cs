// Port of qcsrc/common/mutators/mutator/nades/nade/entrap.qc
//   (nade_entrap_boom + nade_entrap_touch + the PlayerPhysics_UpdateStats / MonsterMove hooks).
//
// The entrap nade spawns an orb (nades_spawn_orb) that:
//   (a) damps the VELOCITY of enemy entities (and projectiles) that pass through it, ticrate-independently;
//   (b) flags every real client inside it with nade_entrap_time, which the entrap PlayerPhysics handler
//       reads to scale the player's MOVEVARS_HIGHSPEED down by g_nades_entrap_speed — this slows the
//       thrower + teammates too (per the cvar's documented behaviour), not just enemies.
//
// (a) is the orb touch (this file). (b) is a MUTATOR_HOOKFUNCTION(nades, PlayerPhysics_UpdateStats) in QC;
// since the part-A NadesMutator (which this task does not own) doesn't wire PlayerPhysics, the entrap speed
// slow is wired here as a small self-contained [Mutator] (g_nades-gated) that rides MutatorHooks.PlayerPhysics
// and multiplies player.SpeedMultiplier — the same SpeedMultiplier the part-A PlayerPhysics edit applies via
// mp.ApplyHighSpeed. The QC MonsterMove hook (entrap.qc:48 — entrap run/walk slow + the veil monster-alpha
// lapse) is ported inline in MonsterAI.Move (the port inlines MonsterMove-hook effects there, like the
// spiderweb slow). Note both monster effects are dormant for the same reason as Base: the orb touch only
// flags nade_entrap_time / nade_veil_time on REAL CLIENTS (IsRealClient), never on monsters.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>The entrap nade detonation — port of <c>nade_entrap_boom</c>.</summary>
public sealed class NadeEntrapBoom : INadeBoom
{
    public string NadeNetName => "entrap";

    /// <summary>QC <c>nade_entrap_boom</c>: spawn the entrap orb and install <see cref="EntrapTouch"/>.</summary>
    public void Boom(Entity nade)
    {
        Entity orb = NadeBoom.SpawnOrb(nade,
            NadeProjectile.Cvar("g_nades_entrap_time", 10f),
            NadeProjectile.Cvar("g_nades_entrap_radius", 500f));
        orb.Touch = EntrapTouch;
    }

    /// <summary>
    /// Port of <c>nade_entrap_touch(entity this, entity toucher)</c> (entrap.qc:6): an enemy (DIFF_TEAM) that
    /// is pushable has its velocity exponentially damped (ticrate-independent: <c>strength ** dt</c>, dt the
    /// time since its last push, capped at 0.15s); any real client (friend or foe) inside the orb is flagged
    /// with <see cref="Entity.NadeEntrapTime"/> for the PlayerPhysics speed slow.
    /// </summary>
    private static void EntrapTouch(Entity orb, Entity toucher)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        Entity? owner = orb.RealOwner;

        if (NadeOrbHelper.DiffTeam(toucher, owner)) // TODO(QC): what if realowner changes team/disconnects?
        {
            if (!MapMover.IsPushable(toucher))
                return;

            float pushDelta = now - toucher.LastPushTime;
            if (pushDelta > 0.15f)
                pushDelta = 0f;
            toucher.LastPushTime = now;
            if (pushDelta == 0f)
                return;

            // div0: ticrate independent, strength ** dt (1 = identity).
            float strength = NadeProjectile.Cvar("g_nades_entrap_strength", 0.01f);
            toucher.Velocity *= MathF.Pow(strength, pushDelta);
        }

        if (NadeOrbHelper.IsRealClient(toucher))
            toucher.NadeEntrapTime = now + 0.1f;
    }
}

/// <summary>
/// The entrap-speed PlayerPhysics applier — port of the QC
/// <c>MUTATOR_HOOKFUNCTION(nades, PlayerPhysics_UpdateStats)</c> (entrap.qc:39). A self-contained
/// <c>g_nades</c>-gated mutator (the part-A NadesMutator owns its own hooks; this rides PlayerPhysics so the
/// entrap orb's speed slow lands without editing that file). Multiplies <see cref="Entity.SpeedMultiplier"/>
/// by <c>g_nades_entrap_speed</c> while the player is flagged entrapped, which the integrator applies via
/// <c>MovementParameters.ApplyHighSpeed</c>.
/// </summary>
[Mutator]
public sealed class NadeEntrapSpeedMutator : MutatorBase
{
    public NadeEntrapSpeedMutator() => NetName = "nades_entrap";

    // QC: the hook is on the nades mutator (REGISTER_MUTATOR(nades, autocvar_g_nades)); gate on the same cvar.
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_nades") != 0f;

    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;

    public override void Hook()
    {
        _onPhysics ??= OnPlayerPhysics;
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
    }

    public override void Unhook()
    {
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
    }

    // MUTATOR_HOOKFUNCTION(nades, PlayerPhysics_UpdateStats): STAT(MOVEVARS_HIGHSPEED, p) *= entrap_speed.
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;
        // "these automatically reset, no need to worry" (QC) — nade_entrap_time is a deadline, not a flag.
        if (player.NadeEntrapTime > Api.Clock.Time)
            player.SpeedMultiplier *= NadeProjectile.Cvar("g_nades_entrap_speed", 0.5f);
        return false;
    }
}
