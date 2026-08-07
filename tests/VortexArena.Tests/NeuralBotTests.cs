using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using VortexArena.Server.Bot;
using VortexArena.Server.Bot.Neural;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The neural-bot subsystem: the baked navigation field, the geodesic distance field, the policy weight
/// format and its evaluator, the observation layout, and the action decode.
///
/// <para>Two of these are guards rather than tests of behaviour, and are the important ones.
/// <see cref="ObservationLayoutMatchesPythonMirror"/> catches the C#/Python layout skew that would
/// otherwise produce a policy quietly reading the wrong columns; <see cref="MoveTablesAgree"/> catches the
/// same skew between the runtime action decode and the training wire encoding.</para>
/// </summary>
[Collection("GlobalState")]
public class NeuralBotTests
{
    public NeuralBotTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    // =============================================================================================
    // NavField
    // =============================================================================================

    /// <summary>A room 1024 qu square with its floor at Z=0 and a ceiling 320 qu up.</summary>
    private static CollisionWorld Room(float half = 512f, float ceiling = 320f)
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-half, -half, -64f), new Vector3(half, half, 0f), SuperContents.Solid));
        w.AddBrush(Brush.FromBox(new Vector3(-half, -half, ceiling), new Vector3(half, half, ceiling + 64f), SuperContents.Solid));
        w.BuildGrid();
        return w;
    }

    [Fact]
    public void Bake_FlatRoom_FindsAStandableFloorUnderEveryInteriorColumn()
    {
        CollisionWorld world = Room();
        Api.Services = new EngineServices(world);

        NavField field = NavFieldBaker.Bake(world, "room", 1);

        Assert.True(field.OccupiedColumns > 100, $"only {field.OccupiedColumns} columns found a floor");

        // A probe well inside the room must land on the floor with a full player's headroom.
        Assert.True(field.TrySampleBelow(new Vector3(0f, 0f, 32f), out FloorSpan s));
        Assert.Equal(0, s.FloorZ);
        Assert.Equal(320, s.CeilZ);
        Assert.True(s.Has(NavContent.Standable));
        Assert.True(s.SlopeDot > 200, $"a flat floor should read near-flat, got {s.SlopeDot}");
    }

    [Fact]
    public void Bake_LowCeiling_IsRecordedButNotStandable()
    {
        // 48 qu of headroom: the surface exists and the policy should see it, but a player does not fit.
        CollisionWorld world = Room(ceiling: 48f);
        Api.Services = new EngineServices(world);

        NavField field = NavFieldBaker.Bake(world, "crawl", 1);

        Assert.True(field.TrySampleBelow(new Vector3(0f, 0f, 8f), out FloorSpan s));
        Assert.Equal(48, s.CeilZ);
        Assert.False(s.Has(NavContent.Standable));
    }

    [Fact]
    public void Bake_StackedWalkways_FindBothLevels()
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-256f, -256f, -64f), new Vector3(256f, 256f, 0f), SuperContents.Solid));
        // A second floor 256 qu up, thick enough that the descent walks through it.
        w.AddBrush(Brush.FromBox(new Vector3(-256f, -256f, 224f), new Vector3(256f, 256f, 256f), SuperContents.Solid));
        w.BuildGrid();
        Api.Services = new EngineServices(w);

        NavField field = NavFieldBaker.Bake(w, "stack", 1);

        ReadOnlySpan<FloorSpan> col = field.Column(field.Width / 2, field.Height / 2);
        Assert.True(col.Length >= 2, $"expected the upper and lower deck, found {col.Length} spans");
        // Descending order: the upper deck first.
        Assert.Equal(256, col[0].FloorZ);
        Assert.Equal(0, col[1].FloorZ);
    }

    [Fact]
    public void SampleRing_WritesTheDocumentedNumberOfFloats_AndFlagsThePitAsHazard()
    {
        // A floor with a hole in the middle of one side.
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-512f, -512f, -64f), new Vector3(0f, 512f, 0f), SuperContents.Solid));
        w.BuildGrid();
        Api.Services = new EngineServices(w);

        NavField field = NavFieldBaker.Bake(w, "ledge", 1);
        var probes = new float[NavField.ProbeFloats];
        // Stand near the edge looking at the void (+X is empty).
        field.SampleRing(new Vector3(-64f, 0f, 26f), Vector3.UnitX, probes);

        Assert.Equal(72, NavField.ProbeFloats);
        // The forward probe of the outer ring is over the hole, so its hazard channel reads maximal.
        // Index: ring 2 (outermost), direction 0 (frame-forward), channel 2 (hazard).
        int idx = (2 * NavField.ProbeDirections + 0) * 3 + 2;
        Assert.Equal(1f, probes[idx]);
        // The backward probe is over solid floor, which reads safe.
        int back = (0 * NavField.ProbeDirections + 4) * 3 + 2;
        Assert.Equal(-1f, probes[back]);
    }

    [Fact]
    public void NavFieldIo_RoundTripsEverySpan()
    {
        CollisionWorld world = Room();
        Api.Services = new EngineServices(world);
        NavField original = NavFieldBaker.Bake(world, "room", 0xDEADBEEFUL);

        using var ms = new MemoryStream();
        NavFieldIo.Write(ms, original);
        ms.Position = 0;
        NavField? read = NavFieldIo.Read(ms);

        Assert.NotNull(read);
        Assert.Equal(original.MapName, read!.MapName);
        Assert.Equal(original.GeometryHash, read.GeometryHash);
        Assert.Equal(original.Width, read.Width);
        Assert.Equal(original.Height, read.Height);
        Assert.Equal(original.SpanCount, read.SpanCount);

        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                ReadOnlySpan<FloorSpan> a = original.Column(x, y);
                ReadOnlySpan<FloorSpan> b = read.Column(x, y);
                Assert.Equal(a.Length, b.Length);
                for (int i = 0; i < a.Length; i++)
                {
                    Assert.Equal(a[i].FloorZ, b[i].FloorZ);
                    Assert.Equal(a[i].CeilZ, b[i].CeilZ);
                    Assert.Equal(a[i].Content, b[i].Content);
                    Assert.Equal(a[i].JumpReachMask, b[i].JumpReachMask);
                }
            }
        }
    }

    [Fact]
    public void NavFieldIo_RejectsGarbageRatherThanThrowing()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Null(NavFieldIo.Read(ms));
    }

    [Fact]
    public void GeometryHash_ChangesWhenTheGeometryDoes()
    {
        CollisionWorld a = Room();
        CollisionWorld b = Room(half: 640f);
        Assert.NotEqual(NavFieldIo.GeometryHash(a), NavFieldIo.GeometryHash(b));
        Assert.Equal(NavFieldIo.GeometryHash(a), NavFieldIo.GeometryHash(Room()));
    }

    [Fact]
    public void BakeParallel_MatchesTheSingleThreadedBake()
    {
        CollisionWorld world = Room(half: 384f);
        Api.Services = new EngineServices(world);

        NavField serial = NavFieldBaker.Bake(world, "room", 1);
        NavField parallel = NavFieldBaker.BakeParallel(world, "room", 1, threads: 4);

        Assert.Equal(serial.SpanCount, parallel.SpanCount);
        Assert.Equal(serial.OccupiedColumns, parallel.OccupiedColumns);
        for (int y = 0; y < serial.Height; y++)
            for (int x = 0; x < serial.Width; x++)
                Assert.Equal(serial.Column(x, y).Length, parallel.Column(x, y).Length);
    }

    // =============================================================================================
    // NavDistanceField
    // =============================================================================================

    [Fact]
    public void DistanceField_GrowsWithDistanceAndIsZeroAtTheGoal()
    {
        CollisionWorld world = Room(half: 512f);
        Api.Services = new EngineServices(world);
        NavField field = NavFieldBaker.Bake(world, "room", 1);

        var goal = new Vector3(-400f, 0f, 26f);
        NavDistanceField dist = NavDistanceField.Build(field, goal);

        Assert.True(dist.ReachedSpans > 100, $"only {dist.ReachedSpans} spans reachable in an open room");
        Assert.Equal(0f, dist.DistanceAt(goal));

        float near = dist.DistanceAt(new Vector3(-200f, 0f, 26f));
        float far = dist.DistanceAt(new Vector3(400f, 0f, 26f));
        Assert.True(near < far, $"near={near} should be closer than far={far}");
        Assert.True(dist.IsReachable(new Vector3(400f, 0f, 26f)));
    }

    [Fact]
    public void DistanceField_SeparateRoomsAreUnreachable()
    {
        // Two floors with a gap far wider than a jump between them.
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-512f, -256f, -64f), new Vector3(-256f, 256f, 0f), SuperContents.Solid));
        w.AddBrush(Brush.FromBox(new Vector3(1024f, -256f, -64f), new Vector3(1280f, 256f, 0f), SuperContents.Solid));
        w.BuildGrid();
        Api.Services = new EngineServices(w);

        NavField field = NavFieldBaker.Bake(w, "split", 1);
        NavDistanceField dist = NavDistanceField.Build(field, new Vector3(-384f, 0f, 26f));

        Assert.True(dist.IsReachable(new Vector3(-300f, 0f, 26f)));
        Assert.False(dist.IsReachable(new Vector3(1150f, 0f, 26f)));
    }

    // =============================================================================================
    // PolicyNetwork
    // =============================================================================================

    [Fact]
    public void PolicyWeights_RoundTripAndProduceIdenticalOutput()
    {
        PolicyNetwork net = PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size, seed: 42);
        var obs = new float[NeuralObservation.Size];
        for (int i = 0; i < obs.Length; i++) obs[i] = MathF.Sin(i * 0.37f);

        var before = new float[ActionSpace.Size];
        net.Evaluate(obs, new PolicyNetwork.Scratch(net), before);

        using var ms = new MemoryStream();
        net.Write(ms);
        ms.Position = 0;
        PolicyNetwork? reloaded = PolicyNetwork.Read(ms, out string? error);

        Assert.Null(error);
        Assert.NotNull(reloaded);
        Assert.Equal(net.InputSize, reloaded!.InputSize);
        Assert.Equal(net.OutputSize, reloaded.OutputSize);
        Assert.Equal(net.ParameterCount, reloaded.ParameterCount);

        var after = new float[ActionSpace.Size];
        reloaded.Evaluate(obs, new PolicyNetwork.Scratch(reloaded), after);
        for (int i = 0; i < before.Length; i++)
            Assert.Equal(before[i], after[i], 5);
    }

    [Fact]
    public void PolicyWeights_RejectGarbageWithAReasonRatherThanThrowing()
    {
        using var ms = new MemoryStream(new byte[64]);
        Assert.Null(PolicyNetwork.Read(ms, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void PolicyNetwork_ClampsWildInputsInsteadOfSaturatingToNaN()
    {
        // A bot falling into the void produces observation values far outside anything training saw. The
        // evaluator clips at 10 sigma precisely so that does not turn into a frozen or NaN action.
        PolicyNetwork net = PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size, seed: 7);
        var obs = new float[NeuralObservation.Size];
        Array.Fill(obs, 1e9f);

        var output = new float[ActionSpace.Size];
        net.Evaluate(obs, new PolicyNetwork.Scratch(net), output);

        Assert.All(output, v => Assert.True(float.IsFinite(v), "policy produced a non-finite output"));
    }

    // =============================================================================================
    // observation + action layout
    // =============================================================================================

    /// <summary>
    /// The C#/Python layout guard. <c>tools/neural/va_neural/layout.py</c> restates every section size, and
    /// its <c>verify()</c> compares the total against the host at handshake. This test pins the C# side of
    /// that contract so a section change here fails a build rather than a training run.
    ///
    /// <para>Skew is worth this much ceremony because nothing crashes when it happens: the network keeps
    /// producing plausible actions from misread columns, and the only symptom is a policy that stops
    /// improving.</para>
    /// </summary>
    [Fact]
    public void ObservationLayoutMatchesPythonMirror()
    {
        Assert.Equal(15, NeuralObservation.ProprioFloats);
        Assert.Equal(12, NeuralObservation.WeaponFloats);
        Assert.Equal(11, NeuralObservation.GoalFloats);
        Assert.Equal(4, NeuralObservation.AimFloats);
        Assert.Equal(8, NeuralObservation.HistoryFloats);
        Assert.Equal(8, NeuralObservation.PrevActionFloats);
        Assert.Equal(72, NavField.ProbeFloats);
        Assert.Equal(64, MapFeatures.ObservationFloats);
        Assert.Equal(12, NeuralObservation.TraceFanFloats);
        Assert.Equal(206, NeuralObservation.Size);

        // Offsets are a prefix sum of the sections; a gap or an overlap means someone edited one and not
        // the other.
        Assert.Equal(0, NeuralObservation.OffProprio);
        Assert.Equal(NeuralObservation.OffProprio + NeuralObservation.ProprioFloats, NeuralObservation.OffWeapon);
        Assert.Equal(NeuralObservation.OffWeapon + NeuralObservation.WeaponFloats, NeuralObservation.OffGoal);
        Assert.Equal(NeuralObservation.OffGoal + NeuralObservation.GoalFloats, NeuralObservation.OffAim);
        Assert.Equal(NeuralObservation.OffAim + NeuralObservation.AimFloats, NeuralObservation.OffHistory);
        Assert.Equal(NeuralObservation.OffHistory + NeuralObservation.HistoryFloats, NeuralObservation.OffPrevAction);
        Assert.Equal(NeuralObservation.OffPrevAction + NeuralObservation.PrevActionFloats, NeuralObservation.OffNavField);
        Assert.Equal(NeuralObservation.OffNavField + NavField.ProbeFloats, NeuralObservation.OffFeatures);
        Assert.Equal(NeuralObservation.OffFeatures + MapFeatures.ObservationFloats, NeuralObservation.OffTraceFan);
        Assert.Equal(NeuralObservation.OffTraceFan + NeuralObservation.TraceFanFloats, NeuralObservation.Size);
    }

    [Fact]
    public void ActionLayoutMatchesPythonMirror()
    {
        Assert.Equal(0, ActionSpace.MoveStart);
        Assert.Equal(9, ActionSpace.MoveCount);
        Assert.Equal(9, ActionSpace.JumpStart);
        Assert.Equal(11, ActionSpace.CrouchStart);
        Assert.Equal(13, ActionSpace.Attack1Start);
        Assert.Equal(15, ActionSpace.Attack2Start);
        Assert.Equal(17, ActionSpace.WeaponStart);
        Assert.Equal(21, ActionSpace.YawIndex);
        Assert.Equal(22, ActionSpace.PitchIndex);
        Assert.Equal(23, ActionSpace.Size);
        Assert.Equal(8, ActionEncoding.Size);
    }

    /// <summary>
    /// The runtime decode (logits to action) and the training wire encoding (index to action) keep separate
    /// copies of the nine-way wishmove table. They must agree, or a policy trained against one set of
    /// directions runs against another.
    /// </summary>
    [Fact]
    public void MoveTablesAgree()
    {
        for (int i = 0; i < ActionEncoding.MoveTable.Length; i++)
        {
            var wire = new float[ActionEncoding.Size];
            wire[0] = i;
            NeuralAction fromWire = ActionEncoding.Decode(wire);

            var logits = new float[ActionSpace.Size];
            logits[ActionSpace.MoveStart + i] = 1f;
            NeuralAction fromLogits = ActionSpace.Decode(logits, weaponAllowed: false, ReadOnlySpan<bool>.Empty);

            Assert.Equal(fromWire.MoveForward, fromLogits.MoveForward, 4);
            Assert.Equal(fromWire.MoveRight, fromLogits.MoveRight, 4);
        }
    }

    [Fact]
    public void Decode_WeaponPermitOff_MasksEveryAttackOutput()
    {
        var logits = new float[ActionSpace.Size];
        // Make the network want to fire everything and switch to the devastator.
        logits[ActionSpace.Attack1Start + 1] = 10f;
        logits[ActionSpace.Attack2Start + 1] = 10f;
        logits[ActionSpace.WeaponStart + 3] = 10f;
        logits[ActionSpace.MoveStart + 1] = 5f;

        ReadOnlySpan<bool> ready = stackalloc bool[] { true, true, true };
        NeuralAction denied = ActionSpace.Decode(logits, weaponAllowed: false, ready);

        Assert.False(denied.Attack1);
        Assert.False(denied.Attack2);
        Assert.Equal(-1, denied.WeaponSelect);
        // Movement is untouched: combat claims the weapon, not the legs.
        Assert.Equal(1f, denied.MoveForward, 3);

        NeuralAction allowed = ActionSpace.Decode(logits, weaponAllowed: true, ready);
        Assert.True(allowed.Attack1);
        Assert.Equal(2, allowed.WeaponSelect);   // index 2 = devastator
    }

    [Fact]
    public void Decode_UnavailableWeapon_IsNotSelected()
    {
        var logits = new float[ActionSpace.Size];
        logits[ActionSpace.WeaponStart + 3] = 10f;   // wants the devastator
        logits[ActionSpace.WeaponStart + 1] = 5f;    // second choice: blaster

        ReadOnlySpan<bool> onlyBlaster = stackalloc bool[] { true, false, false };
        NeuralAction a = ActionSpace.Decode(logits, weaponAllowed: true, onlyBlaster);

        Assert.Equal(0, a.WeaponSelect);   // index 0 = blaster, the best AVAILABLE option
    }

    [Fact]
    public void Decode_ViewDeltasAreRateClamped()
    {
        var logits = new float[ActionSpace.Size];
        logits[ActionSpace.YawIndex] = 1000f;
        logits[ActionSpace.PitchIndex] = -1000f;

        NeuralAction a = ActionSpace.Decode(logits, weaponAllowed: true, ReadOnlySpan<bool>.Empty);

        Assert.True(MathF.Abs(a.YawDelta) <= NeuralAction.MaxYawRate + 0.001f);
        Assert.True(MathF.Abs(a.PitchDelta) <= NeuralAction.MaxPitchRate + 0.001f);
        Assert.True(a.YawDelta > 0f && a.PitchDelta < 0f);
    }

    /// <summary>
    /// Wishmove is chosen in the GOAL frame and projected into the view frame at emit time. That late
    /// projection is what lets combat swing the view without the bot veering off its line, so it is worth
    /// pinning: the same action under two different view angles must produce the same WORLD direction.
    /// </summary>
    [Fact]
    public void ToMoveValues_SameWorldDirectionUnderAnyViewYaw()
    {
        var action = new NeuralAction { MoveForward = 1f, MoveRight = 0f };
        var frame = new Vector3(1f, 0f, 0f);   // goal is due +X

        Vector3 facingGoal = ActionSpace.ToMoveValues(action, frame, viewYaw: 0f, maxSpeed: 400f);
        Vector3 facingAway = ActionSpace.ToMoveValues(action, frame, viewYaw: 90f, maxSpeed: 400f);

        // Facing along +X: pure forward input.
        Assert.Equal(400f, facingGoal.X, 1);
        Assert.Equal(0f, facingGoal.Y, 1);

        // Facing 90 degrees away: the same world direction now needs a pure strafe, not a forward press.
        Assert.Equal(0f, facingAway.X, 1);
        Assert.Equal(400f, MathF.Abs(facingAway.Y), 1);
    }

    // =============================================================================================
    // MapFeatures
    // =============================================================================================

    [Fact]
    public void MapFeatures_ClassifiesAndResolvesAJumpPadExit()
    {
        var es = (EngineServices)Api.Services!;
        Entity dest = es.EntityTable.Spawn();
        dest.ClassName = "info_notnull";
        dest.TargetName = "pad_dest";
        dest.Origin = new Vector3(0f, 0f, 512f);

        Entity pad = es.EntityTable.Spawn();
        pad.ClassName = "trigger_push";
        pad.Origin = new Vector3(0f, 0f, 0f);
        pad.Mins = new Vector3(-64f, -64f, 0f);
        pad.Maxs = new Vector3(64f, 64f, 32f);
        pad.AbsMin = pad.Origin + pad.Mins;
        pad.AbsMax = pad.Origin + pad.Maxs;
        pad.Target = "pad_dest";
        pad.Height = 200f;

        var features = new MapFeatures();
        features.Build(es.EntityTable.All);

        MapFeature found = features.All.Single(f => f.Kind == MapFeatureKind.JumpPad);
        Assert.Equal(dest.Origin, found.Exit);
        // The flight time is the arc up to the apex plus the fall to the destination; a 512 qu climb with a
        // 200 qu apex margin is roughly a second at stock gravity.
        Assert.InRange(found.TransitTime, 0.3f, 6f);
    }

    [Fact]
    public void MapFeatures_ObservationIsZeroWhenNothingIsNearby()
    {
        var features = new MapFeatures();
        features.Build(Array.Empty<Entity>());

        var dest = new float[MapFeatures.ObservationFloats];
        Array.Fill(dest, 7f);
        features.WriteObservation(Vector3.Zero, Vector3.UnitX, dest);

        Assert.All(dest, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void MapFeatures_HurtVolumeIsClassifiedAndFound()
    {
        var es = (EngineServices)Api.Services!;
        Entity hurt = es.EntityTable.Spawn();
        hurt.ClassName = "trigger_hurt";
        hurt.Origin = new Vector3(100f, 0f, 0f);
        hurt.Mins = new Vector3(-32f, -32f, 0f);
        hurt.Maxs = new Vector3(32f, 32f, 64f);
        hurt.AbsMin = hurt.Origin + hurt.Mins;
        hurt.AbsMax = hurt.Origin + hurt.Maxs;
        hurt.Dmg = 1000f;

        var features = new MapFeatures();
        features.Build(es.EntityTable.All);

        Assert.True(features.TryFind(new Vector3(100f, 0f, 32f), MapFeatureKind.Hurt, out MapFeature f));
        Assert.Equal(1000f, f.Damage);
        Assert.False(features.TryFind(new Vector3(500f, 0f, 32f), MapFeatureKind.Hurt, out _));
    }

    // =============================================================================================
    // service wiring
    // =============================================================================================

    [Fact]
    public void Service_WithNoWeightFile_StaysNotReadyAndSaysWhy()
    {
        var messages = new List<string>();
        var svc = new NeuralBotService { Log = messages.Add };

        Assert.False(svc.LoadWeights("no/such/file.vxpw"));
        Assert.False(svc.Ready);
        Assert.Contains(messages, m => m.Contains("no weight file", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("classic steer", messages[0]);
    }

    [Fact]
    public void Service_RefusesAWeightFileOfTheWrongShape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nb-shape-{Guid.NewGuid():N}.vxpw");
        try
        {
            // Right format, wrong observation size: exactly what a stale weight file looks like after the
            // observation layout changes.
            PolicyNetwork wrong = PolicyNetwork.CreateUntrained(NeuralObservation.Size - 1, ActionSpace.Size);
            using (FileStream fs = File.Create(path)) wrong.Write(fs);

            var messages = new List<string>();
            var svc = new NeuralBotService { Log = messages.Add };

            Assert.False(svc.LoadWeights(path));
            Assert.False(svc.Ready);
            Assert.Contains(messages, m => m.Contains("refusing to load", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Service_ForPreparedMap_IsReadyImmediately()
    {
        CollisionWorld world = Room();
        Api.Services = new EngineServices(world);
        NavField field = NavFieldBaker.Bake(world, "room", 1);
        PolicyNetwork net = PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size);

        var svc = NeuralBotService.ForPreparedMap(net, field, new MapFeatures(), "room");

        Assert.True(svc.Ready);
        Assert.Contains("ready", svc.StatusLine);
    }

    // =============================================================================================
    // the whole pipeline, live
    // =============================================================================================

    /// <summary>
    /// A bot with a policy attached produces a usable command every think and does not throw. The policy is
    /// untrained, so this asserts nothing about where the bot goes; it asserts that the branch runs, the
    /// observation builds, the network evaluates, the action decodes and the physics accepts the result.
    /// </summary>
    [Fact]
    public void LiveLoop_NeuralBotProducesFiniteCommandsAndMoves()
    {
        var world = new GameWorld(FlatFloorWorld(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(0f, 0f, 32f)),
        })
        { MapName = "neuraltest" };
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("skill", "8");

        NavField field = NavFieldBaker.Bake(world.Collision, "neuraltest", 1);
        var features = new MapFeatures();
        features.Build(world.Services.EntityTable.All);
        PolicyNetwork net = PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size, seed: 3);
        world.Bots.Neural = NeuralBotService.ForPreparedMap(net, field, features, "neuraltest");

        Cvars.Set("bot_neural", "1");
        Cvars.Set("bot_number", "1");

        // Fill, spawn, and run. The population's per-frame sync attaches the locomotor.
        for (int t = 0; t < 72 * 6; t++) world.Frame(SimulationLoop.TicRate);

        Assert.Single(world.Bots.Brains);
        BotBrain brain = world.Bots.Brains[0];
        Assert.NotNull(brain.Locomotor);

        Vector3 start = brain.Bot.Origin;
        float maxDisplacement = 0f;
        for (int t = 0; t < 72 * 6; t++)
        {
            world.Frame(SimulationLoop.TicRate);
            Assert.True(float.IsFinite(brain.LastInput.MoveValues.X), "wish-move went non-finite");
            Assert.True(float.IsFinite(brain.LastInput.ViewAngles.Y), "view angles went non-finite");
            maxDisplacement = MathF.Max(maxDisplacement, (brain.Bot.Origin - start).Length());
        }

        // An untrained policy is not going anywhere in particular, but it is pressing keys, so the bot must
        // not be standing exactly still. Furthest displacement reached, not final position, for the same
        // reason BotLiveLoopTests uses it: a bot that wanders out and back is not a wedged bot.
        Assert.True(maxDisplacement > 16f, $"neural bot never moved (max displacement {maxDisplacement:F1} qu)");
    }

    [Fact]
    public void LiveLoop_BotNeuralOff_LeavesTheClassicSteerInPlace()
    {
        var world = new GameWorld(FlatFloorWorld(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(0f, 0f, 32f)),
        })
        { MapName = "neuraloff" };
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_neural", "0");
        Cvars.Set("bot_number", "1");

        NavField field = NavFieldBaker.Bake(world.Collision, "neuraloff", 1);
        PolicyNetwork net = PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size);
        world.Bots.Neural = NeuralBotService.ForPreparedMap(net, field, new MapFeatures(), "neuraloff");

        for (int t = 0; t < 72 * 5; t++) world.Frame(SimulationLoop.TicRate);

        Assert.Single(world.Bots.Brains);
        Assert.Null(world.Bots.Brains[0].Locomotor);
    }

    /// <summary>
    /// A bot told to run straight at a target on flat ground must actually run: about sv_maxspeed of
    /// ground speed, closing on the target the whole way.
    ///
    /// <para>Pins the thing the whole feature rests on. The scripted-forward probe in the env host was
    /// closing 24 qu/s where a running player does 320, and no amount of training fixes an action that
    /// does not move the bot.</para>
    /// </summary>
    [Fact]
    public void ExternalAction_HoldForward_RunsAtRoughlyFullSpeed()
    {
        var world = new GameWorld(FlatFloorWorld(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(0f, 0f, 32f)),
        })
        { MapName = "runtest" };
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("skill", "10");

        NavField field = NavFieldBaker.Bake(world.Collision, "runtest", 1);
        PolicyNetwork net = PolicyNetwork.CreateUntrained(NeuralObservation.Size, ActionSpace.Size);
        world.Bots.Neural = NeuralBotService.ForPreparedMap(net, field, new MapFeatures(), "runtest");
        Cvars.Set("bot_neural", "1");
        Cvars.Set("bot_number", "1");

        for (int t = 0; t < 72 * 4 && world.Bots.Brains.Count == 0; t++) world.Frame(SimulationLoop.TicRate);
        Assert.Single(world.Bots.Brains);

        BotBrain brain = world.Bots.Brains[0];
        for (int t = 0; t < 12; t++) world.Frame(SimulationLoop.TicRate);   // let the sync attach + land
        Assert.NotNull(brain.Locomotor);

        var target = new Vector3(1600f, 0f, 24f);
        brain.IntentOverride = intent =>
        {
            intent.GoalPos = target;
            intent.CorridorA = target;
            intent.CorridorB = target;
            intent.AimRequired = false;
            intent.WeaponMovementAllowed = false;
            return intent;
        };
        brain.Locomotor!.UseExternalAction = true;
        // Index 1 in the wishmove table: straight forward in the GOAL frame.
        brain.Locomotor.PendingExternalAction = new NeuralAction { MoveForward = 1f, MoveRight = 0f, WeaponSelect = -1 };

        Vector3 start = brain.Bot.Origin;
        for (int t = 0; t < 72; t++) world.Frame(SimulationLoop.TicRate);   // one second
        float travelled = (brain.Bot.Origin - start).Length();

        // sv_maxspeed is 320 with ground friction and an acceleration ramp, so a second of running from a
        // standstill covers appreciably more than half of it. 24 qu/s (the observed failure) is 13x under.
        Assert.True(travelled > 180f,
            $"a bot holding forward covered only {travelled:F0} qu in one second; expected ~250-320");
        Assert.True((brain.Bot.Origin - target).Length() < (start - target).Length(),
            "the bot moved, but not toward the target");
    }

    // =============================================================================================
    // the training environment
    // =============================================================================================

    /// <summary>
    /// A scripted hold-forward policy must reach the target on stage 1 most of the time, and must score
    /// clearly better than random actions.
    ///
    /// <para><b>The most valuable test here.</b> It is the only one that asks whether the environment is
    /// SOLVABLE, and the bug it was written for was invisible to every other check: <c>bot_number</c> was 0
    /// while the env connected eight bots by hand, so fixcount disconnected them one per frame during
    /// warm-up. The env then stepped freed Player references. Position and velocity froze at their
    /// disconnect values, every observation stayed constant, every reward was the bare time penalty, and
    /// PPO dutifully learned nothing from 1.5M steps of a world where no action had any effect. Nothing
    /// threw. Nothing logged. The training curve just looked slow.</para>
    ///
    /// <para>A reward-shaping mistake, a broken action projection, a frozen world: all of them show up here
    /// as a scripted policy that cannot reach a point on flat ground.</para>
    /// </summary>
    [Fact]
    public void TrainingEnv_ScriptedForward_ArrivesOnFlatGroundAndBeatsRandom()
    {
        (float scriptedReward, float scriptedArrivals) = RunEnvEpisodes(scripted: true);
        (float randomReward, float randomArrivals) = RunEnvEpisodes(scripted: false);

        Assert.True(scriptedArrivals > 0.6f,
            $"hold-forward reached the target in only {scriptedArrivals:P0} of episodes on flat ground");
        Assert.True(scriptedReward > 0f,
            $"hold-forward scored {scriptedReward:F4}/step; a policy that arrives must score positive");
        Assert.True(scriptedReward > randomReward + 0.1f,
            $"hold-forward ({scriptedReward:F4}) barely beat random ({randomReward:F4}); " +
            "the reward does not distinguish progress from noise");
        Assert.True(randomArrivals < scriptedArrivals,
            $"random actions arrived {randomArrivals:P0} vs scripted {scriptedArrivals:P0}");
    }

    /// <summary>Run a few short episodes and return (mean reward per agent-step, arrival rate).</summary>
    private static (float Reward, float Arrivals) RunEnvEpisodes(bool scripted)
    {
        var cfg = new TrainingEnv.Config
        {
            Agents = 4,
            TicksPerStep = 4,
            MaxSteps = 400,
            Stage = CourseGenerator.Stage.Flat,
            Seed = 20260807,
            WeaponChance = 0f,          // stage 1 is locomotion; weapons only add a way to die
            PermitFlipChance = 0f,
            AimConstraintChance = 0f,
            TraceFan = false,           // the fan costs traces and changes nothing about arriving
        };
        var env = new TrainingEnv(cfg);

        int obsSize = TrainingEnv.ObservationSize;
        var obs = new float[cfg.Agents * obsSize];
        var rewards = new float[cfg.Agents];
        var dones = new byte[cfg.Agents];
        var trunc = new byte[cfg.Agents];
        var actions = new float[cfg.Agents * ActionEncoding.Size];
        var rng = new Random(7);

        env.Reset(obs);

        double rewardSum = 0;
        int steps = 0, arrived = 0, agentEpisodes = 0, episodes = 0;
        while (episodes < 4 && steps < 4000)
        {
            for (int i = 0; i < cfg.Agents; i++)
            {
                int o = i * ActionEncoding.Size;
                Array.Clear(actions, o, ActionEncoding.Size);
                if (scripted)
                {
                    actions[o] = 1f;                                   // straight forward in the goal frame
                    actions[o + 1] = steps % 8 == 0 ? 1f : 0f;         // a hop roughly every 0.44 s
                }
                else
                {
                    actions[o] = rng.Next(0, 9);
                    actions[o + 1] = rng.Next(0, 2);
                    actions[o + 6] = (float)(rng.NextDouble() * 2.0 - 1.0);
                }
            }

            env.Step(actions, obs, rewards, dones, trunc);
            for (int i = 0; i < rewards.Length; i++) rewardSum += rewards[i];
            steps++;

            if (!env.AllDone()) continue;
            (int a, _, _) = env.EpisodeSummary();
            arrived += a;
            agentEpisodes += cfg.Agents;
            episodes++;
            env.Reset(obs);
        }

        return ((float)(rewardSum / Math.Max(1, steps * cfg.Agents)),
                agentEpisodes == 0 ? 0f : arrived / (float)agentEpisodes);
    }

    private static CollisionWorld FlatFloorWorld()
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-2048f, -2048f, -64f), new Vector3(2048f, 2048f, 0f), SuperContents.Solid));
        w.BuildGrid();
        return w;
    }
}
