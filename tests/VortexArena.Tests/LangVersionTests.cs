using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <c>Directory.Build.props</c>'s <c>LangVersion</c> and <c>global.json</c>'s SDK floor must agree, because
/// nothing else makes them.
///
/// <para><b>The failure this exists for, reported 2026-08-20.</b> global.json asks for SDK <c>8.0.0</c>, so an
/// SDK 8 satisfies it and the version check passes. <c>LangVersion</c> was <c>13.0</c>, which needs SDK 9 —
/// C# 13 does not exist in SDK 8's compiler. The result on a machine with only SDK 8 was that every project
/// failed to COMPILE with <c>CS1617: Invalid option '13.0' for /langversion</c>, several steps away from
/// either file that caused it. It surfaced on a Fedora ppc64le box, but there is nothing PowerPC about it:
/// SDK 8 is what most distributions package, so any of them would have done.</para>
///
/// <para><b>Why a test rather than a comment.</b> The two files are edited for unrelated reasons, by people
/// looking at one of them, and neither build nor test fails on a machine that happens to have the newer SDK —
/// which every dev box and CI runner here does. That is the same shape as
/// <see cref="LocaleInvarianceTests"/>: a setting whose breakage is invisible exactly where it is edited, and
/// visible only to whoever has the more modest machine.</para>
/// </summary>
public class LangVersionTests
{
    /// <summary>
    /// Highest C# language version each .NET SDK major can compile. Extend this when adopting a new SDK; a
    /// major that is missing is treated as "newer than we know", which is the permissive direction and the
    /// right one — this test exists to catch a lang version that is too HIGH for the declared SDK, not to
    /// police SDKs it has never heard of.
    /// </summary>
    private static readonly Dictionary<int, int> MaxCSharpBySdkMajor = new()
    {
        [6] = 10,
        [7] = 11,
        [8] = 12,
        [9] = 13,
        [10] = 14,
    };

    [Fact]
    public void LangVersion_Is_Supported_By_The_Sdk_Global_Json_Accepts()
    {
        string propsPath = Path.Combine(TestPaths.RepoRoot, "Directory.Build.props");
        string globalPath = Path.Combine(TestPaths.RepoRoot, "global.json");

        Assert.True(File.Exists(propsPath), $"missing {propsPath}");
        Assert.True(File.Exists(globalPath), $"missing {globalPath}");

        Match lang = Regex.Match(File.ReadAllText(propsPath), @"<LangVersion>\s*([^<\s]+)\s*</LangVersion>");
        Assert.True(lang.Success,
            "Directory.Build.props has no <LangVersion>. It was removed or renamed — which puts the tree back "
            + "on the compiler's default and reintroduces the drift the pin exists to stop (CI on one C# "
            + "version, dev boxes on another).");

        string value = lang.Groups[1].Value;

        // "latest"/"preview" are precisely what the pin replaced: they resolve differently per SDK, so the
        // same source compiles as a different language on CI than on a dev box.
        Assert.True(double.TryParse(value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double langVersion),
            $"LangVersion is '{value}', not a concrete version number. 'latest' and 'preview' mean different "
            + "things on different SDKs, which is the drift this pin exists to prevent — name the version.");

        using JsonDocument global = JsonDocument.Parse(File.ReadAllText(globalPath));
        string sdkVersion = global.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        int sdkMajor = int.Parse(sdkVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);

        if (!MaxCSharpBySdkMajor.TryGetValue(sdkMajor, out int maxCSharp))
            return;   // an SDK newer than this table knows about: nothing to assert, and guessing would be worse

        Assert.True(langVersion <= maxCSharp,
            $"LangVersion is {value} but global.json accepts SDK {sdkVersion}, whose compiler tops out at "
            + $"C# {maxCSharp}. A machine with only SDK {sdkMajor} passes global.json's check and then fails "
            + $"EVERY project with 'CS1617: Invalid option \\'{value}\\' for /langversion' — which is what "
            + "happened on 2026-08-20.\n"
            + $"    Either lower LangVersion to {maxCSharp}.0, or raise global.json's sdk.version to "
            + $"{(int)langVersion - 1}.0.0 in the SAME change — and know that raising it drops every machine "
            + "whose distribution ships an older SDK, which is most of them.");
    }

    /// <summary>
    /// <c>rollForward</c> must keep rolling FORWARD. It is what lets a newer SDK build this tree at all, and
    /// the guard above is only safe because the declared version is a floor rather than an exact pin: if this
    /// became <c>disable</c> or an exact match, every machine without that precise SDK would be locked out.
    /// </summary>
    [Fact]
    public void Global_Json_Still_Rolls_Forward()
    {
        using JsonDocument global = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "global.json")));
        string roll = global.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString()!;

        Assert.True(roll is "latestMajor" or "major" or "latestMinor" or "minor" or "latestFeature" or "feature"
                         or "latestPatch" or "patch",
            $"global.json sets rollForward to '{roll}'. Anything that does not roll forward pins the tree to "
            + "one exact SDK, so a machine with a newer one cannot build it — and the LangVersion guard "
            + "beside this test assumes the declared version is a FLOOR.");
    }
}
