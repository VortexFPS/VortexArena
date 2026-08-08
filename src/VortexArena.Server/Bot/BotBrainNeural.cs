using System;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Server.Bot.Neural;

namespace VortexArena.Server.Bot;

/// <summary>
/// The learned-locomotion arm of <see cref="BotBrain.ThinkProduce"/>. Kept in its own file so
/// <c>BotBrain.cs</c> stays the classic HavocBot path, one QC citation per behaviour, with a single branch
/// pointing here.
/// </summary>
public sealed partial class BotBrain
{
    /// <summary>
    /// This bot's learned locomotor, or null to use the classic steer. Set by <see cref="BotPopulation"/>
    /// when <c>bot_neural</c> is on and the service has both a policy and a field.
    /// </summary>
    public NeuralLocomotor? Locomotor;

    /// <summary>The server-wide neural resources (policy weights, baked field, map features).</summary>
    public NeuralBotService? Neural;

    /// <summary>Sim time of this bot's previous neural think, for the true inter-think delta.</summary>
    private float _neuralPrevThink;

    /// <summary>
    /// TRAINING ONLY. Last chance to rewrite the intent before the policy sees it, so the training
    /// environment can substitute its own destination, weapon permit and aim constraint for the ones the
    /// tactician computed. Null on a live server, where the tactician's intent is the intent.
    /// </summary>
    public Func<MoveIntent, MoveIntent>? IntentOverride;

    /// <summary>
    /// Produce this tick's command from the learned policy.
    ///
    /// <para>Runs after the tactician has already chosen the goal, the enemy and the weapon. What is left is:
    /// work out where combat wants the crosshair, decide whether the trigger may be pulled, package both into
    /// a <see cref="MoveIntent"/>, and let the policy drive.</para>
    /// </summary>
    /// <summary>
    /// Health plus armour at or below which the policy loses the weapon for movement.
    ///
    /// <para>60 of a 100 baseline: enough of a margin that a blaster pop's self-damage cannot finish the
    /// bot, without disabling weapon movement so early that the skill never shows up in play.</para>
    /// </summary>
    public const float WeaponMovementMinHealth = 60f;

    /// <summary>Health plus armour, matching what the training reward's damage term counts.</summary>
    private static float Vitality(Player p) => p.Health + p.GetResource(ResourceType.Armor);

    // ---- the live route: a Dijkstra flood over the baked field, per goal ----
    private Vector3 _routeGoal;
    private NavDistanceField? _routeField;
    private System.Threading.Tasks.Task? _routeBuild;

    /// <summary>
    /// The distance field for <paramref name="goal"/>, rebuilt asynchronously when the goal moves.
    ///
    /// <para>This replaces the waypoint graph as the neural bots' router. In training, the corridor
    /// look-aheads and the observation's route channels all come from a Dijkstra flood over the baked
    /// navigation field; at runtime they used to come from the classic waypoint route -- a different
    /// generator for the same inputs, which is a train/deploy distribution gap, and a hard dependency on
    /// authored waypoint data that many maps simply do not ship. Building the same flood live closes
    /// both.</para>
    ///
    /// <para>The build is a few milliseconds, so it runs on the pool rather than the frame. Until it lands,
    /// the previous goal's field keeps serving -- goals move gradually, so stale is close -- and before the
    /// first build the observation's straight-line fallback covers it. The per-think reads sit inside the
    /// existing <c>bot.think</c> profiler scope.</para>
    /// </summary>
    private NavDistanceField? RouteFor(NavField field, Vector3 goal)
    {
        if (_routeField is not null && (goal - _routeGoal).LengthSquared() < 96f * 96f) return _routeField;
        if (_routeBuild is { IsCompleted: false }) return _routeField;
        Vector3 target = goal;
        _routeBuild = System.Threading.Tasks.Task.Run(() =>
        {
            NavDistanceField built = NavDistanceField.Build(field, target);
            // Goal first, field second: a reader that sees the new field sees its goal too. The reverse
            // order could pair the new field with the old goal for one think, and the 96 qu tolerance
            // above would then skip the rebuild that fixes it.
            _routeGoal = target;
            _routeField = built;
        });
        return _routeField;
    }

    private MovementInput NeuralThinkProduce(Player bot, float dt, float now, bool jumpHeld)
    {
        using var _scope = VortexArena.Common.Diagnostics.Prof.Sample("bot.nn");

        NeuralLocomotor loco = Locomotor!;
        NeuralBotService svc = Neural!;

        // The interval since this bot last thought, which is what the policy's view delta and the goal slew
        // are calibrated in. The think throttle is skill-dependent and jittered per bot, so it is not dt.
        float thinkDt = _neuralPrevThink > 0f ? Math.Clamp(now - _neuralPrevThink, 1f / 144f, 0.5f) : dt;
        _neuralPrevThink = now;

        // ---- 1. no-progress watchdog, same as the classic path ----
        if (Nav.HasGoal && Nav.CheckGoalProgress(bot, now))
        {
            Nav.ClearRoute();
            _strategyForced = true;
        }
        if (!Nav.HasGoal && !_pendingGoalSet && !_strategyForced)
            _strategyForced = true;

        // ---- 2. where combat wants the crosshair, and may it shoot ----
        // The deterministic side keeps ownership of WHERE to aim: projectile lead, ballistic arcs for lobbed
        // weapons, and the per-bot skill error that makes a low-skill bot miss. The policy only decides the
        // path the crosshair takes. That split is why aim skill never has to be learned.
        var intent = new MoveIntent
        {
            WeaponMovementAllowed = true,
            HullMins = Nav.Mins,
            HullMaxs = Nav.Maxs,
        };

        Entity? enemy = bot.Enemy is { IsFreed: false } ? bot.Enemy : null;
        Aim.UpdateShotVectors(bot.Origin);
        if (now >= _aimTime)
            _aimTime = now + AimInterval;

        Vector3 fireDir = Vector3.Zero;
        float maxDev = 0f;
        bool losClear = false;

        if (enemy is not null)
        {
            Vector3 enemyCenter = (enemy.AbsMin != enemy.AbsMax)
                ? (enemy.AbsMin + enemy.AbsMax) * 0.5f
                : enemy.Origin + enemy.ViewOfs;

            float shotSpeed = CurrentShotSpeed();
            Vector3 lead = Aim.ShotLead(enemyCenter, enemy.Velocity, shotSpeed);
            fireDir = lead - Aim.ShotOrigin;
            bool lobbed = shotSpeed > 0f && CurrentWeaponIsLobbed();
            if (lobbed)
                fireDir = Aim.BallisticArc(lead, shotSpeed, ProjectileGravity());

            maxDev = Aim.MaxFireDeviation(lead, Skill, ChosenWeapon?.BotAimAccurate() ?? true);
            intent.RequiredAimAngles = Aim.ComputeDesiredAngles(fireDir, Skill, now, hasEnemy: true);
            intent.AimRequired = true;
            // A bot with an enemy in front of it needs the crosshair badly; one whose target is behind cover
            // can afford to keep running. The line-of-fire test decides which, and it is the same test the
            // classic path uses to gate the trigger, so the weight and the fire decision never disagree.
            losClear = lobbed || LineOfFireClear(enemyCenter, enemy);
            intent.AimWeight = losClear ? 1f : 0.35f;

            // Combat has the weapon whenever it could actually take a shot. This is the toggle the brief
            // asked for: while it is false the policy cannot fire at all (a hard mask in ActionSpace.Decode),
            // and the instant combat releases it the policy may weapon-jump again.
            intent.WeaponMovementAllowed = !losClear;
        }
        else
        {
            // No enemy: the tactician has no opinion about the crosshair, so the policy gets the mouse
            // outright and the weapon with it.
            intent.AimRequired = false;
            intent.AimWeight = 0f;
            intent.WeaponMovementAllowed = true;
            IdleReload(bot, now);   // QC havocbot_ai:181-211, unchanged from the classic path
        }

        // Hurt bots stop spending health on movement. Blaster and rocket jumps cost self-damage, which is a
        // fine trade at full health and a way to die at low health, and the policy cannot make that call
        // because it optimises arrival time and never has to survive the next fight.
        //
        // Applied AFTER the combat branches so it overrides both: whatever combat decided, a bot at or below
        // this threshold does not get the weapon for movement. Withdrawing the permit is enough on its own --
        // ActionSpace.Decode masks the attack and weapon-select heads off entirely, so there is nothing to
        // penalise and nothing the policy can do about it. A reward penalty would only make firing
        // expensive, and combat needs a guarantee rather than a price.
        if (Vitality(bot) <= WeaponMovementMinHealth)
            intent.WeaponMovementAllowed = false;

        // ---- 3. the destination ----
        Vector3 goal = Nav.Current ?? bot.Origin;
        intent.GoalPos = goal;
        intent.GoalEntity = Nav.GoalEntity;
        // Corridor and route come from the baked field's distance transform -- the same source the trainer
        // used -- with the classic waypoint route only as the fallback while the first flood builds. See
        // RouteFor for why this replaces the waypoint graph rather than supplementing it.
        NavDistanceField? route = svc.Field is { } fieldForRoute ? RouteFor(fieldForRoute, goal) : null;
        intent.Route = route;
        if (route is not null)
        {
            intent.CorridorA = route.PointAlongRoute(bot.Origin, 320f);
            intent.CorridorB = route.PointAlongRoute(intent.CorridorA, 320f);
        }
        else
        {
            intent.CorridorA = Nav.RouteNode(1, goal);
            intent.CorridorB = Nav.RouteNode(2, intent.CorridorA);
        }
        intent.Urgency = ResolveUrgency(enemy);

        if (IntentOverride is not null)
            intent = IntentOverride(intent);

        // ---- 4. run the policy ----
        // The trace fan is cvar-gated rather than hardcoded on: it is the one part of perception that costs
        // real traces (six box sweeps per think, about 5% of a core across 16 bots), so an operator chasing
        // server CPU needs a way to turn it off. A registered cvar that nothing reads is worse than no cvar
        // at all — see the parity report's list of exactly that.
        MovementInput input = loco.Think(bot, intent, svc.Field, svc.Features,
            Aim.ViewAngles, now, thinkDt, Nav.MaxSpeed, traceFan: Cvars.Bool("bot_neural_tracefan"));

        // ---- 5. the policy's weapon request ----
        // Only honoured while combat has released the weapon; ActionSpace.Decode already masked the request
        // away otherwise, so this is belt and braces on the inventory side.
        if (intent.WeaponMovementAllowed && loco.RequestedWeapon() is { } wanted
            && bot.OwnedWeaponSet.Has(wanted) && !ReferenceEquals(wanted, Inventory.CurrentWeapon(bot)))
        {
            ChosenWeapon = wanted;
            Inventory.SwitchWeapon(bot, wanted);
        }

        // ---- 6. adopt the policy's view, then run the deterministic fire gate against it ----
        // Order matters. The gate measures the deviation between the shot direction and where the view
        // ACTUALLY points, so it has to see the policy's result, not the angle the policy was aiming for.
        Aim.ViewAngles = input.ViewAngles;
        bool wantAttack = false, wantAttack2 = false;
        if (enemy is not null)
        {
            Aim.ArmFireTimer(fireDir, bot.Origin, Skill, now, maxDev);
            wantAttack = losClear && Aim.ShouldFire(now);

            if (wantAttack && ChosenWeapon is { } w)
            {
                _botAimState.Random01 = (float)_rng.NextDouble();
                _botAimState.Actor = bot;
                if (w.BotWantsSecondary((enemy.Origin - bot.Origin).Length(), Skill, ref _botAimState))
                {
                    wantAttack = false;
                    wantAttack2 = true;
                }
            }
            if (ChosenWeapon is { } dw
                && dw.BotWantsDetonate(bot, new WeaponSlot(0), Skill, Players(), ShouldAttack))
            {
                wantAttack = false;
                wantAttack2 = true;
            }
            if (Cvars.Bool("bot_nofire") || Cvars.Bool("_independent_players"))
            {
                wantAttack = false;
                wantAttack2 = false;
            }
            if (ChosenWeapon is { } fw && fw.BotForbidsFire((enemy.Origin - bot.Origin).Length(), Skill))
            {
                wantAttack = false;
                wantAttack2 = false;
            }
        }

        // The policy's own trigger presses (weapon-jumping) survive only where combat did not claim the
        // weapon. Combat's decision wins on the same tick, because a frag beats a boost.
        if (intent.WeaponMovementAllowed)
        {
            wantAttack |= input.ButtonAttack1;
            wantAttack2 |= input.ButtonAttack2;
        }

        bool jump = input.ButtonJump || jumpHeld;
        if (input.ButtonJump)
            _jumpTime = now;   // QC bot_jump_time: keep jump held ~0.2 s so ramp jumps register
        if (wantAttack || wantAttack2)
        {
            _lastAttackTime = now;
            LastFiredWeapon = ChosenWeapon;
        }

        return Emit(bot, input.MoveValues, jump, input.ButtonCrouch, wantAttack, wantAttack2, dt);
    }

    /// <summary>
    /// QC bot_aim's line-of-fire test, lifted out of <see cref="AimAndDecideFire"/> so the neural path can
    /// run it before the shot rather than after. Same hit-mask override and the same reasoning: without it
    /// every <c>common/clip</c> brush reads as "blocked" and the bot holds fire at a visible enemy
    /// (parity report F3).
    /// </summary>
    private bool LineOfFireClear(Vector3 enemyCenter, Entity enemy)
    {
        int savedMask = Bot.DpHitContentsMask;
        Bot.DpHitContentsMask = BotAimHitContentsMask;
        TraceResult tr;
        try
        {
            tr = Api.Trace.Trace(Aim.ShotOrigin, Vector3.Zero, Vector3.Zero, enemyCenter, MoveFilter.Normal, Bot);
        }
        finally
        {
            Bot.DpHitContentsMask = savedMask;
        }
        return tr.Fraction >= 1f || ReferenceEquals(tr.Ent, enemy)
               || (tr.Ent is not null && ShouldAttack(Bot, tr.Ent));
    }

    /// <summary>
    /// How hard the bot should push for its goal, 0..1. Chasing or fleeing a live player is urgent; walking
    /// to an item that respawned thirty seconds ago is not. The policy trades speed against risk on this.
    /// </summary>
    private float ResolveUrgency(Entity? enemy)
    {
        if (Nav.GoalEntity is Player) return 1f;              // a player goal: intercept or escort
        if (enemy is not null) return 0.75f;                  // in a fight, get where you are going
        if (Nav.RouteLength > 6) return 0.6f;                 // a long route: worth carrying speed
        return 0.45f;
    }
}
