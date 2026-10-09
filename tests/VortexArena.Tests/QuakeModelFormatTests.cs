using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using VortexArena.Formats;
using VortexArena.Formats.Images;
using VortexArena.Formats.Mdl;
using VortexArena.Formats.Sprites;
using Xunit;
using Xunit.Abstractions;
using Dp = VortexArena.Tests.DarkPlacesQuakeModelReference;

namespace VortexArena.Tests;

/// <summary>
/// Quake 1 models (<c>.mdl</c> "IDPO" 6) and sprites (<c>.spr</c> "IDSP" 1, 32 and Half-Life's 2) as
/// <see cref="MdlReader"/> and <see cref="SpriteReader"/> read them, against
/// <see cref="DarkPlacesQuakeModelReference"/> - a separate transcription of DarkPlaces' loaders.
///
/// <para>The synthetic cases build files in memory and run everywhere. <see cref="RealPackages_EveryModelAndSprite_MatchesDarkPlaces"/>
/// reads every model and sprite of the Quake packages a server sent; it needs the environment variable
/// <c>VA_QUAKE1_PACKS</c> to name the directory holding them (the legacy download cache,
/// <c>&lt;user data&gt;/legacy/dlcache</c>) and does nothing without it.</para>
/// </summary>
public class QuakeModelFormatTests
{
    private readonly ITestOutputHelper _out;
    public QuakeModelFormatTests(ITestOutputHelper output) => _out = output;

    // ---- a synthetic .mdl ------------------------------------------------------------------------

    private sealed class MdlFile
    {
        public Vector3 Scale = new(0.5f, 0.25f, 2f), Origin = new(-3f, 4f, -8f);
        public int SkinWidth = 4, SkinHeight = 2, Flags, SyncType;
        /// <summary>Each header skin: its pictures (one for a single skin) and, for a group, the intervals.</summary>
        public List<(byte[][] Pictures, float[]? Intervals)> Skins = new();
        public List<(int OnSeam, int S, int T)> StVerts = new();
        public List<(int FacesFront, int A, int B, int C)> Triangles = new();
        /// <summary>Each header frame: its poses (name, four bytes per vertex) and, for a group, the intervals.</summary>
        public List<((string Name, byte[] Verts)[] Poses, float[]? Intervals)> Frames = new();
        public byte[] Trailing = Array.Empty<byte>();

        public byte[] Build()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("IDPO"));
            w.Write(6);
            w.Write(Scale.X); w.Write(Scale.Y); w.Write(Scale.Z);
            w.Write(Origin.X); w.Write(Origin.Y); w.Write(Origin.Z);
            w.Write(0f);                                // boundingradius
            w.Write(0f); w.Write(0f); w.Write(0f);      // eyeposition
            w.Write(Skins.Count);
            w.Write(SkinWidth);
            w.Write(SkinHeight);
            w.Write(StVerts.Count);
            w.Write(Triangles.Count);
            w.Write(Frames.Count);
            w.Write(SyncType);
            w.Write(Flags);
            w.Write(0f);                                // size
            foreach ((byte[][] pictures, float[]? intervals) in Skins)
            {
                w.Write(intervals is null ? 0 : 1);
                if (intervals is not null)
                {
                    w.Write(pictures.Length);
                    foreach (float i in intervals) w.Write(i);
                }
                foreach (byte[] picture in pictures) w.Write(picture);
            }
            foreach ((int onSeam, int s, int t) in StVerts) { w.Write(onSeam); w.Write(s); w.Write(t); }
            foreach ((int ff, int a, int b, int c) in Triangles) { w.Write(ff); w.Write(a); w.Write(b); w.Write(c); }
            foreach (((string Name, byte[] Verts)[] poses, float[]? intervals) in Frames)
            {
                w.Write(intervals is null ? 0 : 1);
                if (intervals is not null)
                {
                    w.Write(poses.Length);
                    w.Write(0); w.Write(0);             // group bboxmin, bboxmax
                    foreach (float i in intervals) w.Write(i);
                }
                foreach ((string name, byte[] verts) in poses)
                {
                    w.Write(0); w.Write(0);             // bboxmin, bboxmax
                    byte[] n = new byte[16];
                    Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, 16), n, 0);
                    w.Write(n);
                    w.Write(verts);
                }
            }
            w.Write(Trailing);
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Five file vertices and three triangles: vertex 1 is on the seam and used by a front and a back
    /// triangle, vertex 3 is on the seam and used by a back triangle only, vertex 4 is used by nothing.
    /// Two skins (a single and a group of three), three frames (single, a group of two, single).
    /// </summary>
    private static MdlFile Sample()
    {
        var f = new MdlFile { Flags = MdlFlags.Rocket | MdlFlags.Rotate | 0x200 };
        f.Skins.Add((new[] { new byte[] { 0, 15, 255, 1, 251, 16, 96, 208 } }, null));
        f.Skins.Add((new[]
        {
            new byte[] { 1, 1, 1, 1, 1, 1, 1, 1 }, new byte[] { 2, 2, 2, 2, 2, 2, 2, 2 }, new byte[] { 3, 3, 3, 3, 3, 3, 3, 3 },
        }, new[] { 0.25f, 9f, 9f }));
        f.StVerts.Add((0, 0, 0));
        f.StVerts.Add((1, 2, 0));
        f.StVerts.Add((0, 0, 1));
        f.StVerts.Add((32, 1, 2));          // any non-zero value means "on the seam"
        f.StVerts.Add((0, 3, 1));
        f.Triangles.Add((1, 0, 1, 2));      // front
        f.Triangles.Add((0, 1, 2, 3));      // back: 1 and 3 take their seam copies
        f.Triangles.Add((0, 0, 2, 0));      // back, no seam vertex
        byte[] Pose(byte bias) => new byte[]
        {
            10, 0, 0, 0,  (byte)(0 + bias), 20, 0, 5,  0, 0, 3, 161,  4, 4, 4, 200,  250, 250, 250, 0,
        };
        f.Frames.Add((new[] { ("stand1", Pose(0)) }, null));
        f.Frames.Add((new[] { ("flameA", Pose(1)), ("flameB", Pose(2)) }, new[] { 0.05f, 7f }));
        f.Frames.Add((new[] { ("sixteencharsname", Pose(3)) }, null));
        return f;
    }

    [Fact]
    public void Mdl_SkinGroupsAndFrameGroups_BecomeScenes()
    {
        MdlData mdl = MdlReader.Read(Sample().Build());

        // Skins: numskins 2, totalskins 4. The group plays at 1 / its FIRST interval.
        Assert.Equal(4, mdl.Skins.Length);
        Assert.Equal(new[]
        {
            new MdlScene("skin 0", 0, 1, 10f, true),
            new MdlScene("skin 1", 1, 3, 4f, true),
        }, mdl.SkinScenes);
        Assert.Equal(new byte[] { 0, 15, 255, 1, 251, 16, 96, 208 }, mdl.Skins[0]);
        Assert.Equal(new byte[] { 3, 3, 3, 3, 3, 3, 3, 3 }, mdl.Skins[3]);
        Assert.Equal(4 * 2 * 4, mdl.SkinRgba.Length);

        // Frames: numframes 3, poses 4. A scene is named after its first pose; a 16-character name has no terminator.
        Assert.Equal(4, mdl.Frames.Length);
        Assert.Equal(new[]
        {
            new MdlScene("stand1", 0, 1, 10f, true),
            new MdlScene("flameA", 1, 2, 20f, true),
            new MdlScene("sixteencharsname", 3, 1, 10f, true),
        }, mdl.Scenes);
        Assert.Equal(new[] { "stand1", "flameA", "flameB", "sixteencharsname" }, mdl.Frames.Select(f => f.Name));

        // translate + byte * scale, per axis.
        Assert.Equal(new Vector3(-3f + 10 * 0.5f, 4f, -8f), mdl.Frames[0].Vertices[0].Position);
        Assert.Equal(new Vector3(-3f + 2 * 0.5f, 4f + 20 * 0.25f, -8f), mdl.Frames[2].Vertices[1].Position);
        // A normal index past the table's 162 entries does not read out of bounds.
        Assert.True(MathF.Abs(mdl.Frames[0].Vertices[3].Normal.Length() - 1f) < 1e-3f);
    }

    [Fact]
    public void Mdl_InvalidGroupInterval_IsATenthOfASecond()
    {
        MdlFile f = Sample();
        f.Skins[1] = (f.Skins[1].Pictures, new[] { 0.005f, 1f, 1f });
        f.Frames[1] = (f.Frames[1].Poses, new[] { float.NaN, 1f });
        MdlData mdl = MdlReader.Read(f.Build());
        Assert.Equal(10f, mdl.SkinScenes[1].FrameRate);
        Assert.Equal(10f, mdl.Scenes[1].FrameRate);
    }

    [Fact]
    public void Mdl_SeamVerticesOfBackFacingTriangles_TakeTheFarHalfOfTheSkin()
    {
        MdlData mdl = MdlReader.Read(Sample().Build());

        // Corners: vertex and resolved UV. Skin is 4 x 2, so s 2 -> 0.5, +0.5 on the seam copy.
        Assert.Equal(new MdlCorner(1, new Vector2(0.5f, 0f)), mdl.Corners[1]);      // front triangle: unshifted
        Assert.Equal(new MdlCorner(1, new Vector2(1.0f, 0f)), mdl.Corners[3]);      // back triangle: shifted
        Assert.Equal(new MdlCorner(2, new Vector2(0f, 0.5f)), mdl.Corners[4]);      // not on the seam
        Assert.Equal(new MdlCorner(3, new Vector2(0.75f, 1f)), mdl.Corners[5]);     // 1/4 + 1/2

        // DarkPlaces' compacted set: file vertices 0, 1, 2 (used unshifted), then the seam copies of 1 and 3.
        // File vertex 3 unshifted and file vertex 4 are used by no triangle and are dropped.
        Assert.Equal(new[] { 0, 1, 2, 1, 3 }, mdl.MeshVertices);
        Assert.Equal(new[]
        {
            new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0.5f), new Vector2(1.0f, 0f), new Vector2(0.75f, 1f),
        }, mdl.MeshTexCoords);
        Assert.Equal(new[] { 0, 1, 2, 3, 2, 4, 0, 2, 0 }, mdl.MeshElements);
    }

    [Fact]
    public void Mdl_Bounds_CountOnlyVerticesATriangleUses()
    {
        MdlData mdl = MdlReader.Read(Sample().Build());
        // File vertex 4 (250, 250, 250) is unused: it would put the box at 122, 66.5, 492.
        Assert.Equal(new Vector3(-3f, 4f, -8f), mdl.Mins);
        Assert.Equal(new Vector3(2f, 9f, 0f), mdl.Maxs);
        Assert.True(mdl.IsAnimated);
        // Farthest used vertex horizontally and in space: file vertex 1 in every pose is (-3..-1.5, 9, -8).
        Assert.Equal(MathF.Sqrt(9f + 81f), mdl.YawRadius, 5);
        Assert.Equal(MathF.Sqrt(9f + 81f + 64f), mdl.Radius, 5);

        MdlFile still = Sample();
        still.Frames.RemoveRange(1, 2);
        Assert.False(MdlReader.Read(still.Build()).IsAnimated);
    }

    [Fact]
    public void Mdl_Flags_BecomeEntityEffects()
    {
        MdlData mdl = MdlReader.Read(Sample().Build());
        Assert.Equal(MdlFlags.Rocket | MdlFlags.Rotate | 0x200, mdl.Flags);
        // The low byte moves to the top byte; bits 8..23 stay (0x200 is EF_FULLBRIGHT).
        Assert.Equal(MdlFlags.EfRocket | MdlFlags.EfRotate | 0x200u, mdl.Effects);
        Assert.Equal(0x80000000u, MdlFlags.ToEffects(MdlFlags.Tracer3));
        Assert.Equal(0x4000u, MdlFlags.ToEffects(MdlFlags.Holey));          // EF_SELECTABLE: nothing
        Assert.Equal(0u, MdlFlags.ToEffects(0x01000000));                    // bits above 23 are dropped
    }

    [Theory]
    [InlineData(MdlFlags.Rocket, "TR_ROCKET")]
    [InlineData(MdlFlags.Grenade, "TR_GRENADE")]
    [InlineData(MdlFlags.Gib, "TR_BLOOD")]
    [InlineData(MdlFlags.Tracer, "TR_WIZSPIKE")]
    [InlineData(MdlFlags.ZomGib, "TR_SLIGHTBLOOD")]
    [InlineData(MdlFlags.Tracer2, "TR_KNIGHTSPIKE")]
    [InlineData(MdlFlags.Tracer3, "TR_VORESPIKE")]
    [InlineData(MdlFlags.Rotate, null)]
    [InlineData(MdlFlags.Gib | MdlFlags.Rocket, "TR_BLOOD")]                // gib wins
    [InlineData(MdlFlags.ZomGib | MdlFlags.Tracer, "TR_SLIGHTBLOOD")]
    [InlineData(MdlFlags.Tracer | MdlFlags.Tracer2 | MdlFlags.Rocket, "TR_WIZSPIKE")]
    [InlineData(MdlFlags.Rocket | MdlFlags.Grenade | MdlFlags.Tracer3, "TR_ROCKET")]
    public void ModelFlags_PickATrail_InDarkPlacesOrder(int flags, string? trail)
    {
        Assert.Equal(trail, MdlFlags.TrailEffect(MdlFlags.ToEffects(flags)));
    }

    [Fact]
    public void ModelEffects_AreAddedToANetworkEntity_UnlessItsOwnUseTheTopBits()
    {
        uint model = MdlFlags.EfRocket | 0x200;
        Assert.Equal(model | 8u, MdlFlags.CombineNetworkEffects(8, model));
        Assert.Equal(MdlFlags.EfNoModelFlags | 8u, MdlFlags.CombineNetworkEffects(MdlFlags.EfNoModelFlags | 8u, model));
        Assert.Equal(MdlFlags.EfGrenade, MdlFlags.CombineNetworkEffects(MdlFlags.EfGrenade, model));
        Assert.Equal("TR_NEHAHRASMOKE", MdlFlags.TrailEffect(MdlFlags.EfGrenade, alphaIsMinusOne: true));

        // EF_ROTATE: ANGLEMOD(100 * time), 16-bit steps of a turn.
        Assert.Equal(0f, MdlFlags.RotateYaw(0));
        Assert.Equal(90f, MdlFlags.RotateYaw(0.9), 2);
        Assert.Equal(90f, MdlFlags.RotateYaw(4.5), 2);                       // 450 degrees
        Assert.InRange(MdlFlags.RotateYaw(123.456), 0f, 360f);
    }

    [Fact]
    public void Mdl_LenientWhereDarkPlacesIs()
    {
        MdlFile f = Sample();
        f.Trailing = new byte[37];                                           // bytes after the last frame
        f.Triangles[2] = (0, 0, 2, 99);                                      // an index outside the table
        MdlData mdl = MdlReader.Read(f.Build());
        Assert.Equal(0, mdl.Corners[8].Vertex);                              // Mod_ValidateElements: the first vertex
        Dp.AliasModel dp = Dp.LoadIdp0(f.Build());
        AssertSameAlias("lenient", f.Build(), mdl, dp, new List<string>());
    }

    [Fact]
    public void Mdl_RefusedWhereDarkPlacesRefuses()
    {
        MdlFile f = Sample();
        f.SyncType = 2;
        Assert.Throws<AssetParseException>(() => MdlReader.Read(f.Build()));

        byte[] bytes = Sample().Build();
        BitConverter.TryWriteBytes(bytes.AsSpan(60), 65536);                 // numverts: BOUNDI(...,0,65536) is exclusive
        Assert.Throws<AssetParseException>(() => MdlReader.Read(bytes));

        byte[] cut = Sample().Build();
        Assert.Throws<AssetParseException>(() => MdlReader.Read(cut.AsSpan(0, cut.Length - 1).ToArray()));
    }

    [Fact]
    public void Mdl_Sample_MatchesTheReference_InEveryField()
    {
        byte[] bytes = Sample().Build();
        var problems = new List<string>();
        AssertSameAlias("sample", bytes, MdlReader.Read(bytes), Dp.LoadIdp0(bytes), problems);
        AssertSameSkins("sample", bytes, MdlReader.Read(bytes), Dp.LoadIdp0(bytes), problems);
        Assert.Empty(problems);
    }

    // ---- a synthetic .spr -------------------------------------------------------------------------

    private static byte[] Sprite(int version, int type, int syncType, params (float[]? Intervals, (int Ox, int Oy, int W, int H, byte[] Pixels)[] Frames)[] slots)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("IDSP"));
        w.Write(version);
        w.Write(type);
        w.Write(0f);            // boundingradius
        w.Write(0); w.Write(0); // width, height
        w.Write(slots.Length);
        w.Write(0f);            // beamlength
        w.Write(syncType);
        foreach ((float[]? intervals, var frames) in slots)
        {
            w.Write(intervals is null ? 0 : 1);
            if (intervals is not null)
            {
                w.Write(frames.Length);
                foreach (float i in intervals) w.Write(i);
            }
            foreach ((int ox, int oy, int fw, int fh, byte[] pixels) in frames)
            {
                w.Write(ox); w.Write(oy); w.Write(fw); w.Write(fh);
                w.Write(pixels);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Sprite_Groups_BecomeScenes_AndFrameNumbersAreSlots()
    {
        byte[] bytes = Sprite(1, 0, 1,
            (null, new[] { (-1, 1, 2, 2, new byte[] { 1, 2, 3, 255 }) }),
            (new[] { 0.05f, 3f, 3f }, new[]
            {
                (-4, 3, 1, 1, new byte[] { 4 }), (0, 0, 1, 1, new byte[] { 5 }), (2, 9, 1, 1, new byte[] { 6 }),
            }),
            (null, new[] { (0, 0, 1, 1, new byte[] { 7 }) }));
        SpriteData spr = SpriteReader.Read(bytes);

        Assert.Equal(5, spr.FrameCount);
        Assert.Equal(1, spr.SyncType);
        Assert.Equal(SpriteType.VpParallelUpright, spr.SpriteType);
        Assert.Equal(new[]
        {
            new SpriteScene("frame 0", 0, 1, 10f),
            new SpriteScene("frame 1", 1, 3, 20f),      // the first interval, for the whole group
            new SpriteScene("frame 2", 4, 1, 10f),
        }, spr.Scenes);
        // Radius: the farthest corner of any frame. Frame 3 spans x 2..3, y 8..9: sqrt(9 + 81).
        Assert.Equal(MathF.Sqrt(90f), spr.Radius, 5);

        var problems = new List<string>();
        AssertSameSprite("groups", spr, Dp.LoadIdsp(bytes, new Dp.Palettes(QuakePalette.DefaultRgb.ToArray(), null)), QuakePalette.Default, problems);
        Assert.Empty(problems);
    }

    [Fact]
    public void Sprite_QuakePixels_UseTheTransparentPalette()
    {
        byte[] bytes = Sprite(1, 2, 0, (null, new[] { (-1, 1, 2, 2, new byte[] { 15, 251, 255, 0 }) }));
        SpriteFrame f = SpriteReader.Read(bytes).Frames[0];
        Assert.Null(f.Rgba);
        Assert.True(f.HasAlpha);
        Assert.Equal(new byte[]
        {
            235, 235, 235, 255,   255, 0, 0, 255,   0, 0, 0, 0,   0, 0, 0, 255,
        }, f.ToRgba(QuakePalette.Quake));       // full-brights are NOT split out of a sprite

        byte[] opaque = Sprite(1, 2, 0, (null, new[] { (0, 0, 1, 1, new byte[] { 15 }) }));
        Assert.False(SpriteReader.Read(opaque).Frames[0].HasAlpha);
    }

    [Fact]
    public void Sprite_RefusedWhereDarkPlacesRefuses()
    {
        // A group whose first interval is below 0.01: "Mod_Sprite_SharedSetup: invalid interval".
        byte[] bad = Sprite(1, 2, 0, (new[] { 0.001f }, new[] { (0, 0, 1, 1, new byte[] { 1 }) }));
        Assert.Throws<AssetParseException>(() => SpriteReader.Read(bad));
        Assert.ThrowsAny<Exception>(() => Dp.LoadIdsp(bad, new Dp.Palettes(QuakePalette.DefaultRgb.ToArray(), null)));

        // A frame larger than 8192 on a side.
        byte[] huge = Sprite(1, 2, 0, (null, new[] { (0, 0, 8193, 0, Array.Empty<byte>()) }));
        Assert.Throws<AssetParseException>(() => SpriteReader.Read(huge));

        // A zero-sized frame is legal (Nehahra's null.spr).
        byte[] empty = Sprite(1, 2, 0, (null, new[] { (0, 0, 0, 0, Array.Empty<byte>()) }));
        Assert.Null(SpriteReader.Read(empty).Frames[0].ToRgba(QuakePalette.Default));
    }

    [Fact]
    public void Sprite32_MatchesTheReference()
    {
        byte[] bytes = Sprite(32, 3, 0, (null, new[] { (-1, 1, 2, 1, new byte[] { 200, 100, 50, 255, 1, 2, 3, 128 }) }));
        SpriteData spr = SpriteReader.Read(bytes);
        Assert.Equal(new byte[] { 200, 100, 50, 255, 1, 2, 3, 128 }, spr.Frames[0].Rgba);    // red first
        Assert.True(spr.Frames[0].HasAlpha);
        var problems = new List<string>();
        AssertSameSprite("spr32", spr, Dp.LoadIdsp(bytes, new Dp.Palettes(QuakePalette.DefaultRgb.ToArray(), null)), QuakePalette.Default, problems);
        Assert.Empty(problems);
    }

    // ---- how each sprite type is turned to the view (r_sprites.c) ------------------------------------

    private static readonly Vector3 ViewOrigin = new(100, 50, 20);

    /// <summary>A view looking along +X and 30 degrees down, in Quake axes: forward, left, up.</summary>
    private static (Vector3 Forward, Vector3 Left, Vector3 Up) TiltedView()
    {
        float c = MathF.Cos(MathF.PI / 6), s = MathF.Sin(MathF.PI / 6);
        return (new Vector3(c, 0, -s), new Vector3(0, 1, 0), new Vector3(s, 0, c));
    }

    [Fact]
    public void SpriteOrientation_VpParallel_IsTheViewPlane_TimesScale()
    {
        (Vector3 fwd, Vector3 left, Vector3 up) = TiltedView();
        Assert.True(SpriteOrientation.Axes(SpriteType.VpParallel, new Vector3(300, 0, 0), 2f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out Vector3 l, out Vector3 u));
        Assert.Equal(left * 2f, l);
        Assert.Equal(up * 2f, u);
    }

    [Fact]
    public void SpriteOrientation_VpParallelUpright_StaysVertical_AndFacesTheViewDirection()
    {
        (Vector3 fwd, Vector3 left, Vector3 up) = TiltedView();
        SpriteOrientation.Axes(SpriteType.VpParallelUpright, new Vector3(300, 0, 0), 2f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out Vector3 l, out Vector3 u);
        Assert.Equal(new Vector3(0, 0, 2f), u);                 // world up, whatever the pitch
        Assert.Equal(0f, l.Z);
        Assert.Equal(2f, l.Length(), 4);                        // not shortened by the view's pitch
        Assert.Equal(0f, Vector3.Dot(l, new Vector3(fwd.X, fwd.Y, 0)), 4);
        Assert.True(l.Y > 0);                                   // the view's left
    }

    [Fact]
    public void SpriteOrientation_FacingUpright_FacesTheEye_NotTheViewPlane()
    {
        (Vector3 fwd, Vector3 left, Vector3 up) = TiltedView();
        // A sprite well off to the view's left: the two upright types differ.
        var origin = new Vector3(200, 250, 20);
        SpriteOrientation.Axes(SpriteType.FacingUpright, origin, 1f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out Vector3 l, out Vector3 u);
        Assert.Equal(Vector3.UnitZ, u);
        Assert.Equal(1f, l.Length(), 4);
        Vector3 toSprite = SpriteOrientation.Nudge(origin, fwd) - ViewOrigin;
        Assert.Equal(0f, Vector3.Dot(l, new Vector3(toSprite.X, toSprite.Y, 0)) / toSprite.Length(), 4);
        SpriteOrientation.Axes(SpriteType.VpParallelUpright, origin, 1f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out Vector3 l2, out _);
        Assert.True((l - l2).Length() > 0.5f);
    }

    [Fact]
    public void SpriteOrientation_Oriented_IgnoresTheView()
    {
        (Vector3 fwd, Vector3 left, Vector3 up) = TiltedView();
        var el = new Vector3(0, 0, 3);
        var eu = new Vector3(3, 0, 0);
        SpriteOrientation.Axes(SpriteType.Oriented, Vector3.Zero, 3f, el, eu, ViewOrigin, fwd, left, up, out Vector3 l, out Vector3 u);
        Assert.Equal(el, l);
        Assert.Equal(eu, u);
    }

    [Fact]
    public void SpriteOrientation_VpParallelOriented_IsTheEntityAxesInViewSpace()
    {
        (Vector3 fwd, Vector3 left, Vector3 up) = TiltedView();
        // An unrotated entity (left = +Y, up = +Z) is exactly a view-plane sprite.
        SpriteOrientation.Axes(SpriteType.VpParallelOriented, Vector3.Zero, 1f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out Vector3 l, out Vector3 u);
        Assert.Equal(left, l);
        Assert.Equal(up, u);
        // One rolled 90 degrees about its forward axis (left = +Z, up = -Y) is that sprite rolled in the view plane.
        SpriteOrientation.Axes(SpriteType.VpParallelOriented, Vector3.Zero, 1f, Vector3.UnitZ, -Vector3.UnitY, ViewOrigin, fwd, left, up, out l, out u);
        Assert.Equal(up, l);
        Assert.Equal(-left, u);
    }

    [Fact]
    public void SpriteOrientation_UnknownTypeDrawsAsVpParallel_AndLabelsAreNotHandled()
    {
        (Vector3 fwd, Vector3 left, Vector3 up) = TiltedView();
        Assert.True(SpriteOrientation.Axes((SpriteType)99, Vector3.Zero, 1f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out Vector3 l, out _));
        Assert.Equal(left, l);
        Assert.False(SpriteOrientation.Axes(SpriteType.Label, Vector3.Zero, 1f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out _, out _));
        Assert.False(SpriteOrientation.Axes(SpriteType.LabelScale, Vector3.Zero, 1f, Vector3.UnitY, Vector3.UnitZ, ViewOrigin, fwd, left, up, out _, out _));
    }

    [Fact]
    public void SpriteCorners_PutThePicturesLeftEdgeAtLeftTimesRight()
    {
        // A frame whose origin is not centred: 4 wide, 2 high, origin (-1, 2): left -1, right 3, up 2, down 0.
        var frame = new SpriteFrame { OriginX = -1, OriginY = 2, Width = 4, Height = 2 };
        Span<Vector3> c = stackalloc Vector3[4];
        // View along +X: left = +Y, up = +Z.
        SpriteOrientation.Corners(frame, new Vector3(10, 0, 0), Vector3.UnitY, Vector3.UnitZ, c);
        Assert.Equal(new Vector3(10, 3, 0), c[0]);      // texcoord (0, 1): the picture's bottom-left, 3 to the LEFT
        Assert.Equal(new Vector3(10, 3, 2), c[1]);      // (0, 0) top-left
        Assert.Equal(new Vector3(10, -1, 2), c[2]);     // (1, 0) top-right, 1 to the right
        Assert.Equal(new Vector3(10, -1, 0), c[3]);     // (1, 1) bottom-right
    }

    // ---- every model and sprite of the real packages ------------------------------------------------

    private static readonly string[] Packages =
    {
        "zzz-quake1-assets_17.pk3", "zzz-quake1-champions_31.pk3", "zzz-quake1-items_25.pk3", "zzz-quake1-monsters_27.pk3", "q1-mc30_01.pk3",
    };

    [Fact]
    public void RealPackages_EveryModelAndSprite_MatchesDarkPlaces()
    {
        string? dir = Environment.GetEnvironmentVariable("VA_QUAKE1_PACKS");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;     // the packages are a server's, not the repository's: nothing to check without them

        var problems = new List<string>();
        int mdl = 0, mdlOther = 0, spr = 0, skins = 0, poses = 0, withSkinGroups = 0, withFrameGroups = 0, withFlags = 0, animatedSprites = 0;
        var formats = new SortedDictionary<string, int>(StringComparer.Ordinal);
        byte[] defaultRgb = QuakePalette.DefaultRgb.ToArray();
        byte[] quakeColormap = new byte[16385];
        quakeColormap[16384] = 32;
        var dpDefault = new Dp.Palettes(defaultRgb, null);

        foreach (string package in Packages)
        {
            string path = Path.Combine(dir, package);
            if (!File.Exists(path)) continue;
            using ZipArchive zip = ZipFile.OpenRead(path);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                bool isMdl = name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
                bool isSpr = name.EndsWith(".spr", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".spr32", StringComparison.OrdinalIgnoreCase);
                if (!isMdl && !isSpr) continue;
                byte[] bytes;
                using (Stream s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                string magic = bytes.Length >= 4 ? Encoding.Latin1.GetString(bytes, 0, 4) : "";
                formats[magic] = formats.GetValueOrDefault(magic) + 1;
                string label = package + ":" + name;

                if (magic == "IDPO")
                {
                    mdl++;
                    Dp.AliasModel? dp = null;
                    MdlData? ours = null;
                    string? dpError = null, ourError = null;
                    try { dp = Dp.LoadIdp0(bytes); } catch (Exception e) { dpError = e.GetType().Name + ": " + e.Message; }
                    try { ours = MdlReader.Read(bytes); } catch (Exception e) { ourError = e.GetType().Name + ": " + e.Message; }
                    if (dp is null || ours is null)
                    {
                        if ((dp is null) != (ours is null)) problems.Add($"{label}: reference {dpError ?? "loads"}, reader {ourError ?? "loads"}");
                        continue;
                    }
                    AssertSameAlias(label, bytes, ours, dp, problems);
                    AssertSameSkins(label, bytes, ours, dp, problems);
                    skins += dp.TotalSkins;
                    poses += dp.NumPoses;
                    if (dp.TotalSkins != dp.NumSkins) withSkinGroups++;
                    if (dp.NumPoses != dp.NumFrames) withFrameGroups++;
                    if (ours.Flags != 0) withFlags++;
                }
                else if (magic == "IDSP")
                {
                    spr++;
                    Dp.Sprite? dp = null;
                    SpriteData? ours = null;
                    string? dpError = null, ourError = null;
                    try { dp = Dp.LoadIdsp(bytes, dpDefault); } catch (Exception e) { dpError = e.GetType().Name + ": " + e.Message; }
                    try { ours = SpriteReader.Read(bytes); } catch (Exception e) { ourError = e.GetType().Name + ": " + e.Message; }
                    if (dp is null || ours is null)
                    {
                        if ((dp is null) != (ours is null)) problems.Add($"{label}: reference {dpError ?? "loads"}, reader {ourError ?? "loads"}");
                        continue;
                    }
                    AssertSameSprite(label, ours, dp, QuakePalette.Default, problems);
                    if (dp.Frames.Length > 1) animatedSprites++;
                }
                else if (isMdl) mdlOther++;
            }
        }

        _out.WriteLine($"IDPO models {mdl}, .mdl files of another format {mdlOther}, sprites {spr}");
        _out.WriteLine($"skin pictures {skins}, poses {poses}; models with skin groups {withSkinGroups}, with frame groups {withFrameGroups}, with flags {withFlags}; sprites with more than one frame {animatedSprites}");
        _out.WriteLine("by magic: " + string.Join(", ", formats.Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (string p in problems.Take(60)) _out.WriteLine("MISMATCH " + p);
        Assert.True(mdl + spr > 0, "VA_QUAKE1_PACKS names a directory without the Quake packages");
        Assert.True(problems.Count == 0, $"{problems.Count} mismatch(es); first: {problems.FirstOrDefault()}");
    }

    // ---- comparisons ---------------------------------------------------------------------------------

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b) || a == b;

    private static void AssertSameAlias(string label, byte[] bytes, MdlData ours, Dp.AliasModel dp, List<string> problems)
    {
        void Bad(string what) { if (problems.Count < 500) problems.Add($"{label}: {what}"); }

        if (ours.SkinScenes.Length != dp.NumSkins) Bad($"numskins {ours.SkinScenes.Length} vs {dp.NumSkins}");
        if (ours.Skins.Length != dp.TotalSkins) Bad($"totalskins {ours.Skins.Length} vs {dp.TotalSkins}");
        if (ours.Scenes.Length != dp.NumFrames) Bad($"numframes {ours.Scenes.Length} vs {dp.NumFrames}");
        if (ours.Frames.Length != dp.NumPoses) Bad($"num_poses {ours.Frames.Length} vs {dp.NumPoses}");
        if (ours.MeshVertices.Length != dp.NumVertices) Bad($"num_vertices {ours.MeshVertices.Length} vs {dp.NumVertices}");
        if (ours.MeshElements.Length != dp.Element3i.Length) Bad($"elements {ours.MeshElements.Length} vs {dp.Element3i.Length}");
        if (ours.Effects != dp.Effects) Bad($"effects {ours.Effects:x} vs {dp.Effects:x}");
        if (ours.SyncType != dp.SyncType) Bad($"synctype {ours.SyncType} vs {dp.SyncType}");
        if (ours.SkinWidth != dp.SkinWidth || ours.SkinHeight != dp.SkinHeight) Bad("skin size");
        if (problems.Count > 0 && problems[^1].StartsWith(label, StringComparison.Ordinal)) return;

        for (int i = 0; i < dp.NumFrames; i++)
        {
            MdlScene s = ours.Scenes[i];
            Dp.AnimScene d = dp.AnimScenes[i];
            if (s.Name != d.Name || s.First != d.FirstFrame || s.Count != d.FrameCount || !Same(s.FrameRate, d.FrameRate) || s.Loop != d.Loop)
            {
                Bad($"animscene {i}: {s} vs {d.Name} {d.FirstFrame} {d.FrameCount} {d.FrameRate}");
                break;
            }
        }
        for (int i = 0; i < dp.NumSkins; i++)
        {
            MdlScene s = ours.SkinScenes[i];
            Dp.AnimScene d = dp.SkinScenes[i];
            if (s.Name != d.Name || s.First != d.FirstFrame || s.Count != d.FrameCount || !Same(s.FrameRate, d.FrameRate) || s.Loop != d.Loop)
            {
                Bad($"skinscene {i}: {s} vs {d.Name} {d.FirstFrame} {d.FrameCount} {d.FrameRate}");
                break;
            }
        }
        if (!ours.MeshElements.AsSpan().SequenceEqual(dp.Element3i)) Bad("element3i differs");
        for (int i = 0; i < dp.NumVertices; i++)
            if (!Same(ours.MeshTexCoords[i].X, dp.TexCoord2f[i * 2]) || !Same(ours.MeshTexCoords[i].Y, dp.TexCoord2f[i * 2 + 1]))
            {
                Bad($"texcoord {i}: {ours.MeshTexCoords[i]} vs {dp.TexCoord2f[i * 2]} {dp.TexCoord2f[i * 2 + 1]}");
                break;
            }

        // Every vertex of every pose, and the corners against the element array.
        float[] v = new float[3];
        bool positionsOk = true;
        for (int pose = 0; pose < dp.NumPoses && positionsOk; pose++)
            for (int i = 0; i < dp.NumVertices; i++)
            {
                dp.Position(pose, i, v);
                Vector3 p = ours.Frames[pose].Vertices[ours.MeshVertices[i]].Position;
                if (!Same(p.X, v[0]) || !Same(p.Y, v[1]) || !Same(p.Z, v[2]))
                {
                    Bad($"pose {pose} vertex {i}: {p} vs {v[0]} {v[1]} {v[2]}");
                    positionsOk = false;
                    break;
                }
            }
        for (int i = 0; i < ours.Corners.Length; i++)
        {
            int e = dp.Element3i[i];
            if (ours.Corners[i].Vertex != ours.MeshVertices[e] || !Same(ours.Corners[i].Uv.X, dp.TexCoord2f[e * 2]) || !Same(ours.Corners[i].Uv.Y, dp.TexCoord2f[e * 2 + 1]))
            {
                Bad($"corner {i} does not agree with element {e}");
                break;
            }
        }

        if (!Same(ours.Mins.X, dp.NormalMins[0]) || !Same(ours.Mins.Y, dp.NormalMins[1]) || !Same(ours.Mins.Z, dp.NormalMins[2])
            || !Same(ours.Maxs.X, dp.NormalMaxs[0]) || !Same(ours.Maxs.Y, dp.NormalMaxs[1]) || !Same(ours.Maxs.Z, dp.NormalMaxs[2]))
            Bad($"bounds {ours.Mins} {ours.Maxs} vs {string.Join(' ', dp.NormalMins)} / {string.Join(' ', dp.NormalMaxs)}");
        if (MathF.Abs(ours.Radius - dp.Radius) > 1e-4f * MathF.Max(1, dp.Radius)) Bad($"radius {ours.Radius} vs {dp.Radius}");
        if (MathF.Abs(ours.YawRadius - dp.YawRadius) > 1e-4f * MathF.Max(1, dp.YawRadius)) Bad($"yaw radius {ours.YawRadius} vs {dp.YawRadius}");
        if (ours.IsAnimated != dp.IsAnimated) Bad($"isanimated {ours.IsAnimated} vs {dp.IsAnimated}");
    }

    /// <summary>
    /// Each skin picture, converted both without a full-bright range (no gfx/colormap.lmp: a Xonotic install)
    /// and with Quake's (224..255), against R_SkinFrame_GenerateTexturesFromQPixels.
    /// </summary>
    private static void AssertSameSkins(string label, byte[] bytes, MdlData ours, Dp.AliasModel dp, List<string> problems)
    {
        byte[] rgb = QuakePalette.DefaultRgb.ToArray();
        byte[] quakeColormap = new byte[16385];
        quakeColormap[16384] = 32;
        (QuakePalette Ours, Dp.Palettes Theirs, string Name)[] cases =
        {
            (QuakePalette.Default, new Dp.Palettes(rgb, null), "no colormap.lmp"),
            (QuakePalette.Quake, new Dp.Palettes(rgb, quakeColormap), "Quake colormap.lmp"),
        };
        int n = dp.SkinWidth * dp.SkinHeight;
        for (int i = 0; i < dp.TotalSkins && i < ours.Skins.Length; i++)
        {
            if (!ours.Skins[i].AsSpan().SequenceEqual(bytes.AsSpan(dp.SkinOffsets[i], n)))
            {
                problems.Add($"{label}: skin {i} indices differ");
                return;
            }
            foreach ((QuakePalette palette, Dp.Palettes theirs, string name) in cases)
            {
                Dp.QuakeSkin want = Dp.InternalQuakeSkin(bytes, dp.SkinOffsets[i], dp.SkinWidth, dp.SkinHeight, theirs);
                QuakeTexture merged = palette.Convert(ours.Skins[i]);
                if (!merged.Base.AsSpan().SequenceEqual(want.Merged)) { problems.Add($"{label}: skin {i} merged texture differs ({name})"); return; }
                if ((merged.Glow is null) != (want.Glow is null) || (merged.Glow is not null && !merged.Glow.AsSpan().SequenceEqual(want.Glow)))
                {
                    problems.Add($"{label}: skin {i} glow texture differs ({name})");
                    return;
                }
                QuakeColormappedTexture? mapped = palette.ConvertColormapped(ours.Skins[i]);
                if ((mapped is null) == want.HasColormapping) { problems.Add($"{label}: skin {i} colormapping {mapped is not null} vs {want.HasColormapping} ({name})"); return; }
                if (mapped is not null && (!mapped.Base.AsSpan().SequenceEqual(want.Base) || !mapped.Pants.AsSpan().SequenceEqual(want.Pants) || !mapped.Shirt.AsSpan().SequenceEqual(want.Shirt)))
                {
                    problems.Add($"{label}: skin {i} colormapped textures differ ({name})");
                    return;
                }
            }
        }
        if (dp.TotalSkins > 0 && n > 0)
        {
            Dp.QuakeSkin first = Dp.InternalQuakeSkin(bytes, dp.SkinOffsets[0], dp.SkinWidth, dp.SkinHeight, cases[0].Theirs);
            if (!ours.SkinRgba.AsSpan().SequenceEqual(first.Merged)) problems.Add($"{label}: SkinRgba differs");
        }
    }

    private static void AssertSameSprite(string label, SpriteData ours, Dp.Sprite dp, QuakePalette palette, List<string> problems)
    {
        void Bad(string what) { if (problems.Count < 500) problems.Add($"{label}: {what}"); }

        if (ours.Scenes.Length != dp.NumFrames) { Bad($"numframes {ours.Scenes.Length} vs {dp.NumFrames}"); return; }
        if (ours.Frames.Length != dp.Frames.Length) { Bad($"frames {ours.Frames.Length} vs {dp.Frames.Length}"); return; }
        int type = dp.Type is >= 0 and <= 7 ? dp.Type : 2;
        if ((int)ours.SpriteType != type) Bad($"type {ours.SpriteType} vs {dp.Type}");
        if (ours.SyncType != dp.SyncType) Bad($"synctype {ours.SyncType} vs {dp.SyncType}");
        if (ours.Additive != dp.Additive) Bad($"additive {ours.Additive} vs {dp.Additive}");
        if (!Same(ours.Radius, dp.Radius)) Bad($"radius {ours.Radius} vs {dp.Radius}");
        for (int i = 0; i < dp.NumFrames; i++)
        {
            SpriteScene s = ours.Scenes[i];
            Dp.AnimScene d = dp.AnimScenes[i];
            if (s.Name != d.Name || s.FirstFrame != d.FirstFrame || s.FrameCount != d.FrameCount || !Same(s.FrameRate, d.FrameRate))
            {
                Bad($"animscene {i}: {s} vs {d.Name} {d.FirstFrame} {d.FrameCount} {d.FrameRate}");
                break;
            }
        }
        for (int i = 0; i < dp.Frames.Length; i++)
        {
            SpriteFrame f = ours.Frames[i];
            Dp.SpriteFrame d = dp.Frames[i];
            if (f.QuadLeft != d.Left || f.QuadRight != d.Right || f.QuadUp != d.Up || f.QuadDown != d.Down)
            {
                Bad($"frame {i} quad {f.QuadLeft} {f.QuadRight} {f.QuadUp} {f.QuadDown} vs {d.Left} {d.Right} {d.Up} {d.Down}");
                break;
            }
            byte[]? rgba = f.ToRgba(palette);
            if ((rgba is null) != (d.Pixels is null) || (rgba is not null && !rgba.AsSpan().SequenceEqual(d.Pixels)))
            {
                Bad($"frame {i} pixels differ");
                break;
            }
            if (f.HasAlpha != d.HasAlpha)
            {
                Bad($"frame {i} hasalpha {f.HasAlpha} vs {d.HasAlpha}");
                break;
            }
        }
    }
}
