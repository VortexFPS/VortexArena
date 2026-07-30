namespace XonoticGodot.Formats;

/// <summary>
/// Thrown when a binary asset (BSP / MD3) is malformed: bad magic, wrong version,
/// truncated data, a lump/section that extends past the end of the buffer, or a
/// "funny lump size" (a lump length that is not a whole multiple of its record size).
///
/// This mirrors the fatal <c>Host_Error</c> paths in the Darkplaces loaders, but as a
/// recoverable C# exception so a host can skip a bad asset instead of crashing.
/// </summary>
public sealed class AssetParseException : Exception
{
    public AssetParseException(string message) : base(message) { }

    public AssetParseException(string message, Exception inner) : base(message, inner) { }
}
