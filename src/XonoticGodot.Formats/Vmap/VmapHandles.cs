using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>What kind of manipulator handle a grab landed on.</summary>
public enum HandleKind
{
    /// <summary>An axis arrow: drag translates along one world axis.</summary>
    MoveAxis,

    /// <summary>A corner pad: drag translates within the plane spanned by two axes.</summary>
    MovePlane,

    /// <summary>A ring: drag rotates about its axis.</summary>
    RotateRing,

    /// <summary>A box on an axis: drag scales along that axis only.</summary>
    ScaleAxis,

    /// <summary>The centre box: drag scales all three axes together.</summary>
    ScaleUniform,
}

/// <summary>
/// One grabbable manipulator handle, in world space and Quake units.
///
/// The same description is both DRAWN and PICKED. Building the handle geometry once and having the renderer
/// and the ray test read the identical numbers is what stops the two drifting apart, which is the classic way
/// a gizmo ends up with a click target that does not match the arrow the mapper can see.
/// </summary>
public readonly record struct EditorHandle(
    HandleKind Kind,
    Vector3 Axis,
    Vector3 Origin,
    Vector3 Tip,
    float Radius)
{
    /// <summary>The second axis of a plane pad (zero for every other kind).</summary>
    public Vector3 Axis2 { get; init; }

    /// <summary>Sign of the axis this handle sits on: +1 or -1, for the scale box handles.</summary>
    public float Sign { get; init; }
}

/// <summary>
/// Builds and ray-picks the manipulator handles (design doc §11.9).
///
/// This exists because grabbing the OBJECT and dragging is not good enough for an editor: the mouse moves in
/// two screen axes at once, so a drag that was meant to raise a wall also slides it sideways, and there is no
/// way to say "only Z". Making the handle the click target means the axis is CHOSEN rather than inferred, and
/// that is the whole point of the two-phase interaction — click one selects and spawns the handles, click two
/// must land on one of them.
///
/// Sizes arrive pre-scaled from the caller (see <see cref="ScreenScale"/>) so that a handle 4000 units away is
/// as grabbable as one at arm's length. Everything here is otherwise pure geometry.
/// </summary>
public static class VmapHandles
{
    /// <summary>Handle shaft length as a fraction of the reference size passed in.</summary>
    private const float ShaftLength = 1.0f;

    /// <summary>
    /// Where an axis shaft STARTS, as a fraction of its length. Non-zero on purpose: the shafts all meet at
    /// the selection centre, and so does the uniform-scale box, so a shaft that ran all the way in would make
    /// the centre a pile of overlapping click targets where whichever was tested first won.
    /// </summary>
    private const float ShaftStart = 0.18f;

    /// <summary>Grab tolerance around a shaft, as a fraction of its length.</summary>
    private const float ShaftRadius = 0.10f;

    /// <summary>Plane-pad offset from the centre along each of its two axes, as a fraction of the shaft.</summary>
    private const float PadOffset = 0.38f;

    /// <summary>Plane-pad half-extent, as a fraction of the shaft.</summary>
    private const float PadHalf = 0.15f;

    /// <summary>Rotation-ring radius, as a fraction of the reference size.</summary>
    private const float RingRadius = 0.95f;

    /// <summary>Grab tolerance either side of a ring's circumference, as a fraction of the reference size.</summary>
    private const float RingBand = 0.11f;

    /// <summary>Scale-box half-extent, as a fraction of the shaft.</summary>
    private const float BoxHalf = 0.11f;

    /// <summary>Uniform-scale centre box half-extent, as a fraction of the shaft.</summary>
    private const float CentreHalf = 0.13f;

    private static readonly Vector3[] Axes =
    {
        new(1f, 0f, 0f),
        new(0f, 1f, 0f),
        new(0f, 0f, 1f),
    };

    /// <summary>
    /// World size one handle should occupy so it stays a constant size on screen: the world distance that
    /// subtends a fixed fraction of the viewport at <paramref name="distance"/> from the camera.
    ///
    /// Without this the handles are unusable at both ends of a map. A fixed world size is a thumbnail at the far
    /// side of a hall and swallows the whole screen when you fly up to a brush.
    /// </summary>
    /// <param name="distance">Distance from the camera to the selection centre, in Quake units.</param>
    /// <param name="tanHalfFov">Tangent of half the vertical field of view.</param>
    /// <param name="viewportFraction">Fraction of the viewport height a handle should span.</param>
    public static float ScreenScale(float distance, float tanHalfFov, float viewportFraction = 0.09f)
        => MathF.Max(1f, distance) * tanHalfFov * 2f * viewportFraction;

    /// <summary>
    /// Build the handle set for <paramref name="set"/> at <paramref name="centre"/>, appending into
    /// <paramref name="into"/> (cleared first). <paramref name="size"/> is the reference world size, normally
    /// from <see cref="ScreenScale"/>.
    /// </summary>
    public static void Build(List<EditorHandle> into, HandleSet set, Vector3 centre, float size)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (set == HandleSet.None || size <= 0f)
            return;

        float shaft = size * ShaftLength;

        switch (set)
        {
            case HandleSet.Move:
                for (int i = 0; i < 3; i++)
                {
                    Vector3 a = Axes[i];
                    into.Add(new EditorHandle(
                        HandleKind.MoveAxis, a,
                        centre + a * (shaft * ShaftStart),
                        centre + a * shaft,
                        shaft * ShaftRadius)
                    { Sign = 1f });
                }

                // Three pads in the XY / YZ / ZX quadrants, for deliberate two-axis motion. Having them
                // explicit is what lets the axis arrows stay strictly one-dimensional.
                for (int i = 0; i < 3; i++)
                {
                    Vector3 a = Axes[i];
                    Vector3 b = Axes[(i + 1) % 3];
                    into.Add(new EditorHandle(
                        HandleKind.MovePlane, a,
                        centre + (a + b) * (shaft * PadOffset),
                        centre + (a + b) * (shaft * PadOffset),
                        shaft * PadHalf)
                    { Axis2 = b, Sign = 1f });
                }
                break;

            case HandleSet.Rotate:
                for (int i = 0; i < 3; i++)
                    into.Add(new EditorHandle(
                        HandleKind.RotateRing, Axes[i],
                        centre, centre,
                        size * RingRadius)
                    { Sign = 1f });
                break;

            case HandleSet.Scale:
                // Six box handles, one per signed axis, so "stretch the +X face" and "stretch the -X face" are
                // different grabs that grow the solid in different directions.
                for (int i = 0; i < 3; i++)
                {
                    Vector3 a = Axes[i];
                    for (int s = 0; s < 2; s++)
                    {
                        float sign = s == 0 ? 1f : -1f;
                        Vector3 dir = a * sign;
                        into.Add(new EditorHandle(
                            HandleKind.ScaleAxis, a,
                            centre + dir * (shaft * ShaftStart),
                            centre + dir * shaft,
                            shaft * BoxHalf)
                        { Sign = sign });
                    }
                }

                into.Add(new EditorHandle(
                    HandleKind.ScaleUniform, Vector3.One, centre, centre, shaft * CentreHalf)
                { Sign = 1f });
                break;
        }
    }

    /// <summary>
    /// Ray-pick the nearest handle. Returns false when the ray misses every one, which is the signal for the
    /// caller to fall through to picking geometry instead.
    /// </summary>
    public static bool TryPick(
        IReadOnlyList<EditorHandle> handles, Vector3 origin, Vector3 direction,
        out EditorHandle hit, out float distance)
    {
        ArgumentNullException.ThrowIfNull(handles);
        hit = default;
        distance = float.MaxValue;

        float dirLen = direction.Length();
        if (dirLen < 1e-9f)
            return false;
        Vector3 dir = direction / dirLen;

        bool found = false;
        for (int i = 0; i < handles.Count; i++)
        {
            EditorHandle h = handles[i];
            float t;
            bool ok;

            switch (h.Kind)
            {
                case HandleKind.MoveAxis:
                    ok = RayHitsSegment(origin, dir, h.Origin, h.Tip, h.Radius, out t);
                    break;
                case HandleKind.MovePlane:
                    ok = RayHitsPad(origin, dir, h.Origin, h.Axis, h.Axis2, h.Radius, out t);
                    break;
                case HandleKind.RotateRing:
                    ok = RayHitsRing(origin, dir, h.Origin, h.Axis, h.Radius,
                        h.Radius * (RingBand / RingRadius), out t);
                    break;
                default:
                    ok = RayHitsSphere(origin, dir, h.Tip, h.Radius, out t);
                    break;
            }

            if (!ok || t < 0f || t >= distance)
                continue;

            distance = t;
            hit = h;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Closest approach between the ray and a segment; a hit when they pass within <paramref name="radius"/>.
    /// This is what makes an axis arrow grabbable: you aim near the shaft, not exactly at it.
    /// </summary>
    public static bool RayHitsSegment(
        Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, float radius, out float rayT)
    {
        rayT = 0f;
        Vector3 seg = b - a;
        float segLen2 = seg.LengthSquared();
        if (segLen2 < 1e-12f)
            return false;

        Vector3 w0 = origin - a;
        float aa = 1f;                            // dir is unit
        float bb = Vector3.Dot(dir, seg);
        float cc = segLen2;
        float dd = Vector3.Dot(dir, w0);
        float ee = Vector3.Dot(seg, w0);

        float denom = aa * cc - bb * bb;
        float sc, tc;
        if (MathF.Abs(denom) < 1e-9f)
        {
            // Ray parallel to the segment: compare at the segment's start.
            sc = -dd;
            tc = 0f;
        }
        else
        {
            sc = (bb * ee - cc * dd) / denom;
            tc = (aa * ee - bb * dd) / denom;
        }

        if (sc < 0f)
            return false;                          // behind the eye
        tc = Math.Clamp(tc, 0f, 1f);

        Vector3 pRay = origin + dir * sc;
        Vector3 pSeg = a + seg * tc;
        if ((pRay - pSeg).LengthSquared() > radius * radius)
            return false;

        rayT = sc;
        return true;
    }

    /// <summary>Ray against a square pad lying in the plane spanned by two axes.</summary>
    public static bool RayHitsPad(
        Vector3 origin, Vector3 dir, Vector3 centre, Vector3 axisU, Vector3 axisV, float half, out float rayT)
    {
        rayT = 0f;
        Vector3 n = Vector3.Cross(axisU, axisV);
        float nLen = n.Length();
        if (nLen < 1e-9f)
            return false;
        n /= nLen;

        float denom = Vector3.Dot(n, dir);
        if (MathF.Abs(denom) < 1e-6f)
            return false;                          // edge-on: nothing to hit

        float t = Vector3.Dot(n, centre - origin) / denom;
        if (t < 0f)
            return false;

        Vector3 p = origin + dir * t - centre;
        if (MathF.Abs(Vector3.Dot(p, Vector3.Normalize(axisU))) > half)
            return false;
        if (MathF.Abs(Vector3.Dot(p, Vector3.Normalize(axisV))) > half)
            return false;

        rayT = t;
        return true;
    }

    /// <summary>
    /// Ray against a ring: intersect its plane, then accept when the hit lands within
    /// <paramref name="band"/> of the circumference. A disc test would swallow everything inside the ring,
    /// including the other two rings and the geometry behind them.
    /// </summary>
    public static bool RayHitsRing(
        Vector3 origin, Vector3 dir, Vector3 centre, Vector3 axis, float radius, float band, out float rayT)
    {
        rayT = 0f;
        float axisLen = axis.Length();
        if (axisLen < 1e-9f)
            return false;
        Vector3 n = axis / axisLen;

        float denom = Vector3.Dot(n, dir);
        if (MathF.Abs(denom) < 1e-6f)
            return false;

        float t = Vector3.Dot(n, centre - origin) / denom;
        if (t < 0f)
            return false;

        float r = (origin + dir * t - centre).Length();
        if (MathF.Abs(r - radius) > band)
            return false;

        rayT = t;
        return true;
    }

    /// <summary>Ray against a sphere — the grab volume for the box handles.</summary>
    public static bool RayHitsSphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius, out float rayT)
    {
        rayT = 0f;
        Vector3 m = origin - centre;
        float b = Vector3.Dot(m, dir);
        float c = Vector3.Dot(m, m) - radius * radius;

        // Origin outside the sphere and the ray pointing away from it.
        if (c > 0f && b > 0f)
            return false;

        float disc = b * b - c;
        if (disc < 0f)
            return false;

        float t = -b - MathF.Sqrt(disc);
        if (t < 0f)
            t = 0f;                                 // eye inside the handle: grab it

        rayT = t;
        return true;
    }

    /// <summary>
    /// Resolve a mouse drag into the transform the grabbed handle means.
    ///
    /// <paramref name="screenDelta"/> is the accumulated pointer motion converted to world units at the grab
    /// depth (the caller owns that conversion because it needs the camera). The result is projected onto
    /// whatever the handle actually permits: one axis for an arrow, two for a pad.
    /// </summary>
    public static Vector3 ConstrainDrag(EditorHandle handle, Vector3 worldDelta) => handle.Kind switch
    {
        HandleKind.MoveAxis => handle.Axis * Vector3.Dot(worldDelta, handle.Axis),
        HandleKind.MovePlane => handle.Axis * Vector3.Dot(worldDelta, handle.Axis)
                                + handle.Axis2 * Vector3.Dot(worldDelta, handle.Axis2),
        _ => worldDelta,
    };

    /// <summary>
    /// Per-axis scale factors for a scale-handle drag: how far the handle moved along its own axis, relative
    /// to how far it started from the pivot.
    ///
    /// <paramref name="reach"/> is the pivot-to-handle distance at grab time, and it must not be near zero or
    /// a one-pixel drag becomes an enormous scale factor; the caller passes the selection's own half-extent so
    /// dragging a big brush's handle scales it proportionally rather than explosively.
    /// </summary>
    public static Vector3 ScaleFactors(EditorHandle handle, float alongAxis, float reach, float minFactor)
    {
        if (reach < 1e-3f)
            return Vector3.One;

        // Moving the +X handle in +X grows the solid; moving the -X handle in -X grows it too, which is why
        // the sign is folded in rather than taken from the raw displacement.
        float f = MathF.Max(minFactor, 1f + handle.Sign * alongAxis / reach);

        if (handle.Kind == HandleKind.ScaleUniform)
            return new Vector3(f, f, f);

        return new Vector3(
            handle.Axis.X != 0f ? f : 1f,
            handle.Axis.Y != 0f ? f : 1f,
            handle.Axis.Z != 0f ? f : 1f);
    }
}
