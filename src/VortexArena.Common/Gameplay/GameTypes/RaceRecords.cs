using System.Globalization;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The outcome of recording a finish time (QC race_setTime's branches → the INFO_RACE_* notifications).</summary>
public enum RaceRecordKind
{
    /// <summary>The time didn't make the rankings (worse than the player's own ranked time, or worse than rank N).</summary>
    Fail,
    /// <summary>A brand-new ranked time at a position that was previously empty (QC INFO_RACE_NEW_SET).</summary>
    NewSet,
    /// <summary>The player improved their own existing ranked time (QC INFO_RACE_NEW_IMPROVED).</summary>
    NewImproved,
    /// <summary>The player broke someone else's record at this rank (QC INFO_RACE_NEW_BROKEN).</summary>
    NewBroken,
}

/// <summary>The detail of a <see cref="RaceRecords.SetTime"/> call, for the host to pick the right notification.</summary>
public readonly struct RaceRecordResult
{
    public readonly RaceRecordKind Kind;
    public readonly int NewPos;          // 1-based rank the time landed at (0 = unranked)
    public readonly int OldPos;          // the player's previous rank (0 = none)
    public readonly float OldRecordTime; // the time previously held at NewPos (0 = empty)
    public readonly string OldRecordHolder;
    public readonly bool IsServerRecord; // NewPos == 1
    public RaceRecordResult(RaceRecordKind kind, int newPos, int oldPos, float oldTime, string oldHolder)
    { Kind = kind; NewPos = newPos; OldPos = oldPos; OldRecordTime = oldTime; OldRecordHolder = oldHolder; IsServerRecord = newPos == 1; }
}

/// <summary>
/// The persistent race/CTS record database — the C# successor to QuakeC's <c>ServerProgsDB</c> race ranking
/// store (server/race.qc <c>race_readTime</c>/<c>race_writeTime</c>/<c>race_readPos</c>/<c>race_setTime</c>).
/// Keys mirror QC's <c>strcat(map, record_type, field, ftos(pos))</c> scheme, so a top-<see cref="RankingsCnt"/>
/// table of (time, owner-UID) is kept per map per record type (race vs CTS). Static + global like ServerProgsDB;
/// the host persists it across matches via <see cref="Export"/>/<see cref="Import"/> (e.g. to a server file).
///
/// Times are stored in seconds (lower is better). A record can only be stored for a player with a persistent
/// id (QC crypto_idfp) — anonymous runs can't be ranked, matching QC's "missing UID" guard.
/// </summary>
public static class RaceRecords
{
    /// <summary>QC RANKINGS_CNT (common/constants.qh): the number of ranked positions kept per map.</summary>
    public const int RankingsCnt = 99;

    /// <summary>QC RACE_RECORD / CTS_RECORD (common/util.qh): the record-type key segment.</summary>
    public const string RaceRecord = "/race100record/";
    public const string CtsRecord = "/cts100record/";

    private static readonly Dictionary<string, string> _db = new(StringComparer.Ordinal);
    // uid -> display name (QC the /uid2name/ sub-db), for naming record holders.
    private static readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    private static string Key(string map, string type, string field, int pos)
        => map + type + field + pos.ToString(CultureInfo.InvariantCulture);

    private static float ParseTime(string? s)
        => string.IsNullOrEmpty(s) ? 0f : float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>QC <c>race_readTime(map, pos)</c>: the ranked time at a position (0 = none / out of range).</summary>
    public static float ReadTime(string map, string type, int pos)
    {
        if (pos < 1 || pos > RankingsCnt) return 0f;
        return _db.TryGetValue(Key(map, type, "time", pos), out string? s) ? ParseTime(s) : 0f;
    }

    /// <summary>QC <c>race_readUID(map, pos)</c>: the owner UID at a position ("" = none).</summary>
    public static string ReadUid(string map, string type, int pos)
        => (pos >= 1 && pos <= RankingsCnt && _db.TryGetValue(Key(map, type, "uid", pos), out string? s)) ? s : "";

    /// <summary>QC <c>race_readName(map, pos)</c>: the display name of the holder at a position ("" = none).</summary>
    public static string ReadName(string map, string type, int pos)
    {
        string uid = ReadUid(map, type, pos);
        return uid.Length == 0 ? "" : (_names.TryGetValue(uid, out string? n) ? n : uid);
    }

    /// <summary>QC <c>race_readPos(map, t)</c>: the 1-based rank a time of <paramref name="t"/> would take (0 = unranked).</summary>
    public static int ReadPos(string map, string type, float t)
    {
        for (int i = 1; i <= RankingsCnt; i++)
        {
            float mt = ReadTime(map, type, i);
            if (mt == 0f || mt > t) return i;
        }
        return 0; // worse than every ranked time
    }

    private static void WriteSlot(string map, string type, int pos, float t, string uid)
    {
        _db[Key(map, type, "time", pos)] = t.ToString(CultureInfo.InvariantCulture);
        _db[Key(map, type, "uid", pos)] = uid;
    }

    /// <summary>QC <c>race_writeTime(map, t, uid)</c>: insert a new ranked time, shifting the table down (and
    /// removing the player's previous lower-ranked entry if they improved).</summary>
    public static void WriteTime(string map, string type, float t, string uid)
    {
        int newpos = ReadPos(map, type, t);
        if (newpos == 0) return;

        int prevpos = 0;
        for (int i = 1; i <= RankingsCnt; i++)
            if (ReadUid(map, type, i) == uid) prevpos = i;

        if (prevpos != 0)
        {
            // player improved an existing record: shift ranks between new and old down by one.
            for (int i = prevpos; i > newpos; i--)
                WriteSlot(map, type, i, ReadTime(map, type, i - 1), ReadUid(map, type, i - 1));
        }
        else
        {
            // new entrant: shift the whole tail down by one (dropping the worst).
            for (int i = RankingsCnt; i > newpos; i--)
            {
                float other = ReadTime(map, type, i - 1);
                if (other != 0f)
                    WriteSlot(map, type, i, other, ReadUid(map, type, i - 1));
            }
        }
        WriteSlot(map, type, newpos, t, uid);
    }

    /// <summary>
    /// QC <c>race_setTime(map, t, uid, name, e, showmessage)</c>: record a finish time and classify the result
    /// for the host to notify on. A run by an anonymous player (<paramref name="uid"/> == "") is never ranked
    /// (returns <see cref="RaceRecordKind.Fail"/>). On a genuine improvement the rankings table is updated and
    /// the uid→name map is refreshed.
    /// </summary>
    public static RaceRecordResult SetTime(string map, string type, float t, string uid, string name)
    {
        int newpos = ReadPos(map, type, t);

        int prevpos = 0;
        for (int i = 1; i <= RankingsCnt; i++)
            if (ReadUid(map, type, i) == uid && uid.Length != 0) prevpos = i;

        // already-ranked player whose new time isn't better than their ranked time → fail (no change).
        if (prevpos != 0 && (newpos == 0 || prevpos < newpos))
            return new RaceRecordResult(RaceRecordKind.Fail, newpos, prevpos, ReadTime(map, type, prevpos), ReadName(map, type, prevpos));
        // unranked time (worse than the worst ranked) → fail.
        if (newpos == 0)
            return new RaceRecordResult(RaceRecordKind.Fail, 0, prevpos, ReadTime(map, type, RankingsCnt), ReadName(map, type, RankingsCnt));
        // anonymous players can't be ranked (the table is keyed by UID).
        if (uid.Length == 0)
            return new RaceRecordResult(RaceRecordKind.Fail, newpos, 0, 0f, "");

        float oldrec = ReadTime(map, type, newpos);
        string oldHolder = ReadName(map, type, newpos);
        if (name.Length != 0) _names[uid] = name;
        WriteTime(map, type, t, uid);

        RaceRecordKind kind =
            newpos == prevpos ? RaceRecordKind.NewImproved :
            oldrec == 0f ? RaceRecordKind.NewSet :
            RaceRecordKind.NewBroken;
        return new RaceRecordResult(kind, newpos, prevpos, oldrec, oldHolder);
    }

    /// <summary>
    /// QC <c>race_setTime</c> classify-only prefix (server/race.qc:376-403): would a time of
    /// <paramref name="t"/> by <paramref name="uid"/> be a NEW record (not a fail) for this map + type? This
    /// is the non-mutating decision the consent gate needs: Base only emits <c>INFO_RACE_NEW_MISSING_NAME</c>
    /// (uid present but the player withheld the <c>cl_allow_uid*</c> consent) once it has confirmed the time
    /// WOULD rank — a non-ranking time still just fails. Mirrors <see cref="SetTime"/>'s fail branches without
    /// touching the table.
    /// </summary>
    public static bool WouldBeNewRecord(string map, string type, float t, string uid)
    {
        int newpos = ReadPos(map, type, t);

        int prevpos = 0;
        for (int i = 1; i <= RankingsCnt; i++)
            if (ReadUid(map, type, i) == uid && uid.Length != 0) prevpos = i;

        // already-ranked player whose new time isn't better → fail; unranked time → fail.
        if (prevpos != 0 && (newpos == 0 || prevpos < newpos)) return false;
        if (newpos == 0) return false;
        return true;
    }

    /// <summary>The server record (rank 1) time for a map + type (0 if none).</summary>
    public static float ServerRecord(string map, string type) => ReadTime(map, type, 1);
    public static string ServerRecordHolder(string map, string type) => ReadName(map, type, 1);

    /// <summary>
    /// QC <c>ClientProgsDB</c> personal-best analogue: find the best (lowest) ranked time for a given player
    /// identified by their UID. Scans the top-<see cref="RankingsCnt"/> slots and returns the time at the
    /// player's highest (lowest-numbered) rank, or 0 if they have no ranked time.
    /// This mirrors the QC client-side personal-best cache for the mod-icon PB display: on the server the DB is
    /// authoritative, so we read it directly rather than caching client-side.
    /// </summary>
    public static float ReadPersonalBest(string map, string type, string uid)
    {
        if (string.IsNullOrEmpty(uid)) return 0f;
        for (int i = 1; i <= RankingsCnt; i++)
        {
            if (ReadUid(map, type, i) == uid)
                return ReadTime(map, type, i); // dense table: first match is the best rank
        }
        return 0f;
    }

    /// <summary>Map a UID to a display name (QC the /uid2name/ store), used by the ranking display.</summary>
    public static void SetName(string uid, string name) { if (uid.Length != 0 && name.Length != 0) _names[uid] = name; }

    /// <summary>
    /// QC <c>race_deleteTime(rank, map)</c>: delete all records up to and including a given rank (1-based),
    /// shifting higher ranks down to fill the gap. Used by the <c>sv_cmd delrec</c> command.
    /// </summary>
    public static void DeleteTime(string map, string type, int rank)
    {
        if (rank < 1 || rank > RankingsCnt) return;
        // Shift all records from rank+1 down to rank, pushing out the record at RankingsCnt.
        for (int i = rank; i < RankingsCnt; i++)
        {
            float t = ReadTime(map, type, i + 1);
            string uid = ReadUid(map, type, i + 1);
            if (t > 0f)
                WriteSlot(map, type, i, t, uid);
            else
                WriteSlot(map, type, i, 0f, "");
        }
        // Clear the last slot.
        WriteSlot(map, type, RankingsCnt, 0f, "");
    }

    // ---- all-time best speed award (QC server/race.qc race_SpeedAwardFrame / race_SendAll) ----
    // QC persists the all-time best planar speed per map+record_type to ServerProgsDB under the keys
    // strcat(GetMapname(), record_type, "speed/speed") and ".../speed/crypto_idfp". Mirror those exact key
    // shapes here so the value rides the existing Export/Import (the C# successor to db_save/db_load).

    /// <summary>QC <c>db_get(ServerProgsDB, strcat(map, record_type, "speed/speed"))</c>: the persisted all-time
    /// best planar speed (qu/s) for a map + record type, or 0 if none recorded yet.</summary>
    public static float ReadSpeedAwardBest(string map, string type)
        => _db.TryGetValue(map + type + "speed/speed", out string? s) ? ParseTime(s) : 0f;

    /// <summary>QC <c>uid2name(db_get(..., "speed/crypto_idfp"))</c>: the display name of the all-time best-speed
    /// holder for a map + record type ("" if none).</summary>
    public static string ReadSpeedAwardBestHolder(string map, string type)
    {
        string uid = _db.TryGetValue(map + type + "speed/crypto_idfp", out string? s) ? s : "";
        return uid.Length == 0 ? "" : (_names.TryGetValue(uid, out string? n) ? n : uid);
    }

    /// <summary>QC <c>db_put(ServerProgsDB, strcat(map, record_type, "speed/..."), ...)</c>: persist a new
    /// all-time best planar speed for a map + record type, keyed by the holder's UID.</summary>
    public static void WriteSpeedAwardBest(string map, string type, float speed, string uid)
    {
        _db[map + type + "speed/speed"] = speed.ToString(CultureInfo.InvariantCulture);
        _db[map + type + "speed/crypto_idfp"] = uid;
    }

    // ---- persistence (the host wires these to a server file; QC db_save/db_load) ----

    /// <summary>Serialize the whole DB (records + names) to a flat string the host can persist.</summary>
    public static string Export()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in _db) sb.Append("R\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
        foreach (var kv in _names) sb.Append("N\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
        return sb.ToString();
    }

    /// <summary>Load a DB previously produced by <see cref="Export"/> (replaces the current contents).</summary>
    public static void Import(string data)
    {
        Clear();
        foreach (string line in data.Split('\n'))
        {
            if (line.Length == 0) continue;
            string[] parts = line.Split('\t');
            if (parts.Length != 3) continue;
            if (parts[0] == "R") _db[parts[1]] = parts[2];
            else if (parts[0] == "N") _names[parts[1]] = parts[2];
        }
    }

    public static void Clear() { _db.Clear(); _names.Clear(); }
}
