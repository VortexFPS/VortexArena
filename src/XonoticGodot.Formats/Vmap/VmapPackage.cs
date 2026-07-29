using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XonoticGodot.Formats.Vfs;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// Finding and writing a <c>.vmap</c> on disk, and reading the two layouts that came before it.
///
/// A <c>.vmap</c> is ONE TEXT FILE — see <see cref="VmapText"/>, which owns the format itself. This type is
/// only the part that touches the filesystem: where a map is, how it is written atomically, and how a file
/// that predates the text form is recognised and read anyway.
///
/// <para><b>The container is the pk3.</b> Earlier versions made <c>.vmap</c> its own container: a directory
/// of JSON sections while editing, a zip of the same when shipping. That duplicated a job the game already
/// does — a <c>.pk3dir</c> IS the loose layout and a <c>.pk3</c> IS the packed one — so a map ended up at
/// <c>stormkeep.pk3dir/maps/stormkeep.vmap/geometry.json</c>, one container inside another with the same
/// purpose. The <c>.vmap</c> is a plain file in <c>maps/</c> now, filling the slot the <c>.bsp</c> fills.</para>
///
/// <para><b>Older layouts still load.</b> <see cref="Read"/> sniffs: a text file is parsed as the current
/// format, a directory of section files or a zip of them is read through the legacy path below. Nothing
/// writes those any more, but a mapper with saves on disk keeps them.</para>
/// </summary>
public static class VmapPackage
{
    public const string ManifestSection = "map.json";
    public const string GeometrySection = "geometry.json";
    public const string EntitiesSection = "entities.json";

    /// <summary>
    /// Named object sets (backlog F8). OPTIONAL, and written only when the map has any — so a package with no
    /// groups is byte-for-byte the package it always was, and an older reader still loads the map.
    /// </summary>
    public const string GroupsSection = "groups.json";

    /// <summary>
    /// Painted layer weights (backlog F2): the INDEX — id, size, resolution, projection. The texels live
    /// beside it, one deflated blob per map, because a hundred kilobytes of base64 in a JSON section would
    /// make the diffable part of the package unreadable.
    ///
    /// OPTIONAL, like the groups section: a map nobody has painted writes neither this nor a blend directory,
    /// and its bytes do not move.
    /// </summary>
    public const string BlendSection = "blend.json";

    /// <summary>Directory the per-map texel blobs live in, as <c>blend/&lt;id&gt;.bin</c>.</summary>
    public const string BlendDirectory = "blend";

    /// <summary>
    /// Format version a package needs to be READ correctly once it carries blend maps.
    ///
    /// Stamped CONDITIONALLY: an unpainted map still writes version 1 and still loads in a build without this
    /// change. Stamping it unconditionally would lock every save out of every older build — including a
    /// co-editing peer's — for a section that map does not have.
    /// </summary>
    public const int BlendFormatVersion = 2;

    /// <summary>Canonical container extension.</summary>
    public const string Extension = ".vmap";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // =============================================================================================
    //  Reading
    // =============================================================================================

    /// <summary>
    /// Read a map from disk, whichever form it is in: the current text file, or one of the two layouts that
    /// preceded it.
    ///
    /// Sniffed by CONTENT rather than by extension, because all three forms are called <c>.vmap</c> — the
    /// text file starts with its own magic, a zip starts with <c>PK</c>, and a directory is a directory.
    /// </summary>
    public static VmapDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Directory.Exists(path))
            return ReadFromDirectory(path);

        if (File.Exists(path))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (VmapText.LooksLikeVmapText(bytes))
                return VmapText.Read(Encoding.UTF8.GetString(bytes));

            using var ms = new MemoryStream(bytes, writable: false);
            return ReadFromZip(ms);
        }

        throw new AssetParseException($"vmap: nothing at '{path}'");
    }

    /// <summary>
    /// Write a map as a single text file, through a sibling temporary so an interrupted write cannot leave a
    /// half-written map where a readable one used to be. The move is same-directory, which is where a
    /// filesystem rename is cheapest and closest to atomic.
    ///
    /// The whole document is serialized BEFORE the existing file is touched: a document that throws part-way
    /// through — and the writer walks every brush, so it can — must not have already replaced the map.
    /// </summary>
    public static void Write(VmapDocument doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string text = VmapText.Write(doc);

        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// LEGACY: read a map saved as a directory of JSON sections. Nothing writes this form any more; it is
    /// kept so saves made before the text format still open.
    /// </summary>
    public static VmapDocument ReadFromDirectory(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        return Assemble(
            ReadTextFile(Path.Combine(dir, ManifestSection), required: true)!,
            ReadTextFile(Path.Combine(dir, GeometrySection), required: false),
            ReadTextFile(Path.Combine(dir, EntitiesSection), required: false),
            ReadTextFile(Path.Combine(dir, GroupsSection), required: false),
            name => ReadBinaryFile(Path.Combine(dir, name)));
    }

    /// <summary>LEGACY: read a map saved as a zip of JSON sections. Same reasoning as the directory form.</summary>
    public static VmapDocument ReadFromZip(Stream zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        return Assemble(
            ReadZipEntry(archive, ManifestSection, required: true)!,
            ReadZipEntry(archive, GeometrySection, required: false),
            ReadZipEntry(archive, EntitiesSection, required: false),
            ReadZipEntry(archive, GroupsSection, required: false),
            name => ReadZipBytes(archive, name));
    }

    /// <summary>
    /// Read a packaged <c>.vmap</c> through the virtual file system, so shipped maps can live inside a pk3
    /// exactly like a <c>.bsp</c> does.
    /// </summary>
    public static VmapDocument ReadFromVfs(VirtualFileSystem vfs, string vpath)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrEmpty(vpath);
        byte[] bytes = vfs.ReadBytes(vpath);
        if (VmapText.LooksLikeVmapText(bytes))
            return VmapText.Read(Encoding.UTF8.GetString(bytes));

        using var ms = new MemoryStream(bytes, writable: false);
        return ReadFromZip(ms);
    }

    private static string? ReadTextFile(string path, bool required)
    {
        if (File.Exists(path))
            return File.ReadAllText(path, Encoding.UTF8);
        if (required)
            throw new AssetParseException($"vmap: missing required section '{Path.GetFileName(path)}' in '{Path.GetDirectoryName(path)}'");
        return null;
    }

    private static string? ReadZipEntry(ZipArchive archive, string name, bool required)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null)
        {
            if (required)
                throw new AssetParseException($"vmap: missing required section '{name}' in package");
            return null;
        }
        using Stream s = entry.Open();
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static VmapDocument Assemble(
        string manifestJson, string? geometryJson, string? entitiesJson, string? groupsJson = null,
        Func<string, byte[]?>? readBinary = null)
    {
        var doc = new VmapDocument();

        ManifestDto manifest = Deserialize<ManifestDto>(manifestJson, ManifestSection);
        doc.FormatVersion = manifest.FormatVersion;
        if (doc.FormatVersion > VmapDocument.CurrentFormatVersion)
            throw new AssetParseException(
                $"vmap: package format version {doc.FormatVersion} is newer than this build supports ({VmapDocument.CurrentFormatVersion})");
        doc.Manifest = new VmapManifest
        {
            Name = manifest.Name ?? string.Empty,
            Title = manifest.Title ?? string.Empty,
            SourceKind = manifest.SourceKind ?? string.Empty,
            SourcePath = manifest.SourcePath ?? string.Empty,
            SourceHash = manifest.SourceHash ?? string.Empty,
        };

        if (readBinary is not null && readBinary(BlendSection) is { } blendIndexBytes)
        {
            BlendIndexDto index = Deserialize<BlendIndexDto>(
                Encoding.UTF8.GetString(blendIndexBytes), BlendSection);
            foreach (BlendMapDto b in index.Maps ?? Array.Empty<BlendMapDto>())
            {
                if (b.Width <= 0 || b.Height <= 0)
                    continue;
                byte[]? packed = readBinary($"{BlendDirectory}/{b.Id}.bin");
                if (packed is null)
                    continue;

                byte[] texels = Inflate(packed, b.Width * b.Height * 4);
                if (texels.Length != b.Width * b.Height * 4)
                    continue;   // a blob that disagrees with its index entry is not a blend map

                doc.BlendMaps.Add(new VmapBlendMap
                {
                    Id = b.Id,
                    Width = b.Width,
                    Height = b.Height,
                    UnitsPerTexel = b.UnitsPerTexel <= 0f ? 4f : b.UnitsPerTexel,
                    Projection = new VmapTexProjection(
                        Vec3(b.AxisU), Vec3(b.AxisV), b.OffsetU, b.OffsetV),
                    Texels = texels,
                });
            }
        }

        if (groupsJson is not null)
        {
            GroupsDto groups = Deserialize<GroupsDto>(groupsJson, GroupsSection);
            foreach (GroupDto g in groups.Groups ?? Array.Empty<GroupDto>())
                doc.Groups.Add(
                    new VmapGroup { Id = g.Id, Name = g.Name ?? string.Empty, Hidden = g.Hidden });
        }

        if (geometryJson is not null)
        {
            GeometryDto geo = Deserialize<GeometryDto>(geometryJson, GeometrySection);
            foreach (BrushDto b in geo.Brushes ?? Array.Empty<BrushDto>())
            {
                var brush = new VmapBrush
                {
                    Id = b.Id, IsDetail = b.Detail, ContentFlags = b.Contents, GroupId = b.Group,
                };
                foreach (FaceDto f in b.Faces ?? Array.Empty<FaceDto>())
                {
                    // The flat fields ARE the base layer, so a package written before layers existed reads
                    // back as a one-layer face with no special case.
                    var face = new VmapFace
                    {
                        BlendMapId = f.BlendMap,
                        Plane = new VmapPlane(Vec3(f.Normal), f.Dist),
                        Material = f.Material ?? string.Empty,
                        Projection = new VmapTexProjection(Vec3(f.AxisU), Vec3(f.AxisV), f.OffsetU, f.OffsetV),
                        SurfaceFlags = f.Surface,
                        ContentFlags = f.Contents,
                    };
                    foreach (LayerDto l in f.ExtraLayers ?? Array.Empty<LayerDto>())
                    {
                        face.Layers.Add(new VmapFaceLayer
                        {
                            Material = l.Material ?? string.Empty,
                            Projection = new VmapTexProjection(
                                Vec3(l.AxisU), Vec3(l.AxisV), l.OffsetU, l.OffsetV),
                            Blend = (VmapBlend)l.Blend,
                            WeightChannel = l.WeightChannel,
                        });
                    }
                    brush.Faces.Add(face);
                }
                doc.Brushes.Add(brush);
            }

            foreach (PatchDto p in geo.Patches ?? Array.Empty<PatchDto>())
            {
                var patch = new VmapPatch
                {
                    Id = p.Id,
                    Material = p.Material ?? string.Empty,
                    Width = p.Width,
                    Height = p.Height,
                    SurfaceFlags = p.Surface,
                    ContentFlags = p.Contents,
                    GroupId = p.Group,
                };
                float[] ctrl = p.Controls ?? Array.Empty<float>();
                for (int i = 0; i + 2 < ctrl.Length; i += 3)
                    patch.Controls.Add(new Vector3(ctrl[i], ctrl[i + 1], ctrl[i + 2]));
                float[] uvs = p.Uvs ?? Array.Empty<float>();
                for (int i = 0; i + 1 < uvs.Length; i += 2)
                    patch.ControlUvs.Add(new Vector2(uvs[i], uvs[i + 1]));
                doc.Patches.Add(patch);
            }
        }

        if (entitiesJson is not null)
        {
            EntitiesDto ents = Deserialize<EntitiesDto>(entitiesJson, EntitiesSection);
            foreach (EntityDto e in ents.Entities ?? Array.Empty<EntityDto>())
            {
                var ent = new VmapEntity
                {
                    Id = e.Id, ClassName = e.ClassName ?? string.Empty, GroupId = e.Group,
                };
                foreach (KeyValuePair<string, string> kv in e.Fields ?? new Dictionary<string, string>())
                    ent.Fields[kv.Key] = kv.Value;
                if (!string.IsNullOrEmpty(ent.ClassName))
                    ent.Fields["classname"] = ent.ClassName;
                ent.BrushIds.AddRange(e.Brushes ?? Array.Empty<int>());
                ent.PatchIds.AddRange(e.Patches ?? Array.Empty<int>());
                doc.Entities.Add(ent);
            }
        }

        return doc;
    }

    private static T Deserialize<T>(string json, string section)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new AssetParseException($"vmap: section '{section}' deserialized to null");
        }
        catch (JsonException ex)
        {
            throw new AssetParseException($"vmap: malformed section '{section}': {ex.Message}", ex);
        }
    }

    private static Vector3 Vec3(float[]? a)
        => a is { Length: >= 3 } ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;

    // =============================================================================================
    //  Shared helpers
    // =============================================================================================

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static byte[]? ReadBinaryFile(string path)
        => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static byte[]? ReadZipBytes(ZipArchive archive, string name)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry is null)
            return null;
        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <param name="expected">
    /// Bytes the index says this map has. A ceiling as much as a hint: without it a hostile blob could inflate
    /// without bound into memory.
    /// </param>
    private static byte[] Inflate(byte[] packed, int expected)
    {
        if (expected <= 0 || expected > 64 * 1024 * 1024)
            return Array.Empty<byte>();
        try
        {
            var result = new byte[expected];
            using var ms = new MemoryStream(packed, writable: false);
            using var gz = new DeflateStream(ms, CompressionMode.Decompress);
            int read = 0;
            while (read < expected)
            {
                int n = gz.Read(result, read, expected - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return read == expected ? result : Array.Empty<byte>();
        }
        catch (InvalidDataException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Content hash of arbitrary source bytes (FNV-1a 64), used to stamp <see cref="VmapManifest.SourceHash"/>
    /// so a bake cache can be keyed to the geometry it was built from and a redundant re-import is detectable.
    /// Not cryptographic — it only has to notice that bytes changed.
    /// </summary>
    public static string HashBytes(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in data)
        {
            h ^= b;
            h *= prime;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    // =============================================================================================
    //  On-disk DTOs — the wire shape, deliberately decoupled from the domain model so refactoring
    //  VmapDocument does not silently change the file format. Property order here IS the file order.
    // =============================================================================================

    private sealed class ManifestDto
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("sourceKind")] public string? SourceKind { get; set; }
        [JsonPropertyName("sourcePath")] public string? SourcePath { get; set; }
        [JsonPropertyName("sourceHash")] public string? SourceHash { get; set; }
    }

    private sealed class GeometryDto
    {
        [JsonPropertyName("brushes")] public BrushDto[]? Brushes { get; set; }
        [JsonPropertyName("patches")] public PatchDto[]? Patches { get; set; }
    }

    private sealed class BrushDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("detail")] public bool Detail { get; set; }
        [JsonPropertyName("contents")] public int Contents { get; set; }
        [JsonPropertyName("faces")] public FaceDto[]? Faces { get; set; }

        /// <summary>
        /// Group membership (backlog F8). Omitted when 0, which is every object on every map that has no
        /// groups, so no existing package changes a byte on its next save.
        /// </summary>
        [JsonPropertyName("group")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Group { get; set; }
    }

    private sealed class FaceDto
    {
        [JsonPropertyName("normal")] public float[]? Normal { get; set; }
        [JsonPropertyName("dist")] public float Dist { get; set; }
        [JsonPropertyName("material")] public string? Material { get; set; }
        [JsonPropertyName("axisU")] public float[]? AxisU { get; set; }
        [JsonPropertyName("axisV")] public float[]? AxisV { get; set; }
        [JsonPropertyName("offsetU")] public float OffsetU { get; set; }
        [JsonPropertyName("offsetV")] public float OffsetV { get; set; }
        [JsonPropertyName("surface")] public int Surface { get; set; }
        [JsonPropertyName("contents")] public int Contents { get; set; }

        /// <summary>
        /// The face's painted weight map (backlog F2). Omitted when 0, so an unpainted face writes what it
        /// always did.
        /// </summary>
        [JsonPropertyName("blendMap")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int BlendMap { get; set; }

        /// <summary>
        /// Layers ABOVE the base one, which lives in the flat fields above. Omitted entirely on a plain face —
        /// not written as null — so a single-layer face is byte-for-byte what it was before layers existed and
        /// no package in the wild churns on its next save.
        /// </summary>
        [JsonPropertyName("extraLayers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LayerDto[]? ExtraLayers { get; set; }
    }

    private sealed class LayerDto
    {
        [JsonPropertyName("material")] public string? Material { get; set; }
        [JsonPropertyName("axisU")] public float[]? AxisU { get; set; }
        [JsonPropertyName("axisV")] public float[]? AxisV { get; set; }
        [JsonPropertyName("offsetU")] public float OffsetU { get; set; }
        [JsonPropertyName("offsetV")] public float OffsetV { get; set; }
        [JsonPropertyName("blend")] public int Blend { get; set; }
        [JsonPropertyName("weightChannel")] public int WeightChannel { get; set; }
    }

    private sealed class PatchDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("material")] public string? Material { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("surface")] public int Surface { get; set; }
        [JsonPropertyName("contents")] public int Contents { get; set; }
        [JsonPropertyName("controls")] public float[]? Controls { get; set; }
        [JsonPropertyName("uvs")] public float[]? Uvs { get; set; }

        /// <summary>
        /// Group membership (backlog F8). Omitted when 0, which is every object on every map that has no
        /// groups, so no existing package changes a byte on its next save.
        /// </summary>
        [JsonPropertyName("group")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Group { get; set; }
    }

    private sealed class EntitiesDto
    {
        [JsonPropertyName("entities")] public EntityDto[]? Entities { get; set; }
    }

    private sealed class EntityDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("classname")] public string? ClassName { get; set; }
        [JsonPropertyName("fields")] public Dictionary<string, string>? Fields { get; set; }
        [JsonPropertyName("brushes")] public int[]? Brushes { get; set; }
        [JsonPropertyName("patches")] public int[]? Patches { get; set; }

        /// <summary>
        /// Group membership (backlog F8). Omitted when 0, which is every object on every map that has no
        /// groups, so no existing package changes a byte on its next save.
        /// </summary>
        [JsonPropertyName("group")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Group { get; set; }
    }

    private sealed class BlendIndexDto
    {
        [JsonPropertyName("maps")] public BlendMapDto[]? Maps { get; set; }
    }

    private sealed class BlendMapDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("unitsPerTexel")] public float UnitsPerTexel { get; set; }
        [JsonPropertyName("axisU")] public float[]? AxisU { get; set; }
        [JsonPropertyName("axisV")] public float[]? AxisV { get; set; }
        [JsonPropertyName("offsetU")] public float OffsetU { get; set; }
        [JsonPropertyName("offsetV")] public float OffsetV { get; set; }
    }

    private sealed class GroupsDto
    {
        [JsonPropertyName("groups")] public GroupDto[]? Groups { get; set; }
    }

    private sealed class GroupDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    }
}
