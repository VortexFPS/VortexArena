using System.Net;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="MasterAddress"/>: which address of a master's DNS answer the browser and the heartbeat use.
/// Most stock Xonotic masters now publish only an IPv6 address, and taking the first answer handed those to
/// an IPv4 socket ("An address incompatible with the requested protocol was used").
/// </summary>
public class MasterAddressTests
{
    private static readonly IPAddress V4 = IPAddress.Parse("164.90.169.60");
    private static readonly IPAddress V6 = IPAddress.Parse("2a03:b0c0:3:d0::169f:f001");

    [Fact]
    public void Prefers_IPv4_Even_When_IPv6_Is_Listed_First()
    {
        Assert.Equal(V4, MasterAddress.Choose(new[] { V6, V4 }, allowIPv6: true));
        Assert.Equal(V4, MasterAddress.Choose(new[] { V6, V4 }, allowIPv6: false));
    }

    [Fact]
    public void An_IPv6_Only_Master_Resolves_Only_For_An_IPv6_Caller()
    {
        Assert.Equal(V6, MasterAddress.Choose(new[] { V6 }, allowIPv6: true));
        Assert.Null(MasterAddress.Choose(new[] { V6 }, allowIPv6: false));
    }

    [Fact]
    public void TryResolve_Reports_Why_An_IPv6_Only_Master_Was_Skipped()
    {
        bool ok = MasterAddress.TryResolve("dpm6.xonotic.xyz:27777", allowIPv6: false, out IPEndPoint? ep,
            out string? problem, dns: _ => new[] { V6 });
        Assert.False(ok);
        Assert.Null(ep);
        Assert.Contains("IPv6-only", problem);
    }

    [Fact]
    public void TryResolve_Keeps_The_Port_And_Never_Throws()
    {
        Assert.True(MasterAddress.TryResolve("master4.xonotic.org:42863", allowIPv6: true, out IPEndPoint? ep,
            out _, dns: _ => new[] { V6 }));
        Assert.Equal(new IPEndPoint(V6, 42863), ep);

        Assert.False(MasterAddress.TryResolve("gone.example:27950", allowIPv6: true, out _, out string? problem,
            dns: _ => throw new System.Net.Sockets.SocketException(11001)));
        Assert.False(string.IsNullOrEmpty(problem));

        Assert.False(MasterAddress.TryResolve("no-port", allowIPv6: true, out _, out _, dns: _ => new[] { V4 }));
        Assert.False(MasterAddress.TryResolve("host:0", allowIPv6: true, out _, out _, dns: _ => new[] { V4 }));
    }
}
