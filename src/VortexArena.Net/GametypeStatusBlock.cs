// Port of qcsrc/common/gametypes/gametype/clanarena/sv_clanarena.qc (CA_count_alive_players → STAT(REDALIVE..PINKALIVE))
//       + qcsrc/common/gametypes/gametype/freezetag/sv_freezetag.qc (freezetag_count_alive_players)
//       + qcsrc/common/gametypes/gametype/keyhunt/sv_keyhunt.qc (kh_update_state → STAT(OBJECTIVE_STATUS))
//       + qcsrc/common/gametypes/gametype/survival/sv_survival.qc (SurvivalStatuses_SendEntity)
//       + qcsrc/server/elimination.qc (EliminatedPlayers_SendEntity)
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Net;

/// <summary>
/// Server→client replication of the per-mode round/objective HUD status (T53) — the C# successor to four QC
/// channels at once: the <c>STAT(REDALIVE..PINKALIVE)</c> alive counts (CA/FreezeTag mod icons), the
/// <c>STAT(OBJECTIVE_STATUS)</c> KeyHunt key pack, the <c>eliminatedPlayers</c> linked-entity bitfield (the
/// scoreboard row grey-out, server/elimination.qc) and the <c>survivalStatuses</c> linked entity (Survival's
/// own-role tag + the hunter-identity disclosure, sv_survival.qc).
///
/// <para><b>Personalized per recipient.</b> Two payloads differ per destination, exactly as in QC: the KeyHunt
/// pack ORs the slot of a key the RECIPIENT carries to 31 (kh_update_state's self override), and the Survival
/// hunter set is only written to a hunter destination — or to everyone once the round is over
/// (SurvivalStatuses_SendEntity's <c>STATUS_SEND_HUNTERS</c> rule). Hunter ids must NEVER ride the wire to a
/// prey/observer mid-round (the anti-cheat invariant), which is why the server serializes this block per peer
/// and gates on a per-peer <see cref="Hash"/> of the recipient's OWN bytes rather than sharing one block.</para>
///
/// <para><b>Snapshot framing.</b> The block sits between the scoreboard block and the delta-compressed entity
/// section, behind a bool gate written EVERY snapshot (steady state and all untracked modes = one false bool).
/// Eliminated players + hunters are id LISTS keyed on the snapshot's stable player net ids (the port networks
/// players by net id, not the QC maxclients bit slots). Value-change gating via <see cref="Hash"/> is
/// observationally identical to QC's stat delta-compression + SendFlags re-sends — DP stats are themselves only
/// transmitted when the value changes (the brief's round-handler cadence note).</para>
/// </summary>
public static class GametypeStatusBlock
{
    /// <summary>Which mode's payload the block carries (the QC <c>gametype.m_modicons</c> dispatch analogue).
    /// <see cref="None"/> is never written — untracked modes simply never send the block.</summary>
    public enum Kind : byte
    {
        None = 0,
        ClanArena = 1,
        FreezeTag = 2,
        KeyHunt = 3,
        Survival = 4,
        // [W1-mod-icons] the objective-feed modes added by Wave 1 so each gametype can push its mod-icon state:
        Ctf = 5,         // QC STAT(OBJECTIVE_STATUS) flag-status pack (ctf_FlagcarrierStatus): per-team taken/lost/carrying
        Domination = 6,  // QC STAT(DOM_TOTAL_PPS / DOM_PPS_*): the points-per-second mod-icon (set_dom_state)
        Keepaway = 7,    // QC the KA_CARRYING mod-icon: who (net id) is currently carrying the ball (0 = nobody)
        TeamKeepaway = 8, // QC STAT(TKA_BALLSTATUS): per-recipient carrying / per-team taken / dropped bit pack
        Lms = 9,         // QC sv_lms.qc recycled STAT(REDALIVE)=lms_leaders, STAT(BLUEALIVE)=lms_leaders_lives_diff,
                         // STAT(OBJECTIVE_STATUS)=lms_visible_leaders: the leader-count mod-icon + the +N lives lead.
        NexBall = 10,    // QC HUD_Mod_NexBall (cl_nexball.qc): the nexball_carrying mod-icon — shown (blinking) only
                         // while the LOCAL player holds the ball. Carries the ball carrier's net id (0 = nobody) plus
                         // the carrier's power-meter PHASE byte (QC NB_METERSTART; 255 = no meter) for the charge bar.
    }

    // [W1-mod-icons] QC ctf.qh CTF_* OBJECTIVE_STATUS bit layout (2 bits per team slot: 1=taken, 2=lost,
    // 3=carrying), so the dormant client CTF mod-icon decode reads exactly the Base stat. Reproduced here so the
    // packing lives next to the wire so Ctf.cs (Wave 2) feeds state, not bit math.
    private const uint CtfRedFlagTaken = 1;     // red slot base; <<2 per subsequent team
    private const uint CtfNeutralFlagTaken = 256;
    private const uint CtfFlagNeutral = 2048;   // one-flag CTF marker
    private const uint CtfShielded = 4096;      // a flag is base-shielded (mod icon stays active)
    private const uint CtfStalemate = 8192;     // anti-stall stalemate flash

    /// <summary>
    /// Team color code (<see cref="Teams.Red"/>/Blue/Yellow/Pink = 4/13/12/9) → 1-based team INDEX
    /// (1 red .. 4 pink; 0 = none/observer). The wire deliberately carries indices, not color codes: the
    /// dormant ModIconsPanel KH decode (and QC's <c>myteam</c>) are index-shaped, and one convention on the
    /// wire avoids the SVQC-vs-CSQC code split QC bridges with its <c>−1</c>.
    /// </summary>
    public static int TeamIndexOf(int teamCode) => teamCode switch
    {
        Teams.Red => 1, Teams.Blue => 2, Teams.Yellow => 3, Teams.Pink => 4, _ => 0,
    };

    /// <summary>
    /// Serialize the personalized status block for <paramref name="viewer"/> (server side). Returns false —
    /// writing NOTHING — for any gametype this block doesn't track, so the caller sends only its false bool
    /// gate. <paramref name="netIdOf"/> supplies the stable per-player wire id (ServerNet.NetIdFor);
    /// <paramref name="roundStarted"/> is the world round handler's phase — part of the splice contract,
    /// reserved here: the per-mode gates come from each mode's OWN state (<see cref="KeyHunt.Phase"/> /
    /// <see cref="Survival.Handler"/>), which are the drivers that actually spawn keys / assign roles.
    /// </summary>
    public static bool Capture(BitWriter w, object? gametype, Player viewer,
        System.Collections.Generic.IReadOnlyList<Player> players,
        System.Func<Player, int> netIdOf, bool roundStarted)
    {
        _ = roundStarted;
        switch (gametype)
        {
            case ClanArena ca:
                WriteHeader(w, Kind.ClanArena, viewer, ca.TeamCount);
                WriteAliveCounts(w, ca.AliveCount);
                WriteIdList(w, players, netIdOf, ca.IsEliminatedPlayer); // QC ca_isEliminated → eliminatedPlayers
                return true;

            case FreezeTag ft:
                WriteHeader(w, Kind.FreezeTag, viewer, ft.TeamCount);
                WriteAliveCounts(w, ft.AliveCount);
                WriteIdList(w, players, netIdOf, ft.IsEliminated); // QC freezetag_isEliminated (frozen || dead)
                return true;

            case KeyHunt kh:
                WriteHeader(w, Kind.KeyHunt, viewer, kh.TeamCount);
                w.WriteULong(kh.PackKeyState(viewer)); // QC kh_update_state, incl. the per-recipient 31 slot
                return true;

            case Ctf ctf:
                WriteHeader(w, Kind.Ctf, viewer, ctf.TeamCount);
                w.WriteULong(PackCtfStatus(ctf, viewer)); // QC STAT(OBJECTIVE_STATUS): per-recipient flag-status pack
                return true;

            case Domination dom:
                WriteHeader(w, Kind.Domination, viewer, dom.TeamCount);
                WriteDominationPps(w, dom); // QC STAT(DOM_TOTAL_PPS / DOM_PPS_*): the pps mod-icon
                return true;

            case Keepaway ka:
                WriteHeader(w, Kind.Keepaway, viewer, 0); // FFA: no visible teams (QC the KA_CARRYING icon only)
                // The net id of the current ball carrier (0 = nobody holds it) — drives the KA_CARRYING mod-icon
                // and the carrier waypoint. netIdOf is the stable wire id (ServerNet.NetIdFor); 0 when ball-less.
                w.WriteUShort(ka.Ball.Carrier is { } carrier ? netIdOf(carrier) : 0);
                return true;

            case TeamKeepaway tka:
                WriteHeader(w, Kind.TeamKeepaway, viewer, tka.TeamCount);
                // QC sv_tka.qc PlayerPreThink → STAT(TKA_BALLSTATUS): the per-recipient carrying bit + the per-team
                // taken / dropped bits. Carrying is recipient-relative, so this is computed per viewer (one byte).
                w.WriteByte(tka.BallStatusFor(viewer) & 0xFF);
                return true;

            case LastManStanding lms:
                WriteHeader(w, Kind.Lms, viewer, 0); // FFA elimination: no visible teams
                // QC sv_lms.qc recycled stats (set in PlayerPreThink / SV_StartFrame, same for every recipient):
                //   STAT(REDALIVE) = lms_leaders, STAT(BLUEALIVE) = lms_leaders_lives_diff,
                //   STAT(OBJECTIVE_STATUS) = lms_visible_leaders. The HUD mod-icon (cl_lms.qc HUD_Mod_LMS_Draw)
                //   draws nothing when lms_leaders == 0.
                w.WriteByte(System.Math.Clamp(lms.LeaderCount, 0, 255));
                w.WriteByte(System.Math.Clamp(lms.LeadersLivesDiff, 0, 255));
                w.WriteByte(lms.LeadersVisible ? 1 : 0);
                return true;

            case Nexball nb:
                WriteHeader(w, Kind.NexBall, viewer, 0); // the QC nexball mod-icon is a self-carry indicator only
                // QC HUD_Mod_NexBall keys off whether the LOCAL player holds the ball — networked as the carrier's
                // stable net id (0 = nobody), resolved against the local net id on the client (same shape as Keepaway).
                Player? nbCarrier = nb.BallEntity?.GtCarrier as Player;
                w.WriteUShort(nbCarrier is not null ? netIdOf(nbCarrier) : 0);
                // QC NB_METERSTART → the basketball power-meter PHASE for the carrier's charge bar (progressbar_nexball).
                // The carrier's slot-0 NbMeterStart is the server time the meter began (BallStealer.WrThink); the HUD
                // wants the wrapped phase, not the raw start. Code it as a sawtooth byte 0..254 over one MeterPeriod
                // (the client re-derives the triangle), with 255 the "no meter" sentinel (inactive → no bar drawn).
                w.WriteByte(NexBallMeterPhaseByte(nb, nbCarrier));
                return true;

            case Survival surv:
                WriteHeader(w, Kind.Survival, viewer, 0); // roleplay-FFA: no visible teams (QC USEPOINTS only)
                // Own role: 0 until the roles are live (the client hides the panel on 0 — QC's
                // GAMESTARTTIME/ROUNDSTARTTIME gate runs server-side here), else 1 prey / 2 hunter.
                w.WriteByte(surv.RoleAssigned ? (int)surv.StatusOf(viewer) : 0);
                if (surv.DisclosesHuntersTo(viewer))
                    WriteIdList(w, players, netIdOf, surv.IsHunter); // hunters know hunters; everyone at round end
                else
                    w.WriteByte(0); // mid-round prey/observer: NEVER leak the hunter ids (anti-cheat)
                WriteIdList(w, players, netIdOf, surv.IsEliminatedPlayer); // QC surv_isEliminated
                return true;

            default:
                return false; // untracked mode: nothing written
        }
    }

    /// <summary>The shared header: mode byte, the recipient's team INDEX (0 none/observer, 1..4 — QC
    /// <c>myteam</c>), and the active team count (2..4 for the team modes; 0 for Survival).</summary>
    private static void WriteHeader(BitWriter w, Kind kind, Player viewer, int teamCount)
    {
        w.WriteByte((byte)kind);
        w.WriteByte(viewer.IsObserver ? 0 : TeamIndexOf((int)viewer.Team));
        w.WriteByte(teamCount);
    }

    /// <summary>QC STAT(REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE): the four alive counts in FIXED
    /// red,blue,yellow,pink order (inactive teams read 0), each clamped to a byte.</summary>
    private static void WriteAliveCounts(BitWriter w, System.Func<int, int> aliveOf)
    {
        for (int i = 0; i < Teams.All.Length; i++)
            w.WriteByte(System.Math.Clamp(aliveOf(Teams.All[i]), 0, 255));
    }

    /// <summary>
    /// [W1-mod-icons] Build the per-recipient QC STAT(OBJECTIVE_STATUS) CTF flag-status pack
    /// (sv_ctf.qc ctf PlayerPreThink): one 2-bit field per team slot (red,blue,yellow,pink at bits 0/2/4/6, the
    /// neutral flag at bit 8) — 1 = taken (an enemy carries it), 2 = lost (dropped on the map), 3 = carrying
    /// (the RECIPIENT carries it). Plus the one-flag marker, the recipient's capture-shield bit, and (Wave-2)
    /// the stalemate bit. Carrying-vs-taken is recipient-relative, so this is computed per viewer.
    /// </summary>
    private static uint PackCtfStatus(Ctf ctf, Player viewer)
    {
        uint status = 0;
        // Slot base shifts: red=CTF_RED_FLAG_TAKEN(1)<<0, then <<2 per team; neutral uses its own base (256).
        foreach (FlagState flag in ctf.Flags.Values)
        {
            uint takenBase = flag.HomeTeam == Teams.None
                ? CtfNeutralFlagTaken
                : CtfRedFlagTaken << (2 * SlotIndex(flag.HomeTeam));
            if (flag.HomeTeam == Teams.None)
                status |= CtfFlagNeutral; // a neutral flag exists → one-flag CTF display

            switch (flag.Status)
            {
                case FlagStatus.Carried:
                case FlagStatus.Passing:
                    // 1 = taken (someone else holds it) / 3 = carrying (the recipient holds it).
                    status |= ReferenceEquals(flag.Carrier, viewer) ? takenBase * 3u : takenBase * 1u;
                    break;
                case FlagStatus.Dropped:
                    status |= takenBase * 2u; // lost
                    break;
            }
        }
        if (viewer.GtCaptureShielded)
            status |= CtfShielded;
        // QC ctf_SetStatus (sv_ctf.qc): once ctf_CheckStalemate has flagged the stalemate state, OR the
        // CTF_STALEMATE bit so the client's flag-status mod-icon lights its stalemate overlay (cl_ctf.qc
        // reads bit 8192). Detection lives in Ctf.CheckStalemate (set/clear ladder); this just replicates it.
        if (ctf.Stalemate)
            status |= CtfStalemate;
        return status;
    }

    /// <summary>Red/Blue/Yellow/Pink → 0..3 slot index for the 2-bit OBJECTIVE_STATUS field (the neutral flag
    /// uses its own base and never calls this).</summary>
    private static int SlotIndex(int teamCode) => System.Math.Max(0, TeamIndexOf(teamCode) - 1);

    /// <summary>
    /// [W1-mod-icons] QC STAT(DOM_TOTAL_PPS / DOM_PPS_RED/BLUE/YELLOW/PINK) (set_dom_state) — the Domination
    /// points-per-second mod-icon. Computed from the control points each team owns: a point contributes
    /// <c>amount/rate</c> points/second to its owner team (QC per-point .frags/.wait, overridden by
    /// g_domination_point_amt/_rate). Written total then the four team values in red,blue,yellow,pink order.
    /// </summary>
    private static void WriteDominationPps(BitWriter w, Domination dom)
    {
        System.Span<float> pps = stackalloc float[4]; // red, blue, yellow, pink
        float total = 0f;
        // QC set_dom_state sums EACH point's own amt/rate: `points = g_domination_point_amt ? amt : it.frags;
        // wait_time = g_domination_point_rate ? rate : it.wait; total_pps += points/wait_time` (per dom point).
        // Use the per-point resolvers (cvar override else this point's .frags/.wait, default 1/5) — NOT the global
        // PointAmount/PointRate properties (whose 1/2 fallback read ~2.5x high vs Base's per-point 1/5). QC adds
        // every point's pps to total_pps (incl. neutral, so they dilute the bars) but only credits a team slot
        // when the point is owned — DrawDomItem then fills each bar by stat/total_pps.
        foreach (ControlPoint cp in dom.Points)
        {
            float rate = dom.PointRateFor(cp);
            float perPoint = rate > 0f ? dom.PointAmountFor(cp) / rate : 0f;
            total += perPoint; // QC total_pps += points/wait_time for ALL points
            int slot = TeamIndexOf(cp.OwnerTeam) - 1;
            if (slot < 0 || slot > 3)
                continue; // unowned / neutral point credits no team slot (but still counts toward total)
            pps[slot] += perPoint;
        }
        w.WriteFloat(total);
        for (int i = 0; i < 4; i++)
            w.WriteFloat(pps[i]);
    }

    /// <summary>
    /// QC NB_METERSTART → the basketball power-meter phase byte for the carrier's HUD charge bar
    /// (progressbar_nexball in cl_nexball.qc). The carrier's slot-0 <c>NbMeterStart</c> is the server time the
    /// meter began charging (BallStealer.WrThink); here we wrap the elapsed time over one
    /// <see cref="Nexball.MeterPeriod"/> into a sawtooth byte 0..254. Returns the 255 sentinel ("no meter") when
    /// the basketball meter is disabled, no one carries, or the carrier isn't charging — the client then draws no
    /// bar. The client re-derives the triangle wave (BallStealer.DoLaunch's <c>2*phase; if &gt;1 → 2−phase</c>).
    /// </summary>
    private static byte NexBallMeterPhaseByte(Nexball nb, Player? carrier)
    {
        if (!nb.MeterEnabled || carrier is null)
            return 255; // no meter / no carrier → no bar
        float meterStart = carrier.WeaponState(new WeaponSlot(0)).NbMeterStart;
        if (meterStart <= 0f)
            return 255; // carrier holds the ball but isn't charging the meter

        float period = nb.MeterPeriod;
        if (period <= 0f)
            return 255; // defensive: a non-positive period has no meaningful phase

        float now = Api.Services is null ? 0f : Api.Clock.Time;
        float phase = ((now - meterStart) % period) / period; // 0..1 sawtooth
        if (phase < 0f) phase = 0f;
        int b = (int)System.MathF.Round(phase * 255f);
        return (byte)System.Math.Clamp(b, 0, 254); // 255 reserved for the "no meter" sentinel
    }

    /// <summary>
    /// QC EliminatedPlayers_SendEntity / the SurvivalStatuses hunter bitfield, re-shaped as an id list (count
    /// byte + that many ushort net ids — the port has stable net ids, not maxclients bit slots). Observers are
    /// skipped (QC's INGAME / IS_PLAYER gates). Two passes over the roster keep the write allocation-free and
    /// the wire order roster-stable (deterministic bytes → stable <see cref="Hash"/>).
    /// </summary>
    private static void WriteIdList(BitWriter w, System.Collections.Generic.IReadOnlyList<Player> players,
        System.Func<Player, int> netIdOf, System.Func<Player, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < players.Count; i++)
            if (!players[i].IsObserver && predicate(players[i]))
                count++;
        if (count > 255) count = 255;
        w.WriteByte(count);

        int written = 0;
        for (int i = 0; i < players.Count && written < count; i++)
        {
            Player p = players[i];
            if (p.IsObserver || !predicate(p))
                continue;
            w.WriteUShort(netIdOf(p));
            written++;
        }
    }

    /// <summary>The decoded per-mode status (client side after <see cref="Deserialize"/>). Consumers:
    /// NetGame.UpdateModIcons (ModIconsPanel feed) + UpdateScoreboard (eliminated grey-out).</summary>
    public sealed class Decoded
    {
        public Kind Mode;
        /// <summary>The recipient's team as a 1-based INDEX (0 = none/observer) — feeds ModIconsPanel.MyTeam.</summary>
        public int MyTeamIndex;
        /// <summary>Active team count (2..4 for CA/FT/KH); 0 for Survival (roleplay-FFA).</summary>
        public int TeamCount;
        /// <summary>Alive counts in red,blue,yellow,pink order (QC STAT(REDALIVE..PINKALIVE)); CA/FT only.</summary>
        public int[] Alive = new int[4];
        /// <summary>Net ids the scoreboard greys out (QC the eliminatedPlayers bitfield); CA/FT/Survival.</summary>
        public System.Collections.Generic.HashSet<int> EliminatedNetIds = new();
        /// <summary>The KH OBJECTIVE_STATUS pack (4 × 5-bit slots) — feeds ModIconsPanel.ObjectiveStatus.</summary>
        public uint KeyState;
        /// <summary>The recipient's own Survival role: 0 none (pre-round/spectating), 1 prey, 2 hunter.</summary>
        public int MyStatus;
        /// <summary>Disclosed hunter net ids (own side mid-round; everyone once the round is over).</summary>
        public System.Collections.Generic.HashSet<int> HunterNetIds = new();

        /// <summary>[W1-mod-icons] CTF QC STAT(OBJECTIVE_STATUS) flag-status pack (2-bit per-team
        /// taken/lost/carrying + neutral/shielded/stalemate bits) — feeds ModIconsPanel.ObjectiveStatus in CTF.</summary>
        public uint ObjectiveStatus;
        /// <summary>[W1-mod-icons] Domination pps: index 0 = total, 1..4 = red,blue,yellow,pink (QC
        /// STAT(DOM_TOTAL_PPS / DOM_PPS_*)) — feeds the Domination mod-icon panel.</summary>
        public float[] DominationPps = new float[5];
        /// <summary>[W1-mod-icons] Keepaway: the net id of the current ball carrier (0 = nobody) — feeds the
        /// KA_CARRYING mod-icon and carrier waypoint. Also reused by NexBall for the ball carrier.</summary>
        public int CarrierNetId;
        /// <summary>NexBall: the carrier's networked power-meter phase (QC NB_METERSTART → progressbar_nexball).
        /// -1 = inactive/no bar; 0..254 = sawtooth phase byte the HUD folds into the triangle charge bar.</summary>
        public int NexBallMeterPhase = -1;
        /// <summary>Team Keepaway: the per-recipient QC STAT(TKA_BALLSTATUS) bit pack (carrying / per-team taken /
        /// dropped) — feeds the TKA mod-icon (HUD_Mod_TeamKeepaway).</summary>
        public int TkaBallStatus;

        /// <summary>LMS: QC <c>STAT(REDALIVE) = lms_leaders</c> — the count of current leaders (the mod-icon's
        /// leader-count number; 0 hides the panel).</summary>
        public int LmsLeaderCount;
        /// <summary>LMS: QC <c>STAT(BLUEALIVE) = lms_leaders_lives_diff</c> — the lives the leader(s) hold over the
        /// next-best player (the colored "+N" readout).</summary>
        public int LmsLivesDiff;
        /// <summary>LMS: QC <c>STAT(OBJECTIVE_STATUS) = lms_visible_leaders</c> — leaders are inside their radar
        /// show-window (the flag_stalemate overlay flashes on the mod-icon while true).</summary>
        public bool LmsLeadersVisible;
    }

    /// <summary>Read a status block (the inverse of <see cref="Capture"/>'s writes). Returns null on a bad
    /// read or an unknown mode byte — the snapshot stream is then corrupt and the caller bails on BadRead.</summary>
    public static Decoded? Deserialize(ref BitReader r)
    {
        int mode = r.ReadByte();
        if (mode < (int)Kind.ClanArena || mode > (int)Kind.NexBall)
            return null; // unknown mode: build-parity should make this unreachable
        var d = new Decoded
        {
            Mode = (Kind)mode,
            MyTeamIndex = r.ReadByte(),
            TeamCount = r.ReadByte(),
        };
        switch (d.Mode)
        {
            case Kind.ClanArena:
            case Kind.FreezeTag:
                for (int i = 0; i < d.Alive.Length; i++)
                    d.Alive[i] = r.ReadByte();
                ReadIdList(ref r, d.EliminatedNetIds);
                break;
            case Kind.KeyHunt:
                d.KeyState = r.ReadULong();
                break;
            case Kind.Survival:
                d.MyStatus = r.ReadByte();
                ReadIdList(ref r, d.HunterNetIds);
                ReadIdList(ref r, d.EliminatedNetIds);
                break;
            case Kind.Ctf:
                d.ObjectiveStatus = r.ReadULong();
                break;
            case Kind.Domination:
                for (int i = 0; i < d.DominationPps.Length; i++)
                    d.DominationPps[i] = r.ReadFloat();
                break;
            case Kind.Keepaway:
                d.CarrierNetId = r.ReadUShort();
                break;
            case Kind.NexBall:
                d.CarrierNetId = r.ReadUShort(); // QC nexball ball carrier net id (0 = nobody) → reused field
                int mb = r.ReadByte(); // QC NB_METERSTART phase byte (255 = no meter)
                d.NexBallMeterPhase = mb == 255 ? -1 : mb;
                break;
            case Kind.TeamKeepaway:
                d.TkaBallStatus = r.ReadByte();
                break;
            case Kind.Lms:
                d.LmsLeaderCount = r.ReadByte();
                d.LmsLivesDiff = r.ReadByte();
                d.LmsLeadersVisible = r.ReadByte() != 0;
                break;
        }
        return r.BadRead ? null : d;
    }

    private static void ReadIdList(ref BitReader r, System.Collections.Generic.HashSet<int> into)
    {
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            int id = r.ReadUShort();
            if (r.BadRead)
                return;
            into.Add(id);
        }
    }

    /// <summary>
    /// FNV-1a over the serialized block bytes, folded to NONZERO: 0 is the per-peer "never sent" sentinel
    /// (PeerState.LastModeStatusHash), so the first snapshot of a live tracked mode always sends and a
    /// hash that happens to compute 0 can't alias it.
    /// </summary>
    public static uint Hash(System.ReadOnlySpan<byte> bytes)
    {
        uint h = 2166136261u;
        for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 16777619u; }
        return h == 0u ? 1u : h;
    }
}
