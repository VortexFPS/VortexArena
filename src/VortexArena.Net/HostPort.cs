namespace VortexArena.Net;

/// <summary>
/// Choosing a UDP port to host on: is this number a port at all, is anything already using it, and what did
/// the user type after <c>--port</c>.
///
/// <para><b>Why "is it free" needs a deliberate probe.</b> Godot's ENet binds with address reuse, so starting
/// a listen server on a port another program already owns <em>succeeds</em>. The other program then receives
/// the inbound packets, the listen server's self-connect never completes, and the game sits on the loading
/// screen forever with nothing in the log to explain it. A DarkPlaces client also defaults to 26000, so this
/// is an ordinary collision rather than a theoretical one. <see cref="IsFree"/> therefore probes with a bind
/// that does NOT enable address reuse, so it fails against an existing binder instead of joining it.</para>
///
/// <para><b>Why it lives here rather than beside the transport.</b> None of this needs Godot: it is
/// arithmetic, a BCL socket and a string parse. Keeping it in the engine library is what lets the suite test
/// it — <c>game/</c> is the Godot host and the tests cannot reference it — and the range check in particular
/// is the kind of guard that looks like defensive noise and gets deleted by someone who does not know what it
/// is for. <c>NetTransport.Server.IsPortFree</c> delegates here.</para>
/// </summary>
public static class HostPort
{
    /// <summary>Lowest bindable port. 0 is excluded deliberately: it means "let the OS choose", which is never
    /// what someone hosting a game server wants and would silently ignore a typo.</summary>
    public const int Min = 1;

    /// <summary>Highest bindable port — the ceiling of the 16-bit port field.</summary>
    public const int Max = 65535;

    /// <summary>
    /// True when <paramref name="port"/> is a number a socket could actually be bound to.
    ///
    /// <para>This exists as its own step because the failure it prevents is not "the bind fails".
    /// <see cref="System.Net.IPEndPoint"/>'s constructor THROWS <see cref="ArgumentOutOfRangeException"/> on an
    /// out-of-range port, and that is not a <see cref="System.Net.Sockets.SocketException"/> — so a probe that
    /// skipped this check would take the whole host path down with an unhandled exception instead of reporting
    /// the port unusable.</para>
    /// </summary>
    public static bool IsValid(int port) => port is >= Min and <= Max;

    /// <summary>
    /// True when nothing else on this machine is bound to UDP <paramref name="port"/>. Invalid ports are
    /// reported as not free rather than throwing — see <see cref="IsValid"/>.
    ///
    /// <para>Probe-then-bind is racy in principle: another program can take the port between this returning
    /// true and the real bind. In practice the window is microseconds, and the loser of that race gets the
    /// same "port busy" answer one step later, so the race cannot produce the silent-swallow this guards
    /// against.</para>
    /// </summary>
    public static bool IsFree(int port)
    {
        if (!IsValid(port))
            return false;

        try
        {
            using var probe = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            // What actually matters is that this probe does NOT enable SO_REUSEADDR: a default UDP bind
            // already fails against an existing binder, which is how an occupied port is detected.
            //
            // MEASURED (Windows, 2026-08-19), because the comment this replaces claimed more than was true:
            // flipping this flag to false on its own changes NOTHING - HostPortTests still passes - so the
            // exclusive-use flag is not by itself what makes the probe work here. Adding SO_REUSEADDR to the
            // probe DOES break it (A_Port_Held_With_Address_Reuse_Is_Still_Not_Free fails), which is the
            // regression the test actually guards. The flag stays because it states the intent explicitly and
            // is the documented way to ask for exclusivity; it is belt, not braces.
            probe.ExclusiveAddressUse = true;
            probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse the value given after <c>--port</c> on the command line.
    ///
    /// <para>Returns false with a player-readable <paramref name="error"/> for a missing value, something that
    /// is not a number, and a number outside <see cref="Min"/>..<see cref="Max"/>. The caller decides what to
    /// do about it; the boot path logs the error and carries on with the default port rather than refusing to
    /// start, because a mistyped flag should not stop someone reaching the menu.</para>
    /// </summary>
    /// <param name="text">The raw argument, or null when <c>--port</c> was the last thing on the line.</param>
    /// <param name="port">The parsed port, or 0 when this returns false.</param>
    /// <param name="error">Null on success; otherwise one sentence naming what was wrong.</param>
    public static bool TryParse(string? text, out int port, out string? error)
    {
        port = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"expects a port number ({Min}-{Max})";
            return false;
        }

        // NumberStyles.Integer + InvariantCulture: a port is a bare number, and the machine's locale has no
        // business in it (Directory.Build.props sets InvariantGlobalization, but being explicit here means the
        // rule survives that property being revisited).
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out int parsed))
        {
            error = $"'{text}' is not a port number";
            return false;
        }

        if (!IsValid(parsed))
        {
            error = $"{parsed} is out of range ({Min}-{Max})";
            return false;
        }

        port = parsed;
        error = null;
        return true;
    }
}
