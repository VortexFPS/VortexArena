using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Protocol;

/// <summary>Constants from protocol/announce-v1.md. Every number a server, a master or a client needs
/// to agree on lives here exactly once, so the three implementations cannot drift by editing their own
/// copy of a timeout.</summary>
public static class AnnounceProtocol
{
    /// <summary>Path segment and the value carried in every body.</summary>
    public const int Version = 1;

    public const string AnnouncePath = "/api/v1/announce";
    public const string ServersPath = "/api/v1/servers";

    /// <summary>Default sv_master_url, and the only host a game server or a client ever needs.
    ///
    /// <para>One deployment answers all of it on this name: the announce protocol, the server list, and
    /// the orchestrator panel. It was two names until 2026-08-06 — master.vortexfps.org for the
    /// directory, conductor.vortexfps.org for the panel — separated so the two could carry different
    /// cache, WAF and rate-limit policy, and so the panel could go down without the server list
    /// following it. That separation was given up deliberately; the README says what it cost.</para>
    ///
    /// <para><b>Changing this value is close to permanent.</b> It is compiled into every shipped build,
    /// so any name that has once reached a player has to keep resolving for as long as that build is in
    /// use. The 2026-08-06 rename was free only because nothing had shipped yet.</para></summary>
    public const string DefaultMasterUrl = "https://conductor.vortexfps.org";

    /// <summary>Re-announce cadence. Also the floor: never send faster than this.</summary>
    public const int AnnounceIntervalSeconds = 180;

    /// <summary>A listing survives this long after its last accepted announce. Deliberately longer
    /// than the interval, so one dropped announce does not delist a live server.</summary>
    public const int TtlSeconds = 300;

    /// <summary>How long a single UDP challenge stays valid. Single-use.</summary>
    public const int ChallengeValiditySeconds = 30;

    /// <summary>Upper bound on re-verification of an already listed server.</summary>
    public const int ChallengeReverifySeconds = 3600;

    /// <summary>A registration that never passes its challenge is dropped after this.</summary>
    public const int UnverifiedDropSeconds = 600;

    /// <summary>How long a server_id is held for a server that has expired, so one that comes back
    /// keeps its identity.</summary>
    public const int ServerIdRetentionSeconds = 86400;

    public const int HostnameMaxLength = 128;
    public const int ListLimitDefault = 200;
    public const int ListLimitMax = 500;
    public const int ListCacheMaxAgeSeconds = 30;

    /// <summary>Lowercase hex sha256.</summary>
    public const int ControlKeyFingerprintLength = 64;

    /// <summary>16 bytes of CSPRNG output as unpadded base64url. The encoding is load-bearing: the
    /// challenge travels inside a classic infostring, where a backslash or a quote would corrupt the
    /// reply, and base64url produces neither.</summary>
    public const int ChallengeBytes = 16;
    public const int ChallengeEncodedLength = 22;

    /// <summary>The one serializer configuration that produces the bytes in the spec. Both ends must
    /// use it; the golden fixtures in the test suite are what stops a local JsonSerializerOptions from
    /// quietly diverging.</summary>
    public static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            // Absent and null mean the same thing (spec §2), so omitting nulls keeps announce bodies
            // small without changing their meaning.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Unknown fields are ignored, never rejected. This is what makes additive v1 changes safe.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
