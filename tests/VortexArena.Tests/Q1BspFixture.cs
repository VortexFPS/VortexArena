using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using VortexArena.Formats.Bsp;

namespace VortexArena.Tests;

/// <summary>
/// A Quake 1 format map built in code, written out as any of the four file layouts. No game content: the
/// level is a room (x and y from -256 to 256, z from 0 to 256) under a sky, with a ramp, and under z = 48
/// water (x over 150) and lava (x under 150), and one submodel ("*1", a block 32 x 64 x 96 standing at x = 100).
///
/// The clipping hulls are made the way a map compiler makes them - the same tree with each plane moved out by
/// the hull's box - so a box trace through them meets the same geometry a point trace through hull 0 does.
/// </summary>
internal sealed class Q1BspFixture
{
    public const int Solid = -2, Empty = -1, Water = -3, Slime = -4, Lava = -5, Sky = -6;

    private sealed record Node(int Plane, int C0, int C1, int FirstFace, int FaceCount);
    private sealed record Face(int Plane, int Side, int FirstEdge, int NumEdges, int TexInfo, byte[] Styles, int LightOfs);

    private readonly List<(Vector3 N, float D)> _planes = new();
    private readonly List<Node> _nodes = new();
    private readonly List<(int Contents, int VisOfs)> _leafs = new();
    private readonly List<(int Plane, int C0, int C1)> _clip = new();
    private readonly List<Vector3> _verts = new() { Vector3.Zero };
    private readonly List<(int A, int B)> _edges = new() { (0, 0) };
    private readonly List<int> _surfEdges = new();
    private readonly List<Face> _faces = new();
    private readonly List<(Vector4 S, Vector4 T, int Mip, int Flags)> _texInfo = new();
    private readonly List<(string Name, int W, int H, bool Embedded)> _textures = new();
    private readonly List<byte> _light = new();
    private readonly List<byte> _vis = new();
    private readonly List<(Vector3 Mins, Vector3 Maxs, int[] Heads, int VisLeafs, int FirstFace, int NumFaces)> _models = new();

    /// <summary>The visibility row written for the water leaf: with <c>false</c> it sees only liquid.</summary>
    public bool WaterSeesAir { get; init; } = true;
    /// <summary>Adds a face with no area (three points on a line) to the floor's node.</summary>
    public bool SliverOnFloor { get; init; }
    public string EntityText { get; init; } = "{\n\"classname\" \"worldspawn\"\n\"message\" \"fixture\"\n}\n{\n\"classname\" \"func_door\"\n\"model\" \"*1\"\n}\n";

    public static readonly (Vector3 Mins, Vector3 Maxs)[] QuakeHulls =
    {
        (Vector3.Zero, Vector3.Zero), (new Vector3(-16, -16, -24), new Vector3(16, 16, 32)), (new Vector3(-32, -32, -24), new Vector3(32, 32, 64)),
    };
    public static readonly (Vector3 Mins, Vector3 Maxs)[] HalfLifeHulls =
    {
        (Vector3.Zero, Vector3.Zero), (new Vector3(-16, -16, -36), new Vector3(16, 16, 36)), (new Vector3(-32, -32, -32), new Vector3(32, 32, 32)), (new Vector3(-16, -16, -18), new Vector3(16, 16, 18)),
    };

    // One split of the level: the plane, and what lies in front of and behind it - another split (by name) or contents.
    private sealed record Split(string Name, Vector3 N, float D, object Front, object Back, bool SolidInFront = false, bool SolidBehind = false);

    private static readonly Split[] World =
    {
        new("x0", Vector3.UnitX, -256, "x1", Solid, SolidBehind: true),
        new("x1", Vector3.UnitX, 256, Solid, "y0", SolidInFront: true),
        new("y0", Vector3.UnitY, -256, "y1", Solid, SolidBehind: true),
        new("y1", Vector3.UnitY, 256, Solid, "z0", SolidInFront: true),
        new("z0", Vector3.UnitZ, 0, "z1", Solid, SolidBehind: true),
        new("z1", Vector3.UnitZ, 256, Sky, "ramp", SolidInFront: true),
        new("ramp", new Vector3(0.6f, 0, 0.8f), 60, "pool", Solid, SolidBehind: true),
        new("pool", Vector3.UnitZ, 48, Empty, "half"),
        new("half", Vector3.UnitX, 150, Water, Lava),
    };

    private static readonly Split[] Door =
    {
        new("dx0", Vector3.UnitX, 100, "dx1", Empty, SolidInFront: true),
        new("dx1", Vector3.UnitX, 132, Empty, "dy0", SolidBehind: true),
        new("dy0", Vector3.UnitY, -32, "dy1", Empty, SolidInFront: true),
        new("dy1", Vector3.UnitY, 32, Empty, "dz0", SolidBehind: true),
        new("dz0", Vector3.UnitZ, 0, "dz1", Empty, SolidInFront: true),
        new("dz1", Vector3.UnitZ, 96, Empty, Solid, SolidBehind: true),
    };

    private int Plane(Vector3 n, float d)
    {
        _planes.Add((n, d));
        return _planes.Count - 1;
    }

    // The clipping hull of a set of splits for one hull box: each plane moved by the box's reach toward the solid side.
    private int AddHull(Split[] splits, (Vector3 Mins, Vector3 Maxs) box)
    {
        int first = _clip.Count;
        Dictionary<string, int> index = new();
        for (int i = 0; i < splits.Length; i++) index[splits[i].Name] = first + i;
        foreach (Split s in splits)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new((c & 1) != 0 ? box.Maxs.X : box.Mins.X, (c & 2) != 0 ? box.Maxs.Y : box.Mins.Y, (c & 4) != 0 ? box.Maxs.Z : box.Mins.Z);
                float d = Vector3.Dot(s.N, corner);
                lo = MathF.Min(lo, d);
                hi = MathF.Max(hi, d);
            }
            float dist = s.SolidBehind ? s.D - lo : s.SolidInFront ? s.D - hi : s.D;
            _clip.Add((Plane(s.N, dist), s.Front is string f ? index[f] : (int)s.Front, s.Back is string b ? index[b] : (int)s.Back));
        }
        return first;
    }

    private int Leaf(int contents)
    {
        if (contents == Solid) return 0;
        _leafs.Add((contents, -1));
        return _leafs.Count - 1;
    }

    // Hull 0: the node tree. Each split's faces were added before (firstFace/count by split name).
    private int AddTree(Split[] splits, Dictionary<string, (int First, int Count)> faces)
    {
        int first = _nodes.Count;
        Dictionary<string, int> index = new();
        for (int i = 0; i < splits.Length; i++) index[splits[i].Name] = first + i;
        Dictionary<int, int> leafOf = new();
        int Child(object c)
        {
            if (c is string name) return index[name];
            int contents = (int)c;
            if (!leafOf.TryGetValue(contents, out int leaf)) leafOf[contents] = leaf = Leaf(contents);
            return -(leaf + 1);
        }
        foreach (Split s in splits)
        {
            faces.TryGetValue(s.Name, out (int First, int Count) f);
            _nodes.Add(new Node(Plane(s.N, s.D), Child(s.Front), Child(s.Back), f.First, f.Count));
        }
        return first;
    }

    private int Texture(string name, int w = 64, int h = 64, bool embedded = true)
    {
        _textures.Add((name, w, h, embedded));
        return _textures.Count - 1;
    }

    private int TexInfo(Vector3 s, Vector3 t, int mip, int flags = 0)
    {
        _texInfo.Add((new Vector4(s, 0), new Vector4(t, 0), mip, flags));
        return _texInfo.Count - 1;
    }

    /// <summary>A polygon, wound clockwise seen from its front. Returns the face's index.</summary>
    private int Poly(int plane, int side, int texInfo, byte[] styles, int lightOfs, bool reverseEdges, Vector3 facing, params Vector3[] points)
    {
        // DarkPlaces' normal of the fan is (v0 - v1) x (v2 - v1): turn the polygon over if that points away
        if (Vector3.Dot(Vector3.Cross(points[0] - points[1], points[2] - points[1]), facing) < 0) Array.Reverse(points);
        int firstEdge = _surfEdges.Count;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 a = points[i], b = points[(i + 1) % points.Length];
            if (reverseEdges)
            {
                // the edge stored end to start and referenced backwards: the face's vertex is the edge's second
                _verts.Add(b); _verts.Add(a);
                _edges.Add((_verts.Count - 2, _verts.Count - 1));
                _surfEdges.Add(-(_edges.Count - 1));
            }
            else
            {
                _verts.Add(a); _verts.Add(b);
                _edges.Add((_verts.Count - 2, _verts.Count - 1));
                _surfEdges.Add(_edges.Count - 1);
            }
        }
        _faces.Add(new Face(plane, side, firstEdge, points.Length, texInfo, styles, lightOfs));
        return _faces.Count - 1;
    }

    private static readonly byte[] NoStyles = { 255, 255, 255, 255 };

    /// <summary>The floor's light block: 33 x 33 samples, style 0 then style 5. Sample (s, t) of layer L is this.</summary>
    public static byte FloorLight(int layer, int s, int t) => (byte)((layer == 0 ? 40 : 10) + s * 3 + t * 2);
    public const int FloorLightSize = 33;

    private Q1BspFixture Compose(bool halfLife)
    {
        (Vector3, Vector3)[] hulls = halfLife ? HalfLifeHulls : QuakeHulls;
        _leafs.Add((Solid, -1)); // leaf 0 is the solid leaf of every model

        int floorTex = Texture("Floor"), wallTex = Texture("WALL1"), skyTex = Texture("sky1", 256, 128), waterTex = Texture("*water0"), lavaTex = Texture("*lava1"),
            animTex = Texture("+0button"), holeTex = Texture("{grate"), externalTex = Texture("crate", 32, 32, embedded: false);
        _ = animTex; _ = holeTex; _ = externalTex;
        int floorInfo = TexInfo(Vector3.UnitX, -Vector3.UnitY, floorTex), wallXInfo = TexInfo(Vector3.UnitY, -Vector3.UnitZ, wallTex), wallYInfo = TexInfo(Vector3.UnitX, -Vector3.UnitZ, wallTex),
            skyInfo = TexInfo(Vector3.UnitX, -Vector3.UnitY, skyTex, 1), waterInfo = TexInfo(Vector3.UnitX, -Vector3.UnitY, waterTex, 1), lavaInfo = TexInfo(Vector3.UnitX, -Vector3.UnitY, lavaTex, 1),
            missingInfo = TexInfo(Vector3.UnitY, -Vector3.UnitZ, 999);

        // the floor's light: two layers of 33 x 33
        int floorLight = _light.Count;
        for (int layer = 0; layer < 2; layer++)
            for (int t = 0; t < FloorLightSize; t++)
                for (int s = 0; s < FloorLightSize; s++)
                    _light.Add(FloorLight(layer, s, t));

        Dictionary<string, (int, int)> worldFaces = new();
        // (the face planes are separate plane entries: a face names a plane and a side)
        int pz0 = Plane(Vector3.UnitZ, 0), pz1 = Plane(Vector3.UnitZ, 256), px0 = Plane(Vector3.UnitX, -256), px1 = Plane(Vector3.UnitX, 256),
            py0 = Plane(Vector3.UnitY, -256), py1 = Plane(Vector3.UnitY, 256), pramp = Plane(new Vector3(0.6f, 0, 0.8f), 60), ppool = Plane(Vector3.UnitZ, 48);
        const float a = 256;
        int first = _faces.Count;
        Poly(px0, 0, wallXInfo, NoStyles, -1, false, Vector3.UnitX, new(-a, a, 0), new(-a, a, a), new(-a, -a, a), new(-a, -a, 0));
        worldFaces["x0"] = (first, 1);
        first = _faces.Count;
        Poly(px1, 1, missingInfo, NoStyles, -1, false, -Vector3.UnitX, new(a, -a, 0), new(a, -a, a), new(a, a, a), new(a, a, 0));
        worldFaces["x1"] = (first, 1);
        first = _faces.Count;
        Poly(py0, 0, wallYInfo, NoStyles, -1, true, Vector3.UnitY, new(-a, -a, 0), new(-a, -a, a), new(a, -a, a), new(a, -a, 0));
        worldFaces["y0"] = (first, 1);
        first = _faces.Count;
        Poly(py1, 1, wallYInfo, NoStyles, -1, false, -Vector3.UnitY, new(a, a, 0), new(a, a, a), new(-a, a, a), new(-a, a, 0));
        worldFaces["y1"] = (first, 1);
        first = _faces.Count;
        Poly(pz0, 0, floorInfo, new byte[] { 0, 5, 255, 255 }, floorLight, false, Vector3.UnitZ, new(-a, -a, 0), new(-a, a, 0), new(a, a, 0), new(a, -a, 0));
        if (SliverOnFloor) Poly(pz0, 0, floorInfo, NoStyles, -1, false, Vector3.UnitZ, new(-64, 0, 0), new(0, 0, 0), new(64, 0, 0));
        worldFaces["z0"] = (first, _faces.Count - first);
        first = _faces.Count;
        Poly(pz1, 1, skyInfo, NoStyles, -1, false, -Vector3.UnitZ, new(-a, -a, a), new(a, -a, a), new(a, a, a), new(-a, a, a));
        worldFaces["z1"] = (first, 1);
        first = _faces.Count;
        // the ramp, from its top edge (x = -100, z = 150) down to the floor (x = 100, z = 0)
        Poly(pramp, 0, floorInfo, NoStyles, -1, false, new Vector3(0.6f, 0, 0.8f), new(-100, -a, 150), new(-100, a, 150), new(100, a, 0), new(100, -a, 0));
        worldFaces["ramp"] = (first, 1);
        first = _faces.Count;
        // (the liquid surfaces cover only the half of the pool with y below zero: the floor shows through the rest)
        Poly(ppool, 0, waterInfo, NoStyles, -1, false, Vector3.UnitZ, new(150, -a, 48), new(150, 0, 48), new(a, 0, 48), new(a, -a, 48));
        Poly(ppool, 0, lavaInfo, NoStyles, -1, false, Vector3.UnitZ, new(36, -a, 48), new(36, 0, 48), new(150, 0, 48), new(150, -a, 48));
        worldFaces["pool"] = (first, 2);
        int worldFaceCount = _faces.Count;

        int worldHead = AddTree(World, worldFaces);
        // leaves no node reaches: they only make the visibility rows longer than a byte
        for (int i = 0; i < 20; i++) _leafs.Add((Empty, -1));
        int worldLeafs = _leafs.Count - 1;

        // the door
        int doorFirstFace = _faces.Count;
        Dictionary<string, (int, int)> doorFaces = new();
        int dx0 = Plane(Vector3.UnitX, 100), dx1 = Plane(Vector3.UnitX, 132), dy0 = Plane(Vector3.UnitY, -32), dy1 = Plane(Vector3.UnitY, 32), dz1 = Plane(Vector3.UnitZ, 96);
        first = _faces.Count;
        Poly(dx0, 1, wallXInfo, NoStyles, -1, false, -Vector3.UnitX, new(100, -32, 0), new(100, -32, 96), new(100, 32, 96), new(100, 32, 0));
        doorFaces["dx0"] = (first, 1);
        first = _faces.Count;
        Poly(dx1, 0, wallXInfo, NoStyles, -1, false, Vector3.UnitX, new(132, 32, 0), new(132, 32, 96), new(132, -32, 96), new(132, -32, 0));
        doorFaces["dx1"] = (first, 1);
        first = _faces.Count;
        Poly(dy0, 1, wallYInfo, NoStyles, -1, false, -Vector3.UnitY, new(132, -32, 0), new(132, -32, 96), new(100, -32, 96), new(100, -32, 0));
        doorFaces["dy0"] = (first, 1);
        first = _faces.Count;
        Poly(dy1, 0, wallYInfo, NoStyles, -1, false, Vector3.UnitY, new(100, 32, 0), new(100, 32, 96), new(132, 32, 96), new(132, 32, 0));
        doorFaces["dy1"] = (first, 1);
        first = _faces.Count;
        Poly(dz1, 0, floorInfo, NoStyles, -1, false, Vector3.UnitZ, new(100, -32, 96), new(100, 32, 96), new(132, 32, 96), new(132, -32, 96));
        doorFaces["dz1"] = (first, 1);
        int doorHead = AddTree(Door, doorFaces);

        // hulls 1.. for both models
        int[] worldHeads = new int[4], doorHeads = new int[4];
        worldHeads[0] = worldHead;
        doorHeads[0] = doorHead;
        for (int h = 1; h < hulls.Length; h++)
        {
            worldHeads[h] = AddHull(World, hulls[h]);
            doorHeads[h] = AddHull(Door, hulls[h]);
        }

        // visibility: one row per world leaf 1..worldLeafs, three bytes wide, run-length encoded
        int clusters = worldLeafs;
        int rowBytes = (clusters + 7) >> 3;
        for (int leaf = 1; leaf <= worldLeafs; leaf++)
        {
            byte[] row = new byte[rowBytes];
            int contents = _leafs[leaf].Contents;
            // clusters 0..3 are the empty, water, lava and sky leaves (in the order the tree made them: sky, empty, water, lava)
            if (leaf <= 4) row[0] = contents is Water or Lava && !WaterSeesAir ? LiquidClusters() : (byte)0x0F;
            _leafs[leaf] = (contents, _vis.Count);
            for (int i = 0; i < rowBytes; i++)
            {
                _vis.Add(row[i]);
                if (row[i] != 0) continue;
                int run = 1;
                while (i + run < rowBytes && row[i + run] == 0) run++;
                _vis.Add((byte)run);
                i += run - 1;
            }
        }

        _models.Add((new Vector3(-a, -a, 0), new Vector3(a, a, a), worldHeads, clusters, 0, worldFaceCount));
        _models.Add((new Vector3(100, -32, 0), new Vector3(132, 32, 96), doorHeads, 0, doorFirstFace, _faces.Count - doorFirstFace));
        return this;
    }

    // the bits of the clusters whose leaves are liquid
    private byte LiquidClusters()
    {
        byte bits = 0;
        for (int leaf = 1; leaf <= 4 && leaf < _leafs.Count; leaf++)
            if (_leafs[leaf].Contents is Water or Lava) bits |= (byte)(1 << (leaf - 1));
        return bits;
    }

    /// <summary>The fixture as a file of the given layout.</summary>
    public static byte[] Build(Q1BspFormat format, bool waterSeesAir = true, bool sliverOnFloor = false) =>
        new Q1BspFixture { WaterSeesAir = waterSeesAir, SliverOnFloor = sliverOnFloor }.Compose(format == Q1BspFormat.HalfLife).Write(format);

    /// <summary>The <c>.lit</c> file of the fixture built as a Quake map: sample i is (i, 2i, 3i) modulo 256.</summary>
    public static byte[] Lit(int lightLumpLength)
    {
        byte[] lit = new byte[8 + lightLumpLength * 3];
        "QLIT"u8.CopyTo(lit);
        lit[4] = 1;
        for (int i = 0; i < lightLumpLength; i++)
        {
            lit[8 + i * 3] = (byte)i;
            lit[8 + i * 3 + 1] = (byte)(i * 2);
            lit[8 + i * 3 + 2] = (byte)(i * 3);
        }
        return lit;
    }

    public static int LightLumpLength => FloorLightSize * FloorLightSize * 2;

    private byte[] Write(Q1BspFormat format)
    {
        bool bsp2 = format is Q1BspFormat.Bsp2 or Q1BspFormat.Bsp2Rmqe, rmqe = format == Q1BspFormat.Bsp2Rmqe, hl = format == Q1BspFormat.HalfLife;
        byte[][] lumps = new byte[15][];
        lumps[0] = Encoding.ASCII.GetBytes(EntityText + "\0");
        lumps[1] = Bytes(w => { foreach ((Vector3 n, float d) in _planes) { V(w, n); w.Write(d); w.Write(0); } });
        lumps[2] = Bytes(w =>
        {
            w.Write(_textures.Count);
            int at = 4 + 4 * _textures.Count;
            List<byte[]> blobs = new();
            foreach ((string name, int tw, int th, bool embedded) in _textures)
            {
                byte[] blob = Bytes(m =>
                {
                    byte[] n = new byte[16];
                    Encoding.ASCII.GetBytes(name).CopyTo(n, 0);
                    m.Write(n);
                    m.Write(tw); m.Write(th);
                    if (!embedded) { m.Write(0); m.Write(0); m.Write(0); m.Write(0); return; }
                    int o = 40;
                    for (int level = 0; level < 4; level++) { m.Write(o); o += (tw >> level) * (th >> level); }
                    for (int level = 0; level < 4; level++)
                        for (int i = 0; i < (tw >> level) * (th >> level); i++) m.Write((byte)(level == 0 ? (i * 7 + name.Length) & 255 : 0));
                    if (hl)
                    {
                        m.Write((short)256);
                        for (int i = 0; i < 256; i++) { m.Write((byte)i); m.Write((byte)(255 - i)); m.Write((byte)(i ^ 0x55)); }
                    }
                });
                blobs.Add(blob);
            }
            foreach (byte[] blob in blobs) { w.Write(at); at += blob.Length; }
            foreach (byte[] blob in blobs) w.Write(blob);
        });
        lumps[3] = Bytes(w => { foreach (Vector3 v in _verts) V(w, v); });
        lumps[4] = _vis.ToArray();
        lumps[5] = Bytes(w =>
        {
            foreach (Node n in _nodes)
            {
                w.Write(n.Plane);
                if (bsp2) { w.Write(n.C0); w.Write(n.C1); } else { w.Write((short)n.C0); w.Write((short)n.C1); }
                Bounds(w, bsp2 && !rmqe);
                if (bsp2) { w.Write(n.FirstFace); w.Write(n.FaceCount); } else { w.Write((ushort)n.FirstFace); w.Write((ushort)n.FaceCount); }
            }
        });
        lumps[6] = Bytes(w =>
        {
            foreach ((Vector4 s, Vector4 t, int mip, int flags) in _texInfo)
            {
                w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.W);
                w.Write(t.X); w.Write(t.Y); w.Write(t.Z); w.Write(t.W);
                w.Write(mip); w.Write(flags);
            }
        });
        lumps[7] = Bytes(w =>
        {
            foreach (Face f in _faces)
            {
                // a Half-Life map's light offset is in bytes of its RGB data
                int lightOfs = f.LightOfs >= 0 && hl ? f.LightOfs * 3 : f.LightOfs;
                if (bsp2) { w.Write(f.Plane); w.Write(f.Side); w.Write(f.FirstEdge); w.Write(f.NumEdges); w.Write(f.TexInfo); }
                else { w.Write((ushort)f.Plane); w.Write((ushort)f.Side); w.Write(f.FirstEdge); w.Write((ushort)f.NumEdges); w.Write((ushort)f.TexInfo); }
                w.Write(f.Styles);
                w.Write(lightOfs);
            }
        });
        lumps[8] = hl ? _light.SelectMany(b => new[] { b, b, b }).ToArray() : _light.ToArray();
        lumps[9] = Bytes(w =>
        {
            foreach ((int plane, int c0, int c1) in _clip)
            {
                w.Write(plane);
                if (bsp2) { w.Write(c0); w.Write(c1); } else { w.Write((short)c0); w.Write((short)c1); }
            }
        });
        lumps[10] = Bytes(w =>
        {
            foreach ((int contents, int visOfs) in _leafs)
            {
                w.Write(contents);
                w.Write(visOfs);
                Bounds(w, bsp2 && !rmqe);
                if (bsp2) { w.Write(0); w.Write(0); } else { w.Write((ushort)0); w.Write((ushort)0); }
                w.Write(0); // ambient levels
            }
        });
        lumps[11] = Array.Empty<byte>();
        lumps[12] = Bytes(w =>
        {
            foreach ((int ea, int eb) in _edges)
            {
                if (bsp2) { w.Write(ea); w.Write(eb); } else { w.Write((ushort)ea); w.Write((ushort)eb); }
            }
        });
        lumps[13] = Bytes(w => { foreach (int e in _surfEdges) w.Write(e); });
        lumps[14] = Bytes(w =>
        {
            foreach ((Vector3 mins, Vector3 maxs, int[] heads, int visLeafs, int firstFace, int numFaces) in _models)
            {
                V(w, mins); V(w, maxs); V(w, Vector3.Zero);
                for (int h = 0; h < 4; h++) w.Write(heads[h]);
                w.Write(visLeafs); w.Write(firstFace); w.Write(numFaces);
            }
        });

        using MemoryStream stream = new();
        using BinaryWriter file = new(stream);
        if (format == Q1BspFormat.Bsp2) file.Write("BSP2"u8);
        else if (rmqe) file.Write("2PSB"u8);
        else file.Write(hl ? 30 : 29);
        int offset = 4 + 15 * 8;
        foreach (byte[] lump in lumps)
        {
            file.Write(offset);
            file.Write(lump.Length);
            offset += lump.Length;
        }
        foreach (byte[] lump in lumps) file.Write(lump);
        return stream.ToArray();
    }

    private static void Bounds(BinaryWriter w, bool floats)
    {
        for (int i = 0; i < 6; i++)
        {
            if (floats) w.Write(i < 3 ? -4096f : 4096f);
            else w.Write((short)(i < 3 ? -4096 : 4096));
        }
    }

    private static void V(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        write(w);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Where lump <paramref name="lump"/> starts in a file made here, and how long it is.</summary>
    public static (int Offset, int Length) LumpOf(byte[] file, int lump) => (BitConverter.ToInt32(file, 4 + lump * 8), BitConverter.ToInt32(file, 8 + lump * 8));
}
