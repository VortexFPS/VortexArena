using System;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Builds the policy's input vector, and owns its layout.
///
/// <para>This file is half of a contract with <c>tools/neural/obs_layout.py</c>. The layout constants below
/// are generated into that file by <c>tools/neural/sync-layout.py</c>, and
/// <c>NeuralObservationTests.LayoutMatchesPythonMirror</c> fails if the two drift. A silent layout skew
/// would not crash anything: the network would keep producing plausible actions from misread inputs, and the
/// only symptom would be a policy that got worse for no reason.</para>
///
/// <para><b>Everything is egocentric and frame-anchored on the direction of travel, not the view.</b> The
/// policy's view can be yanked anywhere by combat. If perception rotated with it, every observation during a
/// fight would come from a distribution the policy had barely trained on. Anchoring on the goal direction
/// keeps "a ledge on my left" meaning one thing whichever way the crosshair points, and it is what makes the
/// aim constraint learnable rather than destabilising.</para>
/// </summary>
public sealed class NeuralObservation
{
    // ---- section sizes ----

    /// <summary>
    /// Velocity(3), speed, onground, slope-underfoot, height-above-floor, clearance-overhead, airtime,
    /// ducked, waterlevel, health, armor, on-ladder, on-mover.
    ///
    /// <para>The three ground terms come out of the baked field, not a trace. There is no stored ground
    /// normal on the entity, and spending a trace per think to recover one would cost more than the whole
    /// geometry section put together.</para>
    /// </summary>
    public const int ProprioFloats = 15;

    /// <summary>Weapon one-hot (8) + movement-weapon ammo (3) + weapon-permit flag.</summary>
    public const int WeaponFloats = 12;

    /// <summary>Goal dir(3), log distance, corridor A dir(3), corridor B dir(3), urgency.</summary>
    public const int GoalFloats = 11;

    /// <summary>Required-aim delta from the current view (2), aim weight, aim-required flag.</summary>
    public const int AimFloats = 4;

    /// <summary>Two ticks of (velocity(3), onground) so the bunnyhop phase is observable.</summary>
    public const int HistoryFloats = 8;

    /// <summary>The previous action vector, which is what makes the jerk penalty learnable.</summary>
    public const int PrevActionFloats = 8;

    /// <summary>Short box sweeps for what the baked field cannot know: doors mid-travel, players, movers.</summary>
    public const int TraceFanRays = 6;
    public const int TraceFanFloats = TraceFanRays * 2;

    /// <summary>Total observation length. The weight file carries the same number and load fails on a mismatch.</summary>
    public static int Size =>
        ProprioFloats + WeaponFloats + GoalFloats + AimFloats + HistoryFloats + PrevActionFloats
        + NavField.ProbeFloats + MapFeatures.ObservationFloats + TraceFanFloats;

    // ---- section offsets, for the tests and the Python mirror ----
    public static int OffProprio => 0;
    public static int OffWeapon => OffProprio + ProprioFloats;
    public static int OffGoal => OffWeapon + WeaponFloats;
    public static int OffAim => OffGoal + GoalFloats;
    public static int OffHistory => OffAim + AimFloats;
    public static int OffPrevAction => OffHistory + HistoryFloats;
    public static int OffNavField => OffPrevAction + PrevActionFloats;
    public static int OffFeatures => OffNavField + NavField.ProbeFloats;
    public static int OffTraceFan => OffFeatures + MapFeatures.ObservationFloats;

    /// <summary>The movement weapons the policy may select, in action-head order.</summary>
    public static readonly string[] MovementWeapons = { "blaster", "crylink", "devastator" };

    // Per-bot history. One observation builder per bot.
    private Vector3 _velPrev1, _velPrev2;
    private float _groundPrev1, _groundPrev2;
    private readonly float[] _prevAction = new float[PrevActionFloats];
    private float _lastGroundTime;
    private bool _historyPrimed;

    /// <summary>Reset the history at a spawn or teleport, so the discontinuity is not read as motion.</summary>
    public void Reset(Vector3 velocity, bool onGround, float now)
    {
        _velPrev1 = _velPrev2 = velocity;
        _groundPrev1 = _groundPrev2 = onGround ? 1f : 0f;
        Array.Clear(_prevAction);
        _lastGroundTime = now;
        _historyPrimed = true;
    }

    /// <summary>Record the action the policy just produced, so the next observation carries it.</summary>
    public void NoteAction(in NeuralAction action)
    {
        _prevAction[0] = action.MoveForward;
        _prevAction[1] = action.MoveRight;
        _prevAction[2] = action.Jump ? 1f : 0f;
        _prevAction[3] = action.Crouch ? 1f : 0f;
        _prevAction[4] = action.YawDelta / NeuralAction.MaxYawRate;
        _prevAction[5] = action.PitchDelta / NeuralAction.MaxPitchRate;
        _prevAction[6] = action.Attack1 ? 1f : 0f;
        _prevAction[7] = action.Attack2 ? 1f : 0f;
    }

    /// <summary>
    /// Fill <paramref name="dest"/> (length <see cref="Size"/>) for this think.
    /// </summary>
    /// <param name="bot">The controlled player.</param>
    /// <param name="intent">What the tactician wants.</param>
    /// <param name="field">The baked navigation field, or null (the geometry section reads as "no data").</param>
    /// <param name="features">Map furniture, or null.</param>
    /// <param name="viewAngles">The bot's current view, which the aim delta is measured against.</param>
    /// <param name="now">Sim time, for the airtime clock.</param>
    /// <param name="traceFan">Whether to spend the six box sweeps this think.</param>
    public void Build(Player bot, in MoveIntent intent, NavField? field, MapFeatures? features,
        Vector3 viewAngles, float now, bool traceFan, Span<float> dest)
    {
        if (dest.Length < Size) throw new ArgumentException($"need {Size} floats", nameof(dest));
        dest[..Size].Clear();

        if (!_historyPrimed) Reset(bot.Velocity, bot.OnGround, now);

        // The frame everything egocentric is expressed in: horizontal direction to the goal, falling back to
        // the velocity and then to the view when the bot is stationary and goalless.
        Vector3 toGoal = intent.GoalPos - bot.Origin;
        Vector3 frame = new(toGoal.X, toGoal.Y, 0f);
        if (frame.LengthSquared() < 1f)
        {
            frame = new Vector3(bot.Velocity.X, bot.Velocity.Y, 0f);
            if (frame.LengthSquared() < 1f)
            {
                QMath.AngleVectors(viewAngles, out Vector3 fwd, out _, out _);
                frame = new Vector3(fwd.X, fwd.Y, 0f);
            }
        }
        float fx = frame.X, fy = frame.Y;
        float flen = MathF.Sqrt(fx * fx + fy * fy);
        if (flen < 1e-4f) { fx = 1f; fy = 0f; } else { fx /= flen; fy /= flen; }

        bool onGround = bot.OnGround;
        if (onGround) _lastGroundTime = now;

        // ---- proprioception ----
        int w = OffProprio;
        Vector3 vLocal = ToFrame(bot.Velocity, fx, fy);
        const float speedScale = 1f / 400f;   // a shade above sv_maxspeed, so ordinary running lands near 1
        dest[w++] = vLocal.X * speedScale;
        dest[w++] = vLocal.Y * speedScale;
        dest[w++] = vLocal.Z * speedScale;
        dest[w++] = bot.Velocity.Length() * speedScale;
        dest[w++] = onGround ? 1f : 0f;

        // Ground geometry from the baked field: slope, how far the feet are above the surface, and the
        // headroom. All three are array reads.
        if (field is not null && field.TrySampleBelow(bot.Origin, out FloorSpan under))
        {
            dest[w++] = under.SlopeDot / 255f;
            dest[w++] = Math.Clamp((bot.Origin.Z - under.FloorZ) / BotNavigation.StepHeight, -8f, 16f);
            dest[w++] = Math.Clamp(under.Clearance / (float)NavField.MinStandClearance, 0f, 4f);
        }
        else
        {
            dest[w++] = 1f;   // assume flat when the field has nothing to say
            dest[w++] = 0f;
            dest[w++] = 1f;
        }

        dest[w++] = MathF.Min(now - _lastGroundTime, 4f);           // airtime, saturating at 4 s
        dest[w++] = bot.IsDucked ? 1f : 0f;
        dest[w++] = bot.WaterLevel * 0.333f;
        dest[w++] = MathF.Min(bot.Health / 100f, 2f);
        dest[w++] = MathF.Min(bot.GetResource(ResourceType.Armor) / 100f, 2f);
        dest[w++] = bot.LadderEntity is not null ? 1f : 0f;
        dest[w++] = bot.GroundEntity is { IsFreed: false } ge && ge.ClassName.StartsWith("func_", StringComparison.Ordinal) ? 1f : 0f;

        // ---- weapon state ----
        w = OffWeapon;
        int weaponSlot = WeaponIndex(bot);
        for (int i = 0; i < 8; i++) dest[w + i] = 0f;
        if (weaponSlot >= 0 && weaponSlot < 8) dest[w + weaponSlot] = 1f;
        w += 8;
        for (int i = 0; i < MovementWeapons.Length; i++)
            dest[w++] = MovementWeaponReady(bot, MovementWeapons[i]) ? 1f : 0f;
        dest[w++] = intent.WeaponMovementAllowed ? 1f : 0f;

        // ---- goal ----
        w = OffGoal;
        WriteDir(dest, ref w, toGoal, fx, fy);
        dest[w++] = MathF.Log(1f + toGoal.Length()) * 0.15f;
        WriteDir(dest, ref w, intent.CorridorA - bot.Origin, fx, fy);
        WriteDir(dest, ref w, intent.CorridorB - bot.Origin, fx, fy);
        dest[w++] = intent.Urgency;

        // ---- aim constraint ----
        // The delta is measured from the CURRENT view, in units of the per-think turn budget, so "1.0" means
        // "one think of turning gets you there". That framing is what lets the same policy work at any think
        // interval without relearning the scale.
        w = OffAim;
        if (intent.AimRequired)
        {
            Vector3 d = WrapPitchYaw(intent.RequiredAimAngles - viewAngles);
            dest[w++] = Math.Clamp(d.Y / NeuralAction.MaxYawRate, -8f, 8f);
            dest[w++] = Math.Clamp(d.X / NeuralAction.MaxPitchRate, -8f, 8f);
            dest[w++] = intent.AimWeight;
            dest[w++] = 1f;
        }
        else
        {
            w += 4;
        }

        // ---- motion history ----
        w = OffHistory;
        Vector3 h1 = ToFrame(_velPrev1, fx, fy);
        Vector3 h2 = ToFrame(_velPrev2, fx, fy);
        dest[w++] = h1.X * speedScale; dest[w++] = h1.Y * speedScale; dest[w++] = h1.Z * speedScale;
        dest[w++] = _groundPrev1;
        dest[w++] = h2.X * speedScale; dest[w++] = h2.Y * speedScale; dest[w++] = h2.Z * speedScale;
        dest[w++] = _groundPrev2;

        // ---- previous action ----
        for (int i = 0; i < PrevActionFloats; i++) dest[OffPrevAction + i] = _prevAction[i];

        // ---- baked geometry ----
        if (field is not null)
            field.SampleRing(bot.Origin, new Vector3(fx, fy, 0f), dest.Slice(OffNavField, NavField.ProbeFloats));
        else
            FillNoField(dest.Slice(OffNavField, NavField.ProbeFloats));

        // ---- map furniture ----
        features?.WriteObservation(bot.Origin, new Vector3(fx, fy, 0f),
            dest.Slice(OffFeatures, MapFeatures.ObservationFloats));

        // ---- the live trace fan ----
        if (traceFan)
            WriteTraceFan(bot, intent, fx, fy, dest.Slice(OffTraceFan, TraceFanFloats));

        // roll the history AFTER the observation is built, so this think sees the previous two ticks.
        _velPrev2 = _velPrev1; _groundPrev2 = _groundPrev1;
        _velPrev1 = bot.Velocity; _groundPrev1 = onGround ? 1f : 0f;
    }

    /// <summary>
    /// What the geometry section reads when no field is baked: floor at foot level, full clearance, no
    /// hazard. Deliberately "flat open ground" rather than zeros, because a zeroed height field would read
    /// as a floor at exactly foot height with zero headroom, i.e. a bot buried in rock.
    /// </summary>
    private static void FillNoField(Span<float> dest)
    {
        for (int i = 0; i + 2 < dest.Length; i += 3)
        {
            dest[i] = 0f;
            dest[i + 1] = 1f;
            dest[i + 2] = -1f;
        }
    }

    /// <summary>
    /// Six short box sweeps at hull height: forward, forward-left, forward-right, left, right, and down-
    /// forward (the ledge probe). Each reports the sweep fraction and how walkable the surface hit was.
    ///
    /// <para>Budget: 6 sweeps per think, 20 Hz, 16 bots is 1,920 sweeps/s. At the 0.0266 ms a 256 qu box
    /// sweep costs (<c>TracePerfBench</c>) that is 51 ms/s, about 5% of a core. It stays inside the existing
    /// 96-traces-per-tick discipline in <see cref="BotTracewalk"/>.</para>
    /// </summary>
    private static void WriteTraceFan(Player bot, in MoveIntent intent, float fx, float fy, Span<float> dest)
    {
        ReadOnlySpan<float> angles = stackalloc float[TraceFanRays] { 0f, 0.5f, -0.5f, 1.4f, -1.4f, 0f };
        const float reach = 224f;

        Vector3 mins = intent.HullMins, maxs = intent.HullMaxs;
        // Shrink the sweep box a little so it clears doorways the real hull squeezes through; a full-width
        // probe reports "blocked" on every gap exactly as wide as the player.
        mins = new Vector3(mins.X * 0.6f, mins.Y * 0.6f, mins.Z * 0.5f);
        maxs = new Vector3(maxs.X * 0.6f, maxs.Y * 0.6f, maxs.Z * 0.5f);

        Vector3 eye = bot.Origin + new Vector3(0f, 0f, 8f);
        for (int i = 0; i < TraceFanRays; i++)
        {
            float a = angles[i];
            float cs = MathF.Cos(a), sn = MathF.Sin(a);
            float dx = fx * cs - fy * sn;
            float dy = fx * sn + fy * cs;
            // The last ray is the ledge probe: forward and down, to catch a drop the baked field smooths over
            // when a ledge falls between lattice columns.
            Vector3 dir = i == TraceFanRays - 1
                ? Vector3.Normalize(new Vector3(dx, dy, -1.2f))
                : new Vector3(dx, dy, 0f);

            TraceResult tr = Api.Trace.Trace(eye, mins, maxs, eye + dir * reach, MoveFilter.Normal, bot);
            dest[i * 2] = tr.Fraction;
            dest[i * 2 + 1] = tr.Fraction >= 1f ? 0f : tr.PlaneNormal.Z;
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Rotate a world vector into the goal frame: (forward, right, up). Right is (fy, -fx), the same
    /// handedness <c>QMath.AngleVectors</c> produces, so the observation's "right" and the action's
    /// <see cref="NeuralAction.MoveRight"/> mean the same thing.
    /// </summary>
    private static Vector3 ToFrame(Vector3 v, float fx, float fy)
        => new(v.X * fx + v.Y * fy, v.X * fy - v.Y * fx, v.Z);

    private static void WriteDir(Span<float> dest, ref int w, Vector3 delta, float fx, float fy)
    {
        const float scale = 1f / 512f;
        Vector3 l = ToFrame(delta, fx, fy);
        dest[w++] = Math.Clamp(l.X * scale, -4f, 4f);
        dest[w++] = Math.Clamp(l.Y * scale, -4f, 4f);
        dest[w++] = Math.Clamp(l.Z * scale, -4f, 4f);
    }

    private static Vector3 WrapPitchYaw(Vector3 a)
    {
        a.Y -= MathF.Floor(a.Y / 360f) * 360f;
        if (a.Y >= 180f) a.Y -= 360f;
        while (a.X < -180f) a.X += 360f;
        while (a.X > 180f) a.X -= 360f;
        return a;
    }

    /// <summary>
    /// The held weapon reduced to eight buckets. The policy does not need to distinguish a machinegun from
    /// an HLAC; it needs to know whether it is holding something it can move with, and how heavy the switch
    /// away would be.
    /// </summary>
    private static int WeaponIndex(Player bot)
    {
        Weapon? held = Inventory.CurrentWeapon(bot);
        if (held is null) return 0;
        return held.NetName switch
        {
            "blaster" => 1,
            "crylink" => 2,
            "devastator" => 3,
            "mortar" => 4,
            "electro" => 5,
            "vortex" or "vaporizer" => 6,
            _ => 7,
        };
    }

    /// <summary>True when the bot owns the named movement weapon and has the ammo to fire it.</summary>
    internal static bool MovementWeaponReady(Player bot, string weaponName)
        => Inventory.ClientHasWeapon(bot, Weapons.ByName(weaponName), andAmmo: true, complain: false);
}
