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
    }

    /// <summary>A generated course: geometry, spawn and target, plus the entities the furniture stage adds.</summary>
    public sealed class Course
    {
        public CollisionWorld World = null!;
        public Vector3 Spawn;
        public Vector3 Target;
        /// <summary>Entity dictionaries the caller spawns into the world (jump pads, teleporters, hurt volumes).</summary>
        public List<(string ClassName, Vector3 Origin, Vector3 Mins, Vector3 Maxs, string Target, string TargetName)> Entities = new();
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
