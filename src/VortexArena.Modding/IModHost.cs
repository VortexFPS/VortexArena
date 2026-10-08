namespace VortexArena.Modding;

/// <summary>
/// Everything a guest can make the host do. This interface IS the capability list: a guest has no
/// filesystem, socket, clock or process access because no member here provides one, not because a
/// check somewhere forbids it. Adding a member widens what a stranger's code can reach, so each one
/// needs the same scrutiny as a new network message.
///
/// Every member is called synchronously from inside a guest call, on the thread that called into the
/// sandbox. Implementations must not block and must not call back into the sandbox: the watchdog
/// cannot interrupt a guest that is parked inside the host.
/// </summary>
public interface IModHost : IModCommandSink
{
    void Log(ModLogLevel level, string message);

    /// <summary>
    /// Copies the record for <paramref name="kind"/> (one of the <c>Mod*State</c> structs) into
    /// <paramref name="destination"/> and returns the bytes written, or -1 when there is no such record
    /// (an entity index out of range). A destination shorter than the record receives a prefix.
    /// </summary>
    int ReadState(ModStateKind kind, int index, Span<byte> destination);

    /// <summary>Number of entities <see cref="ModStateKind.Entity"/> can currently be indexed over.</summary>
    int EntityCount { get; }

    /// <summary>Reads a cvar the mod is allowed to see. Returns false for anything not on the allow-list.</summary>
    bool TryGetCvar(string name, out string value);

    /// <summary>Resolves a path inside the mod's own packs to an id, or 0 when it does not exist there.</summary>
    int ResolveAsset(ModAssetKind kind, string path);

    float MeasureText(int fontId, float size, string text);

    /// <summary>Game time in seconds. The only clock a guest gets.</summary>
    double Time { get; }

    /// <summary>Queues a message for the server half of the mod. Returns false when it was dropped (rate limit).</summary>
    bool SendToServer(ReadOnlySpan<byte> payload);
}

/// <summary>Receives the decoded per-frame command buffer. Coordinates are in the virtual 2D space.</summary>
public interface IModCommandSink
{
    void DrawRect(float x, float y, float width, float height, uint rgba);
    void DrawPic(int assetId, float x, float y, float width, float height, uint rgba);
    void DrawText(int fontId, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8);
    void SetClip(float x, float y, float width, float height);
    void ResetClip();
    void PlaySound(int assetId, int channel, float volume, float pitch);
}
