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

    /// <summary>
    /// True when the manifest was well-formed but names a guest interface this client does not provide.
    /// The offer flow reports that to the server as its own reason, so an operator can tell "your mod
    /// is too new for this client" from "your manifest is broken".
    /// </summary>
    public bool UnsupportedAbi { get; init; }
}

/// <summary>
/// What a mod says it will use beyond drawing. Declared in the manifest, shown to the player in the
/// consent prompt, and enforced by the client: a capability that is not declared is not available.
/// The set is closed - a manifest naming one this client does not know is refused, because the client
/// cannot show the player what it would be agreeing to.
/// </summary>
public static class ModCapabilities
{
    /// <summary>The mod exchanges messages with the server half of the mod (<c>send_to_server</c>, <c>mod_event</c>).</summary>
    public const string Net = "net";
    /// <summary>The mod plays sounds from its own packs.</summary>
    public const string Sound = "sound";

    public static bool IsKnown(string? name) => name is Net or Sound;
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
    public const int MaxCapabilities = 8;
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
    /// <summary>
    /// True when the server will not let a client play without this mod. The client does not act on it
    /// beyond telling the player: refusing a required mod ends with the SERVER closing the connection.
    /// </summary>
    public bool Required { get; init; }
    /// <summary>Names from <see cref="ModCapabilities"/>. Anything not listed is not available to the mod.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public bool HasCapability(string name)
    {
        foreach (string capability in Capabilities)
            if (capability == name) return true;
        return false;
    }

    /// <summary>
    /// Lower-case hex SHA-256 of a manifest exactly as it arrived. Because the manifest carries the hash
    /// of every file, this one value identifies the whole mod - code, assets, the limits it asks for and
    /// the text the player was shown - and is what a remembered consent is bound to.
    /// </summary>
    public static string HashOf(ReadOnlySpan<byte> manifestJson) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestJson)).ToLowerInvariant();

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
        if (Abi != ModAbi.ImportModule)
        {
            // The name is quoted back in a message, so it is cut to something printable first.
            string shown = Abi is { Length: > 0 and <= MaxNameLength } && Abi.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? Abi : "?";
            throw new ModManifestException($"mod was built for interface '{shown}'; this client provides '{ModAbi.ImportModule}'") { UnsupportedAbi = true };
        }
        if (AssetPacks is null || Limits is null || Consent is null || Capabilities is null) throw new ModManifestException("manifest has a null section");
        if (Capabilities.Count > MaxCapabilities) throw new ModManifestException($"manifest lists {Capabilities.Count} capabilities; the limit is {MaxCapabilities}");
        HashSet<string> capabilities = new(StringComparer.Ordinal);
        foreach (string capability in Capabilities)
        {
            if (!ModCapabilities.IsKnown(capability)) throw new ModManifestException("manifest asks for a capability this client does not know");
            if (!capabilities.Add(capability)) throw new ModManifestException($"capability '{capability}' is listed twice");
        }
        if (Limits.MaxMemoryBytes is <= 0 || Limits.FrameBudgetMs is <= 0) throw new ModManifestException("a requested limit is zero or negative");
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
