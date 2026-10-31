using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace VortexArena.Net;

/// <summary>
/// Resolves a master server's <c>host:port</c> to the one address a <see cref="MasterServerLink"/> can reach.
/// Shared by the client browser (getservers) and the dedicated server (heartbeats).
///
/// <para>Taking the first DNS answer, as both callers used to, breaks on today's Xonotic masters: three of the
/// four stock ones (<c>dpm6.xonotic.xyz</c>, <c>master3.xonotic.org</c>, <c>master4.xonotic.org</c>) publish
/// only an IPv6 address, and an IPv6 destination on the IPv4 socket fails with "An address incompatible with
/// the requested protocol was used". IPv4 is preferred whenever the name has one; an IPv6-only name resolves
/// only for a caller that holds an IPv6 socket, and otherwise reports why it was skipped.</para>
/// </summary>
public static class MasterAddress
{
    /// <summary>Split <c>host:port</c> (the last colon is the separator). False for a missing host or a bad port.</summary>
    public static bool TrySplit(string hostPort, out string host, out int port)
    {
        host = "";
        port = 0;
        int colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out port) || port <= 0 || port > 65535)
            return false;
        host = hostPort[..colon];
        return true;
    }

    /// <summary>
    /// The address to use out of a DNS answer: the first IPv4 one, else (when <paramref name="allowIPv6"/>) the
    /// first IPv6 one, else null. Anything that is neither family is ignored.
    /// </summary>
    public static IPAddress? Choose(IEnumerable<IPAddress> addresses, bool allowIPv6)
    {
        IPAddress? v6 = null;
        foreach (IPAddress a in addresses)
        {
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return a;
            if (v6 is null && a.AddressFamily == AddressFamily.InterNetworkV6)
                v6 = a;
        }
        return allowIPv6 ? v6 : null;
    }

    /// <summary>
    /// Resolve <paramref name="hostPort"/> through DNS and <see cref="Choose"/> an address. On false,
    /// <paramref name="problem"/> says why in words fit for a log line (bad address, no such host, or an
    /// IPv6-only master with no IPv6 socket to send from). Never throws.
    /// </summary>
    public static bool TryResolve(string hostPort, bool allowIPv6, out IPEndPoint? endPoint, out string? problem,
        System.Func<string, IPAddress[]>? dns = null)
    {
        endPoint = null;
        problem = null;
        if (!TrySplit(hostPort, out string host, out int port))
        {
            problem = "not a host:port address";
            return false;
        }
        IPAddress[] addresses;
        try
        {
            addresses = (dns ?? Dns.GetHostAddresses)(host);
        }
        catch (System.Exception e)
        {
            problem = e.Message;
            return false;
        }
        IPAddress? chosen = Choose(addresses, allowIPv6);
        if (chosen is null)
        {
            problem = addresses.Length == 0 ? "no address"
                : "IPv6-only master, and this machine has no IPv6 socket to reach it from";
            return false;
        }
        endPoint = new IPEndPoint(chosen, port);
        return true;
    }
}
