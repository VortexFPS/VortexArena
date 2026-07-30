using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;

namespace XonoticGodot.Server;

/// <summary>
/// A power-mean accumulator — the C# successor to the QC <c>MEAN_DECLARE</c> / <c>mean_accumulate</c> /
/// <c>mean_evaluate</c> trio (lib/math.qh). Each statistic carries an exponent <see cref="M"/> and accumulates
/// weighted samples; <see cref="Evaluate"/> returns the generalized mean of order <see cref="M"/>. For
/// <c>M == 0</c> it is the geometric mean (accumulator starts at 1); every anticheat statistic uses a non-zero
/// exponent, so the additive form (accumulator starts at 0) is the common path.
/// </summary>
public struct Mean
{
    public readonly float M;
    private double _acc;
    private double _count;

    public Mean(float m) { M = m; _acc = (m == 0f) ? 1.0 : 0.0; _count = 0.0; }

    /// <summary>QC <c>mean_accumulate(value, weight)</c>.</summary>
    public void Accumulate(double value, double weight)
    {
        if (weight == 0.0) return;
        if (M == 0f) _acc *= System.Math.Pow(value, weight);
        else _acc += System.Math.Pow(value, M) * weight;
        _count += weight;
    }

    /// <summary>QC <c>mean_evaluate()</c>: the generalized mean of order <see cref="M"/> (0 if no samples).</summary>
    public readonly double Evaluate()
    {
        if (_count == 0.0) return 0.0;
        if (M == 0f) return System.Math.Pow(_acc, 1.0 / _count);
        return System.Math.Pow(_acc / _count, 1.0 / M);
    }
}

/// <summary>
/// Per-player anticheat tracking state — the C# successor to the <c>.anticheat_*</c> fields + the per-player
/// MEAN cells on a QC client edict (server/anticheat.qc). Kept as a class the <see cref="AntiCheat"/> table
/// owns, one per player.
/// </summary>
public sealed class PlayerAnticheatState
{
    public float JoinTime;
    public float FixAngleEndTime;

    // div0_evade
    public float EvadeOffset;
    public Vector3 EvadeVAngle;
    public Vector3 EvadeForwardInitial;

    // strafebot / snap-aim
    public Vector3 StrafebotMovementPrev;
    public Vector3 StrafebotForwardPrev;
    public Vector3 SnapbackPrev;

    // speedhack (old reconstruction)
    public float SpeedhackOffset;
    public float SpeedhackMovetimeCount;
    public float SpeedhackMovetimeFrac;

    // speedhack (new decaying accumulator)
    public float SpeedhackAccu;
    public float SpeedhackLastTime;

    // the MEANs
    public Mean Evade = new(5);
    public Mean StrafebotOld = new(5);
    public Mean StrafebotNew = new(5);
    public Mean Snapback = new(5);
    public Mean SnapaimSignal = new(5);
    public Mean SnapaimNoise = new(1);
    public Mean SnapaimM2 = new(2), SnapaimM3 = new(3), SnapaimM4 = new(4), SnapaimM7 = new(7), SnapaimM10 = new(10);
    public Mean Speedhack = new(5);
    public Mean SpeedhackM1 = new(1), SpeedhackM2 = new(2), SpeedhackM3 = new(3), SpeedhackM4 = new(4), SpeedhackM5 = new(5);
}

/// <summary>
/// One anticheat detector row — the C# successor to a QC <c>ANTICHEATS(X)</c> macro entry: a name, the value
/// to read from a player's state, and the display thresholds (<c>tmin</c> = min elapsed-since-join seconds for
/// a verdict; <c>mi</c>/<c>ma</c> = the clean/flagged bounds).
/// </summary>
public readonly struct AnticheatDetector
{
    public readonly string Name;
    public readonly Func<PlayerAnticheatState, double> Value;
    public readonly float Tmin, Mi, Ma;
    public AnticheatDetector(string name, Func<PlayerAnticheatState, double> value, float tmin, float mi, float ma)
    { Name = name; Value = value; Tmin = tmin; Mi = mi; Ma = ma; }
}

/// <summary>
/// The server anticheat — a faithful Godot-free port of server/anticheat.qc. It accumulates statistical
/// detectors (speedhack via two methods, strafebot movement/aim oddity, div0 evade, idle snap-aim, aim
/// snap-back) per player from the per-frame physics data, then formats a verdict report for the admin event
/// log / player stats. It does NOT punish — exactly like QC, the detectors only inform; a server admin reads
/// the report (<c>:anticheat:</c> event-log lines).
///
/// Faithful to QC: the power-mean accumulators and their exponents, the snap-aim suppression window after a
/// forced view change (teleport/respawn → <see cref="FixAngle"/>), the speedhack baseline adaptation, and the
/// global evade phase walk (<c>evasion_delta</c>) advanced twice per frame. The makevectors call uses the
/// shared <see cref="QMath.AngleVectors"/> (honoring the project's pitch convention).
/// </summary>
public sealed class AntiCheat
{
    private readonly Dictionary<Player, PlayerAnticheatState> _states = new();
    private float _evasionDelta;
    private readonly Random _rng;

    /// <summary>QC <c>ANTILAG_LATENCY</c> cap: snap-aim suppression scales with ping, clamped to 0.4 s.</summary>
    public const float MaxLatency = 0.4f;

    public AntiCheat(int seed = 0x4317) => _rng = new Random(seed);

    /// <summary>The detector table (QC the <c>ANTICHEATS(X)</c> expansion) — name, value reader, thresholds.</summary>
    public static readonly AnticheatDetector[] Detectors =
    {
        new("speedhack", s => s.Speedhack.Evaluate(), 240, 0, 9999),
        new("speedhack_m1", s => s.SpeedhackM1.Evaluate(), 240, 1.01f, 1.25f),
        new("speedhack_m2", s => s.SpeedhackM2.Evaluate(), 240, 1.01f, 1.25f),
        new("speedhack_m3", s => s.SpeedhackM3.Evaluate(), 240, 1.01f, 1.25f),
        new("speedhack_m4", s => s.SpeedhackM4.Evaluate(), 240, 1.01f, 1.25f),
        new("speedhack_m5", s => s.SpeedhackM5.Evaluate(), 240, 1.01f, 1.25f),
        new("div0_strafebot_old", s => s.StrafebotOld.Evaluate(), 120, 0.15f, 0.4f),
        new("div0_strafebot_new", s => s.StrafebotNew.Evaluate(), 120, 0.25f, 0.8f),
        new("div0_evade", s => s.Evade.Evaluate(), 120, 0.2f, 0.5f),
        new("idle_snapaim", s => s.SnapaimSignal.Evaluate() - s.SnapaimNoise.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_signal", s => s.SnapaimSignal.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_noise", s => s.SnapaimNoise.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_m2", s => s.SnapaimM2.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_m3", s => s.SnapaimM3.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_m4", s => s.SnapaimM4.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_m7", s => s.SnapaimM7.Evaluate(), 120, 0, 9999),
        new("idle_snapaim_m10", s => s.SnapaimM10.Evaluate(), 120, 0, 9999),
        new("div0_snapback", s => s.Snapback.Evaluate(), 120, 0, 9999),
    };

    /// <summary>The per-player state (created on demand), e.g. for the report or a test.</summary>
    public PlayerAnticheatState Of(Player p)
    {
        if (!_states.TryGetValue(p, out var st))
        {
            st = new PlayerAnticheatState();
            _states[p] = st;
        }
        return st;
    }

    public void Remove(Player p) => _states.Remove(p);

    /// <summary>QC <c>anticheat_init</c>: clear the speedhack baseline + stamp the join time.</summary>
    public void Init(Player p, float serverTime)
    {
        PlayerAnticheatState st = Of(p);
        st.SpeedhackOffset = 0f;
        st.JoinTime = serverTime;
    }

    /// <summary>QC <c>anticheat_prethink</c>: force a div0-evade reseed this frame.</summary>
    public void PreThink(Player p) => Of(p).EvadeOffset = 0f;

    /// <summary>QC <c>anticheat_fixangle</c>: suppress snap-aim detection after a forced view change (ping + 0.2 s).</summary>
    public void FixAngle(Player p, float serverTime, float pingMs)
        => Of(p).FixAngleEndTime = serverTime + System.Math.Min(MaxLatency, pingMs * 0.001f) + 0.2f;

    /// <summary>QC <c>anticheat_spectatecopy</c>: a spectator inherits the spectatee's evade-tracked view angle.</summary>
    public void SpectateCopy(Player viewer, Player spectatee) => viewer.Angles = Of(spectatee).EvadeVAngle;

    /// <summary>QC <c>anticheat_startframe</c>: advance the global evade phase walk.</summary>
    public void StartFrame(float frametime)
        => _evasionDelta += frametime * (0.5f + (float)_rng.NextDouble());

    /// <summary>
    /// QC <c>anticheat_endframe</c>: apply <see cref="FixAngle"/> to any client flagged for a forced view change
    /// this frame, then advance the evade phase walk again. <paramref name="fixAngleClients"/> are the players
    /// whose view was forcibly set (teleport/respawn) this frame.
    /// </summary>
    public void EndFrame(float frametime, float serverTime, IEnumerable<Player>? fixAngleClients = null)
    {
        if (fixAngleClients is not null)
            foreach (Player p in fixAngleClients)
                FixAngle(p, serverTime, PingMsOf(p));
        _evasionDelta += frametime * (0.5f + (float)_rng.NextDouble());
    }

    /// <summary>Per-player ping in ms, if the host wired one (defaults to 0 = a local/bot client).</summary>
    public Func<Player, float> PingProvider { get; set; } = static _ => 0f;
    private float PingMsOf(Player p) => PingProvider(p);

    /// <summary>
    /// QC <c>anticheat_physics</c>: the per-frame statistical accumulation for one real client. Drives the
    /// div0-evade, strafebot movement/aim, idle snap-aim + snap-back, and the two speedhack detectors from this
    /// frame's view angles + movement input. Call once per client per server tick (after movement).
    /// </summary>
    public void Physics(Player p, IMovementInput input, float serverTime, float frametime, float sysFrametime, float slowmo)
    {
        PlayerAnticheatState st = Of(p);
        Vector3 vAngle = input.ViewAngles;
        QMath.AngleVectors(vAngle, out Vector3 forward, out _, out _);

        // ---- div0_evade ----
        if (st.EvadeOffset == 0f)
        {
            float fr = System.Math.Abs(_evasionDelta - MathF.Floor(_evasionDelta) - 0.5f) * 2f; // triangle wave 0..1
            st.EvadeOffset = serverTime + sysFrametime * (3f * fr - 1f);
            st.EvadeVAngle = vAngle;
            st.EvadeForwardInitial = forward;
            st.Evade.Accumulate(0, 1);
        }
        else
        {
            if (serverTime < st.EvadeOffset)
                st.EvadeVAngle = vAngle;
            st.Evade.Accumulate(0.5 - 0.5 * Vector3.Dot(st.EvadeForwardInitial, forward), 1);
        }

        // ---- strafebot (movement oddity) ----
        st.StrafebotOld.Accumulate(MovementOddity(input.MoveValues, st.StrafebotMovementPrev), 1);
        st.StrafebotMovementPrev = input.MoveValues;

        // ---- strafebot_new / snap-aim (only with a prior forward and outside the fixangle window) ----
        if (st.StrafebotForwardPrev != Vector3.Zero && serverTime > st.FixAngleEndTime)
        {
            float cosangle = Vector3.Dot(st.StrafebotForwardPrev, forward);
            float angle = cosangle < -1f ? QMath.Pi : (cosangle > 1f ? 0f : MathF.Acos(cosangle));
            st.StrafebotNew.Accumulate(angle / QMath.Pi, 1);

            if (slowmo > 0f)
            {
                double dt = System.Math.Max(0.001f, frametime) / slowmo;
                double anglespeed = angle / dt;
                st.SnapaimSignal.Accumulate(anglespeed, dt);
                st.SnapaimNoise.Accumulate(anglespeed, dt);
                st.SnapaimM2.Accumulate(anglespeed, dt);
                st.SnapaimM3.Accumulate(anglespeed, dt);
                st.SnapaimM4.Accumulate(anglespeed, dt);
                st.SnapaimM7.Accumulate(anglespeed, dt);
                st.SnapaimM10.Accumulate(anglespeed, dt);

                float f = System.Math.Clamp((float)dt * 4f, 0f, 1f); // ~0.25 s horizon
                Vector3 aimMove = forward - st.StrafebotForwardPrev;
                Vector3 snapbackPrev = st.SnapbackPrev;
                float snapbackLen = snapbackPrev.Length();
                if (snapbackLen != 0f)
                {
                    double aimSnap = System.Math.Max(0.0, Vector3.Dot(aimMove, snapbackPrev) / -snapbackLen);
                    st.Snapback.Accumulate(aimSnap, dt);
                }
                st.SnapbackPrev = snapbackPrev * (1f - f) + aimMove * f;
            }
        }
        st.StrafebotForwardPrev = forward;

        // ---- generic speedhack (old reconstruction) ----
        st.SpeedhackMovetimeFrac += frametime;
        float whole = MathF.Floor(st.SpeedhackMovetimeFrac);
        st.SpeedhackMovetimeFrac -= whole;
        st.SpeedhackMovetimeCount += whole;
        float movetime = st.SpeedhackMovetimeFrac + st.SpeedhackMovetimeCount;
        float delta = movetime - serverTime;
        if (st.SpeedhackOffset == 0f)
            st.SpeedhackOffset = delta;
        else
        {
            st.Speedhack.Accumulate(System.Math.Max(0f, delta - st.SpeedhackOffset), 1);
            st.SpeedhackOffset += (delta - st.SpeedhackOffset) * frametime * 0.1f;
        }

        // ---- generic speedhack (new decaying accumulator) ----
        if (st.SpeedhackLastTime > 0f)
        {
            float dt2 = serverTime - st.SpeedhackLastTime;
            const float falloff = 0.2f;
            st.SpeedhackAccu *= MathF.Exp(-dt2 * falloff);
            st.SpeedhackAccu += frametime * falloff;
            st.SpeedhackLastTime = serverTime;
            st.SpeedhackM1.Accumulate(st.SpeedhackAccu, frametime);
            st.SpeedhackM2.Accumulate(st.SpeedhackAccu, frametime);
            st.SpeedhackM3.Accumulate(st.SpeedhackAccu, frametime);
            st.SpeedhackM4.Accumulate(st.SpeedhackAccu, frametime);
            st.SpeedhackM5.Accumulate(st.SpeedhackAccu, frametime);
        }
        else
        {
            st.SpeedhackAccu = 1f;
            st.SpeedhackLastTime = serverTime;
        }
    }

    /// <summary>QC <c>movement_oddity</c>: a 0..1 "this turn looks botlike" score from two movement vectors.</summary>
    public static double MovementOddity(Vector3 m0, Vector3 m1)
    {
        double cosangle = Vector3.Dot(QMath.Normalize(m0), QMath.Normalize(m1));
        if (cosangle >= 0) return 0;
        return 0.5 - 0.5 * System.Math.Cos(cosangle * cosangle * (4.0 * QMath.Pi));
    }

    /// <summary>QC <c>anticheat_display</c>: format a detector value with its N/Y/- verdict.</summary>
    public static string Display(double f, float elapsed, float tmin, float mi, float ma)
    {
        string s = f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        if (elapsed >= tmin)
        {
            if (f <= mi) return s + ":N"; // clean
            if (f >= ma) return s + ":Y"; // flagged
        }
        return s + ":-"; // inconclusive
    }

    /// <summary>
    /// QC <c>anticheat_report_to_eventlog</c>: emit the per-player anticheat verdict lines to a sink (the event
    /// log). No-op without state. The sink receives each <c>:anticheat:...</c> line.
    /// </summary>
    public void ReportToEventLog(Player p, float serverTime, Action<string> echo)
    {
        if (!_states.TryGetValue(p, out var st)) return;
        float elapsed = serverTime - st.JoinTime;
        echo($":anticheat:_time:{p.PlayerId}:{elapsed:0.######}");
        foreach (AnticheatDetector d in Detectors)
            echo($":anticheat:{d.Name}:{Display(d.Value(st), elapsed, d.Tmin, d.Mi, d.Ma)}");
    }

    /// <summary>QC <c>PLAYERSTATS_ANTICHEAT</c> (playerstats.qh): the event-id prefix for the anticheat feed.</summary>
    public const string PlayerStatsPrefix = "anticheat-";

    /// <summary>
    /// QC <c>anticheat_register_to_playerstats</c> (anticheat.qc:229): pre-register every anticheat event id —
    /// <c>anticheat-_time</c> plus one per detector — with the game-report event table at init, BEFORE any value
    /// is emitted. Mirrors the exact id construction <see cref="ReportToPlayerStats"/> uses, so the register/emit
    /// contract stays in lockstep (QC's <c>PlayerStats_GameReport_Event</c> drops unregistered events).
    /// </summary>
    public static void RegisterPlayerStats(Action<string> addEvent)
    {
        addEvent(PlayerStatsPrefix + "_time");
        foreach (AnticheatDetector d in Detectors)
            addEvent(PlayerStatsPrefix + d.Name);
    }

    /// <summary>
    /// QC <c>anticheat_report_to_playerstats</c>: feed the raw detector values into a player-stats sink
    /// (event id, value). No-op without state.
    /// </summary>
    public void ReportToPlayerStats(Player p, float serverTime, Action<string, double> add)
    {
        if (!_states.TryGetValue(p, out var st)) return;
        add(PlayerStatsPrefix + "_time", serverTime - st.JoinTime);
        foreach (AnticheatDetector d in Detectors)
            add(PlayerStatsPrefix + d.Name, d.Value(st));
    }
}
