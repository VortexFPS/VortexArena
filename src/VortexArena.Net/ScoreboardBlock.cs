using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;          // Teams (entcs team slice cref)
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Net;

/// <summary>One player's networked score row on the wire: the owner's stable net id, the QC <c>entcs</c> display
/// slice (name + team color code) so the client can label/group the row without a separate entcs stream, and the
/// column values in <see cref="GameScores.NetworkedFields"/> order.
///
/// The <see cref="Name"/>/<see cref="Team"/> pair is the C# stand-in for QC <c>entcs_GetName(sv_entnum)</c> /
/// <c>entcs_GetScoreTeam(sv_entnum)</c> (client/hud/panel/scoreboard.qc): the port networks players by name and
/// has no entcs name stream, so the scoreboard would otherwise have only an opaque net id with no way to resolve
/// it to a display name. Carrying the slice here is the minimal faithful entcs equivalent. Both default to
/// ""/<see cref="Teams.None"/> so a caller that only has columns (the score-codec round-trip test) still works.</summary>
public readonly struct ScoreRowWire
{
    public readonly int NetId;
    public readonly string Name;
    public readonly int Team;
    public readonly int[] Columns;
    /// <summary>QC <c>pl.team == NUM_SPECTATOR</c> (scoreboard.qc:2369): this row is a spectator/observer, so the
    /// client lists it in the <c>Scoreboard_Spectators_Draw</c> block instead of the score table. The port has no
    /// NUM_SPECTATOR team sentinel (observers keep their last team color), so we carry the flag explicitly.</summary>
    public readonly bool IsSpectator;
    /// <summary>QC <c>pl.ping_packetloss</c> (scoreboard.qc:1073, SP_PL): the player's measured packet loss
    /// quantized to a 0..255 byte (QC <c>min(ceil(ping_packetloss*255),255)</c>, server/world.qc:74). 0 = no loss
    /// (or a bot / unknown). The client de-quantizes to a 0..1 fraction for the SP_PL column.</summary>
    public readonly int PacketLossByte;
    /// <summary>QC <c>.handicap_level</c> (server/handicap.qh:64) networked via ENTCS (common/ent_cs.qc:180,
    /// WriteByte/ReadByte). An int 0..16 mapped from the player's both-ways average total handicap; 0 = no
    /// handicap. The client draws the <c>player_handicap</c> scoreboard icon (white@1 → red@16) when it's nonzero
    /// (scoreboard.qc:1003-1009). The port has no separate ENTCS stream, so it rides this scoreboard block.</summary>
    public readonly int HandicapLevel;
    /// <summary>QC <c>pl.ping</c> (scoreboard.qc:1041, SP_PING): the player's round-trip time in milliseconds
    /// (ENet RoundTripTime). -1 = unknown (a bot, or not measured); 0 renders as "N/A" (the QC connecting case),
    /// a positive value colorizes by the ping bands. The port has no entcs ping slice, so it rides this block.</summary>
    public readonly int PingMs;
    public ScoreRowWire(int netId, int[] columns, string name = "", int team = 0, bool isSpectator = false,
        int packetLossByte = 0, int handicapLevel = 0, int pingMs = -1)
    { NetId = netId; Columns = columns; Name = name ?? ""; Team = team; IsSpectator = isSpectator; PacketLossByte = packetLossByte; HandicapLevel = handicapLevel; PingMs = pingMs; }
}

/// <summary>The parsed scoreboard (client side after <see cref="ScoreboardBlock.Deserialize"/>).</summary>
public sealed class ScoreboardWire
{
    public readonly List<ScoreRowWire> Rows = new();
    public readonly List<(int team, int score)> Teams = new();
    /// <summary>QC the race/CTS rankings table (Scoreboard_Rankings_Draw grecordtime[]/grecordholder[]): the
    /// top ranked finish times for this map, each an int24 TIME_ENCODE'd time + the holder display name, in rank
    /// order (best first). Empty in non-race modes. The C# stand-in for QC's RACE_NET_SERVER_RANKINGS stream
    /// (client/main.qc TE_CSQC_RACE) — here it rides the existing scoreboard block instead of a separate one.</summary>
    public readonly List<(int timeEncoded, string holder)> Rankings = new();

    // ---- race/CTS speed award (QC race_send_speedaward / _best, scoreboard.qc:2731) ----

    /// <summary>QC <c>race_speedaward</c>: the best planar speed this round (qu/s, rounded), for the scoreboard
    /// "Speed award: N qu/s (holder)" line. 0 = none. The C# stand-in for RACE_NET_SPEED_AWARD.</summary>
    public int SpeedAward;
    /// <summary>QC <c>race_speedaward_holder</c>: the round-best-speed holder display name ("" if none).</summary>
    public string SpeedAwardHolder = "";
    /// <summary>QC <c>race_speedaward_alltimebest</c>: the persisted all-time best planar speed (qu/s, rounded).</summary>
    public int SpeedAwardBest;
    /// <summary>QC <c>race_speedaward_alltimebest_holder</c>: the all-time best-speed holder name ("" if none).</summary>
    public string SpeedAwardBestHolder = "";
}

/// <summary>
/// Server→client replication of the score table — the C# successor to QuakeC's <c>PlayerScore_SendEntity</c> +
/// <c>TeamScore_SendEntity</c> (server/scores.qc). Carries every player's networked score columns (the
/// non-hidden SP_* fields from <see cref="GameScores.NetworkedFields"/>, positionally — both sides derive the
/// same ordered list from the registry, so no names on the wire) plus the per-team totals. Sent on the snapshot
/// channel, gated by <see cref="GameScores.Version"/> so the steady-state cost is a single bool when nothing
/// scored (matching the movevars block's change-gating).
///
/// Faithful to QC's int24 score quantization (<c>WriteInt24_t</c>); the column order is content-hash-agreed via
/// the <see cref="ScoreField"/> registry so a mod that adds a column can't desync the layout.
/// </summary>
public static class ScoreboardBlock
{
    /// <summary>FNV-1a content hash over the networked field names — the layout agreement check (CL must match SV).</summary>
    public static uint LayoutHash()
    {
        uint h = 2166136261u;
        foreach (var f in GameScores.NetworkedFields)
            foreach (char c in f.Name) { h ^= c; h *= 16777619u; }
        return h;
    }

    /// <summary>Gather the wire rows for a set of players (server side), keyed by a caller-supplied net id. The
    /// row also captures the player's display name (<see cref="Entity.NetName"/>) + team for the client's entcs
    /// slice (see <see cref="ScoreRowWire"/>).</summary>
    public static List<ScoreRowWire> CaptureRows(IEnumerable<(int netId, Entity player)> players)
    {
        var rows = new List<ScoreRowWire>();
        foreach (var (netId, p) in players)
            rows.Add(new ScoreRowWire(netId, GameScores.CaptureColumns(p), p.NetName, (int)p.Team,
                isSpectator: p is Player { IsObserver: true },
                handicapLevel: p.HandicapLevel, // QC ENTCS handicap_level slice (common/ent_cs.qc:180)
                pingMs: -1)); // QC SP_PING: CaptureRows has no transport, so ping stays unknown (the live
                              // server path feeds the real RTT in ServerNet.BuildScoreboard).
        return rows;
    }

    /// <summary>Serialize the full scoreboard block: field-count + layout hash (sanity), the player rows, the team
    /// totals, then (race/CTS) the rankings table.</summary>
    public static void Serialize(BitWriter w, IReadOnlyList<ScoreRowWire> rows, IReadOnlyList<(int team, int score)> teamScores,
        IReadOnlyList<(int timeEncoded, string holder)>? rankings = null,
        (int speed, string holder, int best, string bestHolder) speedAward = default)
    {
        int n = GameScores.NetworkedFields.Count;
        w.WriteByte(n);
        w.WriteULong(LayoutHash());

        w.WriteByte(rows.Count > 255 ? 255 : rows.Count);
        int rc = System.Math.Min(rows.Count, 255);
        for (int i = 0; i < rc; i++)
        {
            ScoreRowWire row = rows[i];
            w.WriteUShort(row.NetId);
            w.WriteString(row.Name);    // entcs name slice (QC entcs_GetName)
            w.WriteByte(row.Team & 0xFF); // entcs team slice (QC entcs_GetScoreTeam)
            w.WriteBool(row.IsSpectator); // QC pl.team == NUM_SPECTATOR (drives Scoreboard_Spectators_Draw)
            w.WriteByte(row.PacketLossByte & 0xFF); // QC ping_packetloss byte (server/world.qc:74)
            w.WriteByte(row.HandicapLevel & 0xFF); // QC ENTCS handicap_level WriteByte (common/ent_cs.qc:181)
            // QC SP_PING (scoreboard.qc:1041): the player's RTT in ms. -1 (unknown) is sent as the 0xFFFF
            // sentinel; a real ping (0..65534) rides as a ushort so values past 255 ms survive (unlike packet
            // loss, ping commonly exceeds a byte). Clamp a stray over-range value to 65534 so it can't alias 0xFFFF.
            w.WriteUShort(row.PingMs < 0 ? 0xFFFF : System.Math.Min(row.PingMs, 0xFFFE));
            int m = System.Math.Min(row.Columns.Length, n);
            for (int j = 0; j < n; j++)
                w.WriteInt24(j < m ? row.Columns[j] : 0);
        }

        w.WriteByte(teamScores.Count > 255 ? 255 : teamScores.Count);
        int tc = System.Math.Min(teamScores.Count, 255);
        for (int i = 0; i < tc; i++)
        {
            w.WriteByte(teamScores[i].team & 0xFF);
            w.WriteInt24(teamScores[i].score);
        }

        // QC the race/CTS rankings (Scoreboard_Rankings_Draw grecordtime[]/grecordholder[]): best-first finish
        // times for this map (TIME_ENCODE'd) + holder names. Empty (count 0) in non-race modes — steady-state is
        // a single byte. Capped at 255 like the row/team blocks.
        int rkCount = rankings?.Count ?? 0;
        w.WriteByte(rkCount > 255 ? 255 : rkCount);
        int rkc = System.Math.Min(rkCount, 255);
        for (int i = 0; i < rkc; i++)
        {
            w.WriteInt24(rankings![i].timeEncoded);
            w.WriteString(rankings[i].holder ?? "");
        }

        // QC race_send_speedaward / race_send_speedaward_alltimebest (server/race.qc:267): the round-best + all-time
        // best planar speed (qu/s, rounded) + holder names. 0 speed with "" holder = none (steady-state in a
        // non-race mode is two int24 zeros + two empty strings). Rides the scoreboard block (the C# stand-in for the
        // RACE_NET_SPEED_AWARD / _BEST TE_CSQC_RACE events).
        w.WriteInt24(speedAward.speed);
        w.WriteString(speedAward.holder ?? "");
        w.WriteInt24(speedAward.best);
        w.WriteString(speedAward.bestHolder ?? "");
    }

    /// <summary>Read a scoreboard block. Returns null if the layout hash disagrees (the client mod set differs).</summary>
    public static ScoreboardWire? Deserialize(ref BitReader r)
    {
        int n = r.ReadByte();
        uint hash = r.ReadULong();
        if (n < 0 || n > 64) return null;
        bool layoutOk = hash == LayoutHash();

        var sb = new ScoreboardWire();
        int rows = r.ReadByte();
        for (int i = 0; i < rows; i++)
        {
            int netId = r.ReadUShort();
            string name = r.ReadString();    // entcs name slice
            int team = r.ReadByte();          // entcs team slice
            bool isSpec = r.ReadBool();        // QC pl.team == NUM_SPECTATOR
            int plByte = r.ReadByte();          // QC ping_packetloss byte
            int handicap = r.ReadByte();        // QC ENTCS handicap_level ReadByte (common/ent_cs.qc:182)
            int pingRaw = r.ReadUShort();       // QC SP_PING (0xFFFF = unknown)
            int pingMs = pingRaw == 0xFFFF ? -1 : pingRaw;
            var cols = new int[n];
            for (int j = 0; j < n; j++) cols[j] = r.ReadInt24();
            if (!r.BadRead) sb.Rows.Add(new ScoreRowWire(netId, cols, name, team, isSpec, plByte, handicap, pingMs));
        }
        int teams = r.ReadByte();
        for (int i = 0; i < teams; i++)
        {
            int team = r.ReadByte();
            int score = r.ReadInt24();
            if (!r.BadRead) sb.Teams.Add((team, score));
        }
        int rankings = r.ReadByte();
        for (int i = 0; i < rankings; i++)
        {
            int t = r.ReadInt24();
            string holder = r.ReadString();
            if (!r.BadRead) sb.Rankings.Add((t, holder));
        }
        // QC the speed award trailer (race_send_speedaward / _best).
        sb.SpeedAward = r.ReadInt24();
        sb.SpeedAwardHolder = r.ReadString();
        sb.SpeedAwardBest = r.ReadInt24();
        sb.SpeedAwardBestHolder = r.ReadString();
        if (r.BadRead || !layoutOk) return null; // unreadable, or the client's column layout disagrees
        return sb;
    }
}
