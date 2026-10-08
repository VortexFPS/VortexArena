using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The headless presentation's three answering parts against small data built in the test: the world
/// (cl_collision.c's rules about what a trace clips), the models (tags, attachment chains, frame
/// groups) and the picture cache (gl_draw.c's order-dependent answers). The same parts against the
/// real game data are HeadlessRealDataTests.
/// </summary>
public class HeadlessPresentationTests
{
    internal const int Solid = BspLegacyWorld.ContentsSolid, Water = BspLegacyWorld.ContentsWater, Body = BspLegacyWorld.ContentsBody,
        Corpse = BspLegacyWorld.ContentsCorpse, PlayerClip = BspLegacyWorld.ContentsPlayerClip;

    /// <summary>A host over a one-function program, a temp-directory file system and a headless presentation.</summary>
    internal sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "va-headless-" + Guid.NewGuid().ToString("N"));
        public VirtualFileSystem Vfs { get; } = new();
        public CvarService Cvars { get; } = new();
        public HeadlessLegacyPresentation Presentation { get; }
        public CsqcClientState State { get; } = new();
        public CsqcHost Host { get; private set; } = null!;
        public QcVm Vm => Host.Vm;
        public CsqcFieldOffsets F => Host.Fields;
        public List<string> Warnings { get; } = new();

        /// <param name="mount">A game directory to mount instead of an empty temp directory.</param>
        public Rig(string[] models, string? mount = null, params (string Path, byte[] Data)[] files)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "placeholder.txt"), "x");
            foreach ((string path, byte[] data) in files)
            {
                string full = Path.Combine(Root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
            }
            if (mount is not null) Assert.True(Vfs.MountGameDir(mount));
            Assert.True(Vfs.Mount(Root));
            Presentation = new HeadlessLegacyPresentation(Vfs);
            State.ApplyServerInfo(new DpServerInfo
            {
                Protocol = DpProtocol.ProtocolNumberDp7, MaxClients = 8, GameType = 1, WorldMessage = "Test",
                Models = new[] { "" }.Concat(models).ToArray(), Sounds = new[] { "" },
            });
            State.SetView(1);
        }

        /// <summary>Starts the program. The world must be in place first: its bounds go into entity 0.</summary>
        public Rig Start()
        {
            ProgsBuilder b = new();
            b.Int(0, "self", QcType.Entity);
            b.Float(0, "time");
            b.Vector(0, 0, 0, "v_forward");
            b.Vector(0, 0, 0, "v_right");
            b.Vector(0, 0, 0, "v_up");
            b.Function("CSQC_UpdateView");
            b.Emit(QcOp.Done);
            byte[] program = b.Build();
            ConfigInterpreter interpreter = new(Cvars, path => Vfs.Exists(path) ? Vfs.ReadText(path) : null);
            LegacyQcHost services = new(Cvars, Vfs) { WarningSink = Warnings.Add };
            CsqcConsole console = new(interpreter, services);
            Host = new CsqcHost(program, program.Length, Crc16.Block(program), services, console, Presentation, State);
            Assert.True(Host.Init(), Host.FaultMessage);
            return this;
        }

        /// <summary>spawn + setmodelindex + setsize + setorigin, as far as the presentation can tell.</summary>
        public int Spawn(QcVector origin, QcVector mins, QcVector maxs, float solid, int modelIndex = 0, QcVector angles = default)
        {
            int e = Vm.AllocEdict();
            Vm.FieldVector(e, F.Origin) = origin;
            Vm.FieldVector(e, F.Mins) = mins;
            Vm.FieldVector(e, F.Maxs) = maxs;
            Vm.FieldVector(e, F.Angles) = angles;
            Vm.FieldFloat(e, F.Solid) = solid;
            Vm.FieldFloat(e, F.ModelIndex) = modelIndex;
            Link(e);
            return e;
        }

        public void Link(int e)
        {
            QcVector o = Vm.FieldVector(e, F.Origin), lo = Vm.FieldVector(e, F.Mins), hi = Vm.FieldVector(e, F.Maxs);
            Presentation.World.LinkEdict(e, new QcVector(o.X + lo.X, o.Y + lo.Y, o.Z + lo.Z), new QcVector(o.X + hi.X, o.Y + hi.Y, o.Z + hi.Z));
        }

        public int Field(string name) => Vm.FindField(name)!.Offset;

        public LegacyTrace Line(QcVector from, QcVector to, int move = 0, int ignore = 0, int mask = Solid | Body | Corpse) =>
            Presentation.World.Trace(from, default, default, to, move, ignore, mask, isLine: true);

        public void Dispose()
        {
            Host?.Dispose();
            Vfs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    internal static QcVector Q(float x, float y, float z) => new(x, y, z);

    // A floor slab 64 thick with its top at z = 0, a pool of water above part of it, a player-clip
    // wall, and one submodel: a door 8 thick standing at the model's own origin.
    private static BspCollisionBuilder.Result SyntheticMap()
    {
        CollisionWorld world = new();
        world.AddBrush(Brush.FromBox(new Vector3(-1024, -1024, -64), new Vector3(1024, 1024, 0), SuperContents.Solid | SuperContents.Opaque, Q3SurfaceFlags.NoDamage, "textures/test/floor"));
        world.AddBrush(Brush.FromBox(new Vector3(512, 512, 0), new Vector3(768, 768, 128), SuperContents.Water, 0, "textures/test/water"));
        world.AddBrush(Brush.FromBox(new Vector3(-768, -64, 0), new Vector3(-760, 64, 128), SuperContents.PlayerClip, 0, "textures/common/clip"));
        world.BuildGrid();
        Brush door = Brush.FromBox(new Vector3(-4, -32, 0), new Vector3(4, 32, 96), SuperContents.Solid | SuperContents.Opaque, 0, "textures/test/door");
        return new BspCollisionBuilder.Result
        {
            World = world,
            Submodels = new[] { new BspCollisionBuilder.Submodel("*1", new Vector3(-4, -32, 0), new Vector3(4, 32, 96), new[] { door }) },
        };
    }

    private static Rig SyntheticRig(params (string Path, byte[] Data)[] files)
    {
        Rig rig = new(new[] { "maps/test.bsp", "*1", "models/test.md3", "models/hand.md3" }, null, files);
        rig.Presentation.Map.UseMap("maps/test.bsp", null, SyntheticMap());
        return rig.Start();
    }

    // ---- world -------------------------------------------------------------------------------------

    [Fact]
    public void Contents_Are_Translated_Both_Ways_Between_DarkPlaces_And_The_Collision_Library()
    {
        Assert.Equal(SuperContents.Solid | SuperContents.Body | SuperContents.Corpse, BspLegacyWorld.ContentsToEngine(Solid | Body | Corpse));
        Assert.Equal(SuperContents.Water | SuperContents.Slime | SuperContents.Lava, BspLegacyWorld.ContentsToEngine(BspLegacyWorld.ContentsLiquidsMask));
        Assert.Equal(SuperContents.PlayerClip, BspLegacyWorld.ContentsToEngine(PlayerClip));
        for (int bit = 0; bit < 13; bit++)
            Assert.Equal(1 << bit, BspLegacyWorld.ContentsFromEngine(BspLegacyWorld.ContentsToEngine(1 << bit)));
        // Bits DarkPlaces does not define carry nothing across.
        Assert.Equal(0, BspLegacyWorld.ContentsToEngine(0x2000));
    }

    [Fact]
    public void World_Trace_Hits_The_Floor_With_Plane_Texture_And_DarkPlaces_Contents()
    {
        using Rig rig = SyntheticRig();
        rig.Presentation.World.Bounds(out QcVector mins, out QcVector maxs);
        Assert.Equal(-1024, mins.X);
        Assert.Equal(128, maxs.Z);
        Assert.Equal(new QcVector(-1024, -1024, -64), rig.Vm.FieldVector(0, rig.F.Mins));   // CL_VM_Init: the world entity's size

        LegacyTrace down = rig.Line(Q(0, 0, 100), Q(0, 0, -100));
        Assert.InRange(down.Fraction, 0.499f, 0.5f);          // 100 of 200 units, less the 1/32 nudge
        Assert.InRange(down.EndPos.Z, 0, 0.04f);
        Assert.Equal(Q(0, 0, 1), down.PlaneNormal);
        Assert.Equal(0, down.PlaneDist);
        Assert.Equal("textures/test/floor", down.HitTextureName);
        Assert.Equal(Solid | BspLegacyWorld.ContentsOpaque, down.HitContents);
        Assert.Equal(Q3SurfaceFlags.NoDamage, down.HitQ3SurfaceFlags);
        Assert.Equal(0, down.Entity);
        Assert.False(down.StartSolid);
        // A Quake 3 map never sets these two (only the Quake 1 hull code does).
        Assert.False(down.InOpen);
        Assert.False(down.InWater);

        LegacyTrace air = rig.Line(Q(0, 0, 100), Q(300, 0, 100));
        Assert.Equal(1, air.Fraction);
        Assert.Equal(Q(300, 0, 100), air.EndPos);
        Assert.Equal(default, air.PlaneNormal);
        Assert.Null(air.HitTextureName);

        // A box rests its bottom on the floor.
        LegacyTrace box = rig.Presentation.World.Trace(Q(0, 0, 100), Q(-16, -16, -24), Q(16, 16, 45), Q(0, 0, -100), 0, 0, Solid | Body | PlayerClip, isLine: false);
        Assert.InRange(box.EndPos.Z, 24, 24.04f);

        // Started inside the floor.
        LegacyTrace inside = rig.Line(Q(0, 0, -32), Q(0, 0, -40));
        Assert.True(inside.StartSolid);
        Assert.Equal(Solid | BspLegacyWorld.ContentsOpaque, inside.StartContents);

        // The mask decides what stops a move: water does not stop a default trace but stops one that asks.
        Assert.Equal(1, rig.Line(Q(640, 640, 300), Q(640, 640, 64)).Fraction);
        LegacyTrace intoWater = rig.Line(Q(640, 640, 300), Q(640, 640, 64), mask: Solid | Water);
        Assert.InRange(intoWater.EndPos.Z, 128, 128.04f);
        Assert.Equal(Water, intoWater.HitContents);
        // A player-clip wall stops only a player's mask.
        Assert.Equal(1, rig.Line(Q(-700, 0, 32), Q(-800, 0, 32)).Fraction);
        Assert.InRange(rig.Line(Q(-700, 0, 32), Q(-800, 0, 32), mask: Solid | Body | PlayerClip).EndPos.X, -760, -759.9f);
        // A mask of bits nothing defines stops on nothing.
        Assert.Equal(1, rig.Line(Q(0, 0, 100), Q(0, 0, -100), mask: 0x4000).Fraction);
        // NaN in, nothing out (the builtin faults on NaN before it gets here; this is the backstop).
        Assert.Equal(1, rig.Line(Q(float.NaN, 0, 0), Q(0, 0, -100)).Fraction);
    }

    [Fact]
    public void Point_Contents_And_Pvs_Without_Visibility_Data()
    {
        using Rig rig = SyntheticRig();
        ILegacyWorld world = rig.Presentation.World;
        Assert.Equal(Water, world.PointSuperContents(Q(640, 640, 64)));
        Assert.Equal(Solid | BspLegacyWorld.ContentsOpaque, world.PointSuperContents(Q(0, 0, -32)));
        Assert.Equal(0, world.PointSuperContents(Q(0, 0, 64)));
        Assert.Equal(PlayerClip, world.PointSuperContents(Q(-764, 0, 64)));
        // Collision built by hand has no visibility lump: "no PVS support on this worldmodel".
        Assert.Equal(3, world.CheckPvs(Q(0, 0, 64), Q(-16, -16, 0), Q(16, 16, 64)));
        Assert.Equal(4096, world.DropToFloorDistance);

        // pointcontents does not look at the program's entities at all (CL_PointSuperContents passes
        // hitcsqcentities = false): a solid box entity around the point changes nothing.
        rig.Spawn(Q(0, 0, 64), Q(-32, -32, -32), Q(32, 32, 32), solid: 2);
        Assert.Equal(0, world.PointSuperContents(Q(0, 0, 64)));
    }

    [Fact]
    public void Traces_Clip_Linked_Entities_By_Solid_Owner_Move_Type_And_Mask()
    {
        using Rig rig = SyntheticRig();
        const float bbox = 2, trigger = 1, corpse = 5;
        int box = rig.Spawn(Q(200, 0, 32), Q(-16, -16, -16), Q(16, 16, 16), bbox);
        QcVector from = Q(0, 0, 32), to = Q(400, 0, 32);

        LegacyTrace hit = rig.Line(from, to);
        Assert.Equal(box, hit.Entity);
        Assert.InRange(hit.EndPos.X, 183.9f, 184);
        Assert.Equal(Q(-1, 0, 0), hit.PlaneNormal);
        Assert.Equal(Body, hit.HitContents);
        Assert.Null(hit.HitTextureName);

        // "don't clip against self", "don't clip owner against owned entities" and the reverse.
        Assert.Equal(1, rig.Line(from, to, ignore: box).Fraction);
        int owner = rig.Spawn(Q(0, 500, 32), default, default, 0);
        rig.Vm.FieldInt(box, rig.Field("owner")) = owner;
        Assert.Equal(1, rig.Line(from, to, ignore: owner).Fraction);
        rig.Vm.FieldInt(box, rig.Field("owner")) = 0;
        rig.Vm.FieldInt(owner, rig.Field("owner")) = box;
        Assert.Equal(1, rig.Line(from, to, ignore: owner).Fraction);
        rig.Vm.FieldInt(owner, rig.Field("owner")) = 0;
        Assert.Equal(box, rig.Line(from, to, ignore: owner).Entity);

        // MOVE_NOMONSTERS clips brush entities only, MOVE_WORLDONLY none.
        Assert.Equal(1, rig.Line(from, to, move: 1).Fraction);
        Assert.Equal(1, rig.Line(from, to, move: 3).Fraction);
        Assert.Equal(box, rig.Line(from, to, move: 4).Entity);   // MOVE_HITMODEL: as its box (deviation, see BspLegacyWorld)

        // A trigger is not solid; a corpse is, to a mask with CORPSE, and reports that.
        rig.Vm.FieldFloat(box, rig.F.Solid) = trigger;
        Assert.Equal(1, rig.Line(from, to).Fraction);
        rig.Vm.FieldFloat(box, rig.F.Solid) = corpse;
        Assert.Equal(Corpse, rig.Line(from, to).HitContents);
        Assert.Equal(1, rig.Line(from, to, mask: Solid | Body).Fraction);
        rig.Vm.FieldFloat(box, rig.F.Solid) = bbox;

        // DP_RM_CLIPGROUP: same non-zero group as the passed entity, no clip.
        rig.Vm.FieldFloat(box, rig.Field("clipgroup")) = 3;
        rig.Vm.FieldFloat(owner, rig.Field("clipgroup")) = 3;
        Assert.Equal(1, rig.Line(from, to, ignore: owner).Fraction);
        Assert.Equal(box, rig.Line(from, to).Entity);            // the world passes nothing
        rig.Vm.FieldFloat(owner, rig.Field("clipgroup")) = 4;
        Assert.Equal(box, rig.Line(from, to, ignore: owner).Entity);

        // The fields are read at trace time, the box where it was last linked: moving the origin
        // without relinking leaves the broadphase at the old place.
        rig.Vm.FieldVector(box, rig.F.Origin) = Q(200, 300, 32);
        Assert.Equal(1, rig.Line(from, to).Fraction);
        rig.Link(box);
        Assert.Equal(box, rig.Line(Q(0, 300, 32), Q(400, 300, 32)).Entity);
        rig.Presentation.World.UnlinkEdict(box);
        Assert.Equal(1, rig.Line(Q(0, 300, 32), Q(400, 300, 32)).Fraction);

        // An entity number from nowhere passes nothing and links nothing.
        rig.Link(box);
        Assert.Equal(box, rig.Line(Q(0, 300, 32), Q(400, 300, 32), ignore: 31000).Entity);
        Assert.Equal(box, rig.Line(Q(0, 300, 32), Q(400, 300, 32), ignore: -7).Entity);
        rig.Presentation.World.LinkEdict(40000, Q(0, 0, 0), Q(1, 1, 1));
        rig.Presentation.World.LinkEdict(-5, Q(0, 0, 0), Q(1, 1, 1));
        rig.Presentation.World.UnlinkEdict(99999);
    }

    [Fact]
    public void Missile_Moves_Are_Fifteen_Units_Fatter_Against_Monsters_Only()
    {
        using Rig rig = SyntheticRig();
        int monster = rig.Spawn(Q(200, 0, 32), Q(-16, -16, -16), Q(16, 16, 16), solid: 3);
        rig.Vm.FieldFloat(monster, rig.F.Flags) = 32; // FL_MONSTER
        int crate = rig.Spawn(Q(200, 200, 32), Q(-16, -16, -16), Q(16, 16, 16), solid: 2);

        // A line passing 25 units beside each: 9 outside the box, inside the 15 of a missile's margin.
        Assert.Equal(1, rig.Line(Q(0, 25, 32), Q(400, 25, 32)).Fraction);
        LegacyTrace missile = rig.Line(Q(0, 25, 32), Q(400, 25, 32), move: 2);
        Assert.Equal(monster, missile.Entity);
        Assert.InRange(missile.EndPos.X, 168.9f, 169);                 // 200 - 16 - 15
        Assert.Equal(1, rig.Line(Q(0, 225, 32), Q(400, 225, 32), move: 2).Fraction);
        // Straight at the crate, a missile is no fatter than a line.
        Assert.InRange(rig.Line(Q(0, 200, 32), Q(400, 200, 32), move: 2).EndPos.X, 183.9f, 184);
        Assert.Equal(crate, rig.Line(Q(0, 200, 32), Q(400, 200, 32), move: 2).Entity);
    }

    [Fact]
    public void A_Brush_Submodel_Entity_Is_Clipped_Against_Its_Own_Brushes_Moved_And_Turned()
    {
        using Rig rig = SyntheticRig();
        // The door model is 8 x 64 x 96 around its origin; the entity's box is deliberately far larger.
        Assert.True(rig.Presentation.Models.TryGetBounds("*1", out QcVector mins, out QcVector maxs));
        Assert.Equal(Q(-4, -32, 0), mins);
        Assert.Equal(Q(4, 32, 96), maxs);
        int door = rig.Spawn(Q(300, 0, 0), Q(-64, -64, 0), Q(64, 64, 96), solid: 4, modelIndex: 2);

        LegacyTrace hit = rig.Line(Q(0, 0, 32), Q(600, 0, 32));
        Assert.Equal(door, hit.Entity);
        Assert.InRange(hit.EndPos.X, 295.9f, 296);                     // the brush face at 300 - 4, not the box at 236
        Assert.Equal("textures/test/door", hit.HitTextureName);
        Assert.Equal(Solid | BspLegacyWorld.ContentsOpaque, hit.HitContents);
        // 40 units to the side misses the 64-wide door...
        Assert.Equal(1, rig.Line(Q(0, 40, 32), Q(250, 40, 32)).Fraction);
        Assert.Equal(1, rig.Line(Q(280, 100, 32), Q(280, -100, 32)).Fraction);
        // ...until it is turned a quarter: then it is 64 long in X and 8 thick in Y.
        rig.Vm.FieldVector(door, rig.F.Angles) = Q(0, 90, 0);
        LegacyTrace turned = rig.Line(Q(280, 100, 32), Q(280, -100, 32));
        Assert.Equal(door, turned.Entity);
        Assert.InRange(turned.EndPos.Y, 4, 4.1f);
        Assert.InRange(turned.PlaneNormal.Y, 0.999f, 1.001f);          // the plane comes back in world space
        // MOVE_NOMONSTERS still clips it: it is a brush model.
        Assert.Equal(door, rig.Line(Q(280, 100, 32), Q(280, -100, 32), move: 1).Entity);
        // SOLID_BSP with a model that has no brushes falls back to the entity's box.
        rig.Vm.FieldFloat(door, rig.F.ModelIndex) = 3;
        Assert.InRange(rig.Line(Q(0, 0, 32), Q(600, 0, 32)).EndPos.X, 235.9f, 236);
    }

    [Fact]
    public void Walkmove_Steps_Along_The_Floor_And_Refuses_To_Walk_Off_It()
    {
        using Rig rig = SyntheticRig();
        int walker = rig.Spawn(Q(0, 0, 24.03125f), Q(-16, -16, -24), Q(16, 16, 32), solid: 3);
        rig.Vm.FieldFloat(walker, rig.F.Flags) = 512; // FL_ONGROUND
        Assert.True(rig.Presentation.World.MoveStep(walker, Q(8, 0, 0), setTrace: false));
        Assert.InRange(rig.Vm.FieldVector(walker, rig.F.Origin).X, 7.99f, 8.01f);
        Assert.InRange(rig.Vm.FieldVector(walker, rig.F.Origin).Z, 24, 24.07f);
        // Into the door: blocked (the down-trace starts solid at both heights).
        rig.Spawn(Q(40, 0, 0), default, default, solid: 4, modelIndex: 2);
        Assert.False(rig.Presentation.World.MoveStep(walker, Q(20, 0, 0), setTrace: false));
        // Off the edge of the world's floor.
        rig.Vm.FieldVector(walker, rig.F.Origin) = Q(1000, 0, 24.03125f);
        Assert.False(rig.Presentation.World.MoveStep(walker, Q(60, 0, 0), setTrace: false));
        Assert.Equal(1000, rig.Vm.FieldVector(walker, rig.F.Origin).X);
        Assert.False(rig.Presentation.World.MoveStep(0, Q(1, 0, 0), false));
        Assert.False(rig.Presentation.World.MoveStep(9999, Q(1, 0, 0), false));
    }

    [Fact]
    public void A_Missing_Or_Malformed_Map_Leaves_An_Empty_World()
    {
        using Rig rig = new(new[] { "maps/nowhere.bsp", "maps/junk.bsp" }, null, ("maps/junk.bsp", Encoding.ASCII.GetBytes("IBSP this is not a map at all.............")));
        BspLegacyWorld world = rig.Presentation.Map;
        Assert.False(world.LoadMap("maps/nowhere.bsp"));
        Assert.Contains("not in the game data", world.LoadError);
        Assert.False(world.LoadMap("maps/junk.bsp"));
        Assert.Contains("could not be loaded", world.LoadError);
        Assert.False(world.LoadMap("../outside.bsp"));
        Assert.Null(world.MapName);
        rig.Start();
        Assert.Equal(1, rig.Line(Q(0, 0, 100), Q(0, 0, -100)).Fraction);
        Assert.Equal(0, rig.Presentation.World.PointSuperContents(Q(0, 0, 0)));
        Assert.Equal(3, rig.Presentation.World.CheckPvs(Q(0, 0, 0), Q(0, 0, 0), Q(1, 1, 1)));
        Assert.False(rig.Presentation.Models.TryGetBounds("*1", out _, out _));
    }

    // ---- models ------------------------------------------------------------------------------------

    /// <summary>
    /// An MD3 with <paramref name="frames"/> frames, one triangle whose corners move 1 unit up per
    /// frame, and one tag per name, sitting at (10 * frame, 0, 20) with its axes turned 90 degrees
    /// about Z (forward is +Y).
    /// </summary>
    internal static byte[] BuildMd3(int frames, params string[] tags)
    {
        const int headerSize = 108, frameSize = 56, tagSize = 112, surfaceHeader = 108, shaderSize = 68;
        int ofsFrames = headerSize, ofsTags = ofsFrames + frames * frameSize, ofsSurfaces = ofsTags + frames * tags.Length * tagSize;
        int ofsShaders = surfaceHeader, ofsTriangles = ofsShaders + shaderSize, ofsSt = ofsTriangles + 12, ofsXyz = ofsSt + 3 * 8;
        int surfaceSize = ofsXyz + frames * 3 * 8;
        byte[] d = new byte[ofsSurfaces + surfaceSize];
        void I(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at), v);
        void Fl(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), v);
        void Sh(int at, short v) => BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(at), v);
        void St(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(d, at);

        St(0, "IDP3"); I(4, 15); St(8, "test");
        I(76, frames); I(80, tags.Length); I(84, 1); I(88, 0);
        I(92, ofsFrames); I(96, ofsTags); I(100, ofsSurfaces); I(104, d.Length);
        for (int f = 0; f < frames; f++)
        {
            int at = ofsFrames + f * frameSize;
            Fl(at, -8); Fl(at + 4, -8); Fl(at + 8, f); Fl(at + 12, 8); Fl(at + 16, 8); Fl(at + 20, f + 4); Fl(at + 36, 12);
            St(at + 40, "frame" + f);
            for (int t = 0; t < tags.Length; t++)
            {
                int tag = ofsTags + (f * tags.Length + t) * tagSize;
                St(tag, tags[t]);
                Fl(tag + 64, 10 * f); Fl(tag + 68, t * 5); Fl(tag + 72, 20);
                // axis[0] = (0,1,0), axis[1] = (-1,0,0), axis[2] = (0,0,1)
                Fl(tag + 76 + 4, 1); Fl(tag + 88, -1); Fl(tag + 100 + 8, 1);
            }
        }
        int s0 = ofsSurfaces;
        St(s0, "IDP3"); St(s0 + 4, "tri");
        I(s0 + 72, frames); I(s0 + 76, 1); I(s0 + 80, 3); I(s0 + 84, 1);
        I(s0 + 88, ofsTriangles); I(s0 + 92, ofsShaders); I(s0 + 96, ofsSt); I(s0 + 100, ofsXyz); I(s0 + 104, surfaceSize);
        St(s0 + ofsShaders, "textures/test/tri");
        I(s0 + ofsTriangles, 0); I(s0 + ofsTriangles + 4, 1); I(s0 + ofsTriangles + 8, 2);
        (int X, int Y)[] corners = { (-8, -8), (8, -8), (0, 8) };
        for (int f = 0; f < frames; f++)
            for (int v = 0; v < 3; v++)
            {
                int at = s0 + ofsXyz + (f * 3 + v) * 8;
                Sh(at, (short)(corners[v].X * 64)); Sh(at + 2, (short)(corners[v].Y * 64)); Sh(at + 4, (short)((f + (v == 2 ? 4 : 0)) * 64));
            }
        return d;
    }

    [Fact]
    public void Md3_Bounds_Tags_And_Frame_Groups()
    {
        using Rig rig = SyntheticRig(
            ("models/test.md3", BuildMd3(4, "tag_weapon", "tag_Head")),
            ("models/test.md3.framegroups", Encoding.ASCII.GetBytes("0 1 10 1 // idle\n1 3 6 0 shoot\n90 50 0 1\n")),
            ("models/broken.md3", new byte[] { (byte)'I', (byte)'D', (byte)'P', (byte)'3', 15, 0, 0, 0, 1, 2, 3 }),
            ("models/strange.zym", Encoding.ASCII.GetBytes("ZYMOTICMODEL-and-so-on")));
        ILegacyModels models = rig.Presentation.Models;

        // normalmins / normalmaxs: every vertex of every frame.
        Assert.True(models.TryGetBounds("models/test.md3", out QcVector mins, out QcVector maxs));
        Assert.Equal(Q(-8, -8, 0), mins);
        Assert.Equal(Q(8, 8, 7), maxs);
        Assert.False(models.TryGetBounds("models/absent.md3", out _, out _));
        Assert.False(models.TryGetBounds("models/broken.md3", out _, out _));
        Assert.False(models.TryGetBounds("../models/test.md3", out _, out _));
        // A format this does not read exists, with no size.
        Assert.True(models.TryGetBounds("models/strange.zym", out mins, out maxs));
        Assert.Equal(default, maxs);
        // The model the engine makes up.
        Assert.True(models.TryGetBounds("null", out _, out _));

        // gettagindex: 1-based, names without case.
        Assert.Equal(1, models.TagIndex("models/test.md3", 0, "tag_weapon"));
        Assert.Equal(2, models.TagIndex("models/test.md3", 0, "TAG_HEAD"));
        Assert.Equal(0, models.TagIndex("models/test.md3", 0, "tag_none"));
        Assert.Equal(0, models.TagIndex("models/absent.md3", 0, "tag_weapon"));

        // The .framegroups file replaces the per-frame scenes; a group past the end is clamped onto it.
        Assert.Equal(0, models.FrameForName("models/test.md3", "groupified_0_anim"));
        Assert.Equal(1, models.FrameForName("models/test.md3", "SHOOT"));
        Assert.Equal(-1, models.FrameForName("models/test.md3", "frame0"));
        Assert.Equal(0.1f, models.FrameDuration("models/test.md3", 0));
        Assert.Equal(0.5f, models.FrameDuration("models/test.md3", 1));
        Assert.Equal(1f, models.FrameDuration("models/test.md3", 2));   // 1 frame (clamped) at the minimum rate of 1
        Assert.Equal(0, models.FrameDuration("models/test.md3", 3));
        Assert.Equal(0, models.FrameDuration("models/test.md3", -1));
        // Without the file: one scene per frame, named after it, a tenth of a second each.
        Assert.Equal(-1, models.FrameForName("models/hand.md3", "frame0"));  // not there yet
    }

    [Fact]
    public void Tag_Info_Follows_Entity_Placement_Animation_And_Attachment_Chains()
    {
        using Rig rig = SyntheticRig(
            ("models/test.md3", BuildMd3(4, "tag_weapon", "tag_head")),
            ("models/hand.md3", BuildMd3(1, "tag_shot")));
        ILegacyModels models = rig.Presentation.Models;
        Assert.Equal(0, models.FrameForName("models/hand.md3", "frame0"));
        Assert.Equal(0.1f, models.FrameDuration("models/test.md3", 3));

        int body = rig.Spawn(Q(100, 200, 300), default, default, 0, modelIndex: 3);
        // Codes first: world, free, no model, no such tag.
        Assert.Equal(1, models.TagInfo(0, 1, out LegacyTagInfo info));
        Assert.Equal(Q(1, 0, 0), info.Forward);
        Assert.Equal(Q(0, -1, 0), info.Right);
        int bare = rig.Spawn(default, default, default, 0);
        Assert.Equal(3, models.TagInfo(bare, 1, out _));
        Assert.Equal(2, models.TagInfo(20000, 1, out _));
        Assert.Equal(4, models.TagInfo(body, 3, out _));

        // Tag 0 is the entity itself.
        Assert.Equal(0, models.TagInfo(body, 0, out info));
        Assert.Equal(Q(100, 200, 300), info.Origin);
        Assert.Equal(Q(1, 0, 0), info.Forward);
        Assert.Null(info.Name);

        // Frame 0: the tag is at (0, 0, 20) in the model, facing +Y.
        Assert.Equal(0, models.TagInfo(body, 1, out info));
        Assert.Equal(Q(100, 200, 320), info.Origin);
        Near(Q(0, 1, 0), info.Forward);
        Near(Q(1, 0, 0), info.Right);        // v_right is minus the matrix's left column (-1, 0, 0)
        Near(Q(0, 0, 1), info.Up);
        Assert.Equal("tag_weapon", info.Name);
        Assert.Equal(0, info.Parent);
        Assert.Equal(Q(0, 0, 20), info.LocalOffset);

        // Frame 2: 20 further along the model's X. Then a half-way blend with frame 0.
        rig.Vm.FieldFloat(body, rig.F.Frame) = 2;
        models.TagInfo(body, 1, out info);
        Near(Q(120, 200, 320), info.Origin);
        rig.Vm.FieldFloat(body, rig.F.LerpFrac) = 0.5f;   // .frame2 is 0
        models.TagInfo(body, 1, out info);
        Near(Q(110, 200, 320), info.Origin);
        rig.Vm.FieldFloat(body, rig.F.LerpFrac) = 0;
        // A frame the model does not have is frame 0.
        rig.Vm.FieldFloat(body, rig.F.Frame) = 77;
        models.TagInfo(body, 1, out info);
        Near(Q(100, 200, 320), info.Origin);
        rig.Vm.FieldFloat(body, rig.F.Frame) = 0;

        // The entity turned 90 degrees: the tag's +Y is the world's -X; and scaled by 2.
        rig.Vm.FieldVector(body, rig.F.Angles) = Q(0, 90, 0);
        rig.Vm.FieldFloat(body, rig.F.Scale) = 2;
        models.TagInfo(body, 2, out info);                 // tag_head: (0, 5, 20) in the model
        Near(Q(90, 200, 340), info.Origin);
        Near(Q(-2, 0, 0), info.Forward);
        // An alias model's pitch is negated: 30 degrees of .angles_x tips forward UP.
        rig.Vm.FieldVector(body, rig.F.Angles) = Q(30, 0, 0);
        rig.Vm.FieldFloat(body, rig.F.Scale) = 0;
        models.TagInfo(body, 0, out info);
        Assert.InRange(info.Forward.Z, 0.499f, 0.501f);
        rig.Vm.FieldVector(body, rig.F.Angles) = default;

        // Attachment: the hand rides the body's tag_weapon, so the hand's own tag is placed through both.
        int hand = rig.Spawn(Q(1, 2, 3), default, default, 0, modelIndex: 4);
        rig.Vm.FieldInt(hand, rig.F.TagEntity) = body;
        rig.Vm.FieldFloat(hand, rig.F.TagIndex) = 1;
        Assert.Equal(0, models.TagInfo(hand, 1, out info));
        // hand tag (0,0,20) facing +Y -> hand entity at (1,2,3): (1,2,23)
        // -> body's tag_weapon at (0,0,20) turned 90 about Z: (x,y) -> (-y,x): (-2,1,43)
        // -> body at (100,200,300): (98,201,343). Forward: +Y turned once more -> -X.
        Near(Q(98, 201, 343), info.Origin);
        Near(Q(-1, 0, 0), info.Forward);
        // A loop in the chain is found, not followed forever.
        rig.Vm.FieldInt(body, rig.F.TagEntity) = hand;
        rig.Vm.FieldFloat(body, rig.F.TagIndex) = 1;
        Assert.Equal(5, models.TagInfo(hand, 1, out info));
        Assert.Equal(default, info.Origin);
        rig.Vm.FieldInt(body, rig.F.TagEntity) = 0;

        // RF_VIEWMODEL on the root of the chain: everything is relative to the view.
        rig.Vm.FieldFloat(body, rig.F.RenderFlags) = 1;
        rig.Presentation.Scene.SetProperty(11, Q(1000, 0, 0), default);   // VF_ORIGIN
        rig.Presentation.Scene.SetProperty(17, Q(90, 0, 0), default);     // VF_ANGLES_Y
        models.TagInfo(body, 0, out info);
        Near(Q(800, 100, 300), info.Origin);   // (100,200,300) turned 90 about Z is (-200,100,300), plus the view
        Near(Q(0, 1, 0), info.Forward);

        // No bones in an MD3: no skeleton can be made of it.
        Assert.Equal(0, models.SkelCreate("models/test.md3"));
        Assert.Equal(0, models.SkelNumBones(1));
        Assert.False(models.SkelGetBone(1, 1, true, out _));
        models.SkelDelete(1);
        models.SkelDelete(-3);
    }

    internal static void Near(QcVector expected, QcVector actual, float tolerance = 0.001f)
    {
        Assert.True(MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance && MathF.Abs(expected.Z - actual.Z) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void The_Model_Cache_Is_Bounded_And_Remembers_Failures()
    {
        using Rig rig = SyntheticRig(("models/test.md3", BuildMd3(1, "tag_a")));
        FormatLegacyModels models = rig.Presentation.ModelData;
        for (int i = 0; i < 3 * FormatLegacyModels.MaxCachedModels; i++)
            Assert.Null(models.Load($"models/none{i}.md3"));
        Assert.InRange(models.CachedModels, 1, FormatLegacyModels.MaxCachedModels);
        Assert.Equal(0, models.ModelsParsed);
        Assert.NotNull(models.Load("models/test.md3"));
        Assert.NotNull(models.Load("models/test.md3"));
        Assert.Equal(1, models.ModelsParsed);
        Assert.Null(models.Load(""));
    }

    // ---- pictures ----------------------------------------------------------------------------------

    private static byte[] Png(int width, int height)
    {
        byte[] d = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(d, 0);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(20), height);
        return d;
    }

    private static byte[] Tga(int width, int height, byte imageType = 2, byte bits = 32)
    {
        byte[] d = new byte[18 + 4];
        d[2] = imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), (ushort)height);
        d[16] = bits;
        return d;
    }

    private static byte[] Jpeg(int width, int height)
    {
        // SOI, an APP0 segment of 16 bytes, a quantisation table stub, then SOF0.
        List<byte> d = new() { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 };
        d.AddRange(new byte[14]);
        d.AddRange(new byte[] { 0xFF, 0xDB, 0, 4, 1, 2 });
        d.AddRange(new byte[] { 0xFF, 0xC0, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 0, 0, 0 });
        return d.ToArray();
    }

    private static byte[] Dds(int width, int height)
    {
        byte[] d = new byte[128];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(d, 0);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), 124);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(16), width);
        return d;
    }

    [Fact]
    public void Image_Headers_Give_Sizes_Without_Decoding()
    {
        Assert.True(LegacyPictureCatalog.TryReadImageSize(Png(640, 480), out int w, out int h));
        Assert.Equal((640, 480), (w, h));
        Assert.True(LegacyPictureCatalog.TryReadImageSize(Tga(64, 32), out w, out h));
        Assert.Equal((64, 32), (w, h));
        Assert.True(LegacyPictureCatalog.TryReadImageSize(Tga(8, 8, imageType: 10, bits: 24), out w, out h));   // run-length true colour
        Assert.True(LegacyPictureCatalog.TryReadImageSize(Jpeg(300, 200), out w, out h));
        Assert.Equal((300, 200), (w, h));
        Assert.True(LegacyPictureCatalog.TryReadImageSize(Dds(256, 128), out w, out h));
        Assert.Equal((256, 128), (w, h));

        Assert.False(LegacyPictureCatalog.TryReadImageSize(Array.Empty<byte>(), out _, out _));
        Assert.False(LegacyPictureCatalog.TryReadImageSize(Png(0, 10), out _, out _));
        Assert.False(LegacyPictureCatalog.TryReadImageSize(Png(100000, 10), out _, out _));
        Assert.False(LegacyPictureCatalog.TryReadImageSize(Tga(64, 32, imageType: 7), out _, out _));
        Assert.False(LegacyPictureCatalog.TryReadImageSize(Tga(64, 32, bits: 13), out _, out _));
        Assert.False(LegacyPictureCatalog.TryReadImageSize(Jpeg(300, 200).AsSpan(0, 30), out _, out _));       // cut before the frame header
        Assert.False(LegacyPictureCatalog.TryReadImageSize(Encoding.ASCII.GetBytes("just some text that is long enough"), out _, out _));
        // Garbage of every length: an answer, never an exception.
        Random random = new(7);
        for (int i = 0; i < 2000; i++)
        {
            byte[] junk = new byte[random.Next(0, 200)];
            random.NextBytes(junk);
            if (i % 3 == 0 && junk.Length > 2) { junk[0] = 0xFF; junk[1] = 0xD8; }
            LegacyPictureCatalog.TryReadImageSize(junk, out _, out _);
        }
    }

    [Fact]
    public void The_Picture_Cache_Answers_As_DarkPlaces_Does_Including_For_What_Is_Missing()
    {
        byte[] lmp = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(lmp, 24);
        BinaryPrimitives.WriteInt32LittleEndian(lmp.AsSpan(4), 24);
        using Rig rig = SyntheticRig(
            ("gfx/hud/ammo.tga", Tga(64, 32)),
            ("gfx/hud/ammo.png", Png(999, 999)),          // .tga is searched first
            ("gfx/logo.jpg", Jpeg(300, 200)),
            ("gfx/num_0.png", Png(128, 128)),
            ("gfx/num_0.lmp", lmp),                       // the stock picture's size wins
            ("gfx/bad.tga", new byte[] { 1, 2, 3 }));
        ILegacyDraw draw = rig.Presentation.Draw;

        Assert.True(draw.PictureExists("gfx/hud/ammo"));
        Assert.Equal(Q(64, 32, 0), draw.ImageSize("gfx/hud/ammo"));
        Assert.Equal(Q(64, 32, 0), draw.ImageSize("gfx/hud/ammo.tga"));   // an extension on the name is ignored
        Assert.Equal(Q(300, 200, 0), draw.ImageSize("gfx/logo"));
        Assert.Equal(Q(24, 24, 0), draw.ImageSize("gfx/num_0"));

        // Missing, asked for its size first: the 16 x 16 checkerboard, which then counts as loaded.
        Assert.Equal(Q(16, 16, 0), draw.ImageSize("gfx/absent"));
        Assert.True(draw.PictureExists("gfx/absent"));
        // Missing, precached first: fails, and then has no size.
        Assert.False(draw.PictureExists("gfx/gone"));
        Assert.Equal(Q(0, 0, 0), draw.ImageSize("gfx/gone"));
        Assert.False(draw.PictureExists("gfx/gone"));
        // A file with no readable header is a missing picture.
        Assert.False(draw.PictureExists("gfx/bad"));
        Assert.False(draw.PictureExists(""));
        Assert.False(draw.PictureExists("../gfx/hud/ammo"));

        // freepic forgets; ReadPicture's low-quality stand-in defines.
        draw.FreePicture("gfx/gone");
        Assert.Equal(Q(16, 16, 0), draw.ImageSize("gfx/gone"));
        draw.DefinePicture("gfx/sent", Jpeg(48, 40));
        Assert.True(draw.PictureExists("gfx/sent"));
        Assert.Equal(Q(48, 40, 0), draw.ImageSize("gfx/sent"));
        draw.DefinePicture("gfx/sent2", new byte[] { 9, 9, 9 });
        Assert.False(draw.PictureExists("gfx/sent2"));

        // Bounded: names past the limit are answered and not remembered.
        for (int i = 0; i < LegacyPictureCatalog.MaxPictures + 50; i++) draw.ImageSize("gfx/x" + i);
        Assert.Equal(LegacyPictureCatalog.MaxPictures, rig.Presentation.Pictures.Count);

        // Counted like every other presentation call.
        Assert.True(rig.Presentation.Calls["ImageSize"] > LegacyPictureCatalog.MaxPictures);
        Assert.True(rig.Presentation.Calls["PictureExists"] >= 9);
    }
}
