using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Engine.Collision;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Builds procedural obstacle courses as <see cref="CollisionWorld"/>s, one per training episode batch.
///
/// <para><b>Why generated geometry and not the 32 shipped maps.</b> Risk R-N1 in
/// <c>planning/neural-bots-2026-08-07.md</c> is that the policy memorises maps: fast on stormkeep, lost on
/// anything it has not seen. A network trained only on maps we own has no pressure to generalise, and a
/// training curve cannot tell you it failed. Stages 1 through 5 therefore run on geometry that is different
/// every episode, and the shipped maps are held back for stage 6 and the eval split.</para>
///
/// <para>Each stage adds exactly one skill, and the stages are ordered because each one's reward is only
/// learnable once the previous is in place: you cannot learn gap-jump timing before you can run.</para>
/// </summary>
public static class CourseGenerator
{
    /// <summary>Curriculum stages, in the order they are trainable.</summary>
    public enum Stage
    {
        /// <summary>Flat ground, target somewhere on it. Learns to run and turn.</summary>
        Flat = 1,
        /// <summary>Flat, but long, with a speed reward. Learns bunnyhop and strafe-jump chaining.</summary>
        Corridor = 2,
        /// <summary>Gaps, ledges, ramps, steps. Learns jump timing and landing.</summary>
        Terrain = 3,
        /// <summary>Jump pads, teleporters, hurt volumes. Learns to route through map furniture.</summary>
        Furniture = 4,
        /// <summary>Gaps too wide to jump and ledges too high to climb. Forces weapon jumps.</summary>
        WeaponGaps = 5,

        /// <summary>
        /// The game's real maps, minus a held-out eval split. Not generated: see
        /// <see cref="MapCourseSource"/>, which owns this stage entirely.
        ///
        /// <para>Generated geometry teaches locomotion and stops there. Measured after stages 1 to 5: 97%
        /// arrivals on the corridor stage and 71% on terrain, against 22% and 3.5% for a scripted
        /// straight-line runner — and 3 routes of 8 on stormkeep, where the classic waypoint steer finishes
        /// 7. Stairwells, tight doorways, railings and multi-level loops are not in the generator, and this
        /// is where the policy meets them.</para>
        /// </summary>
        RealMaps = 6,

        /// <summary>
        /// Guaranteed jump-pad, teleporter and warpzone routes. Unlike the real-map furniture stage, every
        /// episode requires the sampled transit to reach an otherwise disconnected landing platform.
        /// </summary>
        Transits = 7,

        /// <summary>
        /// Fast, non-weapon movement challenges: rising gap jumps, corner transfers and narrow landings.
        /// These isolate the timing and air-control primitives used by authored map trick jumps.
        /// </summary>
        TrickJumps = 8,
    }

    /// <summary>An explicit seamless-portal endpoint spawned after the generated world's normal entities.</summary>
    public readonly record struct WarpzoneSpec(
        Vector3 Origin, Vector3 Angles, string TargetName, string Target, Vector3 Mins, Vector3 Maxs);

    /// <summary>A generated course: geometry, spawn and target, plus the entities the furniture stage adds.</summary>
    public sealed class Course
    {
        public CollisionWorld World = null!;
        public Vector3 Spawn;
        public Vector3 Target;
        /// <summary>Entity dictionaries the caller spawns into the world (jump pads, teleporters, hurt volumes).</summary>
        public List<(string ClassName, Vector3 Origin, Vector3 Mins, Vector3 Maxs, string Target, string TargetName)> Entities = new();
        /// <summary>Linked warpzone endpoints. Kept separate because their plane orientation is part of the route.</summary>
        public List<WarpzoneSpec> Warpzones = new();
        /// <summary>
        /// Platform centres and half-extents, in route order, for the stages that decorate a terrain course.
        ///
        /// <para>Furniture has to be placed on the geometry, not on the straight line from spawn to target.
        /// A terrain course wanders by up to half a radian per step, so that line mostly crosses empty air
        /// and occasionally lands square on the one platform the route needs.</para>
        /// </summary>
        public List<(Vector3 Centre, float HalfExtent)> Platforms = new();

        /// <summary>Straight-line distance, for the log line and for sanity-checking a stage's difficulty.</summary>
        public float SpanLength => (Target - Spawn).Length();
    }

    private const float FloorTop = 0f;
    private const float Thickness = 64f;
    private const int WallHeight = 512;

    /// <summary>Generate one course. <paramref name="seed"/> makes it reproducible, which matters for eval.</summary>
    public static Course Generate(Stage stage, int seed)
    {
        var rng = new Random(seed);
        return stage switch
        {
            Stage.Flat => Flat(rng),
            Stage.Corridor => Corridor(rng),
            Stage.Terrain => Terrain(rng),
            Stage.Furniture => Furniture(rng),
            Stage.WeaponGaps => WeaponGaps(rng),
            Stage.Transits => Transits(rng),
            Stage.TrickJumps => TrickJumps(rng),
            _ => Flat(rng),
        };
    }

    /// <summary>Stage 1: an open room. The only thing to learn is that moving toward the target pays.</summary>
    private static Course Flat(Random rng)
    {
        var c = new Course();
        var world = new CollisionWorld();
        float half = 768f + (float)rng.NextDouble() * 1024f;
        AddSlab(world, -half, -half, half, half, FloorTop);
        AddPerimeter(world, -half, -half, half, half);
        world.BuildGrid();
        c.World = world;
        c.Spawn = RandomPointIn(rng, -half + 96f, half - 96f, FloorTop);
        c.Target = RandomPointIn(rng, -half + 96f, half - 96f, FloorTop);
        // A target already underfoot teaches nothing; push it out to at least a few seconds of running.
        if ((c.Target - c.Spawn).Length() < 512f)
            c.Target = c.Spawn + Vector3.Normalize(new Vector3(1f, 1f, 0f)) * MathF.Min(768f, half - 128f);
        return c;
    }

    /// <summary>
    /// Stage 2: a long corridor with gentle bends. Long enough that the only way to a good time is to build
    /// and hold speed, which is what makes bunnyhopping the reward-maximising behaviour rather than a trick
    /// we have to demonstrate.
    /// </summary>
    private static Course Corridor(Random rng)
    {
        var c = new Course();
        var world = new CollisionWorld();

        int segments = 3 + rng.Next(4);
        float width = 256f + (float)rng.NextDouble() * 256f;
        var cursor = new Vector3(0f, 0f, FloorTop);
        float heading = (float)(rng.NextDouble() * Math.Tau);
        c.Spawn = cursor + new Vector3(0f, 0f, 26f);

        for (int i = 0; i < segments; i++)
        {
            float len = 768f + (float)rng.NextDouble() * 1024f;
            heading += (float)(rng.NextDouble() - 0.5) * 1.2f;   // a bend the bot must carry speed through
            var dir = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
            Vector3 end = cursor + dir * len;
            AddCorridorSegment(world, cursor, end, width);
            cursor = end;
        }

        world.BuildGrid();
        c.World = world;
        c.Target = cursor + new Vector3(0f, 0f, 26f);
        return c;
    }

    /// <summary>
    /// Stage 3: platforms at varying heights with gaps between them. Every gap is crossable by a running
    /// jump, and every ledge is reachable, so a failure here is a timing failure rather than an impossible
    /// course.
    /// </summary>
    private static Course Terrain(Random rng)
    {
        var c = new Course();
        var world = new CollisionWorld();

        int steps = 4 + rng.Next(5);
        var cursor = new Vector3(0f, 0f, FloorTop);
        float heading = (float)(rng.NextDouble() * Math.Tau);
        c.Spawn = cursor + new Vector3(0f, 0f, 26f);
        AddSlab(world, cursor.X - 192f, cursor.Y - 192f, cursor.X + 192f, cursor.Y + 192f, cursor.Z);
        c.Platforms.Add((cursor, 192f));

        for (int i = 0; i < steps; i++)
        {
            heading += (float)(rng.NextDouble() - 0.5) * 1.0f;
            var dir = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);

            // A running jump at 400 qu/s clears roughly 320 qu of gap; stay under that so every gap is fair.
            float gap = 96f + (float)rng.NextDouble() * 200f;
            float rise = (float)(rng.NextDouble() - 0.35) * 96f;   // biased upward, so courses climb
            float pad = 160f + (float)rng.NextDouble() * 192f;

            Vector3 next = cursor + dir * (gap + pad) + new Vector3(0f, 0f, rise);
            AddSlab(world, next.X - pad, next.Y - pad, next.X + pad, next.Y + pad, next.Z);
            c.Platforms.Add((next, pad));

            // Every third platform gets a ramp onto it, so ramp jumps appear in the distribution.
            if (i % 3 == 2)
                AddRamp(world, cursor, next, 128f);

            cursor = next;
        }

        world.BuildGrid();
        c.World = world;
        c.Target = cursor + new Vector3(0f, 0f, 26f);
        return c;
    }

    /// <summary>
    /// Stage 4: the terrain course plus map furniture, placed ON the platforms.
    ///
    /// <para><b>The first version placed it on the straight line from spawn to target and it broke the
    /// stage.</b> A terrain course wanders by up to half a radian per step, so that line mostly crosses
    /// empty air (furniture floating in the void, doing nothing) and occasionally lands square on the one
    /// platform the route needs — a 256 x 256 lethal box across the only way through. Stage 4 became a
    /// lottery between "irrelevant" and "impossible", and a policy trained on it learns caution, which is
    /// why the arrival rate DECLINED over 12M steps. Measured: the scripted runner went 7.0% on stage 3 to
    /// 0.7% on stage 4, and the trained policy 36.4% to 3.5%. Anything that halves a scripted baseline is
    /// the course, not the policy.</para>
    ///
    /// <para>Now: a jump pad on a platform aimed two platforms ahead (a real shortcut over a real gap), a
    /// teleporter between two platforms, and the hazard as a lethal floor UNDER the gaps. That last one is
    /// the semantic that was wrong before — in a real arena a hurt volume punishes falling, it does not
    /// wall off the route.</para>
    /// </summary>
    private static Course Furniture(Random rng)
    {
        Course c = Terrain(rng);
        int n = c.Platforms.Count;
        if (n < 4) return c;   // too short to decorate; the plain terrain course is still a valid episode

        // A jump pad on an early platform, aimed two ahead so taking it skips a gap.
        int padIdx = 1 + rng.Next(Math.Max(1, n / 2));
        int padDest = Math.Min(n - 1, padIdx + 2);
        if (padDest > padIdx)
        {
            (Vector3 from, _) = c.Platforms[padIdx];
            (Vector3 to, _) = c.Platforms[padDest];
            c.Entities.Add(("info_notnull", to + new Vector3(0f, 0f, 32f),
                Vector3.Zero, Vector3.Zero, "", "nb_pad_dest"));
            c.Entities.Add(("trigger_push", from + new Vector3(0f, 0f, 8f),
                new Vector3(-56f, -56f, 0f), new Vector3(56f, 56f, 40f), "nb_pad_dest", ""));
        }

        // A teleporter later along the route, also skipping ahead.
        int teleIdx = Math.Max(padDest + 1, n / 2);
        int teleDest = Math.Min(n - 1, teleIdx + 2);
        if (teleIdx < n - 1 && teleDest > teleIdx)
        {
            (Vector3 tin, _) = c.Platforms[teleIdx];
            (Vector3 tout, _) = c.Platforms[teleDest];
            c.Entities.Add(("info_teleport_destination", tout + new Vector3(0f, 0f, 26f),
                Vector3.Zero, Vector3.Zero, "", "nb_tele_dest"));
            c.Entities.Add(("trigger_teleport", tin + new Vector3(0f, 0f, 8f),
                new Vector3(-40f, -40f, 0f), new Vector3(40f, 40f, 72f), "nb_tele_dest", ""));
        }

        // The hazard: a lethal floor well below the platforms, covering the whole course. Falling off a
        // ledge is now a death rather than a slow climb back, which is what a real arena pit does — and it
        // blocks nothing, because it is under the geometry rather than across it.
        Vector3 lo = c.Platforms[0].Centre, hi = lo;
        foreach ((Vector3 centre, float half) in c.Platforms)
        {
            lo = Vector3.Min(lo, centre - new Vector3(half, half, 0f));
            hi = Vector3.Max(hi, centre + new Vector3(half, half, 0f));
        }
        Vector3 mid = (lo + hi) * 0.5f;
        var extent = new Vector3(MathF.Max(1024f, (hi.X - lo.X) * 0.5f + 512f),
                                 MathF.Max(1024f, (hi.Y - lo.Y) * 0.5f + 512f), 0f);
        c.Entities.Add(("trigger_hurt", new Vector3(mid.X, mid.Y, lo.Z - 320f),
            new Vector3(-extent.X, -extent.Y, -256f), new Vector3(extent.X, extent.Y, 64f), "", ""));

        return c;
    }

    /// <summary>
    /// Stage 5: a gap wider than a running jump and a ledge higher than one. The only way across is a
    /// weapon jump, so the reward for reaching the target IS the reward for learning to rocket-jump. No
    /// demonstration and no special-cased reward term: the physics already pays out
    /// (<c>DamageSystem.ApplyKnockback</c> ends at <c>Velocity += farce</c>), and this course makes taking
    /// the payout the only way through.
    /// </summary>
    private static Course WeaponGaps(Random rng)
    {
        var c = new Course();
        var world = new CollisionWorld();

        var cursor = new Vector3(0f, 0f, FloorTop);
        float heading = (float)(rng.NextDouble() * Math.Tau);
        var dir = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);

        AddSlab(world, cursor.X - 320f, cursor.Y - 320f, cursor.X + 320f, cursor.Y + 320f, cursor.Z);
        c.Spawn = cursor + new Vector3(0f, 0f, 26f);

        bool wideGap = rng.Next(2) == 0;
        if (wideGap)
        {
            // 560 qu of nothing. A running jump clears ~320; a rocket jump clears this.
            Vector3 far = cursor + dir * 880f;
            AddSlab(world, far.X - 320f, far.Y - 320f, far.X + 320f, far.Y + 320f, cursor.Z);
            c.Target = far + new Vector3(0f, 0f, 26f);
        }
        else
        {
            // A 250 qu ledge. Jump apex is about 105 qu at stock gravity, so this needs a blaster pop or a
            // rocket under the feet.
            Vector3 up = cursor + dir * 512f + new Vector3(0f, 0f, 250f);
            AddSlab(world, up.X - 288f, up.Y - 288f, up.X + 288f, up.Y + 288f, up.Z);
            c.Target = up + new Vector3(0f, 0f, 26f);
        }

        // Floor the pit with a lethal volume so falling short is a real loss and the policy has to commit
        // rather than nibble at the edge.
        Vector3 mid = (c.Spawn + c.Target) * 0.5f;
        c.Entities.Add(("trigger_hurt", new Vector3(mid.X, mid.Y, cursor.Z - 256f),
            new Vector3(-1024f, -1024f, -128f), new Vector3(1024f, 1024f, 64f), "", ""));

        world.BuildGrid();
        c.World = world;
        return c;
    }

    /// <summary>
    /// Stage 7: one of the three map-assisted movement primitives, on two platforms with no ordinary route
    /// between them. The course cannot be completed by accidentally walking around the feature, so an arrival
    /// is direct evidence that the policy entered it and recovered at its exit.
    /// </summary>
    private static Course Transits(Random rng)
    {
        var c = new Course();
        var world = new CollisionWorld();
        int rotation = rng.Next(4);

        Vector3 P(float along, float side = 0f, float z = 0f)
        {
            Vector3 forward = rotation switch
            {
                1 => Vector3.UnitY,
                2 => -Vector3.UnitX,
                3 => -Vector3.UnitY,
                _ => Vector3.UnitX,
            };
            var right = new Vector3(-forward.Y, forward.X, 0f);
            return forward * along + right * side + Vector3.UnitZ * z;
        }

        void Platform(float from, float to, float halfWidth, float top)
        {
            Vector3 a = P(from, -halfWidth), b = P(to, halfWidth);
            AddSlab(world, a.X, a.Y, b.X, b.Y, top);
        }

        Platform(-1000f, 120f, 224f, 0f);
        Platform(880f, 1720f, 256f, 0f);
        c.Spawn = P(-760f, 0f, 26f);
        c.Target = P(1480f, 0f, 26f);

        int kind = rng.Next(3);
        string linkTag = rng.Next().ToString("x8");
        if (kind == 0)
        {
            // A launch aimed well inside the landing platform. The 760 qu void makes the pad mandatory.
            Vector3 exit = P(1080f, 0f, 32f);
            string targetName = $"nb_transit_pad_exit_{linkTag}";
            c.Entities.Add(("info_notnull", exit, Vector3.Zero, Vector3.Zero, "", targetName));
            c.Entities.Add(("trigger_push", P(-80f, 0f, 8f),
                new Vector3(-64f, -64f, 0f), new Vector3(64f, 64f, 48f), targetName, ""));
        }
        else if (kind == 1)
        {
            // Two disconnected platforms, with the entrance at the end of the approach runway.
            string targetName = $"nb_transit_tele_exit_{linkTag}";
            c.Entities.Add(("info_teleport_destination", P(1080f, 0f, 26f),
                Vector3.Zero, Vector3.Zero, "", targetName));
            c.Entities.Add(("trigger_teleport", P(-64f, 0f, 8f),
                new Vector3(-48f, -72f, 0f), new Vector3(48f, 72f, 80f), targetName, ""));
        }
        else
        {
            // Portal planes face their approaching rooms. Momentum through the first plane emerges along the
            // destination room, so the policy must also recover from the rotated view/velocity frame.
            Vector3 forward = Vector3.Normalize(P(1f));
            var right = new Vector3(-forward.Y, forward.X, 0f);
            float yaw = MathF.Atan2(forward.Y, forward.X) * 180f / MathF.PI;
            var extent = new Vector3(
                MathF.Abs(forward.X) * 28f + MathF.Abs(right.X) * 144f,
                MathF.Abs(forward.Y) * 28f + MathF.Abs(right.Y) * 144f,
                56f);
            c.Warpzones.Add(new WarpzoneSpec(P(0f, 0f, 48f), new Vector3(0f, yaw + 180f, 0f),
                $"nb_transit_warp_in_{linkTag}", $"nb_transit_warp_out_{linkTag}",
                -extent, extent));
            c.Warpzones.Add(new WarpzoneSpec(P(1000f, 0f, 48f), new Vector3(0f, yaw, 0f),
                $"nb_transit_warp_out_{linkTag}", $"nb_transit_warp_in_{linkTag}",
                -extent, extent));
        }

        AddPit(c, -1200f, 1900f, 1200f, P);
        world.BuildGrid();
        c.World = world;
        return c;
    }

    /// <summary>
    /// Stage 8: generated trick-jump primitives. Every route has a long approach, a lethal miss and no movement
    /// weapons. Rotation and handedness vary so the actor learns timing and air control rather than one key chord.
    /// Authored map start/end pairs can be layered after this stage without changing the policy schema.
    /// </summary>
    private static Course TrickJumps(Random rng)
    {
        var c = new Course();
        var world = new CollisionWorld();
        int rotation = rng.Next(4);
        int handedness = rng.Next(2) == 0 ? -1 : 1;

        Vector3 P(float along, float side = 0f, float z = 0f)
        {
            Vector3 forward = rotation switch
            {
                1 => Vector3.UnitY,
                2 => -Vector3.UnitX,
                3 => -Vector3.UnitY,
                _ => Vector3.UnitX,
            };
            var right = new Vector3(-forward.Y, forward.X, 0f);
            return forward * along + right * side + Vector3.UnitZ * z;
        }

        void Platform(float along0, float along1, float side0, float side1, float top)
        {
            Vector3 a = P(along0, side0), b = P(along1, side1);
            AddSlab(world, a.X, a.Y, b.X, b.Y, top);
        }

        // Every variant starts with enough runway to build speed before the commitment point.
        Platform(-1100f, 0f, -176f, 176f, 0f);
        c.Spawn = P(-820f, 0f, 26f);

        switch (rng.Next(3))
        {
            case 0:
                // Rising gap: near the stock running-jump envelope, with a small step-up at the landing.
                Platform(240f, 980f, -192f, 192f, 32f);
                c.Target = P(760f, 0f, 58f);
                break;
            case 1:
                // Ninety-degree transfer: carrying approach speed while turning in the air beats stopping at
                // the corner, which is the circle-jump/air-strafe primitive common to authored shortcuts.
                Platform(160f, 480f, handedness * 280f, handedness * 1040f, 16f);
                c.Target = P(320f, handedness * 820f, 42f);
                break;
            default:
                // Precision landing: full-speed gap with a narrow target pad punishes excess lateral drift.
                Platform(256f, 900f, -76f, 76f, 16f);
                c.Target = P(720f, 0f, 42f);
                break;
        }

        AddPit(c, -1300f, 1200f, 1500f, P);
        world.BuildGrid();
        c.World = world;
        return c;
    }

    /// <summary>Add a lethal floor beneath a generated route without blocking any valid surface.</summary>
    private static void AddPit(Course c, float alongMin, float alongMax, float halfSide,
        Func<float, float, float, Vector3> point)
    {
        Vector3 a = point(alongMin, -halfSide, -320f), b = point(alongMax, halfSide, -320f);
        Vector3 lo = Vector3.Min(a, b), hi = Vector3.Max(a, b);
        Vector3 mid = (lo + hi) * 0.5f;
        Vector3 half = (hi - lo) * 0.5f;
        c.Entities.Add(("trigger_hurt", mid,
            new Vector3(-half.X, -half.Y, -128f), new Vector3(half.X, half.Y, 64f), "", ""));
    }

    // ---- geometry helpers ----

    private static void AddSlab(CollisionWorld w, float x0, float y0, float x1, float y1, float top)
        => w.AddBrush(Brush.FromBox(new Vector3(MathF.Min(x0, x1), MathF.Min(y0, y1), top - Thickness),
                                    new Vector3(MathF.Max(x0, x1), MathF.Max(y0, y1), top), SuperContents.Solid));

    private static void AddPerimeter(CollisionWorld w, float x0, float y0, float x1, float y1)
    {
        const float t = 32f;
        w.AddBrush(Brush.FromBox(new Vector3(x0 - t, y0 - t, FloorTop), new Vector3(x1 + t, y0, FloorTop + WallHeight), SuperContents.Solid));
        w.AddBrush(Brush.FromBox(new Vector3(x0 - t, y1, FloorTop), new Vector3(x1 + t, y1 + t, FloorTop + WallHeight), SuperContents.Solid));
        w.AddBrush(Brush.FromBox(new Vector3(x0 - t, y0 - t, FloorTop), new Vector3(x0, y1 + t, FloorTop + WallHeight), SuperContents.Solid));
        w.AddBrush(Brush.FromBox(new Vector3(x1, y0 - t, FloorTop), new Vector3(x1 + t, y1 + t, FloorTop + WallHeight), SuperContents.Solid));
    }

    /// <summary>A floor slab plus side walls along a segment, approximated by axis-aligned boxes.</summary>
    private static void AddCorridorSegment(CollisionWorld w, Vector3 from, Vector3 to, float width)
    {
        // Step the segment in short axis-aligned chunks. A true swept prism would need arbitrary planes;
        // stepping keeps the geometry to boxes, which is all Brush.FromBox and the grid want, and the
        // resulting stair-stepped walls are if anything harder to run than smooth ones.
        float len = (to - from).Length();
        int steps = Math.Max(1, (int)(len / 96f));
        Vector3 step = (to - from) / steps;
        float half = width * 0.5f;
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = from + step * i;
            AddSlab(w, p.X - half, p.Y - half, p.X + half, p.Y + half, p.Z);
        }
    }

    /// <summary>
    /// A stepped ramp between two platforms. Built from stacked slabs rather than a sloped plane so the
    /// generator stays inside <see cref="Brush.FromBox"/>; the steps are 16 qu, well under step height, so
    /// the physics treats it as a walkable slope.
    /// </summary>
    private static void AddRamp(CollisionWorld w, Vector3 from, Vector3 to, float width)
    {
        float rise = to.Z - from.Z;
        if (MathF.Abs(rise) < 24f) return;
        int steps = Math.Max(2, (int)(MathF.Abs(rise) / 16f));
        Vector3 step = (to - from) / steps;
        float half = width * 0.5f;
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = from + step * i;
            AddSlab(w, p.X - half, p.Y - half, p.X + half, p.Y + half, p.Z);
        }
    }

    private static Vector3 RandomPointIn(Random rng, float lo, float hi, float z)
        => new((float)(lo + rng.NextDouble() * (hi - lo)), (float)(lo + rng.NextDouble() * (hi - lo)), z + 26f);
}
