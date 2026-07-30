namespace Conductor.Protocol;

/// <summary>The field rules from spec §3, implemented once.
///
/// Both ends run this. The master runs it because it must never trust a request; the game server runs
/// it before sending, so a misconfiguration surfaces in the server's own log with the field name
/// rather than as an opaque 400 from a remote host. One implementation is what keeps "valid" from
/// meaning two different things.</summary>
public static class AnnounceValidation
{
    /// <summary>Null when the request is valid, otherwise the first problem found.</summary>
    public static ProtocolError? Validate(AnnounceRequest r)
    {
        if (r.ProtocolVersion != AnnounceProtocol.Version)
            return ProtocolError.Of(ProtocolErrorCodes.UnsupportedProtocolVersion,
                $"this master speaks announce v{AnnounceProtocol.Version}",
                SnakeCase(nameof(r.ProtocolVersion)));

        if (r.Port is < 1 or > 65535)
            return Invalid(nameof(r.Port), "port must be 1-65535");

        if (string.IsNullOrWhiteSpace(r.Hostname))
            return Missing(nameof(r.Hostname));
        if (Sanitize(r.Hostname).Length > AnnounceProtocol.HostnameMaxLength)
            return Invalid(nameof(r.Hostname),
                $"hostname exceeds {AnnounceProtocol.HostnameMaxLength} characters");

        if (string.IsNullOrWhiteSpace(r.Map))
            return Missing(nameof(r.Map));
        if (string.IsNullOrWhiteSpace(r.Gametype))
            return Missing(nameof(r.Gametype));
        if (string.IsNullOrWhiteSpace(r.GameVersion))
            return Missing(nameof(r.GameVersion));

        if (r.MaxPlayers < 1)
            return Invalid(nameof(r.MaxPlayers), "max_players must be at least 1");
        if (r.Players < 0)
            return Invalid(nameof(r.Players), "players cannot be negative");
        if (r.Bots < 0)
            return Invalid(nameof(r.Bots), "bots cannot be negative");
        if (r.Players + r.Bots > r.MaxPlayers)
            return Invalid(nameof(r.MaxPlayers), "players + bots exceeds max_players");

        // sv_public 0 servers do not announce at all. Reaching the master with 0 is a client bug, and
        // answering it with a plain rejection is better than listing something that asked not to be.
        if (r.SvPublic != 1)
            return ProtocolError.Of(ProtocolErrorCodes.NotPublic,
                "sv_public 0 servers must not announce", SnakeCase(nameof(r.SvPublic)));

        if (r.AvailableForControl)
        {
            if (string.IsNullOrEmpty(r.ControlKeyFingerprint))
                return Missing(nameof(r.ControlKeyFingerprint));
            if (!IsHexSha256(r.ControlKeyFingerprint))
                return Invalid(nameof(r.ControlKeyFingerprint),
                    $"expected {AnnounceProtocol.ControlKeyFingerprintLength} lowercase hex characters");
        }

        return null;
    }

    /// <summary>Strip control characters from operator-supplied text before it is stored or shown.
    /// Hostnames reach a server browser and a web panel, and a raw newline or an escape sequence in
    /// one is somebody else's rendering bug.</summary>
    public static string Sanitize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var c in value)
            if (!char.IsControl(c))
                buffer[n++] = c;
        return new string(buffer[..n]).Trim();
    }

    public static bool IsHexSha256(string value)
    {
        if (value.Length != AnnounceProtocol.ControlKeyFingerprintLength)
            return false;
        foreach (var c in value)
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }

    private static ProtocolError Invalid(string field, string message) =>
        ProtocolError.Of(ProtocolErrorCodes.InvalidField, message, SnakeCase(field));

    private static ProtocolError Missing(string field) =>
        ProtocolError.Of(ProtocolErrorCodes.MissingField, $"{SnakeCase(field)} is required",
            SnakeCase(field));

    /// <summary>Report the wire name, not the C# name. An operator reading the error is looking at
    /// JSON, and "MaxPlayers" is not a field they can find in it.</summary>
    private static string SnakeCase(string name) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
