using System.Globalization;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The machine's locale must not reach the numbers.
///
/// <para>Found the hard way (2026-08-03): a contributor running a Russian-locale box saw
/// <c>DeferredCommandsTests</c> fail because <c>$"{remaining,9:0.00}"</c> rendered
/// <c>"-> In      1,50: restart"</c>. Plain interpolation formats with <see cref="CultureInfo.CurrentCulture"/>,
/// and ru-RU's decimal separator is a comma. That one site was fixed with
/// <see cref="FormattableString.Invariant"/>, but ~191 similar sites existed across the tree, so the real fix
/// is <c>&lt;InvariantGlobalization&gt;</c> in Directory.Build.props.</para>
///
/// <para>These tests exist because that fix is INVISIBLE. It is one line in a props file, it has no callers,
/// and nothing else in the suite fails if someone removes it — every other test would keep passing on an
/// en-US machine and start failing only for the next contributor with a comma locale. This is the guard that
/// makes that a red build here instead of a bug report from someone else.</para>
///
/// <para><b>Why the tests force a culture rather than trusting the CI box:</b> the dev machine and the CI
/// runners are all en-US, where a broken build and a fixed one are indistinguishable. Each test below sets a
/// comma-decimal culture explicitly, so it is a real check on every machine rather than a tautology on most.</para>
/// </summary>
public class LocaleInvarianceTests
{
    /// <summary>
    /// The switch is ON. Asserted directly because every other test here would also pass on an en-US box with
    /// the property deleted — this is the one that actually fails when it goes missing.
    /// </summary>
    [Fact]
    public void GlobalizationIsInvariant()
    {
        // In globalization-invariant mode every culture collapses to the invariant one, so the ambient
        // culture reports an empty name no matter what the OS is set to.
        Assert.Equal(string.Empty, CultureInfo.CurrentCulture.Name);
        Assert.Equal(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
    }

    /// <summary>
    /// A comma-decimal culture cannot be imposed on the process. Under invariant globalization, constructing a
    /// specific culture throws — the assertion is that ANY attempt to make numbers locale-shaped fails or is
    /// neutralised, which is exactly the property a config file that must round-trip depends on.
    /// </summary>
    [Fact]
    public void ACommaDecimalCultureCannotChangeFormatting()
    {
        CultureInfo? ru = null;
        try { ru = new CultureInfo("ru-RU"); }
        catch (CultureNotFoundException) { /* invariant mode refused it outright — the strongest outcome */ }

        if (ru is not null)
        {
            // Some hosts hand back a culture object whose DATA is invariant rather than throwing. Either way
            // the separator must be a period; that is the property under test, not which path got us there.
            Assert.Equal(".", ru.NumberFormat.NumberDecimalSeparator);
            Assert.Equal("1.50", 1.5f.ToString("0.00", ru));
        }
    }

    /// <summary>
    /// Formatting stays period-based even with a comma culture pinned to the thread — the shape of the
    /// original failure, exercised through plain interpolation (the construct that actually broke) rather
    /// than through an explicitly-invariant call that could never have failed.
    /// </summary>
    [Fact]
    public void InterpolationStaysPeriodBased_EvenWithAThreadCulturePinned()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            TryPinCommaCulture();
            float f = 1.5f;
            Assert.Equal("1.50", $"{f:0.00}");
            Assert.Equal("-> In      1.50", $"-> In {f,9:0.00}");
            Assert.DoesNotContain(",", $"{1234.5678:F4}");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// The half that silently corrupts rather than merely looking wrong: a cvar written as "1.50" must parse
    /// back as 1.5 on a comma-locale machine. Under CurrentCulture parsing, "1.50" on ru-RU parses to 150 —
    /// a config that survives a round-trip on one machine and multiplies a value by 100 on another.
    /// </summary>
    [Fact]
    public void ParsingPeriodDecimalsRoundTrips_EvenWithAThreadCulturePinned()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            TryPinCommaCulture();
            Assert.True(float.TryParse("1.50", out float parsed));
            Assert.Equal(1.5f, parsed, 5);
            Assert.Equal(0.75f, float.Parse("0.75"), 5);
            // The round trip a cvar actually takes: format, store, read back.
            Assert.Equal(2.25f, float.Parse($"{2.25f:0.00}"), 5);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// The reported failure itself, kept as a regression case against the exact string a human saw. DP prints
    /// this with <c>%9.2f</c>, which is period-always in C's default locale.
    /// </summary>
    [Fact]
    public void DeferredCommandsDescribe_UsesAPeriod_OnACommaLocale()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            TryPinCommaCulture();
            var q = new DeferredCommands();
            q.Defer(2f, "restart", now: 0f);
            string line = q.Describe(0.5f)[0];
            Assert.Contains("1.50", line);
            Assert.DoesNotContain("1,50", line);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// Best-effort attempt to make the thread comma-decimal. Under invariant globalization this cannot
    /// succeed — which is the point. It must not throw, because a test that dies while SETTING UP the hostile
    /// condition proves nothing about the condition.
    /// </summary>
    private static void TryPinCommaCulture()
    {
        try { CultureInfo.CurrentCulture = new CultureInfo("ru-RU"); }
        catch (CultureNotFoundException) { /* refused: already the outcome we want */ }
    }
}
