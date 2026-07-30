// Port of Base/data/xonotic-data.pk3dir/qcsrc/common/util.qc (cons / FOREACH_WORD) +
// common/command/generic.qc (maplist_shuffle).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XonoticGodot.Engine.Console;

/// <summary>
/// Space-separated word-list helpers — the C# successor to the QuakeC string utilities the generic command
/// family leans on: <c>cons(a, b)</c> (common/util.qc) and the <c>FOREACH_WORD(list, cond, body)</c> macro
/// (common/util.qh), plus <c>maplist_shuffle</c> (common/command/generic.qc). Pure and Godot-free.
///
/// <para>The QC semantics these mirror exactly (parity trap R5): <c>cons("", x) == "x"</c> (no leading
/// space), <c>cons(a, "") == "a"</c>, otherwise <c>strcat(a, " ", b)</c>; a "word" is a maximal run of
/// non-whitespace separated by spaces (QC <c>tokenizebyseparator(s, " ")</c> drops empty tokens). A naive
/// <c>string.Join(" ")</c> that leaves a leading space would diverge — these don't.</para>
/// </summary>
public static class WordList
{
    /// <summary>
    /// QC <c>cons(a, b)</c> (common/util.qc): <c>a</c> if <c>b == ""</c>, <c>b</c> if <c>a == ""</c>,
    /// else <c>strcat(a, " ", b)</c> — append <paramref name="b"/> to the list <paramref name="a"/> with a
    /// single separating space and NO leading space on an empty list.
    /// </summary>
    public static string Cons(string a, string b)
    {
        if (string.IsNullOrEmpty(b)) return a ?? "";
        if (string.IsNullOrEmpty(a)) return b;
        return a + " " + b;
    }

    /// <summary>
    /// Split a space-separated list into its words (QC <c>tokenizebyseparator(s, " ")</c> / the iteration the
    /// <c>FOREACH_WORD</c> macro performs): maximal non-whitespace runs, empties dropped. Tabs/newlines are
    /// treated as whitespace too (the console tokenizer's separators), matching how cfg-set lists arrive.
    /// </summary>
    public static List<string> Words(string? list)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(list))
            return result;
        int i = 0, n = list.Length;
        var sb = new StringBuilder();
        while (i < n)
        {
            char c = list[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
            i++;
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Re-join words into a space-separated list (the inverse of <see cref="Words"/>); no leading space.</summary>
    public static string Join(IEnumerable<string> words)
    {
        string acc = "";
        foreach (string w in words)
            acc = Cons(acc, w);
        return acc;
    }

    /// <summary>
    /// QC <c>maplist_shuffle(input)</c> (common/command/generic.qc): a Fisher-Yates style shuffle of the words
    /// of <paramref name="input"/>. The port uses the injected <paramref name="rng"/> (seedable for
    /// deterministic tests) where QC calls <c>random()</c>; the result is a permutation of the same words.
    /// </summary>
    public static string Shuffle(string input, Random rng)
    {
        List<string> words = Words(input);
        // QC iterates _cnt = 0..count-1, inserting each word at a random position in [0.._cnt].
        // The classic in-place Fisher–Yates below produces the same uniform permutation distribution.
        for (int i = words.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (words[i], words[j]) = (words[j], words[i]);
        }
        return Join(words);
    }
}
