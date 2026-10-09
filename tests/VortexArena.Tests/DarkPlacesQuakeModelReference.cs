using System;
using System.Collections.Generic;
using System.Text;

namespace VortexArena.Tests;

/// <summary>
/// A second, independent reading of Quake's model formats, written straight from the C of DarkPlaces
/// (<c>model_alias.c</c> Mod_IDP0_Load / Mod_MDL_LoadFrames / Mod_Alias_CalculateBoundingBox,
/// <c>model_sprite.c</c> Mod_IDSP_Load / Mod_Sprite_SharedSetup, <c>palette.c</c>
/// Palette_SetupSpecialPalettes, <c>gl_rmain.c</c> R_SkinFrame_GenerateTexturesFromQPixels) with its two
/// passes, its pointer walk and its arrays kept as they are there. It shares no code with
/// <c>src/VortexArena.Formats</c>: the tests compare the two. Nothing here is defensive - a damaged file
/// throws whatever it throws, and the caller treats that as "DarkPlaces would refuse it".
/// </summary>
internal static class DarkPlacesQuakeModelReference
{
    internal struct AnimScene
    {
        public string Name;
        public int FirstFrame, FrameCount;
        public float FrameRate;
        public bool Loop;
    }

    internal sealed class AliasModel
    {
        public int NumSkins, TotalSkins, SkinWidth, SkinHeight, NumFrames, NumPoses, NumVertices, NumTriangles, SyncType;
        public uint Effects;
        public float[] Scale = new float[3], Translate = new float[3];
        public int[] Element3i = Array.Empty<int>();
        public float[] TexCoord2f = Array.Empty<float>();
        /// <summary>trivertx_t per pose per compacted vertex: v[0], v[1], v[2], lightnormalindex.</summary>
        public byte[] MorphVertex = Array.Empty<byte>();
        public AnimScene[] AnimScenes = Array.Empty<AnimScene>(), SkinScenes = Array.Empty<AnimScene>();
        /// <summary>Offset in the file of each skin picture, in order.</summary>
        public int[] SkinOffsets = Array.Empty<int>();
        public float[] NormalMins = new float[3], NormalMaxs = new float[3];
        public float YawRadius, Radius;
        public bool IsAnimated;

        /// <summary>Mod_MDL_AnimateVertices with one blend of weight 1: translate + v * scale.</summary>
        public void Position(int pose, int vertex, float[] into)
        {
            int o = (pose * NumVertices + vertex) * 4;
            for (int i = 0; i < 3; i++)
                into[i] = Translate[i] + MorphVertex[o + i] * Scale[i];
        }
    }

    private static int LittleLong(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    private static float LittleFloat(byte[] b, int o) => BitConverter.Int32BitsToSingle(LittleLong(b, o));

    private static string CString(byte[] b, int o, int max)
    {
        int n = 0;
        while (n < max && b[o + n] != 0) n++;
        return Encoding.Latin1.GetString(b, o, n);
    }

    private static void BoundI(int value, int min, int max, string what)
    {
        if (value < min || value >= max) throw new InvalidOperationException($"invalid {what} ({value} exceeds {min} - {max})");
    }

    private const int SizeofMdl = 84, SizeofStvert = 12, SizeofDtriangle = 16, SizeofTrivertx = 4,
        SizeofDaliasframe = 24, SizeofDaliasgroup = 12;

    public static AliasModel LoadIdp0(byte[] buffer)
    {
        var m = new AliasModel();
        int datapointer = 0;
        int pinmodel = datapointer;
        datapointer += SizeofMdl;
        if (LittleLong(buffer, pinmodel + 4) != 6) throw new InvalidOperationException("wrong version number");

        m.NumSkins = LittleLong(buffer, pinmodel + 48);
        BoundI(m.NumSkins, 0, 65536, "numskins");
        int skinwidth = LittleLong(buffer, pinmodel + 52);
        BoundI(skinwidth, 0, 65536, "skinwidth");
        int skinheight = LittleLong(buffer, pinmodel + 56);
        BoundI(skinheight, 0, 65536, "skinheight");
        int numverts = LittleLong(buffer, pinmodel + 60);
        BoundI(numverts, 0, 65536, "numverts");
        m.NumTriangles = LittleLong(buffer, pinmodel + 64);
        BoundI(m.NumTriangles, 0, 65536, "numtris");
        m.NumFrames = LittleLong(buffer, pinmodel + 68);
        BoundI(m.NumFrames, 0, 65536, "numframes");
        m.SyncType = LittleLong(buffer, pinmodel + 72);
        BoundI(m.SyncType, 0, 2, "synctype");
        int flags = LittleLong(buffer, pinmodel + 76);
        m.Effects = (((uint)flags & 255) << 24) | (uint)(flags & 0x00FFFF00);
        m.SkinWidth = skinwidth;
        m.SkinHeight = skinheight;
        for (int i = 0; i < 3; i++)
        {
            m.Scale[i] = LittleFloat(buffer, pinmodel + 8 + i * 4);
            m.Translate[i] = LittleFloat(buffer, pinmodel + 20 + i * 4);
        }

        int startskins = datapointer;
        int totalskins = 0;
        for (int i = 0; i < m.NumSkins; i++)
        {
            int pinskintype = datapointer;
            datapointer += 4;
            int groupskins;
            if (LittleLong(buffer, pinskintype) == 0)
                groupskins = 1;
            else
            {
                int pinskingroup = datapointer;
                datapointer += 4;
                groupskins = LittleLong(buffer, pinskingroup);
                datapointer += 4 * groupskins;
            }
            for (int j = 0; j < groupskins; j++)
            {
                datapointer += skinwidth * skinheight;
                totalskins++;
            }
        }

        int pinstverts = datapointer;
        datapointer += SizeofStvert * numverts;
        int pintriangles = datapointer;
        datapointer += SizeofDtriangle * m.NumTriangles;

        int startframes = datapointer;
        int numMorphFrames = 0;
        for (int i = 0; i < m.NumFrames; i++)
        {
            int pinframetype = datapointer;
            datapointer += 4;
            int groupframes;
            if (LittleLong(buffer, pinframetype) == 0)
                groupframes = 1;
            else
            {
                int pinframegroup = datapointer;
                datapointer += SizeofDaliasgroup;
                groupframes = LittleLong(buffer, pinframegroup);
                datapointer += 4 * groupframes;
            }
            for (int j = 0; j < groupframes; j++)
            {
                datapointer += SizeofDaliasframe;
                datapointer += SizeofTrivertx * numverts;
                numMorphFrames++;
            }
        }
        if (datapointer > buffer.Length) throw new InvalidOperationException("the frames run past the end of the file");
        m.NumPoses = numMorphFrames;

        float[] vertst = new float[numverts * 2 * 2];
        int[] vertremap = new int[numverts * 3];
        int vertonseam = numverts * 2;      // "vertonseam = vertremap + numverts * 2"
        float scales = (float)(1.0 / skinwidth);
        float scalet = (float)(1.0 / skinheight);
        for (int i = 0; i < numverts; i++)
        {
            vertremap[vertonseam + i] = LittleLong(buffer, pinstverts + i * SizeofStvert);
            vertst[i * 2 + 0] = LittleLong(buffer, pinstverts + i * SizeofStvert + 4) * scales;
            vertst[i * 2 + 1] = LittleLong(buffer, pinstverts + i * SizeofStvert + 8) * scalet;
            vertst[(i + numverts) * 2 + 0] = (float)(vertst[i * 2 + 0] + 0.5);
            vertst[(i + numverts) * 2 + 1] = vertst[i * 2 + 1];
        }

        m.Element3i = new int[3 * m.NumTriangles];
        for (int i = 0; i < m.NumTriangles; i++)
            for (int j = 0; j < 3; j++)
                m.Element3i[i * 3 + j] = LittleLong(buffer, pintriangles + i * SizeofDtriangle + 4 + j * 4);
        // Mod_ValidateElements: "if (element3i[i] < firstvertex || element3i[i] >= firstvertex + numvertices) ... element3i[i] = firstvertex"
        for (int i = 0; i < m.NumTriangles * 3; i++)
            if (m.Element3i[i] < 0 || m.Element3i[i] >= numverts)
                m.Element3i[i] = 0;
        for (int i = 0; i < m.NumTriangles; i++)
            if (LittleLong(buffer, pintriangles + i * SizeofDtriangle) == 0)       // backface
                for (int j = 0; j < 3; j++)
                    if (vertremap[vertonseam + m.Element3i[i * 3 + j]] != 0)
                        m.Element3i[i * 3 + j] += numverts;
        for (int i = 0; i < numverts * 2; i++)
            vertremap[i] = 0;
        for (int i = 0; i < m.NumTriangles * 3; i++)
            vertremap[m.Element3i[i]]++;
        m.NumVertices = 0;
        for (int i = 0; i < numverts * 2; i++)
        {
            if (vertremap[i] != 0)
            {
                vertremap[i] = m.NumVertices;
                vertst[m.NumVertices * 2 + 0] = vertst[i * 2 + 0];
                vertst[m.NumVertices * 2 + 1] = vertst[i * 2 + 1];
                m.NumVertices++;
            }
            else
                vertremap[i] = -1;
        }
        for (int i = 0; i < m.NumTriangles * 3; i++)
            m.Element3i[i] = vertremap[m.Element3i[i]];
        m.TexCoord2f = new float[2 * m.NumVertices];
        for (int i = 0; i < m.NumVertices; i++)
        {
            m.TexCoord2f[i * 2 + 0] = vertst[i * 2 + 0];
            m.TexCoord2f[i * 2 + 1] = vertst[i * 2 + 1];
        }

        // Mod_MDL_LoadFrames
        m.AnimScenes = new AnimScene[m.NumFrames];
        m.MorphVertex = new byte[4 * numMorphFrames * m.NumVertices];
        {
            int dp = startframes;
            int pose = 0;
            for (int f = 0; f < m.NumFrames; f++)
            {
                int pframetype = dp;
                dp += 4;
                float interval;
                int groupframes;
                if (LittleLong(buffer, pframetype) == 0)
                {
                    interval = 0.1f;
                    groupframes = 1;
                }
                else
                {
                    int group = dp;
                    dp += SizeofDaliasgroup;
                    groupframes = LittleLong(buffer, group);
                    int intervals = dp;
                    dp += 4 * groupframes;
                    interval = LittleFloat(buffer, intervals);
                    if (interval < 0.01f)
                        interval = 0.1f;
                }
                int pinframe = dp;
                m.AnimScenes[f].Name = CString(buffer, pinframe + 8, 16);
                m.AnimScenes[f].FirstFrame = pose;
                m.AnimScenes[f].FrameCount = groupframes;
                m.AnimScenes[f].FrameRate = 1.0f / interval;
                m.AnimScenes[f].Loop = true;
                for (int i = 0; i < groupframes; i++)
                {
                    dp += SizeofDaliasframe;
                    // Mod_ConvertAliasVerts
                    for (int v = 0; v < numverts; v++)
                    {
                        if (vertremap[v] < 0 && vertremap[v + numverts] < 0)
                            continue;
                        int j = vertremap[v];
                        if (j >= 0)
                            Buffer.BlockCopy(buffer, dp + v * 4, m.MorphVertex, (pose * m.NumVertices + j) * 4, 4);
                        j = vertremap[v + numverts];
                        if (j >= 0)
                            Buffer.BlockCopy(buffer, dp + v * 4, m.MorphVertex, (pose * m.NumVertices + j) * 4, 4);
                    }
                    dp += SizeofTrivertx * numverts;
                    pose++;
                }
            }
        }

        // Mod_Alias_CalculateBoundingBox
        {
            bool firstvertex = true;
            float yawradius = 0, radius = 0;
            float[] v = new float[3], reference = new float[3];
            for (int pose = 0; pose < m.NumPoses; pose++)
                for (int vnum = 0; vnum < m.NumVertices; vnum++)
                {
                    m.Position(pose, vnum, v);
                    if (pose > 0 && !m.IsAnimated)
                    {
                        m.Position(0, vnum, reference);
                        if (BitConverter.SingleToInt32Bits(reference[0]) != BitConverter.SingleToInt32Bits(v[0])
                            || BitConverter.SingleToInt32Bits(reference[1]) != BitConverter.SingleToInt32Bits(v[1])
                            || BitConverter.SingleToInt32Bits(reference[2]) != BitConverter.SingleToInt32Bits(v[2]))
                            m.IsAnimated = true;
                    }
                    if (firstvertex)
                    {
                        firstvertex = false;
                        Array.Copy(v, m.NormalMins, 3);
                        Array.Copy(v, m.NormalMaxs, 3);
                    }
                    else
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            if (m.NormalMins[i] > v[i]) m.NormalMins[i] = v[i];
                            if (m.NormalMaxs[i] < v[i]) m.NormalMaxs[i] = v[i];
                        }
                    }
                    float dist = v[0] * v[0] + v[1] * v[1];
                    if (yawradius < dist) yawradius = dist;
                    dist += v[2] * v[2];
                    if (radius < dist) radius = dist;
                }
            m.Radius = (float)Math.Sqrt(radius);
            m.YawRadius = (float)Math.Sqrt(yawradius);
        }

        // "load the skins" (the branch without .skin files)
        m.SkinScenes = new AnimScene[m.NumSkins];
        var offsets = new List<int>();
        m.TotalSkins = 0;
        datapointer = startskins;
        for (int i = 0; i < m.NumSkins; i++)
        {
            int pinskintype = datapointer;
            datapointer += 4;
            int groupskins;
            float interval;
            if (LittleLong(buffer, pinskintype) == 0)
            {
                groupskins = 1;
                interval = 0.1f;
            }
            else
            {
                int pinskingroup = datapointer;
                datapointer += 4;
                groupskins = LittleLong(buffer, pinskingroup);
                int pinskinintervals = datapointer;
                datapointer += 4 * groupskins;
                interval = LittleFloat(buffer, pinskinintervals);
                if (interval < 0.01f)
                    interval = 0.1f;
            }
            m.SkinScenes[i].Name = "skin " + i;
            m.SkinScenes[i].FirstFrame = m.TotalSkins;
            m.SkinScenes[i].FrameCount = groupskins;
            m.SkinScenes[i].FrameRate = 1.0f / interval;
            m.SkinScenes[i].Loop = true;
            for (int j = 0; j < groupskins; j++)
            {
                offsets.Add(datapointer);
                datapointer += skinwidth * skinheight;
                m.TotalSkins++;
            }
        }
        m.SkinOffsets = offsets.ToArray();
        return m;
    }

    // ---- palette.c --------------------------------------------------------------------------------

    /// <summary>The special palettes of Palette_SetupSpecialPalettes, each entry four bytes B, G, R, A as DarkPlaces packs them.</summary>
    internal sealed class Palettes
    {
        public readonly byte[] Complete = new byte[1024], Transparent = new byte[1024], NoFullbrights = new byte[1024],
            NoFullbrightsTransparent = new byte[1024], OnlyFullbrights = new byte[1024], OnlyFullbrightsTransparent = new byte[1024],
            NoColormap = new byte[1024], NoColormapNoFullbrights = new byte[1024], PantsAsWhite = new byte[1024], ShirtAsWhite = new byte[1024];
        public readonly byte[] FeatureFlags = new byte[256];
        public const byte Standard = 1, Reversed = 2, Pants = 4, Shirt = 8, Glow = 16, Zero = 32, TransparentFeature = 64;

        /// <param name="rgb">palette_rgb: 768 bytes.</param>
        /// <param name="colormap">gfx/colormap.lmp, or null.</param>
        public Palettes(byte[] rgb, byte[]? colormap)
        {
            for (int i = 0; i < 256; i++)
            {
                Complete[i * 4 + 2] = rgb[i * 3 + 0];
                Complete[i * 4 + 1] = rgb[i * 3 + 1];
                Complete[i * 4 + 0] = rgb[i * 3 + 2];
                Complete[i * 4 + 3] = 255;
            }
            int fullbright_start = colormap is not null && colormap.Length >= 16385 ? 256 - colormap[16384] : 256;
            const int fullbright_end = 256, pants_start = 96, pants_end = 112, shirt_start = 16, shirt_end = 32,
                reversed_start = 128, reversed_end = 224, transparentcolor = 255;

            for (int i = 0; i < 256; i++) FeatureFlags[i] = Standard;
            for (int i = reversed_start; i < reversed_end; i++) FeatureFlags[i] = Reversed;
            for (int i = pants_start; i < pants_end; i++) FeatureFlags[i] = Pants;
            for (int i = shirt_start; i < shirt_end; i++) FeatureFlags[i] = Shirt;
            for (int i = fullbright_start; i < fullbright_end; i++) FeatureFlags[i] = Glow;
            FeatureFlags[0] = Zero;
            FeatureFlags[transparentcolor] = TransparentFeature;

            for (int i = 0; i < 256; i++) Set(Transparent, i, Complete, i);
            Clear(Transparent, transparentcolor);

            for (int i = 0; i < fullbright_start; i++) Set(NoFullbrights, i, Complete, i);
            for (int i = fullbright_start; i < fullbright_end; i++) Set(NoFullbrights, i, Complete, 0);
            for (int i = 0; i < 256; i++) Set(NoFullbrightsTransparent, i, NoFullbrights, i);
            Clear(NoFullbrightsTransparent, transparentcolor);

            for (int i = fullbright_start; i < fullbright_end; i++) Set(OnlyFullbrights, i, Complete, i);
            for (int i = 0; i < 256; i++) Set(OnlyFullbrightsTransparent, i, OnlyFullbrights, i);
            Clear(OnlyFullbrightsTransparent, transparentcolor);

            for (int i = 0; i < 256; i++) Set(NoColormapNoFullbrights, i, Complete, i);
            for (int i = pants_start; i < pants_end; i++) Clear(NoColormapNoFullbrights, i);
            for (int i = shirt_start; i < shirt_end; i++) Clear(NoColormapNoFullbrights, i);
            for (int i = fullbright_start; i < fullbright_end; i++) Clear(NoColormapNoFullbrights, i);

            for (int i = 0; i < 256; i++) Set(NoColormap, i, Complete, i);
            for (int i = pants_start; i < pants_end; i++) Clear(NoColormap, i);
            for (int i = shirt_start; i < shirt_end; i++) Clear(NoColormap, i);

            for (int i = pants_start; i < pants_end; i++)
                Set(PantsAsWhite, i, Complete, i >= reversed_start && i < reversed_end ? 15 - (i - pants_start) : i - pants_start);
            for (int i = shirt_start; i < shirt_end; i++)
                Set(ShirtAsWhite, i, Complete, i >= reversed_start && i < reversed_end ? 15 - (i - shirt_start) : i - shirt_start);
        }

        private static void Set(byte[] to, int i, byte[] from, int j) => Buffer.BlockCopy(from, j * 4, to, i * 4, 4);
        private static void Clear(byte[] to, int i) => to[i * 4] = to[i * 4 + 1] = to[i * 4 + 2] = to[i * 4 + 3] = 0;

        /// <summary>Image_Copy8bitBGRA, then the bytes swapped to R, G, B, A for comparison with this client's textures.</summary>
        public static byte[] Copy8bitAsRgba(byte[] file, int offset, int count, byte[] palette)
        {
            byte[] outp = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                int p = file[offset + i] * 4;
                outp[i * 4 + 0] = palette[p + 2];
                outp[i * 4 + 1] = palette[p + 1];
                outp[i * 4 + 2] = palette[p + 0];
                outp[i * 4 + 3] = palette[p + 3];
            }
            return outp;
        }
    }

    /// <summary>What R_SkinFrame_LoadInternalQuake + R_SkinFrame_GenerateTexturesFromQPixels upload for one skin (RGBA).</summary>
    internal sealed class QuakeSkin
    {
        public bool HasColormapping, HasGlow;
        public byte[] Merged = Array.Empty<byte>();
        public byte[]? Glow, Base, Pants, Shirt;
    }

    /// <param name="fence">The texture's name starts with '{' (never the case for a model skin).</param>
    public static QuakeSkin InternalQuakeSkin(byte[] file, int offset, int width, int height, Palettes pal, bool fence = false, bool loadglowtexture = true)
    {
        int featuresmask = 0;
        for (int i = 0; i < width * height; i++)
            featuresmask |= pal.FeatureFlags[file[offset + i]];
        var s = new QuakeSkin
        {
            HasColormapping = (featuresmask & (Palettes.Pants | Palettes.Shirt)) != 0,
            HasGlow = loadglowtexture && (featuresmask & Palettes.Glow) != 0,
        };
        int n = width * height;
        if (s.HasGlow)
            s.Glow = Palettes.Copy8bitAsRgba(file, offset, n, fence ? pal.OnlyFullbrightsTransparent : pal.OnlyFullbrights);
        if (s.HasColormapping)
        {
            s.Base = Palettes.Copy8bitAsRgba(file, offset, n, s.HasGlow ? pal.NoColormapNoFullbrights : pal.NoColormap);
            s.Pants = Palettes.Copy8bitAsRgba(file, offset, n, pal.PantsAsWhite);
            s.Shirt = Palettes.Copy8bitAsRgba(file, offset, n, pal.ShirtAsWhite);
        }
        s.Merged = fence
            ? Palettes.Copy8bitAsRgba(file, offset, n, s.HasGlow ? pal.NoFullbrightsTransparent : pal.Transparent)
            : Palettes.Copy8bitAsRgba(file, offset, n, s.HasGlow ? pal.NoFullbrights : pal.Complete);
        return s;
    }

    // ---- model_sprite.c -----------------------------------------------------------------------------

    internal struct SpriteFrame
    {
        public float Left, Right, Up, Down;
        public int Width, Height;
        /// <summary>The picture as uploaded, R, G, B, A; null for a frame without pixels.</summary>
        public byte[]? Pixels;
        public bool HasAlpha;
    }

    internal sealed class Sprite
    {
        public int NumFrames, Type, SyncType, Version;
        public bool Additive;
        public AnimScene[] AnimScenes = Array.Empty<AnimScene>();
        public SpriteFrame[] Frames = Array.Empty<SpriteFrame>();
        public float Radius;
    }

    private const int SizeofDsprite = 36, SizeofDspritehl = 40, SpriteMaxFrameSize = 8192;

    public static Sprite LoadIdsp(byte[] buffer, Palettes quake)
    {
        var m = new Sprite();
        int datapointer = 0;
        int version = LittleLong(buffer, 4);
        m.Version = version;
        if (version == 1 || version == 32)
        {
            if (buffer.Length < SizeofDsprite) throw new InvalidOperationException("truncated");
            datapointer += SizeofDsprite;
            m.NumFrames = LittleLong(buffer, 24);
            m.Type = LittleLong(buffer, 8);
            m.SyncType = LittleLong(buffer, 32);
            SharedSetup(m, buffer, datapointer, version, null, false, quake);
        }
        else if (version == 2)
        {
            if (buffer.Length < SizeofDspritehl + 2 + 768) throw new InvalidOperationException("truncated");
            datapointer += SizeofDspritehl;
            m.NumFrames = LittleLong(buffer, 28);
            m.Type = LittleLong(buffer, 8);
            m.SyncType = LittleLong(buffer, 36);
            int rendermode = LittleLong(buffer, 12);
            int inp = datapointer;
            datapointer += 2;
            int count = buffer[inp] + buffer[inp + 1] * 256;
            if (count != 256) throw new InvalidOperationException("unexpected number of palette colors");
            inp = datapointer;
            datapointer += 768;
            byte[] palette = new byte[1024];        // B, G, R, A
            switch (rendermode)
            {
                case 0:
                case 1:
                case 3:
                    for (int i = 0; i < 256; i++)
                    {
                        palette[i * 4 + 2] = buffer[inp + i * 3 + 0];
                        palette[i * 4 + 1] = buffer[inp + i * 3 + 1];
                        palette[i * 4 + 0] = buffer[inp + i * 3 + 2];
                        palette[i * 4 + 3] = 255;
                    }
                    if (rendermode == 3)
                        palette[255 * 4] = palette[255 * 4 + 1] = palette[255 * 4 + 2] = palette[255 * 4 + 3] = 0;
                    break;
                case 2:
                    // The C also advances `in` by three per entry while indexing 765..767 from it, so it reads
                    // past the palette for every entry but the first; the evident intent is transcribed here.
                    for (int i = 0; i < 256; i++)
                    {
                        palette[i * 4 + 2] = buffer[inp + 765];
                        palette[i * 4 + 1] = buffer[inp + 766];
                        palette[i * 4 + 0] = buffer[inp + 767];
                        palette[i * 4 + 3] = (byte)i;
                    }
                    break;
                default:
                    throw new InvalidOperationException("unknown texFormat");
            }
            SharedSetup(m, buffer, datapointer, version, palette, rendermode == 1, quake);
        }
        else
            throw new InvalidOperationException("wrong version number");
        return m;
    }

    private static void SharedSetup(Sprite m, byte[] buffer, int startframes, int version, byte[]? palette, bool additive, Palettes quake)
    {
        m.Additive = additive;
        if (m.NumFrames < 1) throw new InvalidOperationException("Invalid # of frames");
        int datapointer = startframes;
        int dataend = buffer.Length;
        void Need(long n) { if (datapointer > dataend || dataend - datapointer < n) throw new InvalidOperationException("truncated or corrupt"); }

        int realframes = 0;
        for (int i = 0; i < m.NumFrames; i++)
        {
            Need(4);
            int type = LittleLong(buffer, datapointer);
            datapointer += 4;
            int groupframes;
            if (type == 0)
                groupframes = 1;
            else
            {
                Need(4);
                groupframes = LittleLong(buffer, datapointer);
                datapointer += 4;
                if (groupframes < 1 || groupframes > (dataend - datapointer) / 4) throw new InvalidOperationException("invalid group frame count");
                datapointer += 4 * groupframes;
            }
            for (int j = 0; j < groupframes; j++)
            {
                Need(16);
                int framewidth = LittleLong(buffer, datapointer + 8), frameheight = LittleLong(buffer, datapointer + 12);
                datapointer += 16;
                if (framewidth < 0 || frameheight < 0 || framewidth > SpriteMaxFrameSize || frameheight > SpriteMaxFrameSize)
                    throw new InvalidOperationException("invalid frame size");
                Need((long)framewidth * frameheight * (version == 32 ? 4 : 1));
                datapointer += framewidth * frameheight * (version == 32 ? 4 : 1);
            }
            realframes += groupframes;
        }

        m.AnimScenes = new AnimScene[m.NumFrames];
        m.Frames = new SpriteFrame[realframes];
        datapointer = startframes;
        realframes = 0;
        float modelradius = 0;
        for (int i = 0; i < m.NumFrames; i++)
        {
            int type = LittleLong(buffer, datapointer);
            datapointer += 4;
            int groupframes;
            float interval;
            if (type == 0)
            {
                groupframes = 1;
                interval = 0.1f;
            }
            else
            {
                groupframes = LittleLong(buffer, datapointer);
                datapointer += 4;
                interval = LittleFloat(buffer, datapointer);
                datapointer += 4 * groupframes;
                if (interval < 0.01f) throw new InvalidOperationException("invalid interval");
            }
            m.AnimScenes[i].Name = "frame " + i;
            m.AnimScenes[i].FirstFrame = realframes;
            m.AnimScenes[i].FrameCount = groupframes;
            m.AnimScenes[i].FrameRate = 1.0f / interval;
            m.AnimScenes[i].Loop = true;

            for (int j = 0; j < groupframes; j++)
            {
                int origin0 = LittleLong(buffer, datapointer), origin1 = LittleLong(buffer, datapointer + 4);
                int width = LittleLong(buffer, datapointer + 8), height = LittleLong(buffer, datapointer + 12);
                datapointer += 16;
                ref SpriteFrame f = ref m.Frames[realframes];
                f.Left = origin0;
                f.Right = origin0 + width;
                f.Up = origin1;
                f.Down = origin1 - height;
                f.Width = width;
                f.Height = height;
                int x = (int)Math.Max(f.Left * f.Left, f.Right * f.Right);
                int y = (int)Math.Max(f.Up * f.Up, f.Down * f.Down);
                if (modelradius < x + y)
                    modelradius = x + y;
                if (width > 0 && height > 0)
                {
                    byte[] pixels;
                    if (version == 32)
                    {
                        pixels = new byte[width * height * 4];
                        for (int k = 0; k < width * height; k++)
                        {
                            // "pixels[x*4+2] = datapointer[x*4+0]; pixels[x*4+0] = datapointer[x*4+2]": the C's buffer
                            // is uploaded as BGRA (R_SkinFrame_LoadInternalBGRA), so its byte 2 - file byte 0 - is red.
                            // Written here in R, G, B, A order: red = file byte 0.
                            pixels[k * 4 + 0] = buffer[datapointer + k * 4 + 0];
                            pixels[k * 4 + 1] = buffer[datapointer + k * 4 + 1];
                            pixels[k * 4 + 2] = buffer[datapointer + k * 4 + 2];
                            pixels[k * 4 + 3] = buffer[datapointer + k * 4 + 3];
                        }
                    }
                    else
                        pixels = Palettes.Copy8bitAsRgba(buffer, datapointer, width * height, palette ?? quake.Transparent);
                    f.Pixels = pixels;
                    for (int k = 3; k < pixels.Length; k += 4)
                        if (pixels[k] < 255) { f.HasAlpha = true; break; }
                }
                datapointer += version == 32 ? width * height * 4 : width * height;
                realframes++;
            }
        }
        m.Radius = (float)Math.Sqrt(modelradius);
    }
}
