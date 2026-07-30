using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Keeps the plain-text attribution file in sync with the in-game credits roll.
///
/// Vortex Arena redistributes the Xonotic content set directly (repo-restructure D1/D2), so the
/// attribution for the people who authored it has to travel with the content — in the repository and
/// in every release zip, not only compiled into a C# screen. <c>data/licenses/CREDITS</c> is generated
/// from <c>CreditsScreen.cs</c> by <c>tools/gen-credits.py</c>; this is the gate that stops the two
/// drifting apart, since nothing else would notice.
///
/// Four names were mis-transcribed when the credits were first ported (a swapped nickname, an ASCII
/// apostrophe for U+2019, a truncated nickname, and a wrong CJK character). Misspelling a contributor
/// is the kind of defect no build or feature test catches, which is why this exists.
/// </summary>
public class CreditsAttributionTests
{
    // "Ant \"Antibody\" Zucaro",  — a name entry inside the Credits table.
    private static readonly Regex NameLiteral =
        new(@"^\s*""(?<name>(?:[^""\\]|\\.)*)""\s*,\s*$", RegexOptions.Compiled);

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string Unescape(string s) =>
        s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>Names from the Credits table in CreditsScreen.cs, in declaration order.</summary>
    private static List<string> NamesFromSource(string path)
    {
        var names = new List<string>();
        bool inTable = false;
        foreach (string line in File.ReadLines(path))
        {
            if (!inTable)
            {
                if (line.Contains("Credits =", StringComparison.Ordinal))
                    inTable = true;
                continue;
            }
            // The table ends at the closing `};` in column 4.
            if (line.StartsWith("    };", StringComparison.Ordinal))
                break;

            var m = NameLiteral.Match(line);
            if (m.Success)
                names.Add(Unescape(m.Groups["name"].Value));
        }
        return names;
    }

    /// <summary>Names from the generated plain-text file (two-space indented entries).</summary>
    private static List<string> NamesFromCreditsFile(string path) =>
        File.ReadLines(path)
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Trim().Length > 0)
            .Select(l => l.Trim())
            .ToList();

    [Fact]
    public void Generated_Credits_File_Matches_The_Credits_Screen()
    {
        string? repo = RepoRoot();
        if (repo is null)
            return;

        string source = Path.Combine(repo, "game", "menu", "CreditsScreen.cs");
        string generated = Path.Combine(repo, "data", "licenses", "CREDITS");
        if (!File.Exists(source) || !File.Exists(generated))
            return; // pre-restructure checkout, or content tree not staged yet

        var fromSource = NamesFromSource(source);
        var fromFile = NamesFromCreditsFile(generated);

        Assert.True(fromSource.Count > 400,
            $"parsed only {fromSource.Count} names out of CreditsScreen.cs — has the table shape changed? " +
            "If so, tools/gen-credits.py needs updating too, since it parses the same shape.");

        var onlyInSource = fromSource.Except(fromFile).ToList();
        var onlyInFile = fromFile.Except(fromSource).ToList();

        Assert.True(
            onlyInSource.Count == 0 && onlyInFile.Count == 0,
            "data/licenses/CREDITS is out of sync with CreditsScreen.cs — run `python tools/gen-credits.py`.\n"
                + $"  in the screen but not the file ({onlyInSource.Count}): {string.Join(", ", onlyInSource.Take(10))}\n"
                + $"  in the file but not the screen ({onlyInFile.Count}): {string.Join(", ", onlyInFile.Take(10))}");
    }

    [Fact]
    public void Content_Licence_Texts_Travel_With_The_Content()
    {
        string? repo = RepoRoot();
        if (repo is null)
            return;

        string licenses = Path.Combine(repo, "data", "licenses");
        if (!Directory.Exists(licenses))
            return; // content tree not staged yet

        // Redistributing the content means the grant and its licence texts ship alongside it.
        foreach (string required in new[] { "COPYING.xonotic", "GPL-3", "GPL-2", "CREDITS", "README" })
        {
            string path = Path.Combine(licenses, required);
            Assert.True(File.Exists(path), $"data/licenses/{required} is missing — the content licence must " +
                                           "travel with the content it covers");
            Assert.True(new FileInfo(path).Length > 0, $"data/licenses/{required} is empty");
        }
    }
}
