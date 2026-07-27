using System.Text;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// The server event log — the Godot-free essence of server/gamelog.qc (<c>GameLogEcho</c> / <c>GameLogInit</c>
/// / <c>GameLogClose</c> + the colon-delimited <c>:event:...</c> line format). It is the structured match log
/// admins parse (XonStat, stat trackers, server tooling): connection, join/part, kills, team changes, votes,
/// scores, the gamestart/gameover bookends.
///
/// Two sinks, exactly like QC: a console sink (<c>sv_eventlog_console</c>) and a file sink
/// (<c>sv_eventlog_files</c>, lazily opened with a zero-padded match counter and a <c>:logversion:3</c> header
/// line). The master <c>sv_eventlog</c> gate lives at the CALL SITES (QC's pattern), so <see cref="Echo"/>
/// itself always writes to whatever sinks are enabled; <see cref="Init"/>/<see cref="Close"/> are gated by the
/// caller. Every echoed line is also captured in <see cref="Recent"/> for inspection/tests.
/// </summary>
public sealed class GameLog
{
    /// <summary>Console sink (QC <c>dedicated_print</c>). When null, console output is dropped.</summary>
    public Action<string>? ConsoleSink { get; set; }

    /// <summary>
    /// File sink (QC <c>fputs(logfile, ...)</c>). Given (filename, line), it appends the line. When null and
    /// <c>sv_eventlog_files</c> is set, lines append to the computed file via <see cref="System.IO.File"/>.
    /// A host/test can override this to redirect or capture file output without touching disk.
    /// </summary>
    public Action<string, string>? FileSink { get; set; }

    private readonly List<string> _recent = new();
    private const int RecentCap = 4096;

    /// <summary>The most-recent echoed lines (capped), for inspection / tests.</summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>QC <c>matchid</c>: the unique id for this match, set at <see cref="Init"/> by the host.</summary>
    public string MatchId { get; set; } = "";

    private bool _fileOpen;
    private string _fileName = "";

    /// <summary>Provider for "now" as a timestamp string (QC strftime). Host can inject; default is empty.</summary>
    public Func<string> TimestampProvider { get; set; } = static () => "";

    /// <summary>
    /// Resolves the bare counter-named log file to a full path. The host wires this to the user-data dir
    /// (<c>XonData/logs/</c>), which is what <c>server.cfg.example</c> documents and where every other writable
    /// file in this codebase lives. Unset ⇒ the bare name, i.e. the process CWD — which for a dedicated server
    /// installed under Program Files, run as a service, or in a container is typically not writable.
    /// </summary>
    public Func<string, string>? PathResolver { get; set; }

    /// <summary>Raised once per session when the file sink cannot be written, so a silent failure can't hide a
    /// whole match's log. The host prints it; tests can assert it.</summary>
    public Action<string>? WriteErrorSink { get; set; }

    private bool _writeErrorReported;

    /// <summary>Clear all captured lines + close any open file (test/world reset).</summary>
    public void Reset()
    {
        _recent.Clear();
        Close();
        _fileOpen = false;
    }

    // =============================================================================================
    // the sink (QC GameLogEcho) — note: does NOT check sv_eventlog (call sites do)
    // =============================================================================================

    /// <summary>QC <c>GameLogEcho</c>: write a line to the enabled sinks + capture it.</summary>
    public void Echo(string s)
    {
        _recent.Add(s);
        if (_recent.Count > RecentCap)
            _recent.RemoveRange(0, _recent.Count - RecentCap);

        if (Cvars.Bool("sv_eventlog_files"))
        {
            EnsureFileOpen();
            string line = Cvars.Bool("sv_eventlog_files_timestamps")
                ? $":time:{TimestampProvider()}\n{s}"
                : s;
            if (FileSink is not null)
                FileSink(_fileName, line);
            else if (!string.IsNullOrEmpty(_fileName))
                TryAppend(_fileName, line + "\n");
        }

        if (Cvars.Bool("sv_eventlog_console"))
            ConsoleSink?.Invoke(s);
    }

    private void EnsureFileOpen()
    {
        if (_fileOpen)
            return;
        _fileOpen = true;

        // QC: increment + persist the match counter, build "<prefix><00000000+counter><suffix>".
        int matches = Cvars.Int("sv_eventlog_files_counter") + 1;
        Cvars.Set("sv_eventlog_files_counter", matches.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string num = matches.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (num.Length < 8)
            num = new string('0', 8 - num.Length) + num;
        string bare = Cvars.String("sv_eventlog_files_nameprefix") + num + Cvars.String("sv_eventlog_files_namesuffix");
        _fileName = PathResolver is not null ? PathResolver(bare) : bare;

        if (FileSink is not null)
            FileSink(_fileName, ":logversion:3");
        else
            TryAppend(_fileName, ":logversion:3\n");
    }

    private void TryAppend(string file, string text)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(file, text);
        }
        catch (Exception ex)
        {
            // A read-only / sandboxed host: the console sink + Recent still capture the log. Report ONCE —
            // silently dropping every line meant an operator got no event log and no diagnostic at all, while
            // sv_eventlog_files_counter kept incrementing as if it were working.
            if (_writeErrorReported) return;
            _writeErrorReported = true;
            WriteErrorSink?.Invoke($"[eventlog] cannot write '{file}' ({ex.GetType().Name}: {ex.Message}); "
                                   + "file logging disabled for this session (console sink unaffected).");
        }
    }

    // =============================================================================================
    // lifecycle (QC GameLogInit / GameLogClose)
    // =============================================================================================

    /// <summary>
    /// QC <c>GameLogInit</c>: emit the <c>:gamestart:</c> + <c>:gameinfo:</c> bookend lines. Requires
    /// <see cref="MatchId"/> set. The caller gates this on <c>sv_eventlog</c>.
    /// </summary>
    public void Init(string gametype, string mapName, IEnumerable<string>? mutators = null)
    {
        Echo($":gamestart:{gametype}_{mapName}:{MatchId}");
        var sb = new StringBuilder(":gameinfo:mutators:LIST");
        if (mutators is not null)
            foreach (string m in mutators)
                sb.Append(':').Append(m);
        Echo(sb.ToString());
        Echo(":gameinfo:end");
    }

    /// <summary>QC <c>GameLogClose</c>: close the log file. We append per-line (no held handle), so this just
    /// drops the open latch so the next match's first <see cref="Echo"/> opens a fresh, counter-named file.</summary>
    public void Close()
    {
        _fileOpen = false;
        _fileName = "";
    }

    // =============================================================================================
    // line builders (QC the strcat/sprintf call sites) — emit common events
    // =============================================================================================

    /// <summary>QC <c>:join:&lt;playerid&gt;:&lt;entity&gt;:&lt;ip|bot&gt;:&lt;name&gt;</c>.</summary>
    public void Join(Player p)
        => Echo($":join:{p.PlayerId}:{p.Index}:{ProcessIp(p.IsBot ? "bot" : p.NetAddress)}:{p.NetName}");

    /// <summary>QC <c>:connect:&lt;playerid&gt;:&lt;entity&gt;:&lt;ip|bot&gt;</c>.</summary>
    public void Connect(Player p)
        => Echo($":connect:{p.PlayerId}:{p.Index}:{(p.IsBot ? "bot" : ProcessIp(p.NetAddress))}");

    /// <summary>QC <c>:part:&lt;playerid&gt;</c>.</summary>
    public void Part(Player p) => Echo($":part:{p.PlayerId}");

    /// <summary>QC <c>:name:&lt;playerid&gt;:&lt;name&gt;</c>.</summary>
    public void NameChange(Player p) => Echo($":name:{p.PlayerId}:{p.NetName}");

    /// <summary>QC <c>:kill:&lt;mode&gt;:&lt;killer&gt;:&lt;killed&gt;:type=&lt;deathtype&gt;</c>.</summary>
    public void Kill(string mode, int killerId, int killedId, string deathType)
        => Echo($":kill:{mode}:{killerId}:{killedId}:type={deathType}");

    /// <summary>QC <c>:team:&lt;playerid&gt;:&lt;team&gt;:&lt;type&gt;</c> (no-op for an unassigned id).</summary>
    public void TeamChange(int playerId, int team, string type)
    {
        if (playerId < 1) return;
        Echo($":team:{playerId}:{ServerTeamCode(team)}:{type}");
    }

    /// <summary>
    /// Translate this port's team value to the code Base's SERVER writes into the event log.
    /// <c>common/teams.qh</c> defines NUM_TEAM_* twice: the CSQC branch is 4/13/12/9 and the SVQC branch is
    /// 5/14/13/10. <see cref="Teams"/> uses the CSQC set repo-wide, but <c>GameLogEcho</c> is server code, so a
    /// stat parser written against Base reads our 13 as YELLOW when it means BLUE, and 4/12/9 as unknown teams.
    /// Convert only at the log boundary — the in-engine values stay as they are.
    /// </summary>
    public static int ServerTeamCode(int portTeam) => portTeam switch
    {
        Teams.Red => 5,
        Teams.Blue => 14,
        Teams.Yellow => 13,
        Teams.Pink => 10,
        _ => portTeam, // 0 / unassigned / already-server-coded: pass through
    };

    /// <summary>QC <c>:gameover</c>.</summary>
    public void GameOver() => Echo(":gameover");

    /// <summary>QC <c>GameLog_ProcessIP</c>: optionally replace ':' with '_' in IPv6 addresses.</summary>
    public static string ProcessIp(string s)
        => Cvars.Bool("sv_eventlog_ipv6_delimiter") ? s.Replace(':', '_') : s;
}
