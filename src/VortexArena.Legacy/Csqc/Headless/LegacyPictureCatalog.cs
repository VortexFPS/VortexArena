// Port of Base/darkplaces/gl_draw.c Draw_CachePic_Flags, Draw_IsPicLoaded, Draw_GetPicWidth / Height,
// Draw_NewPic and Draw_FreePic - the cache of 2D pictures, as far as their existence and size go - with
// image.c Image_GetStockPicSize and the headers of the formats image.c loads (LoadTGA_BGRA, PNG_LoadImage_BGRA,
// JPEG_LoadImage_BGRA, LoadPCX_BGRA) and gl_textures.c R_LoadTextureDDSFile.
using System.Buffers.Binary;
using VortexArena.Formats.Vfs;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// Which 2D pictures exist and how large they are, answered from file headers: no pixel is decoded
/// and no texture made. It is the half of DarkPlaces' picture cache a client program can observe -
/// through precache_pic (does it load?) and draw_getimagesize (how big?) - and a renderer's draw
/// calls can ask it the same things.
///
/// The cache has to be reproduced and not just the lookup, because what DarkPlaces answers depends
/// on what was asked before:
/// <list type="bullet">
/// <item>draw_getimagesize of a picture that does not exist answers 16 x 16 - the size of the
/// checkerboard "notexture" DarkPlaces substitutes - and from then on the picture counts as loaded,
/// so a later precache_pic of the same name succeeds.</item>
/// <item>precache_pic of a missing picture fails (CACHEPICFLAG_FAILONMISSING) and leaves an entry
/// with no texture, for which draw_getimagesize then answers 0 x 0.</item>
/// </list>
/// Stock Xonotic code leans on neither, but a HUD skin that does not ship a picture meets the first.
///
/// Names come from the program; file contents from whatever the server made the client download.
/// Reads are bounded, sizes are range-checked, the cache is bounded.
/// </summary>
public sealed class LegacyPictureCatalog
{
    /// <summary>MAX_CACHED_PICS. A full cache answers further new names without remembering them
    /// (DarkPlaces answers with its first picture, which is no better).</summary>
    public const int MaxPictures = 2048;
    /// <summary>How much of a file is read to find its dimensions. A JPEG keeps them after its
    /// application segments (EXIF, an embedded thumbnail); everything else has them in the first bytes.</summary>
    public const int MaxHeaderBytes = 256 * 1024;
    /// <summary>The checkerboard texture's side (image.c Image_GenerateNoTexture).</summary>
    public const int NoTextureSize = 16;

    private readonly VirtualFileSystem _files;
    private readonly Dictionary<string, Picture> _pictures = new(StringComparer.Ordinal);

    private struct Picture
    {
        public bool HasTexture;      // pic->skinframe && pic->skinframe->base
        public bool FailOnMissing;   // pic->flags & CACHEPICFLAG_FAILONMISSING
        public bool Defined;         // CACHEPICFLAG_NEWPIC: made by Draw_NewPic, never reloaded from disk
        public int Width, Height;
    }

    public LegacyPictureCatalog(VirtualFileSystem files) => _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <summary>Pictures currently cached.</summary>
    public int Count => _pictures.Count;

    /// <summary>#317 precache_pic: Draw_IsPicLoaded(Draw_CachePic_Flags(name, CACHEPICFLAG_FAILONMISSING | CACHEPICFLAG_QUIET)).</summary>
    public bool Precache(string name) => Cache(name, failOnMissing: true).HasTexture;

    /// <summary>#318 draw_getimagesize: Draw_CachePic_Flags(name, CACHEPICFLAG_QUIET | CACHEPICFLAG_NOTPERSISTENT),
    /// then the picture's size if Draw_IsPicLoaded and 0 x 0 if not.</summary>
    public (int Width, int Height) Size(string name)
    {
        Picture picture = Cache(name, failOnMissing: false);
        return picture.HasTexture ? (picture.Width, picture.Height) : (0, 0);
    }

    /// <summary>Whether the picture's own file exists and has a readable header, with its size,
    /// leaving the cache alone: what a renderer asks before it loads the pixels.</summary>
    public bool TryGetFileSize(string name, out int width, out int height) => ReadSize(name, out width, out height);

    /// <summary>Draw_NewPic (#501 ReadPicture when the picture does not exist): a picture given as
    /// data. It replaces any entry of that name, and is never reloaded from disk.</summary>
    public void Define(string name, int width, int height)
    {
        if (name.Length == 0 || (!_pictures.ContainsKey(name) && _pictures.Count >= MaxPictures)) return;
        _pictures[name] = new Picture { HasTexture = true, Defined = true, Width = width, Height = height };
    }

    /// <summary>Draw_NewPic from an encoded image: only the header is read. False if it has none this can read.</summary>
    public bool Define(string name, ReadOnlySpan<byte> image)
    {
        if (!TryReadImageSize(image, out int width, out int height)) return false;
        Define(name, width, height);
        return true;
    }

    /// <summary>
    /// Draw_FreePic (#319 freepic). DarkPlaces unloads the texture and marks the picture to be
    /// loaded again on next use, which no query can tell from the entry not being there.
    /// </summary>
    public void Free(string name) => _pictures.Remove(name);

    /// <summary>CL_ClearState and a renderer restart: every picture is looked up afresh.</summary>
    public void Clear() => _pictures.Clear();

    private Picture Cache(string name, bool failOnMissing)
    {
        if (name.Length == 0) return default;
        // "check whether the picture has already been cached"
        if (_pictures.TryGetValue(name, out Picture cached))
        {
            // "if it was created (or replaced) by Draw_NewPic, just return it"
            if (cached.Defined || cached.HasTexture) return cached;
            // No texture: it was looked for with FAILONMISSING and not found. "return NULL".
            if (cached.FailOnMissing) return cached;
        }

        Picture picture = new() { FailOnMissing = failOnMissing };
        // "load high quality image (this falls back to low quality too)"
        if (ReadSize(name, out picture.Width, out picture.Height)) picture.HasTexture = true;
        else if (!failOnMissing)
        {
            // R_SkinFrame_LoadExternal with fallbacknotexture: the checkerboard stands in.
            picture.HasTexture = true;
            picture.Width = picture.Height = NoTextureSize;
        }
        // "check for a low quality version of the pic and use its size if possible, to match the stock hud"
        StockPicSize(name, ref picture.Width, ref picture.Height);
        if (_pictures.Count < MaxPictures || _pictures.ContainsKey(name)) _pictures[name] = picture;
        return picture;
    }

    // Image_GetStockPicSize: Quake's own HUD pictures are .lmp files, and a replacement picture is
    // drawn at the size of the original it replaces.
    private void StockPicSize(string name, ref int width, ref int height)
    {
        if (string.Equals(name, "gfx/conchars", StringComparison.OrdinalIgnoreCase))
        {
            width = height = 128;
            return;
        }
        string lmp = name + ".lmp";
        if (!LegacyQcHost.IsSafePath(lmp) || !_files.Exists(lmp)) return;
        Span<byte> header = stackalloc byte[8];
        if (ReadHead(lmp, header) < 8) return;
        // The C reads four bytes into an int and accepts 1..32768.
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(header), h = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (w is >= 1 and <= 32768 && h is >= 1 and <= 32768)
        {
            width = (int)w;
            height = (int)h;
        }
    }

    // The file a picture name resolves to (image.c's search: the name without its extension, as
    // .tga, .png, .jpg and so on, with override/ and dds/ variants), and the size in its header.
    private bool ReadSize(string name, out int width, out int height)
    {
        width = height = 0;
        if (!LegacyQcHost.IsSafePath(name)) return false;
        string? path;
        try { path = _files.ResolveImage(name); }
        catch (ArgumentException) { return false; } // a name no path can be made of
        if (path is null) return false;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
        try
        {
            int read = ReadHead(path, buffer.AsSpan(0, MaxHeaderBytes));
            return TryReadImageSize(buffer.AsSpan(0, read), out width, out height);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int ReadHead(string path, Span<byte> into)
    {
        try
        {
            using Stream stream = _files.Open(path);
            int total = 0;
            while (total < into.Length)
            {
                int n = stream.Read(into[total..]);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FileNotFoundException or NotSupportedException)
        {
            return 0;
        }
    }

    // ---- headers -----------------------------------------------------------------------------------

    /// <summary>
    /// The pixel dimensions an image file declares: PNG, JPEG, DDS, TGA or PCX, recognised by
    /// content as image.c does (the extension only chooses which file is opened). False for anything
    /// else, for a truncated header, and for dimensions outside 1..32768.
    /// </summary>
    public static bool TryReadImageSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        bool ok = Png(data, ref width, ref height) || Jpeg(data, ref width, ref height) || Dds(data, ref width, ref height)
            || Pcx(data, ref width, ref height) || Tga(data, ref width, ref height);
        if (ok && width is >= 1 and <= 32768 && height is >= 1 and <= 32768) return true;
        width = height = 0;
        return false;
    }

    // Signature, then the IHDR chunk, which the format requires to be first: width and height big-endian.
    private static bool Png(ReadOnlySpan<byte> d, ref int width, ref int height)
    {
        if (d.Length < 24 || !d[..8].SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) || !d.Slice(12, 4).SequenceEqual("IHDR"u8))
            return false;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(d[16..]), h = BinaryPrimitives.ReadUInt32BigEndian(d[20..]);
        if (w > 32768 || h > 32768) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    // A chain of segments, each "FF marker length"; the dimensions are in the first start-of-frame
    // marker (C0-CF, except C4 the Huffman tables, C8 an extension and CC arithmetic conditioning).
    private static bool Jpeg(ReadOnlySpan<byte> d, ref int width, ref int height)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
        int at = 2;
        while (at + 4 <= d.Length)
        {
            if (d[at] != 0xFF) return false;
            byte marker = d[at + 1];
            if (marker == 0xFF) { at++; continue; }             // fill byte
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { at += 2; continue; } // no length
            if (marker is 0xD9 or 0xDA) return false;           // end of image / start of scan: no frame header came
            int length = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 2)..]);
            if (length < 2) return false;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (at + 9 > d.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 7)..]);
                return true;
            }
            at += 2 + length;
        }
        return false;
    }

    // "DDS " then a 124-byte header: height at 12, width at 16.
    private static bool Dds(ReadOnlySpan<byte> d, ref int width, ref int height)
    {
        if (d.Length < 20 || !d[..4].SequenceEqual("DDS "u8) || BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) != 124) return false;
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]), w = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        if (w > 32768 || h > 32768) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    // LoadPCX_BGRA: manufacturer 0x0a, version 5, encoding 1, 8 bits, 1 plane; the size is max - min + 1.
    private static bool Pcx(ReadOnlySpan<byte> d, ref int width, ref int height)
    {
        if (d.Length < 128 || d[0] != 0x0A || d[1] != 5 || d[2] != 1 || d[3] != 8) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]) - BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) + 1;
        height = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]) - BinaryPrimitives.ReadUInt16LittleEndian(d[6..]) + 1;
        return true;
    }

    // TGA has no signature, so it is tried last and held to what LoadTGA_BGRA accepts: an image type
    // of 1, 2, 3, 9, 10 or 11, a colour map type of 0 or 1, and a pixel size that goes with the type.
    private static bool Tga(ReadOnlySpan<byte> d, ref int width, ref int height)
    {
        if (d.Length < 18) return false;
        int colorMapType = d[1], imageType = d[2], pixelSize = d[16];
        if (colorMapType > 1) return false;
        bool ok = (imageType & ~8) switch
        {
            1 => colorMapType == 1 && pixelSize == 8,     // colour mapped
            2 => pixelSize is 24 or 32,                    // true colour
            3 => pixelSize == 8,                           // greyscale
            _ => false,
        };
        if (!ok) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
        return true;
    }
}
