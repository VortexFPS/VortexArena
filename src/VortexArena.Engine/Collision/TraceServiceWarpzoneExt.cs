// Port of Base/data/xonotic-data.pk3dir/qcsrc/lib/warpzone/common.qc + server.qc — the HOST-SIDE bridge half.
//
// The warpzone-aware trace RECURSION itself (WarpZone_TraceBox/_TraceLine/_FindRadius, the 16-zone guard, the
// transform accumulator) lives in XonoticGodot.Common.Gameplay (WarpzoneTrace / WarpzoneRadiusQuery), because
// the gameplay code that fires the traces (WeaponFiring / WeaponSplash) is in Common and Common must NOT depend
// on Engine (the project reference points Engine → Common, never the reverse). This Engine-side file is the thin
// bridge that lets the concrete TraceService publish THIS world's warpzone manager (QC global g_warpzones) to
// the Common-side ambient the trace extensions resolve — the C# successor to WarpZone_MakeAllSolid making the
// world's zones visible to the trace.
//
// SCOPE (T45, combat-traversal half): hitscan/projectile traces + radius-damage cross seamless portals. The
// CLIENT portal SubViewport RENDER is OUT OF SCOPE this pass (headless-unverifiable, NetGame.cs owned elsewhere).
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// Host-side warpzone bridge for <see cref="TraceService"/>. The trace service holds the concrete map collision,
/// so it is the natural owner of the per-match "which warpzones exist" wiring; it forwards the world's
/// <see cref="WarpzoneManager"/> to the Common-side ambient (<see cref="WarpzoneTrace.AmbientManager"/>) that the
/// warpzone-aware trace extensions (<see cref="WarpzoneTrace.TraceLineWarpzone"/> /
/// <see cref="WarpzoneTrace.TraceBoxWarpzone"/>) and the radius query resolve. Kept as a separate file (rather
/// than folded into TraceService.cs) so the warpzone seam is self-contained and the dependency note above is
/// visible at the bridge point.
/// </summary>
internal static class TraceServiceWarpzoneBridge
{
    /// <summary>Publish (or clear with null) the world's warpzone manager to the Common-side trace ambient.</summary>
    internal static void Publish(WarpzoneManager? manager) => WarpzoneTrace.AmbientManager = manager;
}
