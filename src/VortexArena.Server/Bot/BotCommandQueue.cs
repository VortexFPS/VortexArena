using System.Globalization;
using System.Numerics;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot;

/// <summary>Per-bot command interpreter for Xonotic's server-side bot scripts.</summary>
internal sealed class BotCommandQueue(BotBrain brain, BotPopulation population, GameWorld world)
{
    private enum Verb
    {
        Pause, Continue, Wait, Turn, MoveTo, ResetGoal, ClientCommand, If, Else, Fi, ResetAim, Aim,
        PressKey, ReleaseKey, SelectWeapon, Impulse, WaitUntil, MoveToTarget, AimTarget, Barrier,
        Console, Sound, AssertCanFire,
    }

    private enum Result { Done, Running, Error }
    private enum Parameter { None, Number, Vector, Text }
    private readonly record struct Spec(Verb Verb, Parameter Type, string Usage, string Help);
    private readonly record struct Instruction(Spec Spec, string Text, float Number, Vector3 Position);

    private static readonly Dictionary<string, Spec> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pause"] = new(Verb.Pause, Parameter.None, "", "Stop the bot and ignore commands until continue."),
        ["continue"] = new(Verb.Continue, Parameter.None, "", "Resume a paused bot."),
        ["wait"] = new(Verb.Wait, Parameter.Number, "<seconds>", "Wait for the given duration while holding keys."),
        ["turn"] = new(Verb.Turn, Parameter.Number, "<degrees>", "Add degrees to the bot's yaw."),
        ["moveto"] = new(Verb.MoveTo, Parameter.Vector, "'x y z'", "Navigate to a world coordinate."),
        ["resetgoal"] = new(Verb.ResetGoal, Parameter.None, "", "Clear the navigation goal."),
        ["cc"] = new(Verb.ClientCommand, Parameter.Text, "<command>", "Execute a client command as the bot."),
        ["if"] = new(Verb.If, Parameter.Text, "<condition>", "Test numbers, cvar.NAME, health, speed or flagcarrier with =, > or <."),
        ["else"] = new(Verb.Else, Parameter.None, "", "Begin the other arm of an if block."),
        ["fi"] = new(Verb.Fi, Parameter.None, "", "End an if block."),
        ["resetaim"] = new(Verb.ResetAim, Parameter.None, "", "Set pitch, yaw and roll to zero."),
        ["aim"] = new(Verb.Aim, Parameter.Text, "<yaw pitch [seconds]>", "Change aim relative to the current view."),
        ["presskey"] = new(Verb.PressKey, Parameter.Text, "<key>", "Hold a movement, jump, crouch, attack, use, hook or chat key."),
        ["releasekey"] = new(Verb.ReleaseKey, Parameter.Text, "<key|all>", "Release a held key or all held keys."),
        ["selectweapon"] = new(Verb.SelectWeapon, Parameter.Number, "<weapon id>", "Select an owned and usable weapon."),
        ["impulse"] = new(Verb.Impulse, Parameter.Number, "<number>", "Send a gameplay impulse as the bot."),
        ["wait_until"] = new(Verb.WaitUntil, Parameter.Number, "<seconds>", "Wait until the given time since the last barrier or reset."),
        ["movetotarget"] = new(Verb.MoveToTarget, Parameter.Text, "<targetname>", "Navigate to the named map entity."),
        ["aimtarget"] = new(Verb.AimTarget, Parameter.Text, "<targetname [seconds]>", "Aim at the named map entity."),
        ["barrier"] = new(Verb.Barrier, Parameter.None, "", "Wait for every bot with a queued command to reach a barrier."),
        ["console"] = new(Verb.Console, Parameter.Text, "<command>", "Queue a server console command."),
        ["sound"] = new(Verb.Sound, Parameter.Text, "<[channel [volume [attenuation]]] sample>", "Play a sound at the bot."),
        ["debug_assert_canfire"] = new(Verb.AssertCanFire, Parameter.Number, "<0|1>", "Report a weapon readiness mismatch."),
    };

    [Flags]
    private enum Keys
    {
        None = 0, Forward = 1, Backward = 2, Right = 4, Left = 8, Jump = 16, Attack1 = 32,
        Attack2 = 64, Use = 128, Hook = 256, Crouch = 512, Chat = 1024,
    }

    private readonly Queue<Instruction> _pending = new();
    private readonly Dictionary<string, Entity?> _places = new(StringComparer.Ordinal);
    private readonly record struct AimChange(float Start, float Duration, Vector3 From, Vector3 To);
    private AimChange? _aim;
    private Vector3? _destination;
    private float _routeRetry;
    private int _routeFailures;
    private float? _waitEnds;
    private bool? _condition;
    private bool _else;
    private bool _paused;
    private Keys _keys;

    internal bool HasCommands => _pending.Count > 0;
    internal bool AtBarrier { get; private set; }
    internal int TakeImpulse()
    {
        int impulse = _impulse;
        _impulse = 0;
        return impulse;
    }
    private int _impulse;

    internal static IEnumerable<string> Help(string name)
    {
        if (name.Length == 0)
        {
            foreach (var (key, spec) in Commands)
                yield return $"{key} {spec.Usage}".TrimEnd();
        }
        else if (Commands.TryGetValue(name, out Spec spec))
            yield return $"{name} {spec.Usage}".TrimEnd() + $" — {spec.Help}";
        else
            yield return $"No such bot command '{name}'.";
    }

    internal bool Enqueue(string name, string parameter, out string error)
    {
        error = "";
        if (!Commands.TryGetValue(name, out Spec spec))
        {
            error = $"No such bot command '{name}'.";
            return false;
        }

        string text = Unquote(parameter.Trim());
        float number = 0;
        Vector3 vector = default;
        if (spec.Type != Parameter.None && (text.Length == 0
            || (spec.Type == Parameter.Number && !TryNumber(text, out number))
            || (spec.Type == Parameter.Vector && !TryVector(text, out vector))))
        {
            error = $"Usage: {name} {spec.Usage}";
            return false;
        }

        _pending.Enqueue(new Instruction(spec, text, number, vector));
        // scripting.qc registers the final token as soon as the command enters the queue.
        if (spec.Verb == Verb.Sound && CommandsArgs(text) is { Length: > 0 } args)
            world.Services.SoundImpl.Precache(args[^1]);
        return true;
    }

    internal void Reset()
    {
        _pending.Clear();
        _places.Clear();
        _aim = null;
        _waitEnds = null;
        _condition = null;
        _else = false;
        _impulse = 0;
        AtBarrier = false;
        ClearDestination();
    }

    /// <summary>Run consecutive instantaneous commands, then emit input for the first blocking command.</summary>
    internal bool TryThink(float now, float dt, out MovementInput input)
    {
        input = default;
        while (_pending.TryPeek(out Instruction command))
        {
            Verb verb = command.Spec.Verb;
            if (_paused && verb != Verb.Continue)
            {
                _pending.Dequeue();
                Log.Info($"Bot {brain.Bot.NetName}: commands are ignored while paused. Use continue.");
                return true;
            }

            if (_condition.HasValue && _condition.Value == _else && verb is not (Verb.Else or Verb.Fi))
            {
                _pending.Dequeue();
                continue;
            }

            input = ReadKeys();
            input.FrameTime = dt;
            Result result = Execute(command, now, ref input);
            if (result == Result.Running)
                return true;

            // cc may disconnect the client, but does not execute server-only queue commands.
            _pending.Dequeue();
            if (result == Result.Error)
            {
                Log.Info($"Bot {brain.Bot.NetName}: '{command.Spec.Verb}' failed.");
                return true;
            }
            if (Cvars.Bool("g_debug_bot_commands"))
                InvokeClient($"say ^7{command.Spec.Verb.ToString().ToLowerInvariant()} {command.Text}");
        }
        input = default;
        return _paused;
    }

    private Result Execute(Instruction command, float now, ref MovementInput input)
    {
        Player bot = brain.Bot;
        switch (command.Spec.Verb)
        {
            case Verb.Pause:
                _paused = true;
                _keys = Keys.None;
                break;
            case Verb.Continue:
                _paused = false;
                break;
            case Verb.Wait:
                if (!_waitEnds.HasValue)
                {
                    _waitEnds = now + command.Number;
                    return Result.Running;
                }
                if (now < _waitEnds.Value)
                    return Result.Running;
                _waitEnds = null;
                break;
            case Verb.WaitUntil:
                return now < population.ScriptBarrierTime + command.Number ? Result.Running : Result.Done;
            case Verb.Barrier:
                if (!brain.ScriptBarrierReleased)
                {
                    AtBarrier = true;
                    population.TryReleaseScriptBarrier(now);
                }
                if (!brain.ScriptBarrierReleased)
                    return Result.Running;
                AtBarrier = false;
                brain.ScriptBarrierReleased = false;
                break;
            case Verb.Turn:
                Vector3 turn = brain.Aim.ViewAngles;
                turn.Y += command.Number;
                turn.Y -= MathF.Floor(turn.Y / 360f) * 360f;
                brain.Aim.ViewAngles = turn;
                break;
            case Verb.ResetAim:
                brain.Aim.ViewAngles = Vector3.Zero;
                break;
            case Verb.Aim:
            case Verb.AimTarget:
                return ApplyAim(command, now);
            case Verb.MoveTo:
            case Verb.MoveToTarget:
                return MoveTo(command, now, ref input);
            case Verb.ResetGoal:
                brain.Nav.ClearRoute();
                break;
            case Verb.PressKey:
            case Verb.ReleaseKey:
                ChangeKeys(command.Text, command.Spec.Verb == Verb.PressKey);
                break;
            case Verb.If:
                if (_condition.HasValue)
                {
                    // Keep the current instruction present for the caller's dequeue.
                    _pending.Clear();
                    _pending.Enqueue(command);
                    _condition = null;
                    return Result.Error;
                }
                _condition = Evaluate(command.Text);
                _else = false;
                break;
            case Verb.Else:
                _else = true;
                break;
            case Verb.Fi:
                _condition = null;
                break;
            case Verb.ClientCommand:
                InvokeClient(command.Text);
                break;
            case Verb.Console:
                world.Commands.QueueBotConsole(command.Text);
                break;
            case Verb.SelectWeapon:
                return SelectWeapon(command.Number) ? Result.Done : Result.Error;
            case Verb.Impulse:
                _impulse = (int)command.Number;
                break;
            case Verb.Sound:
                return PlaySound(command.Text) ? Result.Done : Result.Error;
            case Verb.AssertCanFire:
                AssertCanFire(command.Number != 0f, now);
                break;
        }
        return Result.Done;
    }

    private void InvokeClient(string command)
    {
        CommandContext reply = world.Commands.Execute(command, isServerConsole: false, caller: brain.Bot);
        if (reply.Output.Length > 0)
            Log.Info(reply.Output.TrimEnd());
    }

    private Result ApplyAim(Instruction command, float now)
    {
        if (_aim is { } active)
        {
            float progress = System.Math.Clamp((now - active.Start) / active.Duration, 0f, 1f);
            brain.Aim.ViewAngles = Vector3.Lerp(active.From, active.To, progress);
            if (progress < 1f)
                return Result.Running;
            _aim = null;
            return Result.Done;
        }

        string[] args = CommandsArgs(command.Text);
        Vector3 from = brain.Aim.ViewAngles;
        Vector3 destination;
        int durationIndex;
        if (command.Spec.Verb == Verb.Aim)
        {
            if (args.Length is < 2 or > 3 || !TryNumber(args[0], out float yaw) || !TryNumber(args[1], out float pitch))
                return Result.Error;
            destination = from + new Vector3(-pitch, yaw, 0f);
            durationIndex = 2;
        }
        else
        {
            if (args.Length is < 1 or > 2 || FindPlace(args[0]) is not Entity target)
                return Result.Error;
            destination = QMath.FixedVecToAngles(Center(target) - (brain.Bot.Origin + brain.Bot.ViewOfs));
            durationIndex = 1;
        }

        float duration = 0;
        if (args.Length > durationIndex && !TryNumber(args[durationIndex], out duration))
            return Result.Error;
        if (duration <= 0f)
        {
            brain.Aim.ViewAngles = destination;
            return Result.Done;
        }
        _aim = new AimChange(now, duration, from, new Vector3(destination.X, destination.Y, 0f));
        return Result.Running;
    }

    private Result MoveTo(Instruction command, float now, ref MovementInput input)
    {
        Player bot = brain.Bot;
        if (!_destination.HasValue)
        {
            if (command.Spec.Verb == Verb.MoveTo)
                _destination = command.Position;
            else if (FindPlace(command.Text) is Entity target)
                _destination = Center(target);
            else
                return Result.Error;
            _routeRetry = now;
            _routeFailures = 0;
        }

        Vector3 destination = _destination.Value;
        bool stuck = brain.Nav.CheckGoalProgress(bot, now);
        if (now >= _routeRetry || !brain.Nav.HasGoal)
        {
            _routeFailures = stuck ? _routeFailures + 1 : 0;
            if (_routeFailures >= 30)
            {
                ClearDestination();
                return Result.Error;
            }
            var network = population.EnsureWaypointNetwork();
            brain.Nav.SetGoal(bot.Origin, destination, network, onGround: bot.OnGround);
            _routeRetry = now + (stuck ? 2f : 10f);
        }
        if (brain.Nav.Current is Vector3 waypoint)
        {
            Vector3 toward = waypoint - bot.Origin;
            toward.Z = 0f;
            if (toward.LengthSquared() > 0.01f)
                brain.Aim.AimAt(toward, bot.Origin, brain.Skill, input.FrameTime, now, 0f, hasEnemy: false);
        }
        input.MoveValues = brain.Nav.Steer(bot, brain.Aim.ViewAngles.Y, bot.OnGround);
        if (!brain.Nav.HasGoal)
        {
            ClearDestination();
            return Result.Done;
        }
        input.ButtonJump |= brain.Nav.WantJump || brain.Nav.WantBunnyhop;
        input.ButtonCrouch |= brain.Nav.WantCrouch;
        return Result.Running;
    }

    private void ClearDestination()
    {
        if (!_destination.HasValue)
            return;
        _destination = null;
        brain.Nav.ClearRoute();
    }

    private Entity? FindPlace(string name)
    {
        if (!name.StartsWith('@'))
            return MapMover.FindByTargetName(name).FirstOrDefault();

        string cvar = name[1..];
        if (_places.TryGetValue(cvar, out Entity? known))
            return known is { IsFreed: false } ? known : null;

        string list = Cvars.String(cvar).Trim();
        int split = list.IndexOf(' ');
        string target = split < 0 ? list : list[..split];
        if (split >= 0)
            Cvars.Set(cvar, list[(split + 1)..].TrimStart() + " " + target);
        Entity? entity = MapMover.FindByTargetName(target).FirstOrDefault();
        if (_places.Count < 4)
            _places[cvar] = entity;
        return entity;
    }

    private bool Evaluate(string expression)
    {
        foreach (char op in "=><")
        {
            int pivot = expression.IndexOf(op);
            if (pivot <= 0)
                continue;
            float left = EvaluateValue(expression[..pivot].Trim());
            float right = EvaluateValue(expression[(pivot + 1)..].Trim());
            return op switch { '=' => left == right, '>' => left > right, _ => left < right };
        }
        return EvaluateValue(expression) != 0f;
    }

    private float EvaluateValue(string expression)
    {
        if (TryNumber(expression, out float number))
            return number;
        if (expression.StartsWith("cvar.", StringComparison.Ordinal))
            return Cvars.Float(expression[5..]);
        Player bot = brain.Bot;
        return expression switch
        {
            "health" => bot.Health,
            "speed" => bot.Velocity.Length(),
            "flagcarrier" => world.GameType is Ctf ctf && ctf.CarriedBy(bot) is not null ? 1f : 0f,
            _ => 0f,
        };
    }

    private MovementInput ReadKeys()
    {
        float speed = Cvars.FloatOr("sv_maxspeed", 320f);
        float forward = Held(Keys.Forward) ? speed : Held(Keys.Backward) ? -speed : 0f;
        float side = Held(Keys.Right) ? speed : Held(Keys.Left) ? -speed : 0f;
        return new MovementInput
        {
            MoveValues = new Vector3(forward, side, 0),
            ButtonJump = Held(Keys.Jump),
            ButtonCrouch = Held(Keys.Crouch),
            ButtonAttack1 = Held(Keys.Attack1),
            ButtonAttack2 = Held(Keys.Attack2),
            ButtonUse = Held(Keys.Use),
            ButtonHook = Held(Keys.Hook),
            Typing = Held(Keys.Chat),
        };
    }

    private bool Held(Keys key) => (_keys & key) != 0;

    private void ChangeKeys(string name, bool press)
    {
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            _keys = press ? (Keys)2047 & ~Keys.Backward : Keys.None;
            return;
        }
        if (!Enum.TryParse(name, ignoreCase: true, out Keys key) || !Enum.IsDefined(key))
            return;
        Keys opposite = key switch
        {
            Keys.Forward => Keys.Backward,
            Keys.Backward => Keys.Forward,
            Keys.Left => Keys.Right,
            Keys.Right => Keys.Left,
            _ => Keys.None,
        };
        _keys = press ? (_keys | key) & ~opposite : _keys & ~key;
    }

    private bool SelectWeapon(float number)
    {
        Weapon? weapon = WeaponOrder.FromXonoticRegistryId((int)number);
        Player bot = brain.Bot;
        if (weapon is null || !Inventory.ClientHasWeapon(bot, weapon, andAmmo: true, complain: false))
            return false;

        for (int index = 0; index < WeaponFireDriver.MaxWeaponSlots; index++)
        {
            WeaponSlotState state = bot.WeaponState(new WeaponSlot(index));
            if (index == 0 || state.CurrentWeaponId >= 0)
                state.SwitchWeaponId = weapon.RegistryId;
        }
        return true;
    }

    private bool PlaySound(string parameter)
    {
        string[] args = CommandsArgs(parameter);
        if (args.Length == 0)
            return false;
        float channel = (int)SoundChannel.WeaponAuto;
        float volume = SoundLevels.VolBase;
        float attenuation = SoundLevels.AttenMin;
        if ((args.Length >= 2 && !TryNumber(args[0], out channel))
            || (args.Length >= 3 && !TryNumber(args[1], out volume))
            || (args.Length >= 4 && !TryNumber(args[2], out attenuation)))
            return false;
        world.Services.Sound.Play(brain.Bot, (SoundChannel)(int)channel, args[^1], volume, attenuation);
        return true;
    }

    private void AssertCanFire(bool expected, float now)
    {
        Player bot = brain.Bot;
        WeaponSlotState state = bot.WeaponState(new WeaponSlot(0));
        if (state.State != WeaponFireState.Ready)
        {
            if (expected) Inhibited(new Vector3(0, 8, 8), "weapon state");
        }
        else if (state.AttackFinished > now)
        {
            if (expected) Inhibited(new Vector3(8, 0, 8), $"cooldown ({state.AttackFinished - now:0.###} s left)");
        }
        else if (state.TubaNote is not null)
        {
            if (expected) Inhibited(new Vector3(8, 0, 0), "an active Tuba note");
        }
        else if (!expected)
        {
            bot.ColorModKey = new Vector3(8, 8, 0);
            Log.Info($"Bot {bot.NetName}: expected fire to be inhibited, but the weapon is ready.");
        }

        void Inhibited(Vector3 color, string reason)
        {
            bot.ColorModKey = color;
            Log.Info($"Bot {bot.NetName}: fire inhibited by {reason}.");
        }
    }

    private static Vector3 Center(Entity e) => e.Origin + 0.5f * (e.Mins + e.Maxs);
    private static string[] CommandsArgs(string text) => VortexArena.Server.Commands.Tokenize(text);
    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private static bool TryNumber(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);

    private static bool TryVector(string value, out Vector3 result)
    {
        result = default;
        if (value.Length < 2 || value[0] != '\'' || value[^1] != '\'')
            return false;
        string[] args = CommandsArgs(value[1..^1]);
        if (args.Length != 3 || !TryNumber(args[0], out float x) || !TryNumber(args[1], out float y)
            || !TryNumber(args[2], out float z))
            return false;
        result = new Vector3(x, y, z);
        return true;
    }
}
