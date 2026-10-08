using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexArena.Modding;

/// <summary>One downloadable file of a mod, identified by the SHA-256 of its contents.</summary>
public sealed record ModArtifact
{
    /// <summary>File name only - no directories. Shown to the player and used as the mount name.</summary>
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>Lower-case hex SHA-256 of the file. The only thing that identifies the content; the name and URL are hints.</summary>
    public string Sha256 { get; init; } = "";
    /// <summary>Where to fetch it over HTTP(S), or null to request it in-band from the game server.</summary>
    public string? Url { get; init; }
}

public sealed record ModConsent
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string? Url { get; init; }
}

public sealed record ModLimitRequest
{
    public long? MaxMemoryBytes { get; init; }
    public int? FrameBudgetMs { get; init; }
}

public sealed class ModManifestException : Exception
{
    public ModManifestException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// What a server tells a connecting client about its mod: which files make it up and what to show the
/// player before downloading any of them (planning/specs/modding.md, section 9).
///
/// A manifest is input from a stranger's server and is read before the player has agreed to anything,
/// so <see cref="Parse"/> is strict: every count, length, size and name is checked against a cap, and a
/// manifest that fails any check is rejected whole rather than repaired.
/// </summary>
public sealed record ModManifest
{
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxAssetPacks = 64;
    public const int MaxNameLength = 64;
    public const int MaxTextLength = 2048;
    public const int MaxUrlLength = 1024;
    /// <summary>Ceiling for one asset pack. The module itself is capped by <see cref="ModLimits.MaxModuleBytes"/>.</summary>
    public const long MaxAssetPackBytes = 1L << 30;
    public const long MaxTotalBytes = 4L << 30;

    public string ModId { get; init; } = "";
    public string ModVersion { get; init; } = "";
    /// <summary>The server's base protocol hash. Must equal the client's, or the two cannot talk at all.</summary>
    public uint BaseProtocol { get; init; }
    /// <summary>The guest interface the module was built against, e.g. "vortex_1".</summary>
    public string Abi { get; init; } = ModAbi.ImportModule;
    public ModArtifact? ClientModule { get; init; }
    public IReadOnlyList<ModArtifact> AssetPacks { get; init; } = Array.Empty<ModArtifact>();
    public ModLimitRequest Limits { get; init; } = new();
    public ModConsent Consent { get; init; } = new();

    /// <summary>Every file the mod consists of: the module first, then the packs in mount order.</summary>
    [JsonIgnore]
    public IEnumerable<ModArtifact> Artifacts => ClientModule is null ? AssetPacks : AssetPacks.Prepend(ClientModule);

    [JsonIgnore]
    public long TotalBytes => Artifacts.Sum(a => a.SizeBytes);

    /// <summary>The limits the sandbox will actually run with: the server's requests, never above the client's ceiling.</summary>
    public ModLimits EffectiveLimits(ModLimits ceiling) => (ceiling with
    {
        MaxMemoryBytes = Limits.MaxMemoryBytes ?? ceiling.MaxMemoryBytes,
        FrameBudgetMs = Limits.FrameBudgetMs ?? ceiling.FrameBudgetMs,
    }).ClampTo(ceiling);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A manifest nests two levels deep. A document that nests further is not a manifest, and an
        // unbounded depth is how a small input exhausts the parser's stack.
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, s_json);

    /// <exception cref="ModManifestException">The bytes are not a manifest this client will act on.</exception>
    public static ModManifest Parse(ReadOnlySpan<byte> json, ModLimits? clientLimits = null)
    {
        if (json.Length > MaxManifestBytes) throw new ModManifestException($"manifest is {json.Length} bytes; the limit is {MaxManifestBytes}");

        ModManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ModManifest>(json, s_json) ?? throw new ModManifestException("manifest is null"); }
        catch (JsonException e) { throw new ModManifestException($"manifest is not valid: {e.Message}", e); }

        manifest.Validate(clientLimits ?? ModLimits.Default);
        return manifest;
    }

    private void Validate(ModLimits clientLimits)
    {
        RequireName(ModId, "modId");
        RequireText(ModVersion, "modVersion", MaxNameLength);
        if (Abi != ModAbi.ImportModule) throw new ModManifestException($"mod was built for interface '{Abi}'; this client provides '{ModAbi.ImportModule}'");
        if (AssetPacks is null || Limits is null || Consent is null) throw new ModManifestException("manifest has a null section");
        if (AssetPacks.Count > MaxAssetPacks) throw new ModManifestException($"manifest lists {AssetPacks.Count} asset packs; the limit is {MaxAssetPacks}");

        RequireText(Consent.Title, "consent.title", MaxNameLength);
        RequireText(Consent.Description, "consent.description", MaxTextLength);
        RequireText(Consent.Author, "consent.author", MaxNameLength);
        if (Consent.Url is not null) RequireUrl(Consent.Url, "consent.url");

        if (ClientModule is not null) ValidateArtifact(ClientModule, ".wasm", clientLimits.MaxModuleBytes);

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModArtifact pack in AssetPacks)
        {
            if (pack is null) throw new ModManifestException("manifest lists a null asset pack");
            ValidateArtifact(pack, ".pk3", MaxAssetPackBytes);
            if (!names.Add(pack.Name)) throw new ModManifestException($"asset pack '{pack.Name}' is listed twice");
        }

        if (TotalBytes > MaxTotalBytes) throw new ModManifestException($"mod totals {TotalBytes} bytes; the limit is {MaxTotalBytes}");
    }

    private static void ValidateArtifact(ModArtifact artifact, string extension, long maxBytes)
    {
        RequireName(artifact.Name, "artifact name");
        if (!artifact.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ModManifestException($"'{artifact.Name}' must be a {extension} file");
        if (artifact.SizeBytes <= 0 || artifact.SizeBytes > maxBytes)
            throw new ModManifestException($"'{artifact.Name}' is {artifact.SizeBytes} bytes; the limit is {maxBytes}");
        if (!IsSha256(artifact.Sha256))
            throw new ModManifestException($"'{artifact.Name}' has no valid SHA-256");
        if (artifact.Url is not null) RequireUrl(artifact.Url, $"url of '{artifact.Name}'");
    }

    /// <summary>
    /// A name that is safe to show, to log and to use as a file name on every platform: letters, digits,
    /// dot, dash and underscore only, not starting with a dot. This is what keeps a manifest from naming
    /// "../../somewhere" or a Windows device.
    /// </summary>
    private static void RequireName(string? name, string what)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] == '.')
            throw new ModManifestException($"{what} is missing, too long, or starts with a dot");
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                throw new ModManifestException($"{what} '{name}' contains a character other than letters, digits, '.', '-' and '_'");
    }

    private static void RequireText(string? text, string what, int maxLength)
    {
        if (text is null || text.Length > maxLength) throw new ModManifestException($"{what} is missing or longer than {maxLength} characters");
        foreach (char c in text)
            if (char.IsControl(c) && c is not ('\n' or '\t'))
                throw new ModManifestException($"{what} contains a control character");
    }

    private static void RequireUrl(string url, string what)
    {
        if (url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http"))
            throw new ModManifestException($"{what} is not an http(s) URL");
        // Credentials in a URL would be sent to whatever host the manifest names.
        if (uri.UserInfo.Length > 0) throw new ModManifestException($"{what} must not carry credentials");
    }

    internal static bool IsSha256(string? text)
    {
        if (text is null || text.Length != 64) return false;
        foreach (char c in text)
            if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) return false;
        return true;
    }
}
