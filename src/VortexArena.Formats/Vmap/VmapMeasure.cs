using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>How a measured gap can be crossed.</summary>
public enum ReachVerdict
{
    /// <summary>Flat enough and close enough to walk.</summary>
    Walk,

    /// <summary>Needs a jump.</summary>
    Jump,

    /// <summary>Needs a crouch-jump (the extra height from tucking the legs mid-air).</summary>
    CrouchJump,

    /// <summary>Only reachable with bunnyhop speed carried into the jump.</summary>
    Bhop,

    /// <summary>Not crossable on foot.</summary>
    Unreachable,
}

/// <summary>The physics numbers a reachability check needs, so the maths does not have to guess at them.</summary>
/// <param name="JumpVelocity">Upward speed a jump imparts (<c>sv_jumpvelocity</c>, 260).</param>
/// <param name="Gravity">World gravity (<c>sv_gravity</c>, 800).</param>
/// <param name="MaxSpeed">Ground speed cap (<c>sv_maxspeed</c>, 360).</param>
/// <param name="StepHeight">Height a walk can climb without jumping (<c>sv_stepheight</c>, 34).</param>
/// <param name="CrouchJumpBonus">
/// Extra clearance a crouch-jump buys. Tucking the legs raises the hull's FEET, not its head, so the gain is
/// the difference between standing and crouched hull height rather than anything the jump itself does.
/// </param>
/// <param name="BhopSpeed">Horizontal speed a competent bunnyhop sustains, well above the ground cap.</param>
public readonly record struct ReachParams(
    float JumpVelocity,
    float Gravity,
    float MaxSpeed,
    float StepHeight,
    float CrouchJumpBonus,
    float BhopSpeed)
{
    /// <summary>
    /// Xonotic's shipped values, and the ONLY place they are written.
    ///
    /// Deliberately not expressed as defaults on the parameters above: for a record STRUCT, <c>new()</c>
    /// bypasses the primary constructor entirely and zero-initializes, so parameter defaults would silently
    /// not apply and every measurement would come back against zero gravity.
    /// </summary>
    public static ReachParams Default => new(
        JumpVelocity: 260f,     // sv_jumpvelocity
        Gravity: 800f,          // sv_gravity
        MaxSpeed: 360f,         // sv_maxspeed
        StepHeight: 34f,        // sv_stepheight
        CrouchJumpBonus: 20f,
        BhopSpeed: 900f);
}

/// <summary>
/// The measure tool's maths (design doc §11.8, §11.9): distance, angle, and the reachability verdict that
/// Radiant cannot produce.
///
/// The reachability part is the point of having this at all. Radiant can tell a mapper a gap is 214 units
/// wide; only the game knows whether a player clears it, because that answer comes out of the same
/// <c>sv_jumpvelocity</c> and <c>sv_gravity</c> the movement code runs on. Measuring a ledge and being told
/// "crouch-jump" is a different tool from measuring it and being told "214".
///
/// This is a BALLISTIC approximation, not a simulation: it asks whether a projectile launched at the jump
/// velocity, travelling at a given horizontal speed, clears the gap and the rise. It does not model air
/// control, stairs part-way across, or a ceiling in the way. That makes it right about flat ledge-to-ledge
/// jumps — which is the overwhelming majority of what a mapper measures — and honest about being an estimate
/// everywhere else.
/// </summary>
public static class VmapMeasure
{
    /// <summary>
    /// Player hull width in Quake units (the standard -16..16 box). A destination within this of the start is
    /// adjacent rather than across a gap.
    /// </summary>
    public const float PlayerWidth = 32f;

    /// <summary>Straight-line distance between two points, in Quake units.</summary>
    public static float Distance(Vector3 a, Vector3 b) => (b - a).Length();

    /// <summary>Horizontal (XY) distance, ignoring the height difference.</summary>
    public static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y);
    }

    /// <summary>Height difference from <paramref name="a"/> to <paramref name="b"/>; positive means uphill.</summary>
    public static float Rise(Vector3 a, Vector3 b) => b.Z - a.Z;

    /// <summary>
    /// Angle at <paramref name="vertex"/> between the arms to <paramref name="a"/> and <paramref name="b"/>,
    /// in degrees. Returns 0 when either arm has no length.
    /// </summary>
    public static float Angle(Vector3 vertex, Vector3 a, Vector3 b)
    {
        Vector3 u = a - vertex;
        Vector3 v = b - vertex;
        float lu = u.Length(), lv = v.Length();
        if (lu < 1e-6f || lv < 1e-6f)
            return 0f;

        float cos = Math.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f);
        return MathF.Acos(cos) * 180f / MathF.PI;
    }

    /// <summary>
    /// Peak height a jump reaches from a standing start: <c>v² / 2g</c>.
    /// </summary>
    public static float JumpHeight(ReachParams p)
        => p.Gravity <= 0f ? 0f : p.JumpVelocity * p.JumpVelocity / (2f * p.Gravity);

    /// <summary>
    /// Horizontal reach of a jump at <paramref name="speed"/> that must also RISE by
    /// <paramref name="rise"/> units.
    ///
    /// Solves the ballistic flight time for the moment the arc crosses the target height on its way down
    /// (or up, for a drop), and multiplies by the horizontal speed. A rise beyond the jump's apex has no
    /// solution, which is the correct answer: the player simply never gets that high.
    /// </summary>
    public static float JumpReach(ReachParams p, float speed, float rise)
    {
        if (p.Gravity <= 0f || speed <= 0f)
            return 0f;

        // z(t) = v*t - g*t^2/2 = rise   =>   (g/2)t^2 - v*t + rise = 0
        float disc = p.JumpVelocity * p.JumpVelocity - 2f * p.Gravity * rise;
        if (disc < 0f)
            return 0f;   // the arc never reaches that height

        // The LATER root: the far side of the arc, which is the one that maximises horizontal travel.
        float t = (p.JumpVelocity + MathF.Sqrt(disc)) / p.Gravity;
        return speed * t;
    }

    /// <summary>
    /// Can a player get from <paramref name="from"/> to <paramref name="to"/> on foot, and how?
    ///
    /// Answers with the CHEAPEST move that works, because that is what a mapper is asking: "can they walk it"
    /// matters more than "could a very good player bhop it", and a gap that needs a bhop is a design decision
    /// rather than an accident.
    /// </summary>
    public static ReachVerdict Reach(Vector3 from, Vector3 to, ReachParams p)
    {
        float run = HorizontalDistance(from, to);
        float rise = Rise(from, to);

        // The whole verdict assumes there is NO FLOOR between the two points — the tool cannot see whether
        // there is, and a mapper measuring a gap is asking how to cross it, not how to walk around it.
        //
        // So "walk" does not mean "the distance is short". It means the destination is close enough to be
        // directly adjacent (within the player's own hull) and low enough that the movement code steps the
        // player up onto it without a jump. Anything further is a gap, whatever its height.
        if (rise <= p.StepHeight && run <= PlayerWidth)
            return ReachVerdict.Walk;

        if (rise <= JumpHeight(p) && run <= JumpReach(p, p.MaxSpeed, rise))
            return ReachVerdict.Jump;

        if (rise <= JumpHeight(p) + p.CrouchJumpBonus
            && run <= JumpReach(p, p.MaxSpeed, MathF.Max(0f, rise - p.CrouchJumpBonus)))
            return ReachVerdict.CrouchJump;

        if (rise <= JumpHeight(p) + p.CrouchJumpBonus
            && run <= JumpReach(p, p.BhopSpeed, MathF.Max(0f, rise - p.CrouchJumpBonus)))
            return ReachVerdict.Bhop;

        return ReachVerdict.Unreachable;
    }

    /// <summary>A one-line summary for the HUD: the numbers and the verdict together.</summary>
    public static string Describe(Vector3 from, Vector3 to, ReachParams p)
    {
        float dist = Distance(from, to);
        float run = HorizontalDistance(from, to);
        float rise = Rise(from, to);
        ReachVerdict verdict = Reach(from, to, p);

        return $"{dist:0.#}u  (run {run:0.#}, rise {rise:+0.#;-0.#;0})  {VerdictLabel(verdict)}";
    }

    /// <summary>Human-readable verdict.</summary>
    public static string VerdictLabel(ReachVerdict v) => v switch
    {
        ReachVerdict.Walk => "walkable",
        ReachVerdict.Jump => "needs a jump",
        ReachVerdict.CrouchJump => "needs a crouch-jump",
        ReachVerdict.Bhop => "needs bhop speed",
        _ => "UNREACHABLE on foot",
    };
}
