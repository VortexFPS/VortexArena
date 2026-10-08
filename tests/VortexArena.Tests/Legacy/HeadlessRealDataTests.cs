using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using VortexArena.Engine.Collision;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The headless presentation against the real game data in the upstream checkout (../Base): the
/// stormkeep map, a stock player model, a weapon model and a HUD picture. The numbers it finds go to
/// the test output and to _scratch/headless-realdata.txt. Returns early without the checkout.
/// </summary>
public class HeadlessRealDataTests
{
    private const string Map = "maps/stormkeep.bsp", Player = "models/player/erebus.iqm", ViewWeapon = "models/weapons/v_rl.md3",
        HandWeapon = "models/weapons/h_rl.iqm";
    private const int Solid = BspLegacyWorld.ContentsSolid, Body = BspLegacyWorld.ContentsBody, Corpse = BspLegacyWorld.ContentsCorpse;

    private readonly ITestOutputHelper _output;
    private readonly StringBuilder _report = new();
    public HeadlessRealDataTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private void Say(string line)
    {
        _output.WriteLine(line);
        _report.AppendLine(line);
    }

    private void Save(string section)
    {
        try
        {
            string directory = Path.Combine(RepoRoot(), "_scratch");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"headless-realdata-{section}.txt"), _report.ToString());
        }
        catch (IOException) { /* the report is a convenience */ }
    }

    private static string F(QcVector v) => string.Create(CultureInfo.InvariantCulture, $"'{v.X:0.###} {v.Y:0.###} {v.Z:0.###}'");
    private static QcVector Q(float x, float y, float z) => new(x, y, z);

    /// <summary>A rig over ../Base/data with stormkeep loaded, or null without the checkout or the map.</summary>
    private static HeadlessPresentationTests.Rig? RealRig()
    {
        if (!Directory.Exists(TestPaths.BaseData)) return null;
        HeadlessPresentationTests.Rig rig = new(new[] { Map, "*1", Player, ViewWeapon, HandWeapon }, TestPaths.BaseData);
        if (!rig.Vfs.Exists(Map)) { rig.Dispose(); return null; }
        rig.Presentation.BeginLevel(rig.State);
        return rig.Start();
    }

    [Fact]
    public void Stormkeep_Traces_Contents_Visibility_And_Submodels()
    {
        using HeadlessPresentationTests.Rig? rig = RealRig();
        if (rig is null) return;
        BspLegacyWorld map = rig.Presentation.Map;
        ILegacyWorld world = rig.Presentation.World;
        Assert.Null(map.LoadError);
        Assert.Equal(Map, map.MapName);
        Assert.NotNull(map.Bsp);
        Say($"map {map.MapName}: {map.Bsp!.Brushes.Length} brushes in the file, {map.Collision!.Brushes.Count} world collision brushes, {map.Bsp.Models.Length} models, {map.Bsp.Leafs.Length} leafs, {map.Bsp.Vis.ClusterCount} visibility clusters");

        world.Bounds(out QcVector worldMins, out QcVector worldMaxs);
        Say($"world bounds (model 0): {F(worldMins)} .. {F(worldMaxs)}");
        Assert.True(worldMaxs.X - worldMins.X > 1000 && worldMaxs.Z - worldMins.Z > 200);
        Assert.Equal(worldMins, rig.Vm.FieldVector(0, rig.F.Mins));

        // A spawn point from the map's own entity lump.
        List<QcVector> spawns = new();
        foreach (IReadOnlyDictionary<string, string> entity in map.Bsp.Entities)
            if (entity.TryGetValue("classname", out string? classname) && classname.StartsWith("info_player_", StringComparison.Ordinal)
                && entity.TryGetValue("origin", out string? origin))
            {
                float[] parts = origin.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                if (parts.Length == 3) spawns.Add(Q(parts[0], parts[1], parts[2]));
            }
        Assert.NotEmpty(spawns);
        QcVector spawn = spawns[0];
        Say($"{spawns.Count} spawn points; the first is at {F(spawn)}");

        // Straight down from the spawn point: the floor.
        LegacyTrace down = world.Trace(spawn, default, default, Q(spawn.X, spawn.Y, spawn.Z - 512), 0, 0, Solid | Body | Corpse, isLine: true);
        Say($"traceline down 512 from the spawn: fraction {down.Fraction:0.#####}, endpos {F(down.EndPos)}, plane normal {F(down.PlaneNormal)} dist {down.PlaneDist:0.###}, " +
            $"texture \"{down.HitTextureName}\", dphitcontents 0x{down.HitContents:X}, q3surfaceflags 0x{down.HitQ3SurfaceFlags:X}, startsolid {down.StartSolid}");
        Assert.InRange(down.Fraction, 0.001f, 0.6f);
        Assert.False(down.StartSolid);
        Assert.True(down.PlaneNormal.Z > 0.7f, "a floor");
        Assert.InRange(MathF.Sqrt(down.PlaneNormal.X * down.PlaneNormal.X + down.PlaneNormal.Y * down.PlaneNormal.Y + down.PlaneNormal.Z * down.PlaneNormal.Z), 0.999f, 1.001f);
        Assert.False(string.IsNullOrEmpty(down.HitTextureName));
        Assert.NotEqual(0, down.HitContents & Solid);
        Assert.Equal(0, down.Entity);
        // The plane the trace reports passes through the point it stopped at (within the nudge).
        float planeError = down.PlaneNormal.X * down.EndPos.X + down.PlaneNormal.Y * down.EndPos.Y + down.PlaneNormal.Z * down.EndPos.Z - down.PlaneDist;
        Assert.InRange(planeError, 0, 0.1f);

        // A player-sized box dropped from the spawn point stands on the same floor, its feet 24 below its origin.
        QcVector playerMins = Q(-16, -16, -24), playerMaxs = Q(16, 16, 45);
        LegacyTrace stand = world.Trace(spawn, playerMins, playerMaxs, Q(spawn.X, spawn.Y, spawn.Z - 512), 0, 0, Solid | Body | BspLegacyWorld.ContentsPlayerClip, isLine: false);
        Say($"tracebox (player hull) down from the spawn: fraction {stand.Fraction:0.#####}, endpos {F(stand.EndPos)}, startsolid {stand.StartSolid}; feet at z {stand.EndPos.Z - 24:0.###}, line hit at z {down.EndPos.Z:0.###}");
        Assert.False(stand.StartSolid);
        Assert.InRange(stand.EndPos.Z - 24 - down.EndPos.Z, -0.1f, 24f);

        // Open air: a short hop up from where the player stands hits nothing.
        QcVector eye = Q(stand.EndPos.X, stand.EndPos.Y, stand.EndPos.Z + 8);
        LegacyTrace air = world.Trace(stand.EndPos, default, default, eye, 0, 0, Solid | Body | Corpse, isLine: true);
        Say($"traceline 8 up through open air: fraction {air.Fraction}, endpos {F(air.EndPos)}, plane normal {F(air.PlaneNormal)}, texture {(air.HitTextureName is null ? "none" : air.HitTextureName)}");
        Assert.Equal(1, air.Fraction);
        Assert.Equal(eye, air.EndPos);
        Assert.Null(air.HitTextureName);
        Assert.Equal(0, world.PointSuperContents(eye));
        Assert.Equal(0, air.StartContents);

        // Inside the floor: solid.
        QcVector under = Q(down.EndPos.X, down.EndPos.Y, down.EndPos.Z - 4);
        int underContents = world.PointSuperContents(under);
        Say($"pointcontents 4 below the floor: 0x{underContents:X}");
        Assert.NotEqual(0, underContents & Solid);

        // Liquids: take a liquid brush of the map and ask in its middle.
        Dictionary<int, int> liquidBrushes = new();
        Brush? sample = null;
        foreach (Brush brush in map.Collision.Brushes)
        {
            int liquid = brush.Contents & SuperContents.LiquidsMask;
            if (liquid == 0) continue;
            liquidBrushes[liquid] = liquidBrushes.GetValueOrDefault(liquid) + 1;
            // The first one whose middle is liquid and nothing else (some sit inside solid rock).
            Vector3 middle = (brush.Mins + brush.Maxs) * 0.5f;
            if (sample is null && brush.ContainsPoint(middle) && (world.PointSuperContents(Q(middle.X, middle.Y, middle.Z)) & Solid) == 0) sample = brush;
        }
        Say("liquid brushes in the map: " + (liquidBrushes.Count == 0 ? "none" : string.Join(", ", liquidBrushes.Select(kv => $"{kv.Value} with DarkPlaces contents 0x{BspLegacyWorld.ContentsFromEngine(kv.Key):X}"))));
        if (sample is not null)
        {
            Vector3 middle = (sample.Mins + sample.Maxs) * 0.5f;
            int contents = world.PointSuperContents(Q(middle.X, middle.Y, middle.Z));
            Say($"pointcontents in the middle of a liquid brush at {F(Q(middle.X, middle.Y, middle.Z))}: 0x{contents:X} (water 0x2, slime 0x4, lava 0x8)");
            Assert.NotEqual(0, contents & BspLegacyWorld.ContentsLiquidsMask);
            Assert.Equal(BspLegacyWorld.ContentsFromEngine(sample.Contents) & BspLegacyWorld.ContentsLiquidsMask, contents & BspLegacyWorld.ContentsLiquidsMask);
            // A default trace falls through the liquid; one that asks for liquids stops on its surface.
            QcVector above = Q(middle.X, middle.Y, sample.Maxs.Z + 16), below = Q(middle.X, middle.Y, sample.Mins.Z + 1);
            LegacyTrace into = world.Trace(above, default, default, below, 0, 0, Solid | BspLegacyWorld.ContentsLiquidsMask, isLine: true);
            LegacyTrace through = world.Trace(above, default, default, below, 0, 0, Solid | Body | Corpse, isLine: true);
            Say($"traceline down into it from 16 above (start contents 0x{into.StartContents:X}) with the liquids in the mask: fraction {into.Fraction:0.####}, stopped at z {into.EndPos.Z:0.###} " +
                $"(brush top {sample.Maxs.Z:0.###}), dphitcontents 0x{into.HitContents:X}, texture \"{into.HitTextureName}\"; with the default mask: fraction {through.Fraction:0.####}");
            if ((into.StartContents & (Solid | BspLegacyWorld.ContentsLiquidsMask)) == 0)
            {
                Assert.InRange(into.EndPos.Z, sample.Maxs.Z, sample.Maxs.Z + 0.1f);
                Assert.NotEqual(0, into.HitContents & BspLegacyWorld.ContentsLiquidsMask);
                Assert.True(through.Fraction > into.Fraction);
            }
        }

        // Visibility.
        int self = world.CheckPvs(eye, Q(eye.X - 16, eye.Y - 16, eye.Z - 24), Q(eye.X + 16, eye.Y + 16, eye.Z + 45));
        int outside = world.CheckPvs(Q(worldMaxs.X + 5000, worldMaxs.Y + 5000, worldMaxs.Z + 5000), Q(eye.X - 16, eye.Y - 16, eye.Z - 24), Q(eye.X + 16, eye.Y + 16, eye.Z + 45));
        int visible = 0, hidden = 0;
        foreach (QcVector other in spawns)
        {
            int answer = world.CheckPvs(eye, Q(other.X - 16, other.Y - 16, other.Z - 24), Q(other.X + 16, other.Y + 16, other.Z + 45));
            if (answer == 1) visible++; else if (answer == 0) hidden++;
        }
        Say($"checkpvs: own position {self}; from outside the map {outside}; of the {spawns.Count} spawn points {visible} are potentially visible from the first and {hidden} are not");
        Assert.Equal(1, self);
        Assert.Equal(2, outside);
        Assert.Equal(spawns.Count, visible + hidden);

        // Submodels: "*1" has bounds, and an entity showing it is clipped against its brushes.
        ILegacyModels models = rig.Presentation.Models;
        int submodels = 0, withBrushes = 0;
        string? firstWithBrushes = null;
        for (int i = 1; i < map.Bsp.Models.Length; i++)
            if (map.TryGetSubmodel("*" + i, out _, out _, out Brush[] brushes))
            {
                submodels++;
                if (brushes.Length > 0) { withBrushes++; firstWithBrushes ??= "*" + i; }
            }
        Say($"submodels: {submodels}, of which {withBrushes} have collision brushes");
        if (firstWithBrushes is not null)
        {
            Assert.True(models.TryGetBounds(firstWithBrushes, out QcVector subMins, out QcVector subMaxs));
            map.TryGetSubmodel(firstWithBrushes, out _, out _, out Brush[] brushes);
            Say($"{firstWithBrushes}: bounds {F(subMins)} .. {F(subMaxs)}, {brushes.Length} brushes");
            // Put a copy of it 3000 units above the map and shoot down at it.
            rig.State.ApplyPrecache(2, false, firstWithBrushes);
            float lift = worldMaxs.Z + 3000 - subMins.Z;
            int mover = rig.Spawn(Q(0, 0, lift), subMins, subMaxs, solid: 4, modelIndex: 2);
            Vector3 centre = (brushes[0].Mins + brushes[0].Maxs) * 0.5f;
            LegacyTrace onto = world.Trace(Q(centre.X, centre.Y, brushes[0].Maxs.Z + lift + 64), default, default, Q(centre.X, centre.Y, brushes[0].Mins.Z + lift - 64), 0, 0, Solid | Body | Corpse | BspLegacyWorld.ContentsPlayerClip | BspLegacyWorld.ContentsLiquidsMask, isLine: true);
            Say($"traceline onto a SOLID_BSP entity showing {firstWithBrushes}, lifted {lift:0.#}: fraction {onto.Fraction:0.####}, trace_ent {onto.Entity} (the entity is {mover}), texture \"{onto.HitTextureName}\", dphitcontents 0x{onto.HitContents:X}");
        }
        Save("stormkeep");
    }

    [Fact]
    public void Player_Model_Bounds_Bones_Animation_And_Skeleton()
    {
        using HeadlessPresentationTests.Rig? rig = RealRig();
        if (rig is null) return;
        ILegacyModels models = rig.Presentation.Models;
        LegacyModel? model = rig.Presentation.ModelData.Load(Player);
        Assert.NotNull(model);
        Say($"{Player}: format {model!.Format}, {model.NumBones} bones, {model.NumPoses} poses, {model.NumFrames} frame groups");

        Assert.True(models.TryGetBounds(Player, out QcVector mins, out QcVector maxs));
        Say($"bounds (normalmins/maxs): {F(mins)} .. {F(maxs)}");
        // A standing human figure about 100 units tall with its feet near -24 (Xonotic's player origin is at hip height).
        Assert.InRange(maxs.Z - mins.Z, 60, 250);
        Assert.True(mins.Z < 0 && maxs.Z > 0 && mins.X < 0 && maxs.X > 0);

        // Bones by name; the ones the game's own code asks for.
        int head = models.TagIndex(Player, 0, "head"), weapon = models.TagIndex(Player, 0, "tag_weapon"), spine = models.TagIndex(Player, 0, "spine2");
        Say($"gettagindex: head {head}, tag_weapon {weapon}, spine2 {spine}, HEAD {models.TagIndex(Player, 0, "HEAD")}, nosuchbone {models.TagIndex(Player, 0, "nosuchbone")}");
        Say("bones 1..12: " + string.Join(", ", model.BoneNames.Take(12).Select((n, i) => $"{i + 1} {n}<{model.BoneParents[i] + 1}")));
        Assert.True(head > 0);
        Assert.Equal(head, models.TagIndex(Player, 0, "HEAD"));
        Assert.Equal(0, models.TagIndex(Player, 0, "nosuchbone"));

        // Frame groups from erebus.iqm.framegroups: "0 36 30 0 // dieone", "36 20 25 0 // dietwo", ...
        float die = models.FrameDuration(Player, 0), dieTwo = models.FrameDuration(Player, 1);
        Say($"frameduration: group 0 {die:0.####} s (36 frames at 30), group 1 {dieTwo:0.####} s (20 at 25), group {model.NumFrames} {models.FrameDuration(Player, model.NumFrames)}");
        Say("frame groups 0..7: " + string.Join("; ", model.Scenes!.Take(8).Select((s, i) => string.Create(CultureInfo.InvariantCulture, $"{i} \"{s.Name}\" {s.FirstFrame}+{s.FrameCount} @{s.FrameRate} {(s.Loop ? "loop" : "once")}"))));
        Assert.Equal(1.2f, die, 4);
        Assert.Equal(0.8f, dieTwo, 4);
        Assert.Equal(0, models.FrameDuration(Player, model.NumFrames));
        Assert.Equal(-1, models.FrameForName(Player, "dieone"));   // the names are in comments: the groups are "groupified_N_anim"
        Assert.Equal(0, models.FrameForName(Player, "groupified_0_anim"));

        // gettaginfo on an entity showing the model, standing at a known place, in its idle frame.
        int idle = Array.FindIndex(model.Scenes, s => s.FrameCount == 1 && s.Loop) is var found && found >= 0 ? found : 0;
        int player = rig.Spawn(Q(1000, 2000, 300), Q(-16, -16, -24), Q(16, 16, 45), solid: 3, modelIndex: 3);
        rig.Vm.FieldFloat(player, rig.F.Frame) = idle;
        Assert.Equal(0, models.TagInfo(player, head, out LegacyTagInfo info));
        Say($"gettaginfo(player at '1000 2000 300', head) in frame group {idle}: origin {F(info.Origin)}, forward {F(info.Forward)}, up {F(info.Up)}, name \"{info.Name}\", parent {info.Parent}, local offset {F(info.LocalOffset)}");
        Assert.Equal("head", info.Name, ignoreCase: true);
        Assert.True(info.Parent > 0 && info.Parent < head);
        // The head is above the origin, within the model's box.
        Assert.InRange(info.Origin.Z - 300, 10, maxs.Z + 20);
        Assert.InRange(MathF.Abs(info.Origin.X - 1000), 0, 40);
        Assert.InRange(Length(info.Forward), 0.99f, 1.01f);
        Assert.Equal(4, models.TagInfo(player, model.NumBones + 1, out _));

        // Skeleton objects: create, inspect, build from the entity's animation, compare with gettaginfo.
        int skeleton = models.SkelCreate(Player);
        Assert.True(skeleton > 0);
        Assert.Equal(model.NumBones, models.SkelNumBones(skeleton));
        Assert.Equal(model.BoneNames[head - 1], models.SkelBoneName(skeleton, head));
        Assert.Equal(info.Parent, models.SkelBoneParent(skeleton, head));
        Assert.Equal(head, models.SkelFindBone(skeleton, "head"));
        Assert.Null(models.SkelBoneName(skeleton, 0));
        Assert.Null(models.SkelBoneName(skeleton, model.NumBones + 1));
        Assert.Equal(0, models.SkelBoneParent(skeleton, 1));
        // Fresh from skel_create every bone is identity.
        Assert.True(models.SkelGetBone(skeleton, head, absolute: true, out LegacyBoneTransform bone));
        Assert.Equal(default, bone.Origin);
        Assert.Equal(Q(0, -1, 0), bone.Right);

        Assert.Equal(skeleton, models.SkelBuild(skeleton, player, Player, 0, 0, 100000));
        Assert.True(models.SkelGetBone(skeleton, head, absolute: true, out bone));
        Assert.True(models.SkelGetBone(skeleton, head, absolute: false, out LegacyBoneTransform relative));
        Say($"skel_build then skel_get_boneabs(head): origin {F(bone.Origin)}, forward {F(bone.Forward)}; skel_get_bonerel: origin {F(relative.Origin)}");
        // The same bone, the same pose: model space plus the entity's origin is what gettaginfo said.
        HeadlessPresentationTests.Near(info.Origin, Q(bone.Origin.X + 1000, bone.Origin.Y + 2000, bone.Origin.Z + 300), 0.01f);
        HeadlessPresentationTests.Near(info.LocalOffset, relative.Origin, 0.01f);

        // With the skeleton assigned to the entity, gettaginfo reads the skeleton: move the head bone and it follows.
        rig.Vm.FieldFloat(player, rig.F.SkeletonIndex) = skeleton;
        relative.Origin = Q(relative.Origin.X, relative.Origin.Y, relative.Origin.Z + 10);
        models.SkelSetBone(skeleton, head, relative, multiply: false);
        models.TagInfo(player, head, out LegacyTagInfo moved);
        Say($"after skel_set_bone moved the head 10 along its parent's up: gettaginfo origin {F(moved.Origin)} (was {F(info.Origin)})");
        Assert.InRange(Length(Q(moved.Origin.X - info.Origin.X, moved.Origin.Y - info.Origin.Y, moved.Origin.Z - info.Origin.Z)), 9.99f, 10.01f);

        // retainfrac 1 changes nothing; copy and delete.
        models.SkelBuild(skeleton, player, Player, 1, 0, 100000);
        models.SkelGetBone(skeleton, head, false, out LegacyBoneTransform kept);
        HeadlessPresentationTests.Near(relative.Origin, kept.Origin);
        int second = models.SkelCreate(Player);
        Assert.Equal(skeleton + 1, second);
        models.SkelCopyBones(second, skeleton, 0, 100000);
        models.SkelGetBone(second, head, false, out LegacyBoneTransform copied);
        HeadlessPresentationTests.Near(relative.Origin, copied.Origin);
        models.SkelDelete(skeleton);
        Assert.Equal(0, models.SkelNumBones(skeleton));
        Assert.Equal(0, models.SkelBuild(skeleton, player, Player, 0, 0, 100000));
        Assert.Equal(skeleton, models.SkelCreate(Player));   // the freed slot is the first free one
        Assert.Equal(2, rig.Presentation.ModelData.LiveSkeletons);

        // An animated group: a quarter of a second in, the pose is neither the first nor the last frame's.
        int walk = Array.FindIndex(model.Scenes, s => s.FrameCount > 10 && s.Loop);
        if (walk >= 0)
        {
            rig.Vm.FieldFloat(player, rig.F.SkeletonIndex) = 0;
            rig.Vm.FieldFloat(player, rig.F.Frame) = walk;
            rig.Vm.FieldFloat(player, rig.F.Frame1Time) = 10;
            rig.State.Time = 10;
            models.TagInfo(player, head, out LegacyTagInfo t0);
            rig.State.Time = 10.25;
            models.TagInfo(player, head, out LegacyTagInfo t1);
            rig.State.Time = 10 + models.FrameDuration(Player, walk);
            models.TagInfo(player, head, out LegacyTagInfo wrapped);
            Say($"frame group {walk} ({model.Scenes[walk].FrameCount} frames at {model.Scenes[walk].FrameRate}, looping): head at {F(t0.Origin)} at its start, {F(t1.Origin)} 0.25 s in, {F(wrapped.Origin)} one duration later");
            HeadlessPresentationTests.Near(t0.Origin, wrapped.Origin, 0.05f);
        }
        Save("player-model");
    }

    private static float Length(QcVector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    [Fact]
    public void Weapon_Models_And_Hud_Pictures()
    {
        using HeadlessPresentationTests.Rig? rig = RealRig();
        if (rig is null) return;
        ILegacyModels models = rig.Presentation.Models;
        foreach (string name in new[] { ViewWeapon, "models/weapons/g_rl.md3", HandWeapon })
        {
            LegacyModel? model = rig.Presentation.ModelData.Load(name);
            Assert.NotNull(model);
            Assert.True(models.TryGetBounds(name, out QcVector mins, out QcVector maxs));
            Say($"{name}: format {model!.Format}, bounds {F(mins)} .. {F(maxs)}, {model.NumPoses} poses, {model.NumFrames} frame groups, " +
                $"tags [{string.Join(", ", model.TagNames)}], bones [{string.Join(", ", model.BoneNames.Take(8))}{(model.NumBones > 8 ? ", ..." : "")}]");
            Assert.True(maxs.X > mins.X && maxs.Y > mins.Y && maxs.Z > mins.Z);
            Assert.InRange(maxs.X - mins.X, 1, 400);
        }

        // The hand model carries the attachment points: where the shot leaves, where the view model hangs.
        LegacyModel view = rig.Presentation.ModelData.Load(ViewWeapon)!;
        LegacyModel hands = rig.Presentation.ModelData.Load(HandWeapon)!;
        foreach (string wanted in new[] { "shot", "weapon", "tag_weapon", "shell", "handle" })
            Say($"gettagindex({HandWeapon}, \"{wanted}\") = {models.TagIndex(HandWeapon, 0, wanted)}; gettagindex({ViewWeapon}, \"{wanted}\") = {models.TagIndex(ViewWeapon, 0, wanted)}");
        string tagName = hands.BoneNames.FirstOrDefault(t => t.Equals("shot", StringComparison.OrdinalIgnoreCase)) ?? hands.BoneNames[^1];
        int tag = models.TagIndex(HandWeapon, 0, tagName);
        Assert.True(tag > 0);
        int weapon = rig.Spawn(Q(0, 0, 0), default, default, 0, modelIndex: 5);
        Assert.Equal(0, models.TagInfo(weapon, tag, out LegacyTagInfo info));
        Say($"gettaginfo({HandWeapon} at the origin, \"{tagName}\" = {tag}): origin {F(info.Origin)}, forward {F(info.Forward)}, right {F(info.Right)}, up {F(info.Up)}, parent {info.Parent}");
        Assert.Equal(tagName, info.Name);
        Assert.InRange(Length(info.Forward), 0.5f, 2f);
        // The view model attached to the hand's bone is placed through it.
        int attached = rig.Spawn(Q(0, 0, 0), default, default, 0, modelIndex: 4);
        rig.Vm.FieldInt(attached, rig.F.TagEntity) = weapon;
        rig.Vm.FieldFloat(attached, rig.F.TagIndex) = tag;
        Assert.Equal(0, models.TagInfo(attached, 0, out LegacyTagInfo through));
        HeadlessPresentationTests.Near(info.Origin, through.Origin, 0.01f);
        Say($"frameduration({ViewWeapon}, 0) = {models.FrameDuration(ViewWeapon, 0)} (one frame at 10 a second); frameforname(\"{view.Scenes![0].Name}\") = {models.FrameForName(ViewWeapon, view.Scenes[0].Name)}");
        Assert.Equal(0.1f, models.FrameDuration(ViewWeapon, 0));

        // The hand model: animated by a .framegroups file ("fire", "fire2", "idle", "reload").
        LegacyModel hand = rig.Presentation.ModelData.Load(HandWeapon)!;
        Say($"{HandWeapon} frame groups: " + string.Join("; ", hand.Scenes!.Select((s, i) => string.Create(CultureInfo.InvariantCulture, $"{i} {s.FirstFrame}+{s.FrameCount} @{s.FrameRate} = {models.FrameDuration(HandWeapon, i):0.###} s"))));
        Assert.True(hand.NumBones > 0);
        Assert.True(models.FrameDuration(HandWeapon, 0) > 0);

        // HUD pictures: sizes from the file headers, by DarkPlaces' search order.
        ILegacyDraw draw = rig.Presentation.Draw;
        foreach (string picture in new[] { "gfx/hud/default/ammo_rockets", "gfx/hud/default/health", "gfx/hud/luma/health", "gfx/conchars", "gfx/crosshair1", "gfx/hud/default/no_such_picture" })
        {
            bool exists = draw.PictureExists(picture);
            QcVector size = draw.ImageSize(picture);
            Say($"precache_pic(\"{picture}\") {(exists ? "ok" : "FAILED")}; draw_getimagesize = {F(size)}; file {rig.Vfs.ResolveImage(picture) ?? "none"}");
        }
        Assert.True(draw.PictureExists("gfx/hud/default/ammo_rockets"));
        QcVector ammo = draw.ImageSize("gfx/hud/default/ammo_rockets");
        Assert.True(ammo.X >= 16 && ammo.Y >= 16 && ammo.X <= 4096 && ammo.Y <= 4096);
        Assert.Equal(Q(128, 128, 0), draw.ImageSize("gfx/conchars"));      // Image_GetStockPicSize's one hard-coded size
        Assert.False(draw.PictureExists("gfx/hud/default/no_such_picture"));
        Assert.Equal(Q(0, 0, 0), draw.ImageSize("gfx/hud/default/no_such_picture"));
        Save("models-pictures");
    }
}
