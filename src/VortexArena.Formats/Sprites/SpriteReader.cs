namespace XonoticGodot.Formats.Sprites;

/// <summary>
/// Parses Quake-family sprite files into <see cref="SpriteData"/>. Faithful to Darkplaces
/// <c>Mod_IDSP_Load</c> / <c>Mod_IDS2_Load</c> / <c>Mod_Sprite_SharedSetup</c> in <c>model_sprite.c</c>
/// and the on-disk structs in <c>spritegn.h</c>.
///
/// Magic dispatch:
/// <list type="bullet">
///   <item>"IDSP" -&gt; read <c>version</c>: 1 = Quake spr, 2 = Half-Life spr, 32 = spr32.</item>
///   <item>"IDS2" -&gt; Quake2 .sp2 (version must be 2).</item>
/// </list>
///
/// All multi-byte fields are little-endian (DP passes every field through LittleLong/LittleFloat).
/// Every read is bounds-checked via <see cref="BinaryUtil"/>; malformed input throws
/// <see cref="AssetParseException"/> rather than overrunning the buffer.
///
/// Pixel handling:
/// <list type="bullet">
///   <item><b>spr32</b>: source is BGRA; we swap to RGBA (DP does the same B↔R swap).</item>
///   <item><b>sprhl</b>: source is 8-bit indices into the file's embedded 256-color palette; we expand to RGBA
///         using that palette and the rendermode's alpha rules.</item>
///   <item><b>spr</b> (Quake v1): source is 8-bit indices into the *external* Quake palette, which is NOT in the
///         file. We do not embed a palette here, so frames keep their raw indices in
///         <see cref="SpriteFrame.Indices"/>. TODO(host): colour these through gfx/palette.lmp
///         (palette_bgra_complete) when building the texture.</item>
///   <item><b>sp2</b>: no pixels; each frame carries an external image name to resolve via the VFS.</item>
/// </list>
/// </summary>
public static class SpriteReader
{
    // Magic tags.
    private const string MagicIdsp = "IDSP";
    private const string MagicIds2 = "IDS2";

    // Versions (spritegn.h).
    private const int SpriteVersion = 1;     // Quake
    private const int SpriteHlVersion = 2;   // Half-Life
    private const int Sprite32Version = 32;  // spr32
    private const int Sprite2Version = 2;    // Quake2 (.sp2)

    // Frame slot type (spriteframetype_t).
    private const int SprSingle = 0;
    private const int SprGroup = 1;

    // On-disk header sizes (bytes), little-endian.
    private const int DSpriteHeaderSize = 36;   // ident+version+type+boundingradius+width+height+numframes+beamlength+synctype
    private const int DSpriteHlHeaderSize = 40;  // dsprite_t + rendermode (after type)
    private const int DSprite2HeaderSize = 12;   // ident+version+numframes
    private const int DSprite2FrameSize = 80;    // width+height+origin_x+origin_y+name[64]
    private const int DSpriteFrameSize = 16;     // origin[2]+width+height
    private const int Sp2NameLen = 64;

    private const int MaxReasonableFrames = 1 << 20; // guard against hostile counts

    public static SpriteData Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(new ReadOnlySpan<byte>(data));
    }

    public static SpriteData Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new AssetParseException($"Sprite too small: {data.Length} bytes, need at least 8 for magic+version.");

        string magic = BinaryUtil.ReadMagic(data, 0);
        return magic switch
        {
            MagicIdsp => ReadIdsp(data),
            MagicIds2 => ReadIds2(data),
            _ => throw new AssetParseException(
                $"Not a sprite file: magic is \"{magic}\", expected \"{MagicIdsp}\" (spr/spr32/hl) or \"{MagicIds2}\" (sp2)."),
        };
    }

    // ------------------------------------------------------------------ IDSP

    private static SpriteData ReadIdsp(ReadOnlySpan<byte> data)
    {
        int version = BinaryUtil.ReadInt32(data, 4);
        return version switch
        {
            SpriteVersion or Sprite32Version => ReadQuakeSprite(data, version),
            SpriteHlVersion => ReadHlSprite(data),
            _ => throw new AssetParseException(
                $"IDSP sprite has unsupported version {version} (expected 1=quake, 2=halflife, or 32=spr32)."),
        };
    }

    /// <summary>Reads an "IDSP" v1 (paletted) or v32 (BGRA) sprite via the shared frame walker.</summary>
    private static SpriteData ReadQuakeSprite(ReadOnlySpan<byte> data, int version)
    {
        // dsprite_t header.
        if (data.Length < DSpriteHeaderSize)
            throw new AssetParseException($"IDSP sprite truncated: {data.Length} bytes, need {DSpriteHeaderSize} for header.");

        int type = BinaryUtil.ReadInt32(data, 8);
        // [12] boundingradius, [16] width, [20] height -- not needed (per-frame sizes are authoritative).
        int numFrames = BinaryUtil.ReadInt32(data, 24);
        // [28] beamlength, [32] synctype -- unused here.

        (var frames, var groups) = ReadIdspFrames(data, DSpriteHeaderSize, numFrames, version, palette: null,
            additive: false, format: version == Sprite32Version ? SpriteFormat.Spr32 : SpriteFormat.Spr);

        return new SpriteData
        {
            Format = version == Sprite32Version ? SpriteFormat.Spr32 : SpriteFormat.Spr,
            SpriteType = ToSpriteType(type),
            HlRenderMode = SpriteHlRenderMode.Opaque,
            Additive = false,
            Frames = frames,
            GroupRanges = groups,
        };
    }

    /// <summary>Reads an "IDSP" v2 Half-Life sprite: header + embedded 256-color palette, then frames.</summary>
    private static SpriteData ReadHlSprite(ReadOnlySpan<byte> data)
    {
        if (data.Length < DSpriteHlHeaderSize)
            throw new AssetParseException($"HL sprite truncated: {data.Length} bytes, need {DSpriteHlHeaderSize} for header.");

        int type = BinaryUtil.ReadInt32(data, 8);
        int rendermode = BinaryUtil.ReadInt32(data, 12);
        int numFrames = BinaryUtil.ReadInt32(data, 28);

        int p = DSpriteHlHeaderSize;
        // Palette color count: 2-byte little-endian, must be 256.
        int paletteColors = BinaryUtil.ReadInt16(data, p) & 0xFFFF;
        p += 2;
        if (paletteColors != 256)
            throw new AssetParseException($"HL sprite: unexpected palette color count {paletteColors} (must be 256).");

        // 768-byte RGB palette follows.
        BinaryUtilRequire(data, p, 768, "HL palette");
        var palette = BuildHlPalette(data, p, rendermode);
        p += 768;

        bool additive = rendermode == (int)SpriteHlRenderMode.Additive;

        (var frames, var groups) = ReadIdspFrames(data, p, numFrames, SpriteHlVersion, palette,
            additive, SpriteFormat.SprHl);

        return new SpriteData
        {
            Format = SpriteFormat.SprHl,
            SpriteType = ToSpriteType(type),
            HlRenderMode = ToHlRenderMode(rendermode),
            Additive = additive,
            Frames = frames,
            GroupRanges = groups,
        };
    }

    /// <summary>
    /// Shared frame walker for the three "IDSP" variants. Mirrors <c>Mod_Sprite_SharedSetup</c>: each of the
    /// <paramref name="numFrames"/> top-level slots is either a single image (<c>SPR_SINGLE</c>) or a group
    /// (<c>SPR_GROUP</c>) of N images preceded by N intervals; we flatten groups into individual frames and
    /// record the grouping. <paramref name="version"/> selects the pixel stride (1 byte vs 4 bytes per texel).
    /// </summary>
    private static (SpriteFrame[] frames, SpriteGroup[] groups) ReadIdspFrames(
        ReadOnlySpan<byte> data, int offset, int numFrames, int version,
        uint[]? palette, bool additive, SpriteFormat format)
    {
        if (numFrames < 1)
            throw new AssetParseException($"IDSP sprite: invalid frame count {numFrames} (need >= 1).");
        if (numFrames > MaxReasonableFrames)
            throw new AssetParseException($"IDSP sprite: implausible frame count {numFrames}.");

        int bytesPerTexel = version == Sprite32Version ? 4 : 1;

        var frames = new List<SpriteFrame>(numFrames);
        var groups = new SpriteGroup[numFrames];

        int o = offset;
        for (int i = 0; i < numFrames; i++)
        {
            int slotType = BinaryUtil.ReadInt32(data, o);
            o += 4;

            int groupFrames;
            float[] intervals;
            if (slotType == SprSingle)
            {
                groupFrames = 1;
                intervals = Array.Empty<float>();
            }
            else
            {
                // dspritegroup_t { numframes }, then numframes * dspriteinterval_t { float interval }.
                groupFrames = BinaryUtil.ReadInt32(data, o);
                o += 4;
                if (groupFrames < 1 || groupFrames > MaxReasonableFrames)
                    throw new AssetParseException($"IDSP sprite: invalid group frame count {groupFrames} at slot {i}.");
                intervals = new float[groupFrames];
                for (int k = 0; k < groupFrames; k++)
                {
                    // DP treats an interval < 0.01 as corrupt (Host_Error). We store the raw value verbatim
                    // and leave that policy to the host, so a slightly-off interval doesn't reject the whole file.
                    intervals[k] = BinaryUtil.ReadFloat(data, o);
                    o += 4;
                }
            }

            int firstFlat = frames.Count;
            for (int j = 0; j < groupFrames; j++)
            {
                // dspriteframe_t { int origin[2]; int width; int height; }
                int originX = BinaryUtil.ReadInt32(data, o);
                int originY = BinaryUtil.ReadInt32(data, o + 4);
                int width = BinaryUtil.ReadInt32(data, o + 8);
                int height = BinaryUtil.ReadInt32(data, o + 12);
                o += DSpriteFrameSize;

                if (width < 0 || height < 0)
                    throw new AssetParseException($"IDSP sprite: negative frame size {width}x{height} at slot {i}.");

                long byteCount = (long)width * height * bytesPerTexel;
                // Bounds-check the pixel block up-front, comparing in long to avoid int overflow on hostile sizes.
                if ((long)o + byteCount > data.Length)
                    throw new AssetParseException(
                        $"Truncated sprite reading frame {i}/{j} pixels: need {byteCount} byte(s) at offset {o}, buffer is {data.Length} byte(s).");

                SpriteFrame frame = DecodeFrame(data, o, originX, originY, width, height, version, palette, format);
                frames.Add(frame);

                o += (int)byteCount;
            }

            groups[i] = new SpriteGroup(firstFlat, groupFrames, intervals);
        }

        return (frames.ToArray(), groups);
    }

    /// <summary>Decodes one frame's pixels per the format (BGRA→RGBA, HL palette expand, or raw indices).</summary>
    private static SpriteFrame DecodeFrame(
        ReadOnlySpan<byte> data, int pixelOffset, int originX, int originY, int width, int height,
        int version, uint[]? palette, SpriteFormat format)
    {
        int pixelCount = width * height;

        if (version == Sprite32Version)
        {
            // Source BGRA -> RGBA (swap byte 0 and 2). DP: pixels[x*4+0]=src[+2], +1=+1, +2=+0, +3=+3.
            var rgba = new byte[pixelCount * 4];
            for (int x = 0; x < pixelCount; x++)
            {
                int s = pixelOffset + x * 4;
                rgba[x * 4 + 0] = data[s + 2]; // R <- B
                rgba[x * 4 + 1] = data[s + 1]; // G
                rgba[x * 4 + 2] = data[s + 0]; // B <- R
                rgba[x * 4 + 3] = data[s + 3]; // A
            }
            return new SpriteFrame { OriginX = originX, OriginY = originY, Width = width, Height = height, Rgba = rgba };
        }

        if (format == SpriteFormat.SprHl && palette != null)
        {
            // 8-bit indices -> embedded palette (already stored as packed RGBA uint, see BuildHlPalette).
            var rgba = new byte[pixelCount * 4];
            for (int x = 0; x < pixelCount; x++)
            {
                uint c = palette[data[pixelOffset + x]];
                rgba[x * 4 + 0] = (byte)(c & 0xFF);
                rgba[x * 4 + 1] = (byte)((c >> 8) & 0xFF);
                rgba[x * 4 + 2] = (byte)((c >> 16) & 0xFF);
                rgba[x * 4 + 3] = (byte)((c >> 24) & 0xFF);
            }
            return new SpriteFrame { OriginX = originX, OriginY = originY, Width = width, Height = height, Rgba = rgba };
        }

        // Plain Quake spr (v1): the palette lives outside the file. Keep raw indices for the host to colour.
        // TODO(host): expand through the Quake palette (gfx/palette.lmp) when building the texture.
        var indices = new byte[pixelCount];
        data.Slice(pixelOffset, pixelCount).CopyTo(indices);
        return new SpriteFrame { OriginX = originX, OriginY = originY, Width = width, Height = height, Indices = indices };
    }

    /// <summary>
    /// Builds a 256-entry RGBA palette (packed 0xAABBGGRR, i.e. R in the low byte) from the HL sprite's
    /// 768-byte RGB table, applying the rendermode alpha rules from <c>Mod_IDSP_Load</c>.
    /// </summary>
    private static uint[] BuildHlPalette(ReadOnlySpan<byte> data, int offset, int rendermode)
    {
        var pal = new uint[256];
        switch (rendermode)
        {
            case (int)SpriteHlRenderMode.Opaque:
            case (int)SpriteHlRenderMode.Additive:
                for (int i = 0; i < 256; i++)
                {
                    byte r = data[offset + i * 3 + 0];
                    byte g = data[offset + i * 3 + 1];
                    byte b = data[offset + i * 3 + 2];
                    pal[i] = Pack(r, g, b, 255);
                }
                break;
            case (int)SpriteHlRenderMode.IndexAlpha:
                // Color is the last palette entry; alpha ramps with the index.
                {
                    byte r = data[offset + 765];
                    byte g = data[offset + 766];
                    byte b = data[offset + 767];
                    for (int i = 0; i < 256; i++)
                        pal[i] = Pack(r, g, b, (byte)i);
                }
                break;
            case (int)SpriteHlRenderMode.AlphaTest:
                for (int i = 0; i < 256; i++)
                {
                    byte r = data[offset + i * 3 + 0];
                    byte g = data[offset + i * 3 + 1];
                    byte b = data[offset + i * 3 + 2];
                    pal[i] = Pack(r, g, b, 255);
                }
                pal[255] = 0; // index 255 fully transparent
                break;
            default:
                throw new AssetParseException($"HL sprite: unknown rendermode {rendermode} (expected 0-3).");
        }
        return pal;
    }

    private static uint Pack(byte r, byte g, byte b, byte a) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));

    // ------------------------------------------------------------------ IDS2 (sp2)

    /// <summary>Reads a Quake2 ".sp2" sprite: header + flat array of external-image frame references.</summary>
    private static SpriteData ReadIds2(ReadOnlySpan<byte> data)
    {
        if (data.Length < DSprite2HeaderSize)
            throw new AssetParseException($"IDS2 sprite truncated: {data.Length} bytes, need {DSprite2HeaderSize} for header.");

        int version = BinaryUtil.ReadInt32(data, 4);
        if (version != Sprite2Version)
            throw new AssetParseException($"IDS2 sprite has wrong version {version} (expected {Sprite2Version}).");

        int numFrames = BinaryUtil.ReadInt32(data, 8);
        if (numFrames < 1)
            throw new AssetParseException($"IDS2 sprite: invalid frame count {numFrames} (need >= 1).");
        if (numFrames > MaxReasonableFrames)
            throw new AssetParseException($"IDS2 sprite: implausible frame count {numFrames}.");

        var frames = new SpriteFrame[numFrames];
        var groups = new SpriteGroup[numFrames];

        int o = DSprite2HeaderSize;
        for (int i = 0; i < numFrames; i++)
        {
            // dsprite2frame_t { int width, height; int origin_x, origin_y; char name[64]; }
            int width = BinaryUtil.ReadInt32(data, o);
            int height = BinaryUtil.ReadInt32(data, o + 4);
            int originX = BinaryUtil.ReadInt32(data, o + 8);
            int originY = BinaryUtil.ReadInt32(data, o + 12);
            string name = BinaryUtil.ReadFixedString(data, o + 16, Sp2NameLen);
            o += DSprite2FrameSize;

            // sp2 origin_x sign is opposite spr/spr32; negate so the quad derivation in SpriteFrame is uniform
            // (DP: sprframe->left = -origin[0]). origin_y keeps its sign.
            frames[i] = new SpriteFrame
            {
                OriginX = -originX,
                OriginY = originY,
                Width = width,
                Height = height,
                ExternalImage = name,
            };
            groups[i] = new SpriteGroup(i, 1, Array.Empty<float>());
        }

        return new SpriteData
        {
            Format = SpriteFormat.Sp2,
            // DP forces sp2 sprites to SPR_VP_PARALLEL regardless of any stored type.
            SpriteType = SpriteType.VpParallel,
            HlRenderMode = SpriteHlRenderMode.Opaque,
            Additive = false,
            Frames = frames,
            GroupRanges = groups,
        };
    }

    // ------------------------------------------------------------------ helpers

    private static SpriteType ToSpriteType(int t) =>
        t is >= 0 and <= 7 ? (SpriteType)t : SpriteType.VpParallel;

    private static SpriteHlRenderMode ToHlRenderMode(int m) =>
        m is >= 0 and <= 3 ? (SpriteHlRenderMode)m : SpriteHlRenderMode.Opaque;

    /// <summary>
    /// Bounds check helper that throws <see cref="AssetParseException"/> (BinaryUtil keeps its Require private,
    /// so we re-implement the same contract here for the variable-length pixel/palette blocks).
    /// </summary>
    private static void BinaryUtilRequire(ReadOnlySpan<byte> data, int offset, int count, string what)
    {
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new AssetParseException(
                $"Truncated sprite reading {what}: need {count} byte(s) at offset {offset}, buffer is {data.Length} byte(s).");
    }
}
