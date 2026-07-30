using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Per-bot aiming state + logic — the C# port of server/bot/default/aim.qc (bot_aimdir, bot_shotlead,
/// the fire-decision half of bot_aim) and the relevant fields from havocbot.qh.
///
/// The model: each frame the brain hands <see cref="AimAt"/> a desired aim *direction* (already
/// lead-compensated via <see cref="ShotLead"/>). The aimer turns the current view angles toward it,
/// rate-limited and error-injected by skill, mutating <see cref="ViewAngles"/> in place. A separate
/// <see cref="ShouldFire"/> gate decides whether the bot may pull the trigger this frame, mirroring QC's
/// <c>bot_firetimer</c> + max-fire-deviation cone.
///
/// One instance per bot, owned by <see cref="BotBrain"/>. Holds the higher-order aim filters
/// (bot_*_order_aimfilter) that smooth/anticipate target motion the way the QC bot does.
/// </summary>
public sealed class BotAim
{
    // ---- tuning defaults (QC autocvar_bot_ai_aimskill_*, defaults from xonotic-server.cfg) ----
    // These are the FALLBACKS only; the live values are read each AimAt frame from the
    // bot_ai_aimskill_* cvars (QC reads autocvar_* every frame), so server-side aim retuning
    // via those cvars takes effect. Defaults below match stock xonotic-server.cfg.
    private const float OffsetDefault = 1.8f;      // bot_ai_aimskill_offset — magnitude of injected aim error
    private const float ThinkDefault = 1f;         // bot_ai_aimskill_think — aiming velocity (<1 = slower)
    private const float MouseDefault = 1f;         // bot_ai_aimskill_mouse — how much of the filters apply
    private const float FixedRateDefault = 15f;    // bot_ai_aimskill_fixedrate — distance-scaled turn rate
    private const float BlendRateDefault = 2f;     // bot_ai_aimskill_blendrate — floor turn rate
    private const float Mix1Default = 0.01f, Mix2Default = 0.075f, Mix3Default = 0.01f, Mix4Default = 0.0375f, Mix5Default = 0.01f;
    private const float Filt1Default = 0.2f, Filt2Default = 0.2f, Filt3Default = 0.1f, Filt4Default = 0.2f, Filt5Default = 0.25f;

    /// <summary>
    /// QC bot_ai_aimskill_firetolerance: gate fire on the deviation cone (vs a flat 0.2s timer).
    /// Live value is read from the bot_ai_aimskill_firetolerance cvar each frame (default on = true);
    /// set this field to force a value when running headless without the cvar service.
    /// </summary>
    public bool FireTolerance = true;

    /// <summary>QC SUPERBOT (skill &gt; 100): perfect, instant, error-free aim.</summary>
    public const float SuperbotSkill = 100f;

    /// <summary>
    /// QC <c>.bot_aggresskill</c> (server/bot/default/bot.qc:282): the per-bot aggression modifier the brain
    /// stamps each think. Added to <c>skill</c> in the fire decision (aim.qc:325-328) so an aggressive bot fires
    /// more eagerly at long range and recovers faster between shots. Default 0 (stock READSKILL reward 0).
    /// </summary>
    public float AggresSkill;

    /// <summary>
    /// QC <c>.bot_aimskill</c> (server/bot/default/bot.qc:285): the per-bot aiming modifier the brain stamps
    /// each think. Added to <c>skill</c> in the max-fire-deviation formula (aim.qc:372) so a higher-aim bot
    /// requires a tighter aim before shooting. Default 0 (stock READSKILL reward 0).
    /// </summary>
    public float AimSkill;

    /// <summary>
    /// Current view angles in degrees (pitch X, yaw Y, roll Z) — QC <c>.v_angle</c>. The brain copies this
    /// into the per-frame MovementInput.ViewAngles. Pitch uses Quake convention (down-positive).
    /// </summary>
    public Vector3 ViewAngles;

    /// <summary>Eye offset added to origin to get the shot origin (QC <c>.view_ofs</c>).</summary>
    public Vector3 ViewOffset = new(0f, 0f, 24f);

    /// <summary>Last computed shot origin (QC global <c>shotorg</c>): origin + view offset.</summary>
    public Vector3 ShotOrigin;

    /// <summary>Last computed shot direction (QC global <c>shotdir</c>): forward from <see cref="ViewAngles"/>.</summary>
    public Vector3 ShotDir;

    // QC aim-filter / timing state (havocbot.qh fields, prefixed bot_)
    private Vector3 _olddesiredang;
    private Vector3 _mouseaim;
    private Vector3 _f1, _f2, _f3, _f4, _f5;       // bot_*_order_aimfilter
    private float _badAimTime;
    private Vector3 _badAimOffset;
    private float _aimThinkTime;
    private float _prevAimTime;
    private float _fireTimer;
    private bool _initialized;

    private readonly Random _rng;

    public BotAim(int seed = 0) => _rng = seed == 0 ? new Random() : new Random(seed);

    private float Random01() => (float)_rng.NextDouble();
    private float RandomCentered() => (float)(_rng.NextDouble() * 2.0 - 1.0); // QC crandom()
    private Vector3 RandomVec() => new(RandomCentered(), RandomCentered(), RandomCentered());

    /// <summary>
    /// Reset aim state to the current view (QC bot_aim_reset). Call when the bot (re)spawns or teleports so
    /// the filters don't carry stale motion across a discontinuity.
    /// </summary>
    public void Reset(float now)
    {
        _mouseaim = ViewAngles;
        _olddesiredang = ViewAngles;
        _badAimTime = 0f;
        _aimThinkTime = now;
        _prevAimTime = now;
        _f1 = _f2 = _f3 = _f4 = _f5 = Vector3.Zero;
        _fireTimer = 0f;
        _initialized = true;
    }

    /// <summary>
    /// Predict where to aim to hit a moving target (QC bot_shotlead): lead the target by the projectile
    /// travel time. <paramref name="shotSpeed"/> &lt;= 0 means hitscan (no lead). For a ballistic (lobbed)
    /// weapon, combine this with <see cref="BallisticArc"/> to also raise the aim for gravity drop.
    /// </summary>
    public Vector3 ShotLead(Vector3 targetOrigin, Vector3 targetVelocity, float shotSpeed, float shotDelay = 0f)
    {
        if (shotSpeed <= 0f)
            return targetOrigin; // hitscan: aim straight at it
        float travel = shotDelay + (targetOrigin - ShotOrigin).Length() / shotSpeed;
        return targetOrigin + targetVelocity * travel;
    }

    /// <summary>
    /// Compute an aim DIRECTION that arcs a gravity-affected projectile onto <paramref name="leadPoint"/>
    /// (QC findtrajectorywithleading for the mortar/nade lob). Solves the ballistic launch elevation for the
    /// given <paramref name="shotSpeed"/> and <paramref name="gravity"/> so the shot drops onto the target:
    /// it keeps the horizontal aim straight at the lead point and raises the vertical component by the
    /// gravity drop over the projectile's flight time. Falls back to the straight-line direction if the
    /// target is out of ballistic range (too far to reach at this speed).
    /// </summary>
    public Vector3 BallisticArc(Vector3 leadPoint, float shotSpeed, float gravity)
    {
        Vector3 delta = leadPoint - ShotOrigin;
        var flat = new Vector3(delta.X, delta.Y, 0f);
        float flatDist = flat.Length();
        Vector3 straight = QMath.Normalize(delta);
        if (shotSpeed <= 0f || gravity <= 0f || flatDist < 1f)
            return straight;

        // flight time to cover the horizontal distance at the projectile's horizontal speed.
        // (approximate the horizontal speed as the full shot speed; the elevation refines below.)
        float flightTime = flatDist / shotSpeed;
        // gravity drop over that time: rise the aim point by 0.5*g*t^2 so the projectile falls onto target.
        float drop = 0.5f * gravity * flightTime * flightTime;
        Vector3 aimPoint = leadPoint + new Vector3(0f, 0f, drop);
        Vector3 dir = QMath.Normalize(aimPoint - ShotOrigin);

        // out-of-range guard: if even a 45° launch can't reach (g*d > v^2), just aim straight.
        if (gravity * flatDist > shotSpeed * shotSpeed)
            return straight;
        return dir == Vector3.Zero ? straight : dir;
    }

    /// <summary>
    /// Turn the view toward direction <paramref name="dir"/> (QC bot_aimdir). Mutates <see cref="ViewAngles"/>.
    /// <paramref name="skill"/> scales error, turn rate and smoothing; at <see cref="SuperbotSkill"/> the aim
    /// snaps exactly onto the target. <paramref name="maxFireDeviation"/> (degrees) arms the fire timer when
    /// the view is within the cone (pass 0 to skip the fire decision, e.g. when only steering).
    /// </summary>
    public void AimAt(Vector3 dir, Vector3 origin, float skill, float dt, float now, float maxFireDeviation, bool hasEnemy)
    {
        if (!_initialized) Reset(now);

        // keep yaw sane
        ViewAngles.Y -= MathF.Floor(ViewAngles.Y / 360f) * 360f;
        ViewAngles.Z = 0f;

        // SUPERBOT: exact aim, fire next tick
        if (skill > SuperbotSkill)
        {
            var ang = QMath.VecToAngles(QMath.Normalize(dir));
            ang.X *= -1f; // QC negates pitch here
            ViewAngles = ang;
            UpdateShotVectors(origin);
            _fireTimer = now + 0.001f;
            return;
        }

        if (dir == Vector3.Zero) return; // invalid (bot overlaps target)

        // Live aim-skill tuning (QC reads autocvar_bot_ai_aimskill_* every frame). Falls back to the
        // stock defaults when the cvar service is absent (headless tests), so behaviour is unchanged.
        float offsetCvar = Cvars.FloatOr("bot_ai_aimskill_offset", OffsetDefault);
        float thinkCvar = Cvars.FloatOr("bot_ai_aimskill_think", ThinkDefault);
        float mouseCvar = Cvars.FloatOr("bot_ai_aimskill_mouse", MouseDefault);
        float fixedRate = Cvars.FloatOr("bot_ai_aimskill_fixedrate", FixedRateDefault);
        float blendRate = Cvars.FloatOr("bot_ai_aimskill_blendrate", BlendRateDefault);
        float mix1 = Cvars.FloatOr("bot_ai_aimskill_order_mix_1st", Mix1Default);
        float mix2 = Cvars.FloatOr("bot_ai_aimskill_order_mix_2nd", Mix2Default);
        float mix3 = Cvars.FloatOr("bot_ai_aimskill_order_mix_3th", Mix3Default);
        float mix4 = Cvars.FloatOr("bot_ai_aimskill_order_mix_4th", Mix4Default);
        float mix5 = Cvars.FloatOr("bot_ai_aimskill_order_mix_5th", Mix5Default);
        float filt1 = Cvars.FloatOr("bot_ai_aimskill_order_filter_1st", Filt1Default);
        float filt2 = Cvars.FloatOr("bot_ai_aimskill_order_filter_2nd", Filt2Default);
        float filt3 = Cvars.FloatOr("bot_ai_aimskill_order_filter_3th", Filt3Default);
        float filt4 = Cvars.FloatOr("bot_ai_aimskill_order_filter_4th", Filt4Default);
        float filt5 = Cvars.FloatOr("bot_ai_aimskill_order_filter_5th", Filt5Default);

        // walking (no enemy) gets a gentler minimum skill so turns look natural
        float effSkill = hasEnemy ? skill : MathF.Max(4f, skill);

        // periodic aim-error offset (QC bot_badaimoffset)
        if (now >= _badAimTime)
        {
            _badAimTime = MathF.Max(_badAimTime + 0.2f + 0.3f * Random01(), now);
            float f = QMath.Clamp(1f - 0.1f * effSkill, 0f, 1f);
            _badAimOffset = RandomVec() * f * offsetCvar;
            _badAimOffset.X *= 0.7f; // less vertical error
        }
        float enemyFactor = hasEnemy ? 5f : 2f;

        var desiredang = QMath.VecToAngles(dir) + _badAimOffset * enemyFactor;
        if (desiredang.X >= 180f) desiredang.X -= 360f;
        desiredang.X = QMath.Clamp(-desiredang.X, -90f, 90f);
        desiredang.Z = ViewAngles.Z;

        // higher-order filters anticipate the coming aim direction
        var diffang = WrapPitchYaw(desiredang - _olddesiredang);
        _olddesiredang = desiredang;

        float deltaT = MathF.Max(1e-4f, now - _prevAimTime);
        _prevAimTime = now;
        _f1 += (diffang * (1f / deltaT) - _f1) * QMath.Clamp(filt1, 0f, 1f);
        _f2 += (_f1 - _f2) * QMath.Clamp(filt2, 0f, 1f);
        _f3 += (_f2 - _f3) * QMath.Clamp(filt3, 0f, 1f);
        _f4 += (_f3 - _f4) * QMath.Clamp(filt4, 0f, 1f);
        _f5 += (_f4 - _f5) * QMath.Clamp(filt5, 0f, 1f);

        float blend = QMath.Clamp(effSkill, 0f, 10f) * 0.1f;
        desiredang += blend * (_f1 * mix1 + _f2 * mix2 + _f3 * mix3 + _f4 * mix4 + _f5 * mix5);
        desiredang.X = QMath.Clamp(desiredang.X, -90f, 90f);

        // mouse-think: jitter the intermediate aim target on a slower clock
        diffang = WrapPitchYaw(desiredang - _mouseaim);
        if (now >= _aimThinkTime)
        {
            _aimThinkTime = MathF.Max(_aimThinkTime + 0.5f - 0.05f * effSkill, now);
            _mouseaim += diffang * (1f - Random01() * 0.1f * QMath.Bound(1f, 10f - effSkill, 10f));
        }

        diffang = WrapPitchYaw(_mouseaim - desiredang);
        desiredang += diffang * QMath.Clamp(thinkCvar, 0f, 1f);

        // final turn toward desiredang, rate-limited by skill
        diffang = WrapPitchYaw(desiredang - ViewAngles);
        float dist = diffang.Length();

        float fixedrate = fixedRate / QMath.Bound(1f, dist, 1000f);
        float r = MathF.Max(fixedrate, blendRate);
        r = QMath.Bound(deltaT, r * deltaT * (2f + MathF.Pow(effSkill, 3f) * 0.005f - Random01()), 1f);
        ViewAngles += diffang * (r + (1f - r) * QMath.Clamp(1f - mouseCvar, 0f, 1f));
        ViewAngles.Z = 0f;
        ViewAngles.Y -= MathF.Floor(ViewAngles.Y / 360f) * 360f;

        if (maxFireDeviation <= 0f)
            return;

        UpdateShotVectors(origin);

        // QC: !autocvar_bot_ai_aimskill_firetolerance → flat 0.2s fire timer (no deviation cone).
        // Read the live cvar (default 1/on); fall back to the FireTolerance field when headless.
        bool fireTolerance = Api.Services is not null
            ? Cvars.Bool("bot_ai_aimskill_firetolerance")
            : FireTolerance;
        if (!fireTolerance)
        {
            _fireTimer = now + 0.2f;
            return;
        }

        // fire only if the actual view direction is within the deviation cone of the wanted direction
        var deviation = WrapPitchYaw(QMath.VecToAngles(dir) - QMath.VecToAngles(ShotDir));
        if (MathF.Abs(deviation.X) < maxFireDeviation && MathF.Abs(deviation.Y) < maxFireDeviation)
        {
            // QC bot_aim fire gate (aim.qc:325-328), including the aggression term: always fire when the impact
            // point is near (close-range fast path, the range band scaling with skill+aggresskill), otherwise
            // the long-range fire chance rises with skill+aggresskill — a low-skill/timid bot hesitates at
            // distance (the Random01*Random01 vs the bound term is QC's random fire-delay at long range). The
            // fire-timer cooldown also shortens with skill+aggresskill.
            float aggr = skill + AggresSkill; // QC (skill + this.bot_aggresskill)
            var tr = Api.Trace.Trace(ShotOrigin, Vector3.Zero, Vector3.Zero,
                ShotOrigin + ShotDir * 1000f, MoveFilter.Normal, null);
            float hitDist = (tr.EndPos - ShotOrigin).Length();
            if (hitDist < 500f + 500f * QMath.Bound(0f, aggr, 10f)
                || Random01() * Random01() > QMath.Bound(0f, aggr * 0.05f, 1f))
            {
                _fireTimer = now + QMath.Bound(0.1f, 0.5f - aggr * 0.05f, 0.5f);
            }
        }
    }

    /// <summary>
    /// Compute the max fire deviation cone (degrees) for a shot at a target (QC bot_aim's
    /// distance-&gt;maxfiredeviation formula). Wider for closer targets / lower skill.
    /// </summary>
    public float MaxFireDeviation(Vector3 leadPoint, float skill, bool accurate)
    {
        float dist = MathF.Max(10f, (leadPoint - ShotOrigin).Length());
        float dev = 1000f / (dist - 9f) - 0.35f;
        float f = accurate ? 1f : 1.6f;
        // QC aim.qc:372: f += bound(0, (10 - (skill + this.bot_aimskill)) * 0.3, 3) — a higher aim skill
        // narrows the fire cone (tighter aim required before the bot will shoot).
        f += QMath.Bound(0f, (10f - (skill + AimSkill)) * 0.3f, 3f);
        return MathF.Min(90f, dev * f);
    }

    /// <summary>True if the fire timer is armed this frame (QC <c>time &gt; this.bot_firetimer</c> inverted).</summary>
    public bool ShouldFire(float now) => now <= _fireTimer;

    /// <summary>Recompute <see cref="ShotOrigin"/>/<see cref="ShotDir"/> from current view (QC makevectors block).</summary>
    public void UpdateShotVectors(Vector3 origin)
    {
        QMath.AngleVectors(ViewAngles, out var forward, out _, out _);
        ShotOrigin = origin + ViewOffset;
        ShotDir = forward;
    }

    /// <summary>Wrap an angle delta's pitch/yaw into (-180, 180] (QC diffang yaw wrap, extended to pitch).</summary>
    private static Vector3 WrapPitchYaw(Vector3 a)
    {
        a.Y -= MathF.Floor(a.Y / 360f) * 360f;
        if (a.Y >= 180f) a.Y -= 360f;
        while (a.X < -180f) a.X += 360f;
        while (a.X > 180f) a.X -= 360f;
        return a;
    }
}
