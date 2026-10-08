// Port of Base/darkplaces/cl_parse.c CL_NetworkTimeReceived (lines 3294-3397) and the two lines of
// cl_main.c CL_Frame that advance the client's clock (2879-2880).
namespace VortexArena.Legacy.Protocol;

/// <summary>
/// cl.time on a live connection: the client's own idea of the server's clock.
///
/// The server stamps every update with its time (svc_time). The client does not jump to those stamps -
/// they arrive with network jitter - but runs its own clock at wall speed and lets each stamp nudge
/// it. Everything timed is derived from this clock: the time written into input commands (which the
/// server uses for lag compensation), the interpolation point between the last two updates, and the
/// <c>time</c> global the client program animates by.
///
/// The clock aims at the <em>previous</em> stamp, not the newest: a client draws one update behind
/// so that it always has two to interpolate between.
///
/// No wall clock in here. The owner calls <see cref="Advance"/> once per frame with the frame's
/// length, then <see cref="NetworkTimeReceived"/> for each svc_time parsed in that frame.
/// </summary>
public sealed class DpClientClock
{
    private const int TimeSyncErrors = 32; // NUM_TS_ERRORS
    private readonly float[] _errors = new float[TimeSyncErrors];
    private int _errorIndex;

    /// <summary>cl.time.</summary>
    public double Time { get; private set; }
    /// <summary>cl.oldtime: <see cref="Time"/> before the last <see cref="Advance"/>.</summary>
    public double OldTime { get; private set; }
    /// <summary>cl.mtime[0]: the newest stamp.</summary>
    public double ServerTime { get; private set; }
    /// <summary>cl.mtime[1]: the one before, never more than 0.1 s older.</summary>
    public double ServerPrevTime { get; private set; }

    /// <summary>cl_nettimesyncboundmode, 0..7. DarkPlaces defaults to 6; Xonotic's configuration sets 5.</summary>
    public int BoundMode { get; set; } = 6;
    /// <summary>cl_nettimesyncfactor (modes 1-3).</summary>
    public double SyncFactor { get; set; }
    /// <summary>cl_nettimesyncboundtolerance (modes 2 and 3).</summary>
    public double BoundTolerance { get; set; } = 0.25;
    /// <summary>cl.movevars_timescale: the server's slowmo.</summary>
    public double TimeScale { get; set; } = 1;
    /// <summary>cl.movevars_ticrate, or 0 if unknown (mode 7 aims one tick behind the newest stamp).</summary>
    public double TicRate { get; set; }

    /// <summary>cls.demoplayback: the clock runs free between the recording's time stamps and is only
    /// pulled forward over a gap. <see cref="TimeDemo"/> (cls.timedemo): the clock IS the newest stamp.</summary>
    public bool Demo { get; set; }
    public bool TimeDemo { get; set; }

    public void Reset()
    {
        Time = OldTime = ServerTime = ServerPrevTime = 0;
        Array.Clear(_errors);
        _errorIndex = 0;
    }

    /// <summary>CL_Frame: the clock runs on by one frame. <paramref name="frameTime"/> is wall time,
    /// which the C limits to 0.1 s ("networking assumes at least 10fps") and scales by slowmo.</summary>
    public void Advance(double frameTime, bool paused = false)
    {
        OldTime = Time;
        if (!paused)
            Time += Math.Clamp(frameTime, 0, 0.1) * TimeScale;
    }

    /// <summary>
    /// CL_NetworkTimeReceived: a svc_time arrived.
    /// </summary>
    /// <param name="signedOn">cls.signon == SIGNONS. Until then the clock simply is the server's.</param>
    public void NetworkTimeReceived(double newTime, bool signedOn)
    {
        ServerPrevTime = ServerTime;
        ServerTime = newTime;
        if (ServerPrevTime == ServerTime || !signedOn || (Demo && TimeDemo))
        {
            Time = ServerPrevTime = newTime;
            return;
        }
        if (Demo)
        {
            // "when time falls behind during demo playback it means the cl.mtime[1] was altered due to a
            // large time gap, so treat it as an instant change in time"
            if (Time < newTime - 0.1) ServerPrevTime = Time = newTime;
            return;
        }

        ServerPrevTime = Math.Max(ServerPrevTime, ServerTime - 0.1);
        double m0 = ServerTime, m1 = ServerPrevTime;
        // An unknown mode is reset to the default by the C before it switches on it.
        int mode = BoundMode is >= 0 and <= 7 ? BoundMode : 6;
        switch (mode)
        {
            case 1:
            case 2:
            case 3:
            {
                Time += (m1 - Time) * Math.Clamp(SyncFactor, 0, 1);
                double timeHigh = m1 + (m0 - m1) * BoundTolerance;
                if (mode == 1)
                    Time = Bound(m1, Time, m0);
                else if (mode == 2)
                {
                    if (Time < m1 || Time > timeHigh) Time = m1;
                }
                else if ((Time < m1 && OldTime < m1) || (Time > timeHigh && OldTime > timeHigh))
                    Time = m1;
                break;
            }
            case 4:
                if (Math.Abs(Time - m1) > 0.5) Time = m1;                       // reset
                else if (Math.Abs(Time - m1) > 0.1) Time += 0.5 * (m1 - Time);  // fast
                else if (Time > m1) Time -= 0.002 * TimeScale;                  // fall into the past by 2ms
                else Time += 0.001 * TimeScale;                                 // creep forward 1ms
                break;
            case 5:
                if (Math.Abs(Time - m1) > 0.5) Time = m1;
                else if (Math.Abs(Time - m1) > 0.1) Time += 0.5 * (m1 - Time);
                else Time = Bound(Time - 0.002 * TimeScale, m1, Time + 0.001 * TimeScale);
                break;
            case 6:
                Time = Bound(m1, Time, m0);
                Time = Bound(Time - 0.002 * TimeScale, m1, Time + 0.001 * TimeScale);
                break;
            case 7:
            {
                // "the rolling harmonic mean gives large time error outliers low significance;
                // correction rate is dynamic and gradual (max 10% of mean error per tic)"
                // in event of packet loss, cl.mtime[1] could be very old, so avoid if possible
                double target = TicRate > 0 ? m0 - TicRate : m1;
                _errors[_errorIndex] = 1.0f / Math.Max((float)Math.Abs(Time - target), 1.17549435e-38f); // FLT_MIN
                _errorIndex = (_errorIndex + 1) % TimeSyncErrors;
                float error = 0;
                for (int i = 0; i < TimeSyncErrors; i++) error += _errors[i];
                error = 0.1f / (error / TimeSyncErrors);
                Time = Bound(Time - error, target, Time + error);
                break;
            }
        }
    }

    // mathlib.h bound(): min is tested first, which is what decides a reversed interval.
    private static double Bound(double min, double value, double max) => value < min ? min : value > max ? max : value;
}
