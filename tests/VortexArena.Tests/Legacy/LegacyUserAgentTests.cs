using VortexArena.Legacy.Downloads;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The User-Agent of a legacy session's HTTP requests keeps the shape of DarkPlaces' engineversion
/// ("Xonotic OS build"): a community download site answered 404 to anything else.
/// </summary>
public class LegacyUserAgentTests
{
    [Fact]
    public void The_Default_Begins_As_DarkPlaces_Does_And_Names_This_Client()
    {
        string agent = LegacyUserAgent.Default;
        Assert.StartsWith("Xonotic " + LegacyUserAgent.OsName + " ", agent);
        Assert.Contains("VortexArena", agent);
        Assert.Contains(LegacyUserAgent.OsName, new[] { "Windows64", "Windows", "Linux", "macOS", "FreeBSD" });
    }

    [Theory]
    [InlineData("Xonotic Linux X", "", "Xonotic Linux X")]
    [InlineData("Xonotic Linux X", "mod/1", "Xonotic Linux X mod/1")]
    [InlineData("Xonotic Linux X ", "mod/1", "Xonotic Linux X mod/1")]
    [InlineData("", "mod/1", "mod/1")]
    public void The_Append_String_Follows_After_One_Space(string agent, string append, string expected) =>
        Assert.Equal(expected, LegacyUserAgent.WithAppend(agent, append));
}
