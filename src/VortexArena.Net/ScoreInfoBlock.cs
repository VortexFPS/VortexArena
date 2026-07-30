// Port of qcsrc/server/scores.qc (ScoreInfo_SendEntity) + qcsrc/client/main.qc (NET_HANDLE ENT_CLIENT_SCORES_INFO)
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Net;

/// <summary>
/// Server→client replication of the ACTIVE score LAYOUT — the C# successor to QuakeC's
/// <c>ScoreInfo_SendEntity</c> / <c>ENT_CLIENT_SCORES_INFO</c> (the <c>ent_client_scoreinfo</c> linked entity,
/// server/scores.qc:205 / client/main.qc:1049). It carries, in registry order, the per-column label + flags
/// (QC <c>scores_label(it)</c> / <c>scores_flags(it)</c> for <c>FOREACH(Scores, true)</c> — i.e. ALL fields,
/// client-only ones too), the two team-score slots' label + flags, plus the active gametype's NetName and the
/// teamplay bool.
///
/// <para><b>Why this is THE fix.</b> The scoreboard wire layout (<see cref="ScoreboardBlock"/>) is keyed on
/// <see cref="GameScores.NetworkedFields"/>, which is LABEL-derived: a field is networked iff its label is
/// non-empty. The SERVER's gametype runs <c>ScoreRulesBasics</c> at match start, which blanks every column then
/// re-declares its own — so the server's networked set is mode-specific. A remote client only ran
/// <c>RegisterAll</c> (every column keeps its default label), so its networked set DIFFERS — the layout hash
/// disagrees and <see cref="ScoreboardBlock.Deserialize"/> drops the WHOLE block every snapshot. Applying this
/// block client-side (<see cref="Apply"/> → <see cref="GameScores.SetLabel"/> per field) makes the client's
/// label/flag set identical to the server's, so the hash matches and scores finally render.</para>
///
/// <para>Sent owner-agnostic (same to all) and <strong>only when changed</strong> — gated by a content
/// <see cref="Hash"/> over <see cref="GameScores.LayoutGeneration"/>, exactly like <see cref="MoveVarsBlock"/>
/// gates on the physics hash — so the steady-state cost is a single bool per snapshot; the labels are
/// re-networked only on a gametype/mode switch (QC the <c>SendFlags |= 1</c> force-resend on
/// <c>ScoreInfo_Init</c>). Strings are positional, no field-name keys on the wire.</para>
///
/// <para><b>Listen-server nuance.</b> On NetGame's in-process listen server the server already ran
/// ScoreRulesBasics on the SHARED static <see cref="GameScores"/>, so the labels (and thus the layout hash)
/// ALREADY match in-process — this block's <see cref="Apply"/> writes the same values back (idempotent, a
/// no-op). The desync only bites a remote <c>--connect</c> client whose GameScores ran only RegisterAll; for it
/// this block is load-bearing. <see cref="Apply"/> never blanks a label the co-located server set differently
/// from what it sends, because it writes exactly the values it carries.</para>
/// </summary>
public static class ScoreInfoBlock
{
    /// <summary>
    /// A content hash of the active layout for cheap "did the score layout change?" change-detection — folds the
    /// <see cref="GameScores.LayoutGeneration"/> stamp (bumped on every label/flag change) with the gametype +
    /// teamplay so a mode switch (which also re-stamps the generation) re-sends. FNV-1a over the stamp bytes.
    /// </summary>
    public static uint Hash()
    {
        uint h = 2166136261u;
        uint gen = (uint)GameScores.LayoutGeneration;
        for (int b = 0; b < 4; b++) { h ^= gen & 0xFF; h *= 16777619u; gen >>= 8; }
        foreach (char c in GameScores.Gametype) { h ^= c; h *= 16777619u; }
        h ^= GameScores.Teamplay ? 1u : 0u; h *= 16777619u;
        return h;
    }

    /// <summary>
    /// Serialize the active layout (server side): the gametype NetName + teamplay bool, then every
    /// <see cref="ScoreField"/> in registry order as (label, flags), then the two team slots as (label, flags).
    /// Mirrors <c>ScoreInfo_SendEntity</c> (WriteRegistered(Gametypes) is replaced by the NetName string — ADR
    /// 0011: we own the layout, and the NetName is what the client's column filter / per-mode logic consumes).
    /// The welcome-message tail is intentionally omitted (it is a notification concern, not score layout).
    /// </summary>
    public static void Serialize(BitWriter w)
    {
        w.WriteString(GameScores.Gametype);
        w.WriteBool(GameScores.Teamplay);

        var fields = GameScores.Fields;
        w.WriteByte(fields.Count > 255 ? 255 : fields.Count);
        int n = System.Math.Min(fields.Count, 255);
        for (int i = 0; i < n; i++)
        {
            ScoreField f = fields[i];
            w.WriteString(f.Label);
            w.WriteShort((int)f.Flags); // SFL_* fits a byte today, but a short is future-proof + cheap
        }

        for (int i = 0; i < GameScores.MaxTeamScore; i++)
        {
            w.WriteString(GameScores.TeamLabel(i));
            w.WriteShort((int)GameScores.TeamFlags(i));
        }
    }

    /// <summary>The decoded layout (client side after <see cref="Deserialize"/>) — held so the panel can read the
    /// gametype/teamplay even before it's applied to the shared <see cref="GameScores"/> singleton.</summary>
    public sealed class Decoded
    {
        public string Gametype = "dm";
        public bool Teamplay;
        public string[] Labels = System.Array.Empty<string>();
        public ScoreFlags[] Flags = System.Array.Empty<ScoreFlags>();
        public string[] TeamLabels = new string[GameScores.MaxTeamScore];
        public ScoreFlags[] TeamFlags = new ScoreFlags[GameScores.MaxTeamScore];
    }

    /// <summary>Read the layout block (the inverse of <see cref="Serialize"/>). Returns null on a bad read.</summary>
    public static Decoded? Deserialize(ref BitReader r)
    {
        var d = new Decoded
        {
            Gametype = r.ReadString(),
            Teamplay = r.ReadBool(),
        };
        int n = r.ReadByte();
        if (n < 0 || n > 255) return null;
        d.Labels = new string[n];
        d.Flags = new ScoreFlags[n];
        for (int i = 0; i < n; i++)
        {
            d.Labels[i] = r.ReadString();
            d.Flags[i] = (ScoreFlags)r.ReadShort();
        }
        for (int i = 0; i < GameScores.MaxTeamScore; i++)
        {
            d.TeamLabels[i] = r.ReadString();
            d.TeamFlags[i] = (ScoreFlags)r.ReadShort();
        }
        return r.BadRead ? null : d;
    }

    /// <summary>
    /// QC <c>NET_HANDLE(ENT_CLIENT_SCORES_INFO)</c> (client/main.qc:1049): apply a received layout into the
    /// shared <see cref="GameScores"/> — set <see cref="GameScores.Gametype"/>/<see cref="GameScores.Teamplay"/>,
    /// then <see cref="GameScores.SetLabel"/> each column and <see cref="GameScores.SetTeamLabel"/> each team
    /// slot (which invalidate the layout cache, so <see cref="GameScores.NetworkedFields"/> and the scoreboard
    /// layout hash recompute), then <see cref="GameScores.ResolveSortKeys"/> (QC <c>Scoreboard_InitScores</c>).
    /// Idempotent: re-applying the same values is a no-op (the listen-server case).
    ///
    /// <para>The per-field apply is by REGISTRY INDEX, which is valid only when the client and server registries
    /// agree on order/size — guaranteed by the build-parity gate (the registry content hash is folded into
    /// <see cref="NetProtocol.BuildParity"/>), so a mismatched client is rejected at handshake before any block
    /// arrives. Extra/short entries are clamped defensively.</para>
    /// </summary>
    public static void Apply(Decoded d)
    {
        GameScores.Gametype = string.IsNullOrEmpty(d.Gametype) ? "dm" : d.Gametype;
        GameScores.Teamplay = d.Teamplay;

        var fields = GameScores.Fields;
        int n = System.Math.Min(d.Labels.Length, fields.Count);
        for (int i = 0; i < n; i++)
            GameScores.SetLabel(fields[i], d.Labels[i] ?? "", d.Flags[i]);

        for (int i = 0; i < GameScores.MaxTeamScore && i < d.TeamLabels.Length; i++)
            GameScores.SetTeamLabel(i, d.TeamLabels[i] ?? "", d.TeamFlags[i]);

        GameScores.ResolveSortKeys(); // QC Scoreboard_InitScores: derive primary/secondary from the applied flags
    }
}
