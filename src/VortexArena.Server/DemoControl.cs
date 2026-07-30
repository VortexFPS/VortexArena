using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// Server-side demo recording control — the C# home for the demo feature that, in Xonotic, lives almost
/// entirely in the engine (the <c>sv_autodemo</c> server-demo recorder and the per-client <c>cl_autodemo</c>
/// recorder) with only thin QC glue. There is no QuakeC <c>record</c>/<c>stop</c> command: the QC side just
/// (a) blesses a per-client autodemo so it is not auto-deleted when the client joins a match or sets a record,
/// and (b) leaves the actual byte recording to the engine. This class is the faithful server analogue: it
/// drives recording start/stop at match boundaries (gated by <c>sv_autodemo</c>), decides which clients get a
/// per-client demo (<c>sv_autodemo_perclient</c>), and tracks the "keep this demo" (don't-discard) preservation
/// — handing the actual recording to the host via <see cref="StartRecording"/>/<see cref="StopRecording"/>
/// (which a real engine host wires to its demo writer).
/// </summary>
public sealed class DemoControl
{
    /// <summary>Begin recording the whole-server demo to the given name (host wires to the engine recorder).</summary>
    public Action<string>? StartRecording { get; set; }

    /// <summary>Stop the whole-server demo (host wires to the engine recorder).</summary>
    public Action? StopRecording { get; set; }

    /// <summary>Begin a per-client demo (clientId, name). Host wires to the engine per-client recorder.</summary>
    public Action<int, string>? StartClientRecording { get; set; }

    /// <summary>Stop a per-client demo (clientId).</summary>
    public Action<int>? StopClientRecording { get; set; }

    /// <summary>True while a whole-server demo is recording.</summary>
    public bool Recording { get; private set; }

    /// <summary>The name of the demo currently recording, or "" when idle.</summary>
    public string CurrentDemoName { get; private set; } = "";

    private readonly HashSet<int> _clientRecording = new();
    private readonly HashSet<int> _clientKeep = new(); // QC: cl_autodemo_delete bit 0 cleared = "keep this demo"

    /// <summary>Build a demo name (QC <c>cl_autodemo_nameformat</c> spirit): <c>demos/&lt;map&gt;_&lt;gametype&gt;_&lt;n&gt;</c>.</summary>
    public static string NameFor(string mapName, string gametype, int counter)
        => $"demos/{mapName}_{gametype}_{counter}";

    /// <summary>
    /// QC the match-start demo hook: when <c>sv_autodemo</c> is set, begin recording the whole-server demo for
    /// this match, and begin per-client demos per <c>sv_autodemo_perclient</c> (1 = players, 2 = all clients).
    /// No-op (and stops any stale recording) when <c>sv_autodemo</c> is off.
    /// </summary>
    public void OnMatchStart(string mapName, string gametype, IEnumerable<Player> clients)
    {
        if (!Cvars.Bool("sv_autodemo"))
        {
            if (Recording) OnMatchEnd();
            return;
        }

        int counter = Cvars.Int("sv_eventlog_files_counter"); // share the match counter for a stable name
        CurrentDemoName = NameFor(mapName, gametype, counter);
        Recording = true;
        StartRecording?.Invoke(CurrentDemoName);

        int perClient = Cvars.Int("sv_autodemo_perclient");
        if (perClient > 0)
            foreach (Player p in clients)
                if (ShouldRecordClient(p, perClient))
                    BeginClientDemo(p, mapName, gametype, counter);
    }

    /// <summary>QC the match-end demo hook: stop the whole-server demo + every per-client demo.</summary>
    public void OnMatchEnd()
    {
        if (Recording)
        {
            StopRecording?.Invoke();
            Recording = false;
            CurrentDemoName = "";
        }
        foreach (int id in new List<int>(_clientRecording))
            StopClientRecording?.Invoke(id);
        _clientRecording.Clear();
    }

    /// <summary>
    /// QC <c>sv_autodemo_perclient</c> selection: 0 = none, 1 = in-game players, 2 = all clients. A bot is never
    /// recorded per-client (no remote stream to capture).
    /// </summary>
    public static bool ShouldRecordClient(Player p, int perClientMode)
    {
        if (perClientMode <= 0 || p.IsBot) return false;
        if (perClientMode == 1) return !p.IsDead; // players (in-game)
        return true;                              // all clients
    }

    /// <summary>Begin a per-client demo for a client that connects mid-match while recording is active.</summary>
    public void OnClientConnect(Player p, string mapName, string gametype)
    {
        if (!Recording) return;
        int perClient = Cvars.Int("sv_autodemo_perclient");
        if (perClient > 0 && ShouldRecordClient(p, perClient))
            BeginClientDemo(p, mapName, gametype, Cvars.Int("sv_eventlog_files_counter"));
    }

    /// <summary>Stop a client's per-client demo on disconnect.</summary>
    public void OnClientDisconnect(Player p)
    {
        if (_clientRecording.Remove(p.Index))
            StopClientRecording?.Invoke(p.Index);
        _clientKeep.Remove(p.Index);
    }

    private void BeginClientDemo(Player p, string mapName, string gametype, int counter)
    {
        if (!_clientRecording.Add(p.Index)) return;
        StartClientRecording?.Invoke(p.Index, NameFor(mapName + "_" + p.NetName, gametype, counter));
    }

    /// <summary>
    /// QC the demo-preservation glue (spawnpoints.qc / cl_race.qc): mark a client's current demo as "keep"
    /// (clear the auto-delete bit) — done when the client joins a match (<c>cl_autodemo_delete_keepmatches</c>)
    /// or sets a personal record (<c>cl_autodemo_delete_keeprecords</c>). The actual deletion is the engine's;
    /// here we record the intent so a host can honor it.
    /// </summary>
    public void KeepDemo(Player p) => _clientKeep.Add(p.Index);

    /// <summary>Whether a client's current demo has been marked to keep (not auto-delete).</summary>
    public bool IsDemoKept(Player p) => _clientKeep.Contains(p.Index);

    /// <summary>Is a per-client demo currently recording for this client?</summary>
    public bool IsClientRecording(Player p) => _clientRecording.Contains(p.Index);
}
