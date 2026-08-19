using System.Net;
using System.Net.Sockets;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Guards on <see cref="HostPort"/> — the port checks that stand between a listen server and the failure it
/// exists to prevent: hosting on a port another program already owns, which Godot's ENet permits (it binds
/// with address reuse) and which then swallows the self-connect, leaving the game on the loading screen
/// forever with nothing in the log.
///
/// <para>These tests exist because both halves of that guard look like defensive noise, and nothing else in
/// the suite would catch either one being deleted. The range check reads as belt-and-braces until you know
/// that <see cref="IPEndPoint"/> THROWS rather than failing the bind, so skipping it takes the host path down
/// instead of reporting the port unusable. The probe reads as an ordinary bind until you know it must not
/// enable address reuse, or it joins the other binder instead of detecting it.</para>
///
/// <para><b>Each test below was checked against the mutation it is supposed to catch</b> (2026-08-19), because
/// a guard test that passes whatever the code does is worse than none. Deleting the range check fails five
/// cases here; giving the probe <c>SO_REUSEADDR</c> fails
/// <see cref="A_Port_Held_With_Address_Reuse_Is_Still_Not_Free"/>. One thing that did NOT move the tests:
/// flipping <see cref="Socket.ExclusiveAddressUse"/> to false on its own, on Windows — so that flag is not
/// what makes the probe work here, and <see cref="HostPort"/> now says so rather than implying otherwise.</para>
/// </summary>
public class HostPortTests
{
    // ---- range ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(26000)]        // the game's default
    [InlineData(65535)]
    public void Valid_Ports_Are_Accepted(int port) => Assert.True(HostPort.IsValid(port));

    [Theory]
    [InlineData(0)]            // "let the OS choose" — never what a host wants, and usually a typo
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Out_Of_Range_Ports_Are_Rejected(int port) => Assert.False(HostPort.IsValid(port));

    /// <summary>
    /// The specific reason the range check exists: an out-of-range port must come back as "not free" and must
    /// NOT throw. <see cref="IPEndPoint"/>'s constructor raises <see cref="ArgumentOutOfRangeException"/>,
    /// which is not a <see cref="SocketException"/> and so would sail straight through the probe's catch and
    /// take the caller down. Asserting "does not throw" is the whole point of this test.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Out_Of_Range_Ports_Report_Busy_Rather_Than_Throwing(int port)
    {
        bool free = HostPort.IsFree(port);   // must not throw
        Assert.False(free);
    }

    // ---- the probe --------------------------------------------------------------------------------------

    /// <summary>A port this test is holding is not free. The baseline: if this fails, the probe is not
    /// probing.</summary>
    [Fact]
    public void A_Bound_Port_Is_Not_Free()
    {
        using Socket held = Bind(out int port);
        Assert.False(HostPort.IsFree(port));
    }

    /// <summary>
    /// <b>The test that encodes the actual bug.</b> A port held by a socket that set
    /// <see cref="Socket.ExclusiveAddressUse"/> to false — i.e. bound with address reuse, exactly as Godot's
    /// ENet does — must still be reported busy. A naive probe (one that binds the same permissive way) returns
    /// "free" here, hosts anyway, and the packets go to the other binder.
    /// </summary>
    [Fact]
    public void A_Port_Held_With_Address_Reuse_Is_Still_Not_Free()
    {
        using var held = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        held.ExclusiveAddressUse = false;
        held.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        held.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)held.LocalEndPoint!).Port;

        Assert.False(HostPort.IsFree(port));
    }

    /// <summary>
    /// A port becomes free again once released — the probe reports live state rather than latching.
    ///
    /// <para>Retried across a few ports because the OS may hand the just-released ephemeral port to another
    /// process in the window between the release and the probe. That race is rare and is not what is under
    /// test, so the test asks only that it succeed for one of several attempts rather than pinning a specific
    /// port and inheriting the flake.</para>
    /// </summary>
    [Fact]
    public void A_Released_Port_Becomes_Free_Again()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int port;
            using (Bind(out port)) { /* held only for as long as this block */ }
            if (HostPort.IsFree(port))
                return;
        }

        Assert.Fail("no released port was reported free across 5 attempts — the probe is latching, or this "
                    + "machine is unusually busy on the ephemeral range");
    }

    /// <summary>Bind an OS-assigned UDP port and report which one, so no test has to pick a fixed number and
    /// hope the machine running it agrees.</summary>
    private static Socket Bind(out int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        port = ((IPEndPoint)s.LocalEndPoint!).Port;
        return s;
    }

    // ---- the --port argument ----------------------------------------------------------------------------

    [Theory]
    [InlineData("26000", 26000)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("  26001  ", 26001)]   // trimmed: a quoted argument can arrive padded
    public void TryParse_Accepts_A_Port(string text, int expected)
    {
        Assert.True(HostPort.TryParse(text, out int port, out string? error));
        Assert.Equal(expected, port);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]        // `--port` was the last thing on the command line
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("26000x")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("99999999999999")]   // overflows int: must be rejected, not wrapped
    public void TryParse_Rejects_Anything_Unbindable(string? text)
    {
        Assert.False(HostPort.TryParse(text, out int port, out string? error));
        Assert.Equal(0, port);
        // The message is what the boot path logs, so an empty one would leave a player with a flag that
        // silently did nothing — which is the behaviour this replaced.
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>A rejected value must not reach the caller as a port. Stated separately because "returns
    /// false" and "leaves the out parameter alone" are different promises, and the boot path relies on the
    /// second to keep hosting on its default.</summary>
    [Fact]
    public void TryParse_Leaves_No_Port_Behind_On_Failure()
    {
        Assert.False(HostPort.TryParse("70000", out int port, out string? error));
        Assert.Equal(0, port);
        Assert.Contains("70000", error);
        Assert.Contains("out of range", error);
    }
}
