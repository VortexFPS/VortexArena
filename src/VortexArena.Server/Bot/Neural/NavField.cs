using System;
using System.Numerics;

namespace VortexArena.Server.Bot.Neural;

/// <summary>Hazard/medium bits stamped on a <see cref="FloorSpan"/> by the baker.</summary>
[Flags]
public enum NavContent : byte
{
    None = 0,
    /// <summary>Standing here kills: lava, or a trigger_hurt with lethal damage.</summary>
    Lethal = 1 << 0,
    /// <summary>Standing here hurts: slime, a low-damage trigger_hurt.</summary>
    Harmful = 1 << 1,
    /// <summary>Water (slows movement, allows swimming, breaks a bunnyhop chain).</summary>
    Water = 1 << 2,
    /// <summary>The span's floor is a sky brush, i.e. this is the void. Falling here is a death.</summary>
    Void = 1 << 3,
    /// <summary>A player hull fits between floor and ceiling and the slope is walkable.</summary>
    Standable = 1 << 4,
    /// <summary>The floor is a mover (func_plat / func_door / func_train): the height is not static.</summary>
    Mover = 1 << 5,
}

/// <summary>
/// One walkable surface in a column, plus the free space above it. 12 bytes, laid out so the whole field
/// for a 4000x4000 qu map is under a megabyte.
///
/// <para>Heights are 1 qu integers. Quake maps live inside +/-16384 qu, so a signed short covers the range
/// with room to spare, and a 1 qu quantisation error is a twentieth of a step height.</para>
/// </summary>
public struct FloorSpan
{
    /// <summary>Top of the walkable surface (world Z).</summary>
    public short FloorZ;

    /// <summary>First solid above the floor, or <see cref="FloorZ"/> + <see cref="NavField.MaxClearance"/> if none.</summary>
    public short CeilZ;

    /// <summary>Ground normal . up, quantised to 0..255. 255 = flat, below ~178 (0.7) = unwalkable slope.</summary>
    public byte SlopeDot;

    /// <summary><see cref="NavContent"/> bits.</summary>
    public byte Content;

    /// <summary>
    /// One bit per compass neighbour (bit 0 = +X, counter-clockwise through bit 7), set when that neighbour's
    /// best span is reachable from this one by walking or by a single jump. The policy never reads this
    /// directly; the reward's geodesic potential does, and so does the training-time reachability check that
    /// stops the course generator handing out impossible A/B pairs.
    /// </summary>
    public byte JumpReachMask;

    /// <summary>Clearance above the floor in qu.</summary>
    public readonly int Clearance => CeilZ - FloorZ;

    public readonly bool Has(NavContent c) => ((NavContent)Content & c) != 0;
}

/// <summary>
/// The baked navigation field: a 32 qu column lattice over the map's playable volume, each column holding
/// the list of standable spans found in it.
///
/// <para><b>Why this exists.</b> A trace costs 0.0277 ms
/// (<c>tests/VortexArena.Tests/Perf/TracePerfBench.cs</c>, atelier, 2048 qu point trace). The policy needs
/// roughly 72 geometry samples per think; as traces that is 2.0 ms per think, and across 8 bots at 20 Hz it
/// is a third of a core spent on perception alone. Read out of this array the same 72 samples are
/// single-digit microseconds. Everything about the field's shape follows from that ratio.</para>
///
/// <para><b>Why 32 qu.</b> The player hull is 32 x 32 x 69 qu
/// (<see cref="BotNavigation.Mins"/>/<see cref="BotNavigation.Maxs"/>), so one cell is exactly one
/// footprint. A coarser lattice would blur ledges narrower than a player; a finer one quadruples the file
/// for detail the hull cannot use.</para>
/// </summary>
public sealed class NavField
{
    /// <summary>Horizontal lattice pitch in Quake units. One player footprint.</summary>
    public const int CellSize = 32;

    /// <summary>How far above a floor the baker looks for a ceiling before calling it open sky.</summary>
    public const int MaxClearance = 512;

    /// <summary>Spans kept per column. Stacked walkways past this depth are dropped, deepest first.</summary>
    public const int MaxSpansPerColumn = 4;

    /// <summary>Minimum floor-to-ceiling gap for <see cref="NavContent.Standable"/> (hull height + headroom).</summary>
    public const int MinStandClearance = 72;

    /// <summary>Minimum ground normal Z for a walkable slope (matches the physics' walkable-plane cutoff).</summary>
    public const float MinWalkableSlope = 0.7f;

    /// <summary>World position of cell (0,0)'s centre, XY only; Z is unused.</summary>
    public Vector3 Origin { get; }

    /// <summary>Lattice extent in cells.</summary>
    public int Width { get; }

    /// <summary>Lattice extent in cells.</summary>
    public int Height { get; }

    /// <summary>The map this was baked from, so a stale field is detected rather than silently used.</summary>
    public string MapName { get; }

    /// <summary>Hash of the collision geometry the bake ran against. See <see cref="NavFieldIo"/>.</summary>
    public ulong GeometryHash { get; }

    // Column c = y * Width + x indexes into _spans at _columnStart[c], for _columnCount[c] entries.
    // A flat span array with per-column offsets rather than a jagged array: one allocation, and the sampler's
    // inner loop stays in cache while it walks a column.
    private readonly int[] _columnStart;
    private readonly byte[] _columnCount;
    private readonly FloorSpan[] _spans;

    internal NavField(string mapName, ulong geometryHash, Vector3 origin, int width, int height,
        int[] columnStart, byte[] columnCount, FloorSpan[] spans)
    {
        MapName = mapName;
        GeometryHash = geometryHash;
        Origin = origin;
        Width = width;
        Height = height;
        _columnStart = columnStart;
        _columnCount = columnCount;
        _spans = spans;
    }

    /// <summary>Total spans stored (a size/coverage diagnostic, and what the bake test asserts on).</summary>
    public int SpanCount => _spans.Length;

    /// <summary>Columns that found at least one span. Low coverage means the bake missed the playable volume.</summary>
    public int OccupiedColumns
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _columnCount.Length; i++) if (_columnCount[i] > 0) n++;
            return n;
        }
    }

    /// <summary>Approximate heap footprint in bytes, for the bake report.</summary>
    public long ApproxBytes => (long)_spans.Length * 8 + (long)_columnStart.Length * 4 + _columnCount.Length;

    /// <summary>World XY to lattice cell. Returns false when the point is outside the baked area.</summary>
    public bool TryCell(Vector3 world, out int cx, out int cy)
    {
        cx = (int)MathF.Floor((world.X - Origin.X) / CellSize + 0.5f);
        cy = (int)MathF.Floor((world.Y - Origin.Y) / CellSize + 0.5f);
        return cx >= 0 && cy >= 0 && cx < Width && cy < Height;
    }

    /// <summary>Centre of a cell in world XY (Z zero).</summary>
    public Vector3 CellCentre(int cx, int cy)
        => new(Origin.X + cx * CellSize, Origin.Y + cy * CellSize, 0f);

    /// <summary>The spans in a column, in descending floor height (highest first).</summary>
    public ReadOnlySpan<FloorSpan> Column(int cx, int cy)
    {
        if (cx < 0 || cy < 0 || cx >= Width || cy >= Height) return default;
        int c = cy * Width + cx;
        return new ReadOnlySpan<FloorSpan>(_spans, _columnStart[c], _columnCount[c]);
    }

    /// <summary>
    /// Project a point on visible map geometry (for example a crosshair trace hit) onto the nearest
    /// standable navigation span and return the player-origin position on that surface.
    /// </summary>
    /// <remarks>
    /// A trace hit on a floor is a surface Z, while every query in <see cref="NavDistanceField"/> uses a
    /// player origin, 24 qu above its floor. Feeding the raw hit to the router therefore selects the span
    /// below a raised platform. Search a small neighbourhood as well as the hit cell because a marker on
    /// the lip of a platform can quantise into the adjacent lattice column.
    /// </remarks>
    public bool TryProjectSurfaceGoal(Vector3 surfacePoint, out Vector3 playerOrigin)
    {
        playerOrigin = default;
        if (!TryCell(surfacePoint, out int centreX, out int centreY)) return false;

        const int searchCells = 2;
        float bestScore = float.PositiveInfinity;
        for (int y = centreY - searchCells; y <= centreY + searchCells; y++)
        {
            for (int x = centreX - searchCells; x <= centreX + searchCells; x++)
            {
                Vector3 centre = CellCentre(x, y);
                float dx = centre.X - surfacePoint.X;
                float dy = centre.Y - surfacePoint.Y;
                foreach (FloorSpan span in Column(x, y))
                {
                    if (!span.Has(NavContent.Standable)) continue;
                    float dz = span.FloorZ - surfacePoint.Z;
                    float score = dx * dx + dy * dy + dz * dz;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    playerOrigin = new Vector3(centre.X, centre.Y,
                        span.FloorZ + NavDistanceField.OriginAboveFloor);
                }
            }
        }
        return bestScore < float.PositiveInfinity;
    }

    /// <summary>
    /// The span a body at <paramref name="world"/> is standing on or would fall onto: the highest span whose
    /// floor is at or below <paramref name="world"/>.Z plus a step-height tolerance. Returns false for a
    /// column outside the field or with nothing under the point.
    /// </summary>
    public bool TrySampleBelow(Vector3 world, out FloorSpan span)
    {
        span = default;
        if (!TryCell(world, out int cx, out int cy)) return false;
        ReadOnlySpan<FloorSpan> col = Column(cx, cy);
        // Descending order, so the first span at or below the probe is the one underfoot. The step-height
        // tolerance stops a bot standing 2 qu inside its own floor (which the physics allows) from reading
        // the span BELOW as its ground.
        float probe = world.Z + BotNavigation.StepHeight;
        for (int i = 0; i < col.Length; i++)
        {
            if (col[i].FloorZ <= probe) { span = col[i]; return true; }
        }
        return false;
    }

    /// <summary>
    /// Cheapest possible ground query: the height of the surface under <paramref name="world"/>, or
    /// <paramref name="fallback"/> when the column is empty or off-field.
    /// </summary>
    public float GroundHeight(Vector3 world, float fallback)
        => TrySampleBelow(world, out FloorSpan s) ? s.FloorZ : fallback;

    /// <summary>
    /// The egocentric probe pattern the observation uses: <see cref="ProbeRings"/> radii by
    /// <see cref="ProbeDirections"/> compass directions, each yielding four numbers: floor height, headroom, hazard, and the geodesic distance-to-goal delta. Written into
    /// <paramref name="dest"/>, which must hold at least <see cref="ProbeFloats"/> entries.
    ///
    /// <para>The pattern is rotated into the goal frame by <paramref name="forward"/> rather than the view
    /// frame, deliberately. The policy's view can be yanked anywhere by combat; if perception rotated with
    /// it, every observation during a fight would be a fresh distribution. Anchoring on the direction of
    /// travel keeps "there is a ledge on my left" meaning the same thing whichever way the bot is looking.</para>
    /// </summary>
    public void SampleRing(Vector3 origin, Vector3 forward, Span<float> dest,
        NavDistanceField? route, Vector3 goal)
    {
        if (dest.Length < ProbeFloats) throw new ArgumentException($"need {ProbeFloats} floats", nameof(dest));

        float fx = forward.X, fy = forward.Y;
        float flen = MathF.Sqrt(fx * fx + fy * fy);
        if (flen < 1e-4f) { fx = 1f; fy = 0f; }
        else { fx /= flen; fy /= flen; }

        // The fourth channel per probe: the geodesic distance-to-goal delta at the probe, against the
        // bot's own. Perception of which directions make progress THROUGH the geometry -- a probe on the
        // far side of a wall shows a worse delta, so walls are implicit. Straight-line fallback when there
        // is no route field (live, while a goal's flood is still building) or the bot is off-graph mid-air.
        float dHere = route is not null ? route.DistanceAt(origin) : NavDistanceField.Unreachable;
        bool useRoute = dHere < NavDistanceField.Unreachable;
        if (!useRoute) dHere = (origin - goal).Length();

        int w = 0;
        for (int r = 0; r < ProbeRings; r++)
        {
            float radius = ProbeRadii[r];
            for (int d = 0; d < ProbeDirections; d++)
            {
                // Rotate the compass direction into the goal frame: (cos,sin) composed with (fx,fy).
                float cs = RingCos[d], sn = RingSin[d];
                float dx = fx * cs - fy * sn;
                float dy = fx * sn + fy * cs;
                var probe = new Vector3(origin.X + dx * radius, origin.Y + dy * radius, origin.Z);

                float dProbe = useRoute ? route!.DistanceAt(probe) : (probe - goal).Length();
                float delta = dProbe >= NavDistanceField.Unreachable
                    ? 1.5f
                    : Clamp((dProbe - dHere) / radius, -1.5f, 1.5f);

                if (TrySampleBelow(probe, out FloorSpan s))
                {
                    // Floor height relative to the bot's own feet, in step-heights. Positive = a step up.
                    // Scaled by StepHeight rather than normalised over the map, so "one step" is always 1.0
                    // and the network reads the same number on a 512 qu map and a 8192 qu one.
                    dest[w++] = Clamp((s.FloorZ - origin.Z) / BotNavigation.StepHeight, -8f, 8f);
                    dest[w++] = Clamp(s.Clearance / (float)MinStandClearance, 0f, 4f);
                    dest[w++] = HazardScore(s);
                    dest[w++] = delta;
                }
                else
                {
                    // No column and no floor read the same to the policy, and should: both mean "do not walk
                    // there". The distinction (off-field versus a genuine hole) is a bake-coverage question,
                    // not a gameplay one.
                    dest[w++] = -8f;
                    dest[w++] = 0f;
                    dest[w++] = 1f;
                    dest[w++] = delta;
                }
            }
        }
    }

    /// <summary>First ring index <see cref="SampleRingAbove"/> reads: the outer two radii only.</summary>
    public const int UpperProbeRingStart = 1;

    /// <summary>Floats <see cref="SampleRingAbove"/> writes: outer rings x directions x (height, clearance, hazard).</summary>
    public static int UpperProbeFloats => (ProbeRings - UpperProbeRingStart) * ProbeDirections * 3;

    /// <summary>
    /// The overhead counterpart of <see cref="SampleRing"/>: for the outer probe rings, the nearest
    /// walkable surface ABOVE the bot's level. The floor probes collapse each column to the bot's own
    /// storey, which left mezzanines, walkways and rocket-jumpable ledges invisible on exactly the
    /// multi-level maps the curriculum now trains on. The spans were always in the baked field; this reads
    /// them. The inner ring is skipped because directly overhead is already the proprioceptive clearance.
    /// </summary>
    public void SampleRingAbove(Vector3 origin, Vector3 forward, Span<float> dest)
    {
        if (dest.Length < UpperProbeFloats) throw new ArgumentException($"need {UpperProbeFloats} floats", nameof(dest));

        float fx = forward.X, fy = forward.Y;
        float flen = MathF.Sqrt(fx * fx + fy * fy);
        if (flen < 1e-4f) { fx = 1f; fy = 0f; }
        else { fx /= flen; fy /= flen; }

        int w = 0;
        for (int r = UpperProbeRingStart; r < ProbeRings; r++)
        {
            float radius = ProbeRadii[r];
            for (int d = 0; d < ProbeDirections; d++)
            {
                float cs = RingCos[d], sn = RingSin[d];
                var probe = new Vector3(origin.X + (fx * cs - fy * sn) * radius,
                                        origin.Y + (fx * sn + fy * cs) * radius, origin.Z);
                if (TrySampleAbove(probe, out FloorSpan s))
                {
                    // Height in jump-relevant units: 128 qu is about one blaster jump, 4.0 the ceiling of
                    // anything reachable by movement tricks.
                    dest[w++] = Clamp((s.FloorZ - origin.Z) / 128f, 0f, 4f);
                    dest[w++] = Clamp(s.Clearance / (float)MinStandClearance, 0f, 4f);
                    dest[w++] = HazardScore(s);
                }
                else
                {
                    // Open sky. "Far away, no hazard" -- deliberately distinct from the floor probes
                    // sentinel, which means "do not walk there".
                    dest[w++] = 4f;
                    dest[w++] = 0f;
                    dest[w++] = 0f;
                }
            }
        }
    }

    /// <summary>
    /// The lowest walkable span strictly ABOVE the bot at <paramref name="world"/> -- the surface a jump, a
    /// pad or a rocket jump could put it on. Two step-heights of margin keeps the bot's own floor (and the
    /// physics' small ground embed) out of the answer.
    /// </summary>
    public bool TrySampleAbove(Vector3 world, out FloorSpan span)
    {
        span = default;
        if (!TryCell(world, out int cx, out int cy)) return false;
        ReadOnlySpan<FloorSpan> col = Column(cx, cy);
        float floorAbove = world.Z + BotNavigation.StepHeight * 2f;
        bool found = false;
        // The column is stored in descending order, so keep overwriting while spans sit above the
        // threshold: the LAST hit is the lowest such span, the one the bot could actually reach first.
        for (int i = 0; i < col.Length; i++)
        {
            if (col[i].FloorZ <= floorAbove) break;
            span = col[i];
            found = true;
        }
        return found;
    }

    /// <summary>-1 (safe and standable) through +1 (lethal), the single hazard number a probe reports.</summary>
    public static float HazardScore(in FloorSpan s)
    {
        if (s.Has(NavContent.Lethal) || s.Has(NavContent.Void)) return 1f;
        if (s.Has(NavContent.Harmful)) return 0.5f;
        if (s.Has(NavContent.Water)) return 0.15f;
        return s.Has(NavContent.Standable) ? -1f : 0.25f;
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    // ---- the probe pattern ----

    /// <summary>Probe radii in Quake units: one stride, one jump, one bunnyhop-second.</summary>
    public static readonly float[] ProbeRadii = { 48f, 144f, 352f };

    public static int ProbeRings => ProbeRadii.Length;
    public const int ProbeDirections = 8;

    /// <summary>Floats <see cref="SampleRing"/> writes: rings x directions x (height, clearance, hazard).</summary>
    public static int ProbeFloats => ProbeRings * ProbeDirections * 4;

    private static readonly float[] RingCos = BuildCos();
    private static readonly float[] RingSin = BuildSin();

    private static float[] BuildCos()
    {
        var a = new float[ProbeDirections];
        for (int i = 0; i < ProbeDirections; i++) a[i] = MathF.Cos(i * MathF.Tau / ProbeDirections);
        return a;
    }

    private static float[] BuildSin()
    {
        var a = new float[ProbeDirections];
        for (int i = 0; i < ProbeDirections; i++) a[i] = MathF.Sin(i * MathF.Tau / ProbeDirections);
        return a;
    }
}
