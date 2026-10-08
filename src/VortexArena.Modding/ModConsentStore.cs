using System.Text.Json;
using System.Text.Json.Serialization;

namespace VortexArena.Modding;

/// <summary>What the player has already said about an offered mod.</summary>
public enum ModConsentAnswer
{
    /// <summary>Nothing remembered: the player has to be asked.</summary>
    Ask,
    Allow,
    Deny,
}

/// <summary>What the player answers when asked about an offered mod.</summary>
public enum ModConsentDecision
{
    /// <summary>Run it for this connection only; ask again next time.</summary>
    AllowOnce,
    /// <summary>Run exactly this mod whenever this server offers it.</summary>
    AllowOnThisServer,
    /// <summary>Run exactly this mod whichever server offers it.</summary>
    AllowEverywhere,
    /// <summary>Do not run anything from this server for this connection; ask again next time.</summary>
    DenyOnce,
    /// <summary>Never run exactly this mod, whichever server offers it.</summary>
    DenyThisMod,
    /// <summary>Never run any mod this server offers, and do not ask.</summary>
    DenyThisServer,
}

/// <summary>One remembered answer, as listed to the player.</summary>
public sealed record ModConsentEntry
{
    /// <summary>The server the answer applies to, or "" for an answer that applies on every server.</summary>
    public string Server { get; init; } = "";
    /// <summary>The manifest hash the answer applies to, or "" for a whole-server denial.</summary>
    public string Sha256 { get; init; } = "";
    public bool Allow { get; init; }
    /// <summary>The mod's id when the answer was given. For display only; never used to match.</summary>
    public string Label { get; init; } = "";
    public long SavedUtcTicks { get; init; }
}

/// <summary>
/// The player's remembered answers about server-offered mods.
///
/// Three rules keep a remembered answer from being worth more than the player meant:
/// an allowance is always bound to the manifest's SHA-256, so a server that changes anything about its
/// mod - the code, the assets, the limits, the text it showed - is a new question; there is no
/// "always trust this server" answer; and a denial outranks an allowance when both match. Everything
/// uncertain resolves to <see cref="ModConsentAnswer.Ask"/>: an unreadable file, an entry that fails
/// validation, an entry that had to be dropped to stay inside the size cap.
///
/// This class decides nothing about <c>cl_allow_mods</c>; with mods off the offer flow never gets as far
/// as asking it.
/// </summary>
public sealed class ModConsentStore
{
    public const int MaxEntries = 512;
    public const int MaxFileBytes = 512 * 1024;
    public const int MaxServerKeyLength = 255;

    private readonly string? _path;
    private readonly List<ModConsentEntry> _entries = new();

    /// <param name="filePath">Where answers are kept, or null to keep them in memory only.</param>
    public ModConsentStore(string? filePath = null)
    {
        _path = filePath is null ? null : Path.GetFullPath(filePath);
        Load();
    }

    public IReadOnlyList<ModConsentEntry> Entries => _entries;

    /// <summary>
    /// The key a server is remembered under: its address as the player connected to it, trimmed and
    /// lower-cased. Returns "" for anything that is not a plausible address, and "" never matches.
    /// </summary>
    public static string NormalizeServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return "";
        string key = server.Trim().ToLowerInvariant();
        if (key.Length > MaxServerKeyLength) return "";
        foreach (char c in key)
            if (c <= ' ' || c > '~') return "";
        return key;
    }

    public ModConsentAnswer Lookup(string? server, string manifestSha256)
    {
        if (!ModManifest.IsSha256(manifestSha256)) return ModConsentAnswer.Ask;
        string key = NormalizeServer(server);
        bool allowed = false;
        foreach (ModConsentEntry entry in _entries)
        {
            bool serverMatches = entry.Server.Length == 0 || (key.Length > 0 && entry.Server == key);
            bool hashMatches = entry.Sha256.Length == 0 || entry.Sha256 == manifestSha256;
            if (!serverMatches || !hashMatches) continue;
            if (!entry.Allow) return ModConsentAnswer.Deny;
            allowed = true;
        }
        return allowed ? ModConsentAnswer.Allow : ModConsentAnswer.Ask;
    }

    /// <summary>
    /// Remembers <paramref name="decision"/>. The two "once" decisions are not stored - they belong to the
    /// connection - and a decision that needs a server key is not stored when there is none.
    /// Returns true when something was written.
    /// </summary>
    public bool Record(string? server, string manifestSha256, ModConsentDecision decision, string? label = null)
    {
        if (!ModManifest.IsSha256(manifestSha256)) return false;
        string key = NormalizeServer(server);
        ModConsentEntry? entry = decision switch
        {
            ModConsentDecision.AllowOnThisServer when key.Length > 0 => new ModConsentEntry { Server = key, Sha256 = manifestSha256, Allow = true },
            ModConsentDecision.AllowEverywhere => new ModConsentEntry { Server = "", Sha256 = manifestSha256, Allow = true },
            ModConsentDecision.DenyThisMod => new ModConsentEntry { Server = "", Sha256 = manifestSha256, Allow = false },
            ModConsentDecision.DenyThisServer when key.Length > 0 => new ModConsentEntry { Server = key, Sha256 = "", Allow = false },
            _ => null,
        };
        if (entry is null) return false;

        entry = entry with { Label = CleanLabel(label), SavedUtcTicks = DateTime.UtcNow.Ticks };
        // A new answer replaces older ones it contradicts or repeats: the same scope, and - for an
        // allowance - any denial that would otherwise still outrank it here: of exactly this mod, or of
        // the server the player is on while allowing it.
        _entries.RemoveAll(e => e.Server == entry.Server && e.Sha256 == entry.Sha256);
        if (entry.Allow)
            _entries.RemoveAll(e => !e.Allow && (e.Sha256.Length == 0
                ? key.Length > 0 && e.Server == key
                : e.Sha256 == manifestSha256 && (e.Server.Length == 0 || e.Server == key)));
        _entries.Add(entry);
        // Oldest first out. Losing an entry can only turn an answer back into a question.
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        Save();
        return true;
    }

    /// <summary>Forgets every answer about <paramref name="server"/>, or every answer when it is null. Returns how many went.</summary>
    public int Forget(string? server = null)
    {
        string key = NormalizeServer(server);
        int removed = server is null ? _entries.Count : _entries.RemoveAll(e => key.Length > 0 && e.Server == key);
        if (server is null) _entries.Clear();
        if (removed > 0) Save();
        return removed;
    }

    // ------------------------------------------------------------------------------------------------

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<ModConsentEntry?>? Entries { get; set; }
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private void Load()
    {
        if (_path is null) return;
        try
        {
            FileInfo file = new(_path);
            if (!file.Exists || file.Length > MaxFileBytes) return;
            FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(_path), s_json);
            if (model?.Entries is null || model.Version != 1) return;
            foreach (ModConsentEntry? entry in model.Entries)
            {
                if (_entries.Count >= MaxEntries) break;
                if (entry is not null && IsValid(entry)) _entries.Add(entry with { Label = CleanLabel(entry.Label) });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable file means nothing is remembered, which means the player is asked.
            _entries.Clear();
        }
    }

    // The file is the player's own, but a hand-edited or damaged one must not produce an entry that
    // matches more than its writer could have meant: an allowance with no hash would allow everything.
    private static bool IsValid(ModConsentEntry entry)
    {
        if (entry.Server is null || entry.Sha256 is null) return false;
        if (entry.Server.Length > 0 && NormalizeServer(entry.Server) != entry.Server) return false;
        if (entry.Sha256.Length > 0 && !ModManifest.IsSha256(entry.Sha256)) return false;
        if (entry.Allow) return entry.Sha256.Length > 0;
        return entry.Sha256.Length > 0 || entry.Server.Length > 0;
    }

    private static string CleanLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return "";
        Span<char> clean = stackalloc char[Math.Min(label.Length, ModManifest.MaxNameLength)];
        for (int i = 0; i < clean.Length; i++) clean[i] = char.IsControl(label[i]) || char.IsSurrogate(label[i]) ? '?' : label[i];
        return new string(clean);
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Written beside the real file and moved over it, so a crash mid-write leaves the old answers.
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new FileModel { Entries = _entries.ToList<ModConsentEntry?>() }, s_json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to save costs the player a repeated question, nothing else.
        }
    }
}
