using System;
using System.Reflection;
using System.Text;

namespace VortexArena.Common;

/// <summary>
/// What this build calls itself — the one place the game's version string is derived.
///
/// <para>Stamped from the assembly's <see cref="AssemblyInformationalVersionAttribute"/>, which SourceLink
/// fills with <c>&lt;version&gt;+&lt;commit sha&gt;</c> and which CI can override wholesale
/// (<c>-p:InformationalVersion=…</c>) with no new plumbing. A local build therefore reports its actual
/// commit rather than pretending to be a release.</para>
///
/// <para>Upstream's counterpart is the <c>g_xonoticversion</c> cvar, whose shipped value is the literal
/// string <c>git</c> — a placeholder their release tooling substitutes at package time. That is no use as a
/// build identifier for anyone running from source, which is why this is read off the binary instead of out
/// of a config file.</para>
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The build string, e.g. <c>1.0.0+a3c3c25a</c> (release builds: just <c>1.2.3</c>). Safe to embed in a
    /// Darkplaces infostring and in the colon-separated <c>qcstatus</c> line — see <see cref="Sanitize"/>.
    /// </summary>
    public static string Version { get; } = Resolve();

    /// <summary>How many characters of the commit sha to keep. Enough to identify a commit, short enough to
    /// sit in a server-browser column.</summary>
    private const int ShaLength = 8;

    private static string Resolve()
    {
        string raw = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";

        if (string.IsNullOrWhiteSpace(raw))
            return "0.0.0-dev";

        // SourceLink appends "+<40-char sha>". A full sha is unreadable in a list column and pointlessly
        // wide on the wire, so keep a short prefix of it.
        int plus = raw.IndexOf('+');
        if (plus >= 0)
        {
            string version = raw[..plus];
            string sha = raw[(plus + 1)..];
            raw = sha.Length > ShaLength ? $"{version}+{sha[..ShaLength]}" : raw;
        }
        return Sanitize(raw);
    }

    /// <summary>
    /// Strip the characters that would corrupt the two wire formats this string travels in: <c>\</c> separates
    /// Darkplaces infostring keys from values, and <c>:</c> separates <c>qcstatus</c> tokens — a version
    /// containing either would be read as the start of the next field. Quotes and whitespace go too, since the
    /// value also passes through the config interpreter's tokenizer. Replaced rather than dropped so a mangled
    /// version is visibly odd instead of quietly shorter.
    /// </summary>
    public static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c is ':' or '\\' or '"' or '\'' or ';' || char.IsWhiteSpace(c) ? '-' : c);
        return sb.ToString();
    }
}
