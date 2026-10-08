// Port of Base/darkplaces/collision.c Collision_ClipExtendPrepare / Collision_ClipExtendFinish and
// the extendtraceinfo_t they share.
using System.Numerics;
using VortexArena.Common.Services;

namespace VortexArena.Engine.Collision;

/// <summary>
/// DarkPlaces runs every trace a little further than it was asked to - collision_extendtracelinelength
/// and collision_extendtraceboxlength (1 unit, the traceline and tracebox builtins),
/// collision_extendmovelength (16 units, the engine's own entity moves) - "to ensure detection of
/// collisions within the collision_impactnudge distance so that short moves do not degrade across
/// frames (this does not alter the final trace length)". An impact is reported 1/32 of a unit before
/// the surface; a move that ends within that 1/32 has, unextended, no impact at all, and the next
/// frame's move starts closer still. Extended, the surface is found, and an impact that lies only in
/// the extra length is thrown away again.
///
/// <see cref="Prepare"/> gives the longer end to trace to; <see cref="Finish"/> turns the result of
/// that trace back into one about the move that was asked for. The native game does not extend its
/// traces (<see cref="TraceService.Trace"/>); <see cref="TraceService.TraceExtended"/> and legacy mode do.
/// </summary>
public struct TraceExtension
{
    /// <summary>realstart, realdelta: the move as asked for.</summary>
    public Vector3 RealStart, RealDelta;
    /// <summary>extendend: where to trace to instead.</summary>
    public Vector3 ExtendEnd;
    /// <summary>scaletoextend: the extended length over the real one; 1 when nothing was extended.</summary>
    public float ScaleToExtend;

    /// <summary>Collision_ClipExtendPrepare: "make the trace longer according to the extend parameter".</summary>
    public static TraceExtension Prepare(Vector3 start, Vector3 end, float extend)
    {
        TraceExtension e = new() { RealStart = start, RealDelta = end - start, ExtendEnd = end, ScaleToExtend = 1.0f };
        float realLength = e.RealDelta.Length();
        // "if (extendtraceinfo->reallength && extendtraceinfo->extend)". (The finite checks are not
        // DarkPlaces': a move of unbounded length is left as it is rather than scaled into NaN.)
        if (realLength != 0 && extend != 0 && float.IsFinite(extend) && float.IsFinite(realLength))
        {
            float scale = (realLength + extend) / realLength;
            Vector3 extendEnd = start + scale * e.RealDelta;
            if (float.IsFinite(extendEnd.X) && float.IsFinite(extendEnd.Y) && float.IsFinite(extendEnd.Z))
            {
                e.ScaleToExtend = scale;
                e.ExtendEnd = extendEnd;
            }
        }
        return e;
    }

    /// <summary>
    /// Collision_ClipExtendFinish on the result of the trace to <see cref="ExtendEnd"/>: "undo the
    /// extended trace length"; "if the extended trace hit something that the unextended trace did not
    /// hit (even considering the collision_impactnudge), then we have to clear the hit information";
    /// "clamp things"; "calculate the end position". True if the hit was cleared that way - the
    /// caller then has no entity to name either ("note that ent may refer to either startsolid or
    /// fraction&lt;1, we can't restore the startsolid ent unfortunately").
    /// </summary>
    public readonly bool Finish(ref TraceResult trace)
    {
        double fraction = FinishFraction(trace.Fraction, out bool cleared);
        if (cleared)
        {
            trace.Ent = null;
            trace.DpHitQ3SurfaceFlags = 0;
            trace.DpHitContents = 0;
            trace.DpHitTextureName = null;
            trace.PlaneNormal = Vector3.Zero;
            trace.PlaneDist = 0;
        }
        trace.Fraction = (float)fraction;
        trace.EndPos = EndPos(fraction);
        return cleared;
    }

    /// <summary>
    /// The fraction half of <see cref="Finish"/>, for a caller with its own result type. The answer is a
    /// double because trace_t.fraction is one: DarkPlaces multiplies the swept fraction by scaletoextend
    /// and takes the end position from the product without rounding it to single precision in between,
    /// and the last bit of an end position is what the next move starts from.
    /// </summary>
    public readonly double FinishFraction(float sweptFraction, out bool cleared)
    {
        cleared = false;
        double fraction = sweptFraction;
        if (fraction != 1.0f)
        {
            // undo the extended trace length
            fraction *= ScaleToExtend;
            if (fraction > 1.0f) cleared = true;
        }
        // clamp things: bound(0, fraction, 1)
        return fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
    }

    /// <summary>"calculate the end position": VectorMA(realstart, fraction, realdelta, endpos), in double.</summary>
    public readonly Vector3 EndPos(double fraction) => new(
        (float)(RealStart.X + fraction * RealDelta.X), (float)(RealStart.Y + fraction * RealDelta.Y), (float)(RealStart.Z + fraction * RealDelta.Z));
}
