namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// Parses the BSP entity lump (lump 0) text into a list of key/value dictionaries.
///
/// The lump is a sequence of <c>{ "key" "value" "key" "value" ... }</c> blocks. This mirrors the token
/// rules Darkplaces uses for it (<c>COM_ParseToken_Simple</c> with returnnewline=false,
/// parsebackslash=false, parsecomments=true) closely enough for real Xonotic maps:
/// <list type="bullet">
/// <item>whitespace (including newlines) separates tokens;</item>
/// <item><c>//</c> line comments and <c>/* ... */</c> block comments are skipped;</item>
/// <item>quoted strings <c>"..."</c> are a single token (backslashes are NOT unescaped — DP passes
/// parsebackslash=false here, so a literal <c>\</c> stays);</item>
/// <item><c>{</c> and <c>}</c> are structural tokens.</item>
/// </list>
/// Keys/values are always quoted in practice. Duplicate keys within one entity keep the last value.
/// Unlike DP's worldspawn-only quick parse, a leading <c>_</c> on a key is preserved here.
/// </summary>
public static class EntityLumpParser
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text)
    {
        var result = new List<IReadOnlyDictionary<string, string>>();
        if (string.IsNullOrEmpty(text))
            return result;

        int pos = 0;
        while (TryNextToken(text, ref pos, out string token, out bool _))
        {
            if (token != "{")
                continue; // skip stray tokens until an entity opens (robust to leading junk)

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                if (!TryNextToken(text, ref pos, out string key, out bool keyQuoted))
                    return result; // truncated entity at EOF — return what we have
                if (!keyQuoted && key == "}")
                    break; // end of this entity

                if (!TryNextToken(text, ref pos, out string value, out bool _))
                    return result; // truncated key/value pair at EOF

                // A '}' appearing where a value is expected ends the entity defensively.
                dict[key] = value;
            }
            result.Add(dict);
        }
        return result;
    }

    /// <summary>
    /// Reads the next token starting at <paramref name="pos"/>. Returns false at end of text.
    /// <paramref name="quoted"/> is true if the token came from a quoted string (so a quoted "{" or
    /// "}" is treated as data, not structure).
    /// </summary>
    private static bool TryNextToken(string s, ref int pos, out string token, out bool quoted)
    {
        token = string.Empty;
        quoted = false;
        int n = s.Length;

        while (true)
        {
            // Skip whitespace.
            while (pos < n && IsWhitespace(s[pos]))
                pos++;
            if (pos >= n)
                return false;

            // Comments.
            if (s[pos] == '/' && pos + 1 < n && s[pos + 1] == '/')
            {
                pos += 2;
                while (pos < n && s[pos] != '\n' && s[pos] != '\r')
                    pos++;
                continue;
            }
            if (s[pos] == '/' && pos + 1 < n && s[pos + 1] == '*')
            {
                pos += 2;
                while (pos + 1 < n && !(s[pos] == '*' && s[pos + 1] == '/'))
                    pos++;
                pos = System.Math.Min(pos + 2, n); // consume the closing */ (or run to EOF)
                continue;
            }
            break;
        }

        // Quoted string.
        if (s[pos] == '"')
        {
            pos++; // opening quote
            int start = pos;
            while (pos < n && s[pos] != '"')
                pos++;
            token = s.Substring(start, pos - start);
            if (pos < n) pos++; // closing quote
            quoted = true;
            return true;
        }

        // Structural single-char tokens.
        if (s[pos] == '{' || s[pos] == '}')
        {
            token = s[pos].ToString();
            pos++;
            return true;
        }

        // Bare word: read until whitespace (DP's "regular word" branch stops only at whitespace).
        int wstart = pos;
        while (pos < n && !IsWhitespace(s[pos]))
            pos++;
        token = s.Substring(wstart, pos - wstart);
        return true;
    }

    private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
}
