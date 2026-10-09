using System;
using System.Runtime.InteropServices;

namespace VortexArena.Legacy.Downloads;

/// <summary>
/// The User-Agent a legacy session sends on HTTP requests a server asks for (package downloads, the client
/// program's uri_get).
///
/// DarkPlaces sends its <c>engineversion</c> string, which is "&lt;game name&gt; &lt;OS name&gt; &lt;build&gt;"
/// (host.c; libcurl.c CheckPendingDownloads with curl_useragent 1, the default) - for Xonotic that reads
/// "Xonotic Windows64 v0.8.6 ..." or "Xonotic Linux ...". Community download sites test for it: one refused
/// every package with 404 to "VortexArena (legacy compatibility; DarkPlaces protocol)" and to a bare "Xonotic",
/// and served the same address to anything beginning "Xonotic Windows64" or "Xonotic Linux". So the string
/// keeps DarkPlaces' shape - the game whose data is being fetched, then the OS under DarkPlaces' own name
/// for it - and says who is really asking in the build position, where DarkPlaces puts its own version.
/// </summary>
public static class LegacyUserAgent
{
    /// <summary>DP_OS_NAME (qdefs.h) for this machine.</summary>
    public static string OsName
    {
        get
        {
            bool wide = IntPtr.Size == 8;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return wide ? "Windows64" : "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)) return "FreeBSD";
            return "Linux";
        }
    }

    /// <summary>What is sent unless the player turns curl_useragent off; curl_useragent_append is added by the caller.</summary>
    public static string Default => $"Xonotic {OsName} VortexArena";

    /// <summary>libcurl.c: the append string follows, separated by one space unless the base already ends in one.</summary>
    public static string WithAppend(string baseAgent, string? append)
    {
        if (string.IsNullOrEmpty(append)) return baseAgent;
        if (baseAgent.Length == 0) return append;
        return baseAgent.EndsWith(' ') ? baseAgent + append : baseAgent + " " + append;
    }
}
