using System;
using System.Collections.Generic;
using System.Linq;

namespace VortexArena.Engine.Console;

/// <summary>What kind of console entity a <see cref="SearchCandidate"/> is (drives its colour + label).</summary>
public enum SearchKind
{
    /// <summary>A registered command (DP <c>cmd_function_t</c>) — printed green, like DP's <c>^2</c>.</summary>
    Command,

    /// <summary>A cvar (DP <c>cvar_t</c>) — printed yellow, like DP's <c>^3</c>.</summary>
    Cvar,

    /// <summary>An alias (DP <c>cmd_alias_t</c>) — printed cyan, like DP's <c>^5</c>. Its "description" is its body.</summary>
    Alias,
}

/// <summary>One searchable console entity: its kind, its name, and the text <c>search</c> may match besides the
/// name (a cvar/command help string, or an alias body).</summary>
public readonly record struct SearchCandidate(SearchKind Kind, string Name, string Description);

/// <summary>A candidate that matched, with the relevance score it was ranked by.</summary>
public readonly record struct SearchHit(SearchKind Kind, string Name, string Description, double Score);

/// <summary>
/// The relevance engine behind <c>search</c>/<c>apropos</c> — DP's <c>Cmd_Apropos_f</c> (cmd.c:1400) widened
/// from "one glob, print in declaration order" to "several keywords, ranked".
///
/// <para>DP already matched the pattern against <c>cvar-&gt;description</c> as well as the name, but only ever
/// took ONE pattern, so a natural-language query was impossible: <c>apropos max fps</c> globbed for the literal
/// string "max fps", which nothing contains. Here every argument is a separate keyword, ALL of which must appear
/// (in the name or the description) for a candidate to match — so <c>search max fps</c> finds <c>cl_maxfps</c>
/// through its name and <c>cl_maxidlefps</c> through both.</para>
///
/// <para>Ranking exists because description matching alone is far too broad — "max" and "fps" between them
/// appear in hundreds of help strings. Scoring is deliberately name-biased: a keyword in the NAME is worth
/// several times one in the description, and the strongest single signal is the keywords appearing contiguously
/// in the name once separators are ignored (<c>max</c>+<c>fps</c> → <c>maxfps</c> ⊂ <c>cl_<b>maxfps</b></c>),
/// which is what lifts the cvar the player meant above its neighbours.</para>
///
/// <para>Results are printed WORST FIRST, best last, so the most likely answer ends up on the line directly
/// above the prompt where a long result set has just scrolled everything else away. That inverts DP's ordering
/// on purpose; it is the ordering a scrollback console actually wants.</para>
///
/// <para>Pure and Godot-free: the caller supplies the candidates and formats the hits.</para>
/// </summary>
public static class ConsoleSearch
{
    /// <summary>Characters that separate words inside a cvar/command name (<c>cl_max_fps</c>, <c>gl-foo</c>).</summary>
    private static readonly char[] NameSeparators = { '_', '-', '.', ':' };

    /// <summary>
    /// Rank every candidate that matches ALL <paramref name="keywords"/>, ascending by score — element 0 is the
    /// least likely, the last element the most likely. Ties break by name so the order is stable. An empty
    /// keyword list matches nothing (the caller prints usage instead).
    /// </summary>
    public static List<SearchHit> Rank(IReadOnlyList<string> keywords, IEnumerable<SearchCandidate> candidates)
    {
        var hits = new List<SearchHit>();
        if (keywords is null || keywords.Count == 0 || candidates is null)
            return hits;

        string[] keys = keywords
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k.ToLowerInvariant())
            .ToArray();
        if (keys.Length == 0)
            return hits;

        // The keywords run together ("max","fps" -> "maxfps"), compared against the name with its separators
        // removed. This is the contiguity test that distinguishes cl_maxfps from cl_maxidlefps.
        string joined = string.Concat(keys);
        string phrase = string.Join(' ', keys);

        foreach (SearchCandidate c in candidates)
        {
            if (string.IsNullOrEmpty(c.Name))
                continue;
            string name = c.Name.ToLowerInvariant();
            string desc = (c.Description ?? "").ToLowerInvariant();

            double score = ScoreOrReject(keys, joined, phrase, name, desc);
            if (double.IsNegativeInfinity(score))
                continue;
            hits.Add(new SearchHit(c.Kind, c.Name, c.Description ?? "", score));
        }

        // Ascending: worst first, best LAST (see the type remarks).
        hits.Sort((a, b) =>
        {
            int byScore = a.Score.CompareTo(b.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(b.Name, a.Name);
        });
        return hits;
    }

    /// <summary>
    /// The relevance score for one candidate, or <see cref="double.NegativeInfinity"/> when some keyword is
    /// absent from both the name and the description (the AND gate — the candidate is not a match at all).
    /// </summary>
    private static double ScoreOrReject(string[] keys, string joined, string phrase, string name, string desc)
    {
        string[] words = name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        string squashed = Squash(name);

        double score = 0;
        bool allInName = true;

        foreach (string k in keys)
        {
            // --- name tiers (strongest first; only the best one counts) ---
            double inName;
            if (words.Any(w => Matches(w, k))) inName = 14;                 // a whole name word IS the keyword
            else if (words.Any(w => StartsWith(w, k))) inName = 10;         // a name word begins with it
            else if (Contains(name, k)) inName = 7;                         // anywhere in the name
            else { inName = 0; allInName = false; }

            // --- description tiers (a keyword in BOTH is stronger than in either alone, so these add) ---
            double inDesc;
            if (WordIn(desc, k)) inDesc = 3;
            else if (Contains(desc, k)) inDesc = 1;
            else inDesc = 0;

            if (inName == 0 && inDesc == 0)
                return double.NegativeInfinity;    // keyword matched nowhere — reject the candidate
            score += inName + inDesc;
        }

        // The keywords, run together, appear contiguously in the name once separators are dropped. The single
        // most telling signal that this is the cvar the player was describing: "max fps" -> cl_MAXFPS.
        if (keys.Length > 1 && Contains(squashed, joined))
            score += 40;

        if (allInName)
            score += 25;

        // A name word that IS the whole query ("maxfps") — a near-exact hit even with a prefix in front of it.
        if (keys.Length > 1 && words.Any(w => Matches(w, joined)))
            score += 12;

        // Exact-name hits: the player typed the cvar, spaced or not (`search cl maxfps`, `search cl_maxfps`).
        if (Matches(name, joined) || Matches(squashed, joined) ||
            Matches(name, string.Join('_', keys)) || Matches(name, phrase))
            score += 100;

        // Positional agreement: names read prefix-first, so a query whose last word ends the name (…_fps) or
        // whose first word opens it is more likely to be the intended one.
        if (EndsWith(name, keys[^1])) score += 6;
        if (StartsWith(name, keys[0])) score += 4;

        // The whole phrase spelled out in the description ("maximum fps cap" for a `search fps cap`).
        if (keys.Length > 1 && Contains(desc, phrase))
            score += 12;

        // Brevity tiebreak: between two names that matched the same way, the shorter is the more general — and
        // is nearly always the one meant (cl_maxfps over cl_maxfps_alwayssleep). Small on purpose: it settles
        // ties, it never outweighs a real match signal.
        score += Math.Max(0, 24 - name.Length) * 0.25;

        return score;
    }

    /// <summary>The name with its word separators removed (<c>cl_max_fps</c> → <c>clmaxfps</c>).</summary>
    private static string Squash(string name)
    {
        Span<char> buf = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        int n = 0;
        foreach (char ch in name)
            if (Array.IndexOf(NameSeparators, ch) < 0)
                buf[n++] = ch;
        return new string(buf[..n]);
    }

    // ---- keyword matching: a plain substring, or a glob when the keyword carries * / ? (DP ispattern) --------

    private static bool IsPattern(string k) => k.IndexOf('*') >= 0 || k.IndexOf('?') >= 0;

    private static bool Matches(string s, string k) => IsPattern(k) ? Glob(s, k) : s == k;

    private static bool Contains(string s, string k)
        => IsPattern(k) ? Glob(s, "*" + k + "*") : s.Contains(k, StringComparison.Ordinal);

    private static bool StartsWith(string s, string k)
        => IsPattern(k) ? Glob(s, k + "*") : s.StartsWith(k, StringComparison.Ordinal);

    private static bool EndsWith(string s, string k)
        => IsPattern(k) ? Glob(s, "*" + k) : s.EndsWith(k, StringComparison.Ordinal);

    /// <summary>True when <paramref name="k"/> appears in <paramref name="text"/> delimited by non-alphanumerics
    /// (so "fps" is a whole word in "maximum fps cap" but not in "maxfps").</summary>
    private static bool WordIn(string text, string k)
    {
        if (IsPattern(k))
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => Glob(w.Trim(',', '.', ';', ':', '(', ')', '"'), k));
        int at = 0;
        while ((at = text.IndexOf(k, at, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + k.Length;
            bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk)
                return true;
            at = end;
        }
        return false;
    }

    /// <summary>
    /// DP <c>matchpattern</c>'s subset: <c>*</c> (any run) and <c>?</c> (one character), matched case-sensitively
    /// against already-lowercased text. Iterative with a backtrack point, so a pathological pattern can't blow
    /// the stack the way naive recursion would.
    /// </summary>
    public static bool Glob(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;      // remember where the star was, and how far the text had got
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;    // backtrack: let the last star swallow one more character
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
