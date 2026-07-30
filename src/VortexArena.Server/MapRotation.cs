using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The server map rotation + next-map selection — the Godot-free essence of server/intermission.qc's maplist
/// machinery (<c>Maplist_Init</c> / <c>GetNextMap</c> / <c>MaplistMethod_Random/Iterate/Repeat</c> /
/// <c>Map_Check</c> + the recent-maps store <c>Map_MarkAsRecent</c> / <c>Map_IsRecent</c>). It parses
/// <c>g_maplist</c> into an ordered buffer (optionally shuffled), tracks the rotation cursor
/// (<c>g_maplist_index</c>), excludes recently-played maps (<c>g_maplist_mostrecent</c>), and picks the next
/// map by the configured strategy (random → iterate → repeat fallback).
///
/// The map-existence + gametype-support checks (QC <c>fexists</c> / <c>MapInfo_CheckMap</c>) are pluggable
/// (<see cref="MapExists"/> / <see cref="MapSupportsGametype"/>) so this stays Godot-free and testable; a host
/// with the asset layer wires the real checks. The shuffle/random tiebreaks use a seeded RNG for determinism.
/// </summary>
public sealed class MapRotation
{
    private readonly List<string> _buffer = new();
    private int _current;
    private Random _rng;

    /// <summary>Does the named map file exist? (QC <c>fexists</c>). Default true (no asset layer).</summary>
    public Func<string, bool> MapExists { get; set; } = static _ => true;

    /// <summary>Does the map support the current gametype? (QC <c>MapInfo_CheckMap</c>). Default true.</summary>
    public Func<string, bool> MapSupportsGametype { get; set; } = static _ => true;

    public MapRotation(int seed = 0x5EED) => _rng = new Random(seed);

    /// <summary>Reseed the shuffle/random RNG (determinism for headless runs/tests).</summary>
    public void Reseed(int seed) => _rng = new Random(seed);

    /// <summary>The current rotation buffer (read-only).</summary>
    public IReadOnlyList<string> Buffer => _buffer;

    /// <summary>QC <c>Map_Current</c>: the rotation cursor index into the buffer.</summary>
    public int Current => _current;

    /// <summary>How many maps are in the buffer.</summary>
    public int Count => _buffer.Count;

    // =============================================================================================
    // init (QC Maplist_Init)
    // =============================================================================================

    /// <summary>
    /// QC <c>Maplist_Init</c>: parse <c>g_maplist</c> into the buffer (filtering to maps that exist + support
    /// the gametype), optionally shuffling (<c>g_maplist_shuffle</c>), and set the cursor to the current map's
    /// position (<c>g_maplist_index</c>). <paramref name="currentMap"/> is the map running now (so the cursor
    /// resumes after it). Returns the buffer size.
    /// </summary>
    public int Init(string currentMap)
    {
        _buffer.Clear();
        string list = Cvars.String("g_maplist");
        var maps = new List<string>();
        foreach (string m in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (MapExists(m) && MapSupportsGametype(m) && !maps.Contains(m))
                maps.Add(m);

        if (Cvars.Bool("g_maplist_shuffle"))
        {
            // Fisher-Yates with the seeded RNG (QC the inline shuffle in Maplist_Init).
            for (int i = maps.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (maps[i], maps[j]) = (maps[j], maps[i]);
            }
        }
        _buffer.AddRange(maps);

        _current = GetMaplistPosition(currentMap);
        if (_buffer.Count > 0)
            _current = System.Math.Clamp(_current, 0, _buffer.Count - 1);
        return _buffer.Count;
    }

    /// <summary>QC <c>GetMaplistPosition</c>: resume at <c>g_maplist_index</c> if it names the current map, else scan.</summary>
    private int GetMaplistPosition(string currentMap)
    {
        int idx = Cvars.Int("g_maplist_index");
        if (idx >= 0 && idx < _buffer.Count && string.Equals(_buffer[idx], currentMap, StringComparison.OrdinalIgnoreCase))
            return idx;
        for (int i = 0; i < _buffer.Count; i++)
            if (string.Equals(_buffer[i], currentMap, StringComparison.OrdinalIgnoreCase))
                return i;
        return idx < 0 ? 0 : idx;
    }

    // =============================================================================================
    // next-map selection (QC GetNextMap + the three methods)
    // =============================================================================================

    /// <summary>
    /// QC <c>GetNextMap</c>: pick the next map by the configured strategy — random (<c>g_maplist_selectrandom</c>)
    /// then iterate then repeat — honoring the recent-map exclusion. Returns "" if no map qualifies. On success
    /// the cursor + <c>g_maplist_index</c> are advanced to the chosen map.
    /// </summary>
    public string GetNextMap()
    {
        if (_buffer.Count == 0)
            return "";

        int next = -1;
        if (Cvars.Bool("g_maplist_selectrandom"))
            next = MethodRandom();
        if (next == -1)
            next = MethodIterate();
        if (next == -1)
            next = MethodRepeat();

        if (next >= 0)
        {
            _current = next;
            Cvars.Set("g_maplist_index", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return _buffer[next];
        }
        return "";
    }

    /// <summary>Build a ballot of up to <paramref name="count"/> distinct next-map candidates (QC the votable fill).</summary>
    public List<string> BuildBallot(int count)
    {
        var ballot = new List<string>();
        int attempts = _buffer.Count < 2 ? _buffer.Count : System.Math.Min(_buffer.Count * 5, 100);
        for (int i = 0; i < attempts && ballot.Count < count; i++)
        {
            string m = GetNextMap();
            if (!string.IsNullOrEmpty(m) && !ballot.Contains(m))
                ballot.Add(m);
        }
        return ballot;
    }

    private int MethodRandom()
    {
        if (_buffer.Count < 2) return -1;
        for (int tries = 0; tries < 43; tries++)
        {
            int m = (_current + 1 + _rng.Next(_buffer.Count - 1)) % _buffer.Count;
            if (MapCheck(m, 1)) return m;
        }
        return -1;
    }

    private int MethodIterate()
    {
        for (int i = 1, m = (_current + 1) % _buffer.Count; i <= _buffer.Count; i++, m = (m + 1) % _buffer.Count)
            if (MapCheck(m, 1)) return m;
        return (_current + 1) % _buffer.Count; // fallback: the literal next map
    }

    private int MethodRepeat()
        => MapCheck(_current, 2) ? _current : -1;

    /// <summary>QC <c>Map_Check</c>: pass≤1 excludes recent maps + checks gametype support; pass 2 is existence-only.</summary>
    private bool MapCheck(int index, int pass)
    {
        if (index < 0 || index >= _buffer.Count) return false;
        string m = _buffer[index];
        if (pass <= 1 && IsRecent(m)) return false;
        if (pass == 2) return MapExists(m);
        return MapExists(m) && MapSupportsGametype(m);
    }

    // =============================================================================================
    // recent maps (QC Map_MarkAsRecent / Map_IsRecent via g_maplist_mostrecent)
    // =============================================================================================

    /// <summary>QC <c>Map_MarkAsRecent</c>: prepend a map to the recent list, truncated to the configured count.</summary>
    public void MarkAsRecent(string map)
    {
        if (string.IsNullOrEmpty(map)) return;
        int keep = System.Math.Max(0, Cvars.Int("g_maplist_mostrecent_count"));
        var words = new List<string> { map };
        foreach (string w in Cvars.String("g_maplist_mostrecent").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!string.Equals(w, map, StringComparison.OrdinalIgnoreCase) && words.Count < keep)
                words.Add(w);
        Cvars.Set("g_maplist_mostrecent", string.Join(' ', words));
    }

    /// <summary>QC <c>Map_IsRecent</c>: is the map in the recent-played list?</summary>
    public static bool IsRecent(string map)
    {
        if (string.IsNullOrEmpty(map)) return false;
        foreach (string w in Cvars.String("g_maplist_mostrecent").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(w, map, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
