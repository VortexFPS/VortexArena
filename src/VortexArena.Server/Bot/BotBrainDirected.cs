using System;
using System.Numerics;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Physics;

namespace VortexArena.Server.Bot;

public sealed partial class BotBrain
{
    /// <summary>
    /// When set, this brain is a deliberately minimal waypoint follower: no role, enemy scan, weapon choice,
    /// combat aim, item rating, or firing. The provider returns the host's most recently pointed-at HERE marker.
    /// Locomotion still uses the selected movement implementation (classic or learned policy).
    /// </summary>
    public Func<Vector3?>? DirectedGoalProvider;

    private Vector3 _directedGoal;
    private bool _directedGoalKnown;

    private MovementInput DirectedThinkProduce(Player bot, float dt, float now, bool jumpHeld)
    {
        // It participates in the population's round-robin token only so it cannot stall everyone else; it never
        // spends that token on a role/rating pass of its own.
        if (StrategyTokenHeld) OnStrategyTokenUsed?.Invoke();
        bot.Enemy = null;
        Vector3? requested = DirectedGoalProvider?.Invoke();
        if (requested is not Vector3 goal)
            return Emit(bot, Vector3.Zero, false, false, false, false, dt);

        bool changed = !_directedGoalKnown || Vector3.DistanceSquared(goal, _directedGoal) > 16f * 16f;
        _directedGoal = goal;
        _directedGoalKnown = true;
        float remaining2 = Vector3.DistanceSquared(bot.Origin, goal);
        if (changed || (!Nav.HasGoal && remaining2 > 48f * 48f))
        {
            Nav.SetGoal(bot.Origin, goal, Network, goalEntity: null, bot.OnGround);
            if (changed)
                NeuralHardRetarget(goal);
        }

        if (remaining2 <= 48f * 48f)
        {
            Nav.ClearRoute();
            return Emit(bot, Vector3.Zero, false, false, false, false, dt);
        }

        if (Locomotor is not null && Neural is { Ready: true })
            return NeuralThinkProduce(bot, dt, now, jumpHeld);

        Vector3 move = Nav.Steer(bot, Aim.ViewAngles.Y, bot.OnGround);
        if (Nav.Current is Vector3 current)
        {
            Vector3 look = current - (bot.Origin + Aim.ViewOffset);
            look.Z = 0f;
            if (look.LengthSquared() > 0.001f)
                Aim.AimAt(look, bot.Origin, Skill, dt, now, 0f, hasEnemy: false);
        }
        bool jump = Nav.WantJump || Nav.WantBunnyhop;
        if (jump) _jumpTime = now;
        return Emit(bot, move, jump || jumpHeld, Nav.WantCrouch, false, false, dt);
    }
}
