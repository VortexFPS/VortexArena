using System.Collections.Generic;
using Godot;
using VortexArena.Common.Services;

namespace VortexArena.Game.Hud;

/// <summary>
/// Mod-icons panel — port of Base/.../qcsrc/client/hud/panel/modicons.qc (HUD panel #10) and the per-gametype
/// draw functions it dispatches to (<c>HUD_Mod_CTF</c>, <c>HUD_Mod_KH</c>, <c>HUD_Mod_Dom</c>,
/// <c>HUD_Mod_CA</c>/<c>HUD_Mod_FreezeTag</c>, <c>HUD_Mod_Survival</c>, … in
/// common/gametypes/gametype/*/cl_*.qc). The QC version drew the team-objective status for the active
/// gametype: CTF flag states (taken / lost / carrying / shielded, per team) decoded from
/// <c>STAT(OBJECTIVE_STATUS)</c>; Keyhunt key slots; Domination point-per-second bars; the Clan
/// Arena / Freeze Tag alive-count grid; etc. Which renderer runs is chosen at gametype-init
/// (<c>HUD_ModIcons_SetFunc</c> -> <c>gametype.m_modicons</c>).
///
/// The objective status is a server/net concern, so the net layer drives it here through
/// <see cref="ObjectiveStatus"/> + the team membership/count, and selects the renderer via
/// <see cref="Mode"/> (the analogue of the QC per-gametype <c>m_modicons</c> binding). This port covers:
/// CTF flags, Keyhunt keys, Domination points, the Clan Arena / Freeze Tag alive-count grid
/// (<c>HUD_Mod_CA</c>/<c>HUD_Mod_FreezeTag</c> reading STAT(REDALIVE..PINKALIVE)), the Survival own-role
/// tag (<c>HUD_Mod_Survival</c>) and an Assault objective-role tag (port extension, using the shipped
/// <c>as_defend</c>/<c>as_destroy</c> skin art) — each reading the same bitpacked/stat data the QC code does.
///
/// Icons render via the real Xonotic skin art (<c>flag_&lt;color&gt;_&lt;state&gt;</c>, <c>kh_*</c>,
/// <c>player_&lt;color&gt;</c>, <c>dom_icon_*</c>, <c>as_*</c>) through <see cref="HudPanel.DrawSkinPic"/>, with a
/// drawn-primitive (colored swatch + text) fallback so nothing is invisible when a pic is missing. The
/// decode (bitfields, per-team layout, HUD_GetRowCount grid, carrying-blink, status-change expand,
/// stalemate overlay) is faithful to the .qc.
/// </summary>
public partial class ModIconsPanel : HudPanel
{
    /// <summary>Which gametype's objective renderer to draw (QC <c>gametype.m_modicons</c> selection).</summary>
    public enum ModIconsMode { None, Ctf, Keyhunt, Domination, ClanArena, FreezeTag, Survival, Assault, Keepaway, TeamKeepaway, Lms, Race, NexBall }

    /// <summary>The active renderer; <see cref="ModIconsMode.None"/> draws nothing (QC: no m_modicons set).</summary>
    public ModIconsMode Mode { get; set; } = ModIconsMode.None;

    /// <summary>QC <c>STAT(OBJECTIVE_STATUS)</c>: the bitpacked team-objective status (flags / keys / ...).</summary>
    public int ObjectiveStatus { get; set; }

    /// <summary>The local player's team number (QC <c>myteam</c>: 1=red, 2=blue, 3=yellow, 4=pink).</summary>
    public int MyTeam { get; set; } = 1;

    /// <summary>Number of teams in play (QC <c>team_count</c> / <c>_teams_available</c> bits).</summary>
    public int TeamCount { get; set; } = 2;

    // Per-team point-per-second values for Domination (QC STAT(DOM_PPS_*) / DOM_TOTAL_PPS), index 0..3.
    private readonly float[] _domPps = new float[4];
    private float _domTotalPps;

    // Per-team alive counts for ClanArena/FreezeTag (QC STAT(REDALIVE..PINKALIVE)), index 0..3 = red..pink.
    private readonly int[] _alive = new int[4];

    /// <summary>Set the CA/FT alive counts (QC STAT(REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE)).</summary>
    public void SetAliveCounts(int red, int blue, int yellow, int pink)
    {
        _alive[0] = red; _alive[1] = blue; _alive[2] = yellow; _alive[3] = pink;
        QueueRedraw();
    }

    /// <summary>The local player's Survival role (QC <c>playerslots[player_localnum].survival_status</c>):
    /// 0 = none (draws nothing — the server sends 0 pre-round/spectating, its side of QC's
    /// GAMESTARTTIME/ROUNDSTARTTIME hide), 1 = prey ("Survivor"), 2 = hunter ("Hunter").</summary>
    public int SurvivalStatus { get; set; }

    /// <summary>Port extension: the local player's Assault objective role. 0 = none (draws nothing),
    /// 1 = defend (attackers destroyed / you guard), 2 = destroy (you attack the objective). Drawn with the
    /// shipped <c>as_defend</c>/<c>as_destroy</c> skin art. Stock Xonotic has no Assault modicons renderer,
    /// so this is fed by the port's net layer when it knows the role; 0 keeps parity (panel blank).</summary>
    public int AssaultStatus { get; set; }

    /// <summary>Keepaway (QC <c>HUD_Mod_Keepaway</c>): true when the LOCAL player carries the ball (QC
    /// <c>STAT(OBJECTIVE_STATUS) &amp; KA_CARRYING</c>). The port networks the carrier's net id in the status block;
    /// <see cref="NetGame"/> resolves it against the local net id and sets this. The carrying icon flashes
    /// (<c>blink</c>) + does the QC status-change expand transition; false draws nothing.</summary>
    public bool KeepawayCarrying { get; set; }

    /// <summary>NexBall (QC <c>HUD_Mod_NexBall</c>, cl_nexball.qc): true when the LOCAL player holds the nexball
    /// (the wire carries the carrier net id; <see cref="NetGame"/> resolves it against the local net id). The
    /// <c>nexball_carrying</c> icon flashes (<c>blink</c>) + does the QC status-change expand transition; false
    /// draws nothing (QC mod_active forced 1 but nothing to show when not carrying).</summary>
    public bool NexBallCarrying { get; set; }

    /// <summary>NexBall power-meter phase (QC <c>HUD_Mod_NexBall</c> charge bar, cl_nexball.qc): the carrier's
    /// <c>nb_pb_period</c> triangle-wave phase byte (0..255), networked in the NexBall status block and resolved by
    /// <see cref="NetGame"/>. -1 = inactive (no charge → no bar drawn). While &gt;= 0 the local carrier is charging a
    /// throw, and <see cref="DrawNexBall"/> draws the power bar whose length is the QC triangle wave
    /// (launch power ramps minpower→maxpower→minpower over the meter period).</summary>
    public int NexBallMeterPhase { get; set; } = -1;

    // QC nexball prevstatus / statuschange_time: the carry edge + flip time for the expand transition (parallel to
    // the Keepaway tracker).
    private bool _nbPrevCarrying;
    private double _nbStatusChangeTime;

    // QC kaball_prevstatus / kaball_statuschange_time: the carrying-state edge + the time it last flipped, so the
    // expanding-icon transition (drawpic_aspect_skin_expanding) plays for the first ~0.5s after pickup/loss.
    private bool _kaPrevCarrying;
    private double _kaStatusChangeTime;

    /// <summary>Team Keepaway (QC <c>HUD_Mod_TeamKeepaway</c>): the LOCAL player's <c>STAT(TKA_BALLSTATUS)</c> bit
    /// pack (carrying / per-team taken / dropped) — fed by <see cref="NetGame"/> from the per-recipient status
    /// block. The carrying icon (or the team-taken icon) flashes (<c>blink</c>) + does the QC status-change expand
    /// transition; 0 draws nothing.</summary>
    public int TkaBallStatus { get; set; }

    /// <summary>LMS (QC <c>HUD_Mod_LMS_Draw</c>): the leader count (QC <c>STAT(REDALIVE)</c>); 0 hides the panel.</summary>
    public int LmsLeaderCount { get; set; }
    /// <summary>LMS: the leader(s)' lives lead over the next-best player (QC <c>STAT(BLUEALIVE)</c>) — the colored
    /// "+N" readout (yellow / orange at 3 / red at ≥4).</summary>
    public int LmsLivesDiff { get; set; }
    /// <summary>LMS: leaders are inside their radar show-window (QC <c>STAT(OBJECTIVE_STATUS)</c>) — overlays the
    /// flag_stalemate icon while true.</summary>
    public bool LmsLeadersVisible { get; set; }

    // ---- Race/CTS mod-icon data (QC HUD_Mod_Race, cl_race.qc:59-164) ----
    // The Race/CTS mod-icon shows the client's personal best (cached clientside in QC's ClientProgsDB) and the
    // server record for this map, plus the race-award medal flash (race_status + race_status_name) with a 5-second
    // fade.  The net layer feeds the three values + the medal-flash state on each record event and each frame.

    /// <summary>QC <c>t = stof(db_get(ClientProgsDB, strcat(mi_shortname, rr, "time")))</c>: the local player's
    /// personal-best lap time (seconds) for this map + record type. 0 = no personal best yet.</summary>
    public float RaceModIconPb { get; set; }

    /// <summary>QC <c>race_server_record</c> (RACE_NET_SERVER_RECORD): the server's all-time rank-1 time (seconds)
    /// for this map. 0 = no server record yet.</summary>
    public float RaceModIconServerRecord { get; set; }

    // QC crecordtime_prev / srecordtime_prev: previous values to detect changes (for the expand animation).
    private float _racePbPrev = -1f;
    private double _racePbChangeTime;
    private float _raceServerRecordPrev = -1f;
    private double _raceServerRecordChangeTime;

    // QC race_status / race_status_name / race_status_time: the medal flash state.
    /// <summary>QC <c>race_status</c>: -1 none, 0 fail, 1 new personal best (newtime), 2 new rank (green/yellow),
    /// 3 new server record. The mod-icon reflects the same event as the split-timer medal flash.</summary>
    public int RaceModIconStatus { get; set; } = -1;

    /// <summary>QC <c>race_status_name</c>: the player name shown under the medal (record holder / local player).</summary>
    public string RaceModIconStatusName { get; set; } = "";

    // QC race_status_prev / race_status_name_prev: edge-detect to re-arm the 5-second fade window.
    private int _raceStatusPrev = -1;
    private string _raceStatusNamePrev = "";
    private double _raceStatusTime; // QC race_status_time = time + 5 (absolute expiry)

    /// <summary>QC rank-color resolution for status==2: true = green medal (local player improved their own rank),
    /// false = yellow (someone else moved into the rank). Set by the net layer alongside the status.</summary>
    public bool RaceModIconStatusRankIsMine { get; set; } = true;

    // QC tka.qh TKA_BALLSTATUS bits: per-team taken (BIT 0..3), self-carrying (BIT 4), dropped (BIT 5).
    private const int TkaTakenRed = 1 << 0, TkaTakenBlue = 1 << 1, TkaTakenYellow = 1 << 2, TkaTakenPink = 1 << 3;
    private const int TkaCarrying = 1 << 4;

    // QC tkaball_prevstatus / tkaball_statuschange_time: the carrying edge + flip time for the expand transition.
    private int _tkaPrevCarrying;
    private double _tkaStatusChangeTime;

    // ---- CTF flag-status bit layout (QC common/gametypes/gametype/ctf/ctf.qh CTF_*_FLAG_TAKEN) ----
    // Each flag's 2-bit status is packed at a multiplier base; status = (stat / base) & 3:
    //   1 = taken (away from base), 2 = lost (dropped), 3 = carrying (you hold it).
    private const int CtfRedBase = 1;       // CTF_RED_FLAG_TAKEN     (ctf.qh:41)
    private const int CtfBlueBase = 4;      // CTF_BLUE_FLAG_TAKEN    (ctf.qh:44)
    private const int CtfYellowBase = 16;   // CTF_YELLOW_FLAG_TAKEN  (ctf.qh:47)
    private const int CtfPinkBase = 64;     // CTF_PINK_FLAG_TAKEN    (ctf.qh:50)
    private const int CtfNeutralBase = 256; // CTF_NEUTRAL_FLAG_TAKEN (ctf.qh:53)
    private const int CtfFlagNeutral = 2048; // CTF_FLAG_NEUTRAL (one-flag mode marker, ctf.qh:56)
    private const int CtfShielded = 4096;   // CTF_SHIELDED (ctf.qh:57)
    private const int CtfStalemate = 8192;  // CTF_STALEMATE (ctf.qh:58)

    // QC cl_ctf.qc per-flag status-change tracking (redflag_prevframe/_prevstatus/_statuschange_time, …): when a
    // flag's 2-bit status changes the OLD status crossfades out (drawpic_aspect_skin_expanding) while the NEW one
    // fades in over 0.5s (f = bound(0, elapsed*2, 1)). Index 0..4 = red/blue/yellow/pink/neutral.
    private readonly int[] _ctfFlagPrevFrame = new int[5];
    private readonly int[] _ctfFlagPrevStatus = new int[5];
    private readonly double[] _ctfFlagChangeTime = new double[5];

    // QC NUM_TEAM_* team-membership bits of _teams_available: BIT(0)=red BIT(1)=blue BIT(2)=yellow BIT(3)=pink.
    private const int Num1 = 1, Num2 = 2, Num3 = 3, Num4 = 4;

    /// <summary>This panel changes only when objective status is pushed, but carrying-blink animates.</summary>
    public override bool IsDynamic => true;

    private double _localClock;
    // (perf 2026-08-03, R1) Driven centrally by HudManager — no per-panel Godot _Process callback.
    public override void DriveFrame(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private double CurrentTime()
    {
        // Prefer the sim clock when wired, but never let a half-initialised services container (or a NaN/Inf
        // clock value) leak into the per-frame draw math — fall back to the local accumulator.
        if (Api.Services is not null)
        {
            try
            {
                float t = Api.Clock.Time;
                if (float.IsFinite(t)) return t;
            }
            catch { /* services container present but clock not yet ready */ }
        }
        return _localClock;
    }

    /// <summary>Set the Domination point-per-second readout (QC STAT(DOM_PPS_*) + DOM_TOTAL_PPS).</summary>
    public void SetDominationPps(float red, float blue, float yellow, float pink, float total)
    {
        _domPps[0] = red; _domPps[1] = blue; _domPps[2] = yellow; _domPps[3] = pink;
        _domTotalPps = total;
        QueueRedraw();
    }

    protected override void DrawPanel()
    {
        switch (Mode)
        {
            case ModIconsMode.Ctf:        DrawCtf(); break;
            case ModIconsMode.Keyhunt:    DrawKeyhunt(); break;
            case ModIconsMode.Domination: DrawDomination(LayoutCvar("dom_layout")); break;
            case ModIconsMode.ClanArena:  DrawCaStyle(LayoutCvar("ca_layout")); break;
            case ModIconsMode.FreezeTag:  DrawCaStyle(LayoutCvar("freezetag_layout")); break;
            case ModIconsMode.Survival:   DrawSurvival(); break;
            case ModIconsMode.Assault:    DrawAssault(); break;
            case ModIconsMode.Keepaway:   DrawKeepaway(); break;
            case ModIconsMode.TeamKeepaway: DrawTeamKeepaway(); break;
            case ModIconsMode.Lms:        DrawLms(); break;
            case ModIconsMode.Race:       DrawRace(); break;
            case ModIconsMode.NexBall:    DrawNexBall(); break;
            default: return; // None: draw nothing
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Behavior cvars (defaults registered into the shared store; HudConfig invokes this by reflection).
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC the modicons layout cvars exported by each gametype's m_modicons_export
    /// (cl_clanarena.qc/cl_freezetag.qc/cl_domination.qc HUD_Write_Cvar) + the panel's dynamichud flag.
    /// Registered into the SHARED store so console/menu edits flip the layout live.</summary>
    public static void RegisterDefaults(VortexArena.Engine.Simulation.CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // _hud_descriptions.cfg ships ca/freezetag layout = 1 (icon + number); dom layout 1 = icon + percent.
        c.Register("hud_panel_modicons_ca_layout", "1", save);
        c.Register("hud_panel_modicons_freezetag_layout", "1", save);
        c.Register("hud_panel_modicons_dom_layout", "1", save);
        c.Register("hud_panel_modicons_dynamichud", "1", save);
    }

    /// <summary>Read a <c>hud_panel_modicons_&lt;suffix&gt;</c> layout cvar from the shared store (live, so the
    /// console can flip it). Defaults to 1 when unregistered (the shipped skin cfgs set 1).</summary>
    private int LayoutCvar(string suffix)
    {
        string s = CvarStr(suffix);
        if (string.IsNullOrWhiteSpace(s)) return 1;
        float v = CvarF(suffix, 1f);
        if (!float.IsFinite(v)) return 1;
        // Layouts are 0 (icon-only) / 1 (icon+pct) / 2 (icon+avg). Clamp so a bogus cvar can't yield int
        // overflow ((int)Infinity) or a wild value that confuses the layout==2 branch.
        return (int)Mathf.Clamp(v, 0f, 2f);
    }

    // QC NUM_TEAM_* swatch fallback colors: red '1 0 0', blue '0 0 1', yellow '1 1 0', pink '1 0 1'.
    private static readonly Color[] TeamColors =
    {
        new(1f, 0.2f, 0.2f, 1f),  // red
        new(0.2f, 0.45f, 1f, 1f), // blue
        new(1f, 1f, 0.2f, 1f),    // yellow
        new(1f, 0.3f, 0.8f, 1f),  // pink
    };

    // Pure-component team colors used by the count text (QC DrawCAItem/DrawDomItem '1 0 0' style).
    private static readonly Color[] PureTeamColors =
    {
        new(1f, 0f, 0f, 1f),
        new(0f, 0f, 1f, 1f),
        new(1f, 1f, 0f, 1f),
        new(1f, 0f, 1f, 1f),
    };

    private static readonly string[] TeamSkin = { "red", "blue", "yellow", "pink" };

    /// <summary>Aspect-fit-center a content box inside <paramref name="cell"/> at <paramref name="aspect"/>
    /// (width/height), exactly as QC DrawCAItem/DrawDomItem reflow the cell before drawing.</summary>
    private static Rect2 AspectFit(Rect2 cell, float aspect)
    {
        Vector2 pos = cell.Position, size = cell.Size;
        if (size.X / Mathf.Max(1f, size.Y) > aspect)
        {
            float w = aspect * size.Y;
            pos.X += (size.X - w) * 0.5f;
            size.X = w;
        }
        else
        {
            float h = size.X / Mathf.Max(0.0001f, aspect);
            pos.Y += (size.Y - h) * 0.5f;
            size.Y = h;
        }
        return new Rect2(pos, size);
    }

    /// <summary>Draw a skin icon aspect-fitted (QC <c>drawpic_aspect_skin</c>): the source pic keeps a square
    /// aspect, fitted-centered into <paramref name="box"/>, modulated by white at <paramref name="alpha"/>.
    /// Returns false if the pic was missing (so the caller can draw a stand-in).</summary>
    private bool DrawIconAspect(Rect2 box, string pic, float alpha)
    {
        Rect2 fit = AspectFit(box, 1f);
        return DrawSkinPic(pic, fit, new Color(1f, 1f, 1f, alpha));
    }

    /// <summary>QC <c>drawpic_aspect_skin_expanding</c> (client/draw.qc): draw <paramref name="pic"/> aspect-fitted,
    /// scaled up around its center by the QC <c>expandingbox_sizefactor</c> (<c>1.2/(1.2-fadelerp)</c>) and faded by
    /// <c>(1-fadelerp)</c>. Used by the CTF status-change crossfade for the OLD-status flag icon. Silent on a miss
    /// (the new icon already draws the swatch fallback).</summary>
    private void DrawIconExpanding(Rect2 box, string pic, float alpha, float fadelerp)
    {
        fadelerp = Mathf.Clamp(fadelerp, 0f, 1f);
        Rect2 fit = AspectFit(box, 1f);
        float sz = 1.2f / (1.2f - fadelerp);              // QC expandingbox_sizefactor_from_fadelerp
        var newSize = fit.Size * sz;
        var newPos = fit.Position + fit.Size * (0.5f * (1f - sz)); // QC expandingbox_resize_centered_box_offset
        float a = alpha * (1f - fadelerp);               // QC theAlpha * (1 - fadelerp)
        if (a > 0f)
            DrawSkinPic(pic, new Rect2(newPos, newSize), new Color(1f, 1f, 1f, a));
    }

    /// <summary>Resolve a skin pic (skin -> default fall-through) the same way <see cref="HudPanel.DrawSkinPic"/>
    /// does, returning the raw texture (or null on miss) — needed for region draws.</summary>
    private static Texture2D? ResolveSkinPic(string bareName)
        => TextureCache.GetFirst($"gfx/hud/{HudSkin.SkinName}/{bareName}", $"gfx/hud/default/{bareName}");

    /// <summary>Draw the bottom <paramref name="ratio"/> band of a skin pic into <paramref name="dst"/> (the QC
    /// drawsetcliparea stand-in for the Domination highlight fill). Returns false on a missing pic.</summary>
    private bool DrawIconRegionBottom(Rect2 dst, string pic, float ratio, float alpha)
    {
        Texture2D? tex = ResolveSkinPic(pic);
        if (tex is null) return false;
        Vector2 ts = tex.GetSize();
        float h = ts.Y * Mathf.Clamp(ratio, 0f, 1f);
        var src = new Rect2(0f, ts.Y - h, ts.X, h);
        DrawTextureRectRegion(tex, dst, src, new Color(1f, 1f, 1f, alpha));
        return true;
    }

    // ----------------------------------------------------------------------------------------------- CTF
    // Port of HUD_Mod_CTF (cl_ctf.qc): decode each flag's 2-bit status and draw the per-team flag icon at the
    // QC fs/fs2/fs3 layout positions (rotated so the local team's flag is centered), with the carrying-blink,
    // the status-change expand transition, and the stalemate overlay.
    private void DrawCtf()
    {
        int s = ObjectiveStatus;
        int redflag = (s / CtfRedBase) & 3;
        int blueflag = (s / CtfBlueBase) & 3;
        int yellowflag = (s / CtfYellowBase) & 3;
        int pinkflag = (s / CtfPinkBase) & 3;
        int neutralflag = (s / CtfNeutralBase) & 3;
        bool oneFlag = (s & CtfFlagNeutral) != 0;
        bool stalemate = (s & CtfStalemate) != 0;

        // QC mod_active gate: nothing to show => blank panel.
        bool active = redflag != 0 || blueflag != 0 || yellowflag != 0 || pinkflag != 0
                      || neutralflag != 0 || (s & CtfShielded) != 0;
        if (!active) return;

        DrawBackground();

        int nteams = TeamsAvailableBits();
        int tcount = 2;
        if ((nteams & (1 << 2)) != 0) tcount = 3;
        if ((nteams & (1 << 3)) != 0) tcount = 4;

        // QC blink(0.85, 0.15, 5): the carrying flag pulses.
        double now = CurrentTime();
        float carryAlpha = Blink(0.85f, 0.15f, 5f, now);

        // QC X(team) status-change tracker: latch prevstatus on each change, compute elapsed → expand factor f.
        float fRed = CtfTrack(0, redflag, now);
        float fBlue = CtfTrack(1, blueflag, now);
        float fYellow = CtfTrack(2, yellowflag, now);
        float fPink = CtfTrack(3, pinkflag, now);
        float fNeutral = CtfTrack(4, neutralflag, now);

        // Resolve each flag's current icon + alpha (QC X(team, cond) macro).
        bool redCond = MyTeam != Num1 && (nteams & (1 << 0)) != 0;
        bool blueCond = MyTeam != Num2 && (nteams & (1 << 1)) != 0;
        bool yellowCond = MyTeam != Num3 && (nteams & (1 << 2)) != 0;
        bool pinkCond = MyTeam != Num4 && (nteams & (1 << 3)) != 0;
        string? redIcon = CtfIcon("red", redflag, s, redCond, carryAlpha, out float ra);
        string? blueIcon = CtfIcon("blue", blueflag, s, blueCond, carryAlpha, out float ba);
        string? yellowIcon = CtfIcon("yellow", yellowflag, s, yellowCond, carryAlpha, out float ya);
        string? pinkIcon = CtfIcon("pink", pinkflag, s, pinkCond, carryAlpha, out float pa);
        string? neutralIcon = CtfIcon("neutral", neutralflag, s, oneFlag, carryAlpha, out float na);

        // Resolve each flag's PREVIOUS-status icon + alpha (QC X switch on team##flag_prevstatus). The crossfade-out
        // art uses the carrying icon while currently carrying (QC "make it more visible") and the same shielded gate.
        string? redPrev = CtfPrevIcon("red", _ctfFlagPrevStatus[0], redflag, s, redCond, carryAlpha, out float rpa);
        string? bluePrev = CtfPrevIcon("blue", _ctfFlagPrevStatus[1], blueflag, s, blueCond, carryAlpha, out float bpa);
        string? yellowPrev = CtfPrevIcon("yellow", _ctfFlagPrevStatus[2], yellowflag, s, yellowCond, carryAlpha, out float ypa);
        string? pinkPrev = CtfPrevIcon("pink", _ctfFlagPrevStatus[3], pinkflag, s, pinkCond, carryAlpha, out float ppa);
        string? neutralPrev = CtfPrevIcon("neutral", _ctfFlagPrevStatus[4], neutralflag, s, oneFlag, carryAlpha, out float npa);

        // QC layout split factors by team count (cl_ctf.qc:123-132).
        float fs, fs2, fs3;
        if (oneFlag)
        {
            // only the neutral flag in one-flag mode (QC nulls the team icons + their prevstatus)
            redIcon = blueIcon = yellowIcon = pinkIcon = null;
            redPrev = bluePrev = yellowPrev = pinkPrev = null;
            fs = fs2 = fs3 = 1f;
        }
        else
        {
            switch (tcount)
            {
                case 3: fs = 1f; fs2 = 0.35f; fs3 = 0.35f; break;
                case 4: fs = 0.75f; fs2 = 0.25f; fs3 = 0.5f; break;
                default: fs = 0.5f; fs2 = 0.5f; fs3 = 0.5f; break;
            }
        }

        // QC: long axis = size1 (split between flags), short axis = size2. Here panels are usually wide.
        Vector2 size = Size2;
        bool wide = size.X > size.Y;
        float size1 = wide ? size.X : size.Y;
        float size2 = wide ? size.Y : size.X;

        // The QC rotates flag positions so the LOCAL team's flag sits at the origin (pos) and the rest fan out.
        // Build per-flag offsets along the long axis (e1) by my team.
        float redOff, blueOff, yellowOff, pinkOff;
        switch (MyTeam)
        {
            case Num2: redOff = fs2; blueOff = 0f; yellowOff = -fs2; pinkOff = fs3; break;
            case Num3: redOff = fs3; blueOff = -fs2; yellowOff = 0f; pinkOff = fs2; break;
            case Num4: redOff = -fs2; blueOff = fs3; yellowOff = fs2; pinkOff = 0f; break;
            default:   redOff = 0f; blueOff = fs2; yellowOff = -fs2; pinkOff = fs3; break;
        }

        // flag_size: e1 * fs * size1 (long axis) + e2 * size2 (short axis).
        Vector2 flagSize = wide ? new Vector2(fs * size1, size2) : new Vector2(size2, fs * size1);

        DrawFlag(redIcon, redPrev, fRed, OffsetPos(redOff, size1, wide), flagSize, ra, rpa, stalemate, "R", TeamColors[0]);
        DrawFlag(blueIcon, bluePrev, fBlue, OffsetPos(blueOff, size1, wide), flagSize, ba, bpa, stalemate, "B", TeamColors[1]);
        DrawFlag(yellowIcon, yellowPrev, fYellow, OffsetPos(yellowOff, size1, wide), flagSize, ya, ypa, stalemate, "Y", TeamColors[2]);
        DrawFlag(pinkIcon, pinkPrev, fPink, OffsetPos(pinkOff, size1, wide), flagSize, pa, ppa, stalemate, "P", TeamColors[3]);
        DrawFlag(neutralIcon, neutralPrev, fNeutral, Vector2.Zero, flagSize, na, npa, stalemate, "N", new Color(0.85f, 0.85f, 0.85f, 1f));
    }

    /// <summary>QC X(team) status-change tracker (cl_ctf.qc:63-70): when the flag's status differs from the
    /// previous frame, latch the old status into prevstatus and stamp the change time. Returns
    /// <c>f = bound(0, elapsed*2, 1)</c> — the 0→1 fade-in factor for the new icon (and 1→0 expand for the old).</summary>
    private float CtfTrack(int idx, int flag, double now)
    {
        if (flag != _ctfFlagPrevFrame[idx])
        {
            _ctfFlagChangeTime[idx] = now;
            _ctfFlagPrevStatus[idx] = _ctfFlagPrevFrame[idx];
            _ctfFlagPrevFrame[idx] = flag;
        }
        float elapsed = (float)(now - _ctfFlagChangeTime[idx]);
        return Mathf.Clamp(elapsed * 2f, 0f, 1f);
    }

    /// <summary>QC X(team) prevstatus icon resolver (cl_ctf.qc:95-107): the crossfade-out art for the OLD status.
    /// Like <see cref="CtfIcon"/> but the default branch keeps the carrying icon when the flag is currently being
    /// carried ("make it more visible"), else falls to the shielded icon under the same gate.</summary>
    private string? CtfPrevIcon(string team, int prevStatus, int curFlag, int statItems, bool cond, float carryAlpha, out float alpha)
    {
        alpha = 1f;
        switch (prevStatus)
        {
            case 1: return $"flag_{team}_taken";
            case 2: return $"flag_{team}_lost";
            case 3: alpha = carryAlpha; return $"flag_{team}_carrying";
            default:
                if (curFlag == 3) return $"flag_{team}_carrying";
                if ((statItems & CtfShielded) != 0 && cond) return $"flag_{team}_shielded";
                return null;
        }
    }

    private Vector2 OffsetPos(float frac, float size1, bool wide)
        => wide ? new Vector2(frac * size1, 0f) : new Vector2(0f, frac * size1);

    /// <summary>QC X(team, cond): pick the flag icon name for a 2-bit status (1=taken,2=lost,3=carrying),
    /// or the shielded icon when the flag is home AND the cond (it's an enemy team / one-flag) holds.</summary>
    private string? CtfIcon(string team, int flag, int statItems, bool cond, float carryAlpha, out float alpha)
    {
        alpha = 1f;
        switch (flag)
        {
            case 1: return $"flag_{team}_taken";
            case 2: return $"flag_{team}_lost";
            case 3: alpha = carryAlpha; return $"flag_{team}_carrying";
            default:
                if ((statItems & CtfShielded) != 0 && cond) return $"flag_{team}_shielded";
                return null;
        }
    }

    /// <summary>QC X(team) draw macro (cl_ctf.qc:180-188): stalemate overlay (when a current icon is set), then the
    /// PREVIOUS-status icon expanding+fading out while the transition factor <paramref name="f"/> &lt; 1, then the
    /// current flag icon faded in by <paramref name="f"/> (aspect-fit skin pic with a colored-swatch + tag fallback).</summary>
    private void DrawFlag(string? icon, string? prevIcon, float f, Vector2 offset, Vector2 flagSize,
        float alpha, float prevAlpha, bool stalemate, string fallbackLabel, Color fallbackColor)
    {
        var box = new Rect2(offset, flagSize);

        // QC: if (team##_icon && ctf_stalemate) draw flag_stalemate.
        if (!string.IsNullOrEmpty(icon) && stalemate)
            DrawIconAspect(box, "flag_stalemate", LiveFgAlpha);

        // QC: if (team##_icon_prevstatus && f < 1) drawpic_aspect_skin_expanding(... prevstatus, alpha_prevstatus, f).
        if (!string.IsNullOrEmpty(prevIcon) && f < 1f)
            DrawIconExpanding(box, prevIcon, LiveFgAlpha * prevAlpha, f);

        if (string.IsNullOrEmpty(icon)) return;

        // QC: if (team##_icon) drawpic_aspect_skin(... team##_icon, alpha * f).
        float a = LiveFgAlpha * alpha * f;
        if (!DrawIconAspect(box, icon, a))
        {
            // Stand-in so the flag is never invisible: a team swatch + a short state tag.
            Rect2 fit = AspectFit(box, 1f);
            string tag = icon.EndsWith("_taken") ? "took"
                       : icon.EndsWith("_lost") ? "lost"
                       : icon.EndsWith("_carrying") ? "CARRY"
                       : icon.EndsWith("_shielded") ? "shld" : "";
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.12f, fit.Position.Y + fit.Size.Y * 0.1f,
                fit.Size.X * 0.36f, fit.Size.Y * 0.8f), new Color(fallbackColor.R, fallbackColor.G, fallbackColor.B, a));
            int sz = (int)Mathf.Clamp(fit.Size.Y * 0.32f, 8f, 16f);
            DrawText(new Vector2(fit.Position.X + fit.Size.X * 0.5f, fit.Position.Y + fit.Size.Y * 0.1f),
                fallbackLabel, new Color(1f, 1f, 1f, a), sz);
            DrawText(new Vector2(fit.Position.X + fit.Size.X * 0.5f, fit.Position.Y + fit.Size.Y * 0.5f),
                tag, new Color(1f, 1f, 1f, a * 0.85f), Mathf.Max(8, sz - 2));
        }
    }

    // ------------------------------------------------------------------------------------------- Keyhunt
    // Port of HUD_Mod_KH (cl_keyhunt.qc): the status packs four 5-bit key fields; decode each key's owner and
    // lay out the per-key icons (kh_<color>_taken/carrying + kh_dropped) in QC's quad/horizontal/vertical grid.
    private void DrawKeyhunt()
    {
        int state = ObjectiveStatus;
        if (state == 0) return; // QC: if(!state) return;

        // Per-team key counts (index 0..3 = red..pink, 4 = dropped) and carry total (QC team*_keys/carrying_keys).
        int[] teamKeys = new int[5];
        int allKeys = 0, carrying = 0;
        for (int i = 0; i < 4; i++)
        {
            int keyState = ((state >> (i * 5)) & 31) - 1;
            if (keyState == -1) continue;
            if (keyState == 30) { carrying++; keyState = MyTeam; } // 30 = carried-by-me
            switch (keyState)
            {
                case Num1: teamKeys[0]++; break;
                case Num2: teamKeys[1]++; break;
                case Num3: teamKeys[2]++; break;
                case Num4: teamKeys[3]++; break;
                case 29: teamKeys[4]++; break; // 29 = dropped
            }
            allKeys++;
        }
        if (allKeys == 0) return;

        DrawBackground();

        Vector2 size = Size2;
        float pad = Padding;
        var origin = new Vector2(pad, pad);
        Vector2 area = new(size.X - pad * 2f, size.Y - pad * 2f);

        // QC KH_SLOTS layout: quadratic when all 4 keys + the panel is roughly square; else horizontal/vertical.
        var slots = new Vector2[allKeys];
        Vector2 slotSize;
        if (allKeys == 4 && area.X * 0.5f < area.Y && area.Y * 0.5f < area.X)
        {
            slotSize = new Vector2(area.X * 0.5f, area.Y * 0.5f);
            slots[0] = origin;
            slots[1] = origin + new Vector2(slotSize.X, 0f);
            slots[2] = origin + new Vector2(0f, slotSize.Y);
            slots[3] = origin + new Vector2(slotSize.X, slotSize.Y);
        }
        else if (area.X > area.Y)
        {
            slotSize = new Vector2(area.X / allKeys, area.Y);
            for (int i = 0; i < allKeys; i++) slots[i] = origin + new Vector2(slotSize.X * i, 0f);
        }
        else
        {
            slotSize = new Vector2(area.X, area.Y / allKeys);
            for (int i = 0; i < allKeys; i++) slots[i] = origin + new Vector2(0f, slotSize.Y * i);
        }

        // QC carrying-all blink: oscillate 0.2..1 when the local team carries every key.
        double now = CurrentTime();
        float blink = 0.6f + Mathf.Sin((float)now * 2f * Mathf.Pi) * 0.4f;
        float baseAlpha = LiveFgAlpha;

        int slot = 0;
        // QC draws teams in order red, blue, yellow, pink, then dropped; the local team's keys use the
        // *_carrying art while carrying_keys remain, else *_taken.
        int carryRemain = carrying;
        for (int t = 0; t < 4; t++)
        {
            int n = teamKeys[t];
            // QC: when the local team carries every key, ALL its icons blink (t+1 == MyTeam => teamKeys[t]).
            bool teamBlinks = carrying != 0 && t + 1 == MyTeam && teamKeys[t] == allKeys;
            float a = baseAlpha * (teamBlinks ? blink : 1f);
            while (n-- > 0 && slot < slots.Length)
            {
                bool mine = t + 1 == MyTeam && carryRemain > 0;
                string pic = mine ? $"kh_{TeamSkin[t]}_carrying" : $"kh_{TeamSkin[t]}_taken";
                if (mine) carryRemain--;
                DrawKeySlot(slots[slot++], slotSize, pic, a, TeamColors[t]);
            }
        }
        int dropped = teamKeys[4];
        while (dropped-- > 0 && slot < slots.Length)
            DrawKeySlot(slots[slot++], slotSize, "kh_dropped", baseAlpha, new Color(0.6f, 0.6f, 0.6f, 1f));
    }

    private void DrawKeySlot(Vector2 pos, Vector2 slotSize, string pic, float alpha, Color fallback)
    {
        var box = new Rect2(pos, slotSize);
        if (!DrawIconAspect(box, pic, alpha))
        {
            Rect2 fit = AspectFit(box, 1f);
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.18f, fit.Position.Y + fit.Size.Y * 0.12f,
                fit.Size.X * 0.64f, fit.Size.Y * 0.76f),
                new Color(fallback.R, fallback.G, fallback.B, fallback.A * alpha));
        }
    }

    // ---------------------------------------------------------------------------------------- Domination
    // Port of HUD_Mod_Dom / DrawDomItem (cl_domination.qc): a HUD_GetRowCount grid of one cell per team; each
    // draws the dom_icon_<color> base + a clip-masked dom_icon_<color>-highlighted fill grown bottom-up by the
    // team's share of total points-per-second; layout 1 also draws the percentage text in the team color.
    private void DrawDomination(int layout)
    {
        DrawBackground();

        int n = Mathf.Clamp(TeamCount, 2, 4);
        float aspectRatio = layout != 0 ? 3f : 1f; // QC aspect_ratio = (layout) ? 3 : 1
        float pad = Padding;
        var inner = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);

        int rows = Mathf.Max(1, HudGetRowCount(n, inner.Size, aspectRatio));
        int columns = Mathf.Max(1, Mathf.CeilToInt(n / (float)rows));
        var itemSize = new Vector2(inner.Size.X / columns, inner.Size.Y / rows);

        int row = 0, column = 0;
        for (int i = 0; i < n; i++)
        {
            var pos = new Vector2(inner.Position.X + column * itemSize.X, inner.Position.Y + row * itemSize.Y);
            DrawDomItem(new Rect2(pos, itemSize), aspectRatio, layout, i);
            ++row;
            if (row >= rows) { row = 0; ++column; }
        }
    }

    /// <summary>QC <c>DrawDomItem</c>: the dom_icon for team <paramref name="i"/> with a bottom-up highlight fill
    /// proportional to its pps share, plus the optional percentage text.</summary>
    private void DrawDomItem(Rect2 cell, float aspectRatio, int layout, int i)
    {
        float stat = _domPps[i];
        float ratio = _domTotalPps > 0f ? Mathf.Clamp(stat / _domTotalPps, 0f, 1f) : 0f;
        string pic = $"dom_icon_{TeamSkin[i]}";

        Rect2 fit = AspectFit(cell, aspectRatio);
        Vector2 pos = fit.Position, size = fit.Size;

        // QC draws the icon as a square block sized to mySize.y on the left.
        float iconW = size.Y;
        var iconBox = new Rect2(pos, new Vector2(iconW, size.Y));

        if (layout != 0) // show text too (QC layout 2 = average pps with 2 decimals, else percentage)
        {
            Color baseCol = PureTeamColors[i];
            float sat = 0.5f + ratio * 0.5f; // half-saturated at min pps, full at max (QC color *= 0.5 + ratio*0.5)
            var col = new Color(baseCol.R * sat, baseCol.G * sat, baseCol.B * sat, LiveFgAlpha);
            int sz = (int)Mathf.Clamp(size.Y * 0.7f, 9f, 30f);
            float textX = pos.X + iconW;
            float textW = (2f / 3f) * size.X;
            string text = layout == 2
                ? stat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) // QC ftos_decimals(stat,2)
                : Mathf.RoundToInt(ratio * 100f) + "%";                                     // QC floor(ratio*100+0.5)+"%"
            DrawTextCentered(new Vector2(textX, pos.Y + (size.Y - sz) * 0.5f), textW, text, col, sz);
        }

        // Icon base + bottom-up highlight fill (QC clip-area trick) — skin art, swatch fallback.
        if (!DrawIconAspect(iconBox, pic, LiveFgAlpha))
        {
            DrawRect(iconBox, new Color(0f, 0f, 0f, 0.35f * LiveFgAlpha));
            DrawRect(new Rect2(iconBox.Position.X, iconBox.Position.Y + iconW * (1f - ratio), iconW, iconW * ratio),
                new Color(TeamColors[i].R, TeamColors[i].G, TeamColors[i].B, LiveFgAlpha));
            return;
        }
        if (ratio > 0f)
        {
            // QC clip-area trick: draw the -highlighted variant only over the bottom (ratio) band. Godot's C#
            // CanvasItem has no drawsetcliparea, so emulate it by region-drawing the matching slice of the
            // highlighted texture into the bottom band of the aspect-fit icon box.
            Rect2 iconFit = AspectFit(iconBox, 1f);
            float fillH = iconFit.Size.Y * ratio;
            var dst = new Rect2(iconFit.Position.X, iconFit.Position.Y + (iconFit.Size.Y - fillH),
                iconFit.Size.X, fillH);
            DrawIconRegionBottom(dst, pic + "-highlighted", ratio, LiveFgAlpha);
        }
    }

    // ------------------------------------------------------------------------- ClanArena / FreezeTag
    // Port of HUD_Mod_CA / HUD_Mod_CA_Draw / DrawCAItem (cl_clanarena.qc:19-92); FreezeTag reuses the same
    // drawer with its own layout cvar (HUD_Mod_FreezeTag, cl_freezetag.qc:10-18). A HUD_GetRowCount grid of one
    // cell per active team showing the alive count from STAT(REDALIVE..PINKALIVE); layout 1 adds the per-team
    // player_<color> skin icon on the cell's left half + the count, team-colored, on the right.
    private void DrawCaStyle(int layout)
    {
        DrawBackground();

        int n = Mathf.Clamp(TeamCount, 2, 4);
        float aspectRatio = layout != 0 ? 2f : 1f; // QC aspect_ratio = (layout) ? 2 : 1
        float pad = Padding;
        var inner = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);

        int rows = Mathf.Max(1, HudGetRowCount(n, inner.Size, aspectRatio));
        int columns = Mathf.Max(1, Mathf.CeilToInt(n / (float)rows));
        var itemSize = new Vector2(inner.Size.X / columns, inner.Size.Y / rows);

        int row = 0, column = 0;
        for (int i = 0; i < n; i++)
        {
            var pos = new Vector2(inner.Position.X + column * itemSize.X, inner.Position.Y + row * itemSize.Y);
            DrawCaItem(new Rect2(pos, itemSize), aspectRatio, layout, i);
            ++row;
            if (row >= rows) { row = 0; ++column; } // QC: cells fill row-first down each column
        }
    }

    /// <summary>QC <c>HUD_GetRowCount</c> (client/hud/hud.qc:165-170), formula carried verbatim:
    /// <c>bound(1, floor((sqrt(4*item_aspect*aspect*N + aspect^2) + aspect + 0.5) * 0.5), N)</c>
    /// with aspect = panel height/width.</summary>
    private static int HudGetRowCount(int itemCount, Vector2 size, float itemAspect)
    {
        if (itemCount < 1) return 1;
        float aspect = size.Y / Mathf.Max(1f, size.X);
        float v = Mathf.Floor((Mathf.Sqrt(4f * itemAspect * aspect * itemCount + aspect * aspect) + aspect + 0.5f) * 0.5f);
        if (!float.IsFinite(v)) return 1; // a degenerate panel size must never yield rows=0 (→ /0 in the caller)
        return (int)Mathf.Clamp(v, 1f, itemCount);
    }

    /// <summary>QC <c>DrawCAItem</c>: aspect-fit-center the cell content, then layout 1 = the player_&lt;color&gt;
    /// team icon on the left half + the alive count in the team color on the right half; layout 0 = count only.</summary>
    private void DrawCaItem(Rect2 cell, float aspectRatio, int layout, int i)
    {
        int stat = _alive[i];
        Color countColor = new(PureTeamColors[i].R, PureTeamColors[i].G, PureTeamColors[i].B, LiveFgAlpha);

        Rect2 fit = AspectFit(cell, aspectRatio);
        Vector2 pos = fit.Position, size = fit.Size;

        int fontSize = (int)Mathf.Clamp(size.Y * 0.8f, 9f, 40f);
        if (layout != 0)
        {
            var iconBox = new Rect2(pos, new Vector2(0.5f * size.X, size.Y));
            if (!DrawIconAspect(iconBox, $"player_{TeamSkin[i]}", LiveFgAlpha))
            {
                Rect2 ifit = AspectFit(iconBox, 1f);
                DrawRect(new Rect2(ifit.Position.X + ifit.Size.X * 0.12f, ifit.Position.Y + ifit.Size.Y * 0.12f,
                    ifit.Size.X * 0.76f, ifit.Size.Y * 0.76f),
                    new Color(TeamColors[i].R, TeamColors[i].G, TeamColors[i].B, 0.85f * LiveFgAlpha));
            }
            DrawTextCentered(new Vector2(pos.X + 0.5f * size.X, pos.Y + (size.Y - fontSize) * 0.5f),
                0.5f * size.X, stat.ToString(), countColor, fontSize);
        }
        else
        {
            DrawTextCentered(new Vector2(pos.X, pos.Y + (size.Y - fontSize) * 0.5f),
                size.X, stat.ToString(), countColor, fontSize);
        }
    }

    // ---------------------------------------------------------------------------------------- Survival
    // Port of HUD_Mod_Survival (cl_survival.qc:47-75): one centered own-role tag — "Hunter" in '1 0 0' /
    // "Survivor" in '0 1 0'. The QC pre-round/spectating hide (STAT(GAMESTARTTIME)/STAT(ROUNDSTARTTIME) >
    // time || spectating) is the SERVER's job in the port: it sends status 0 until the roles are live,
    // and 0 draws nothing.
    private void DrawSurvival()
    {
        if (SurvivalStatus == 0) return;
        DrawBackground();
        bool hunter = SurvivalStatus == 2; // SURV_STATUS_HUNTER (survival.qh:37)
        string text = hunter ? "Hunter" : "Survivor";
        Color color = hunter ? new Color(1f, 0f, 0f, LiveFgAlpha) : new Color(0f, 1f, 0f, LiveFgAlpha);
        float pad = Padding;
        int fontSize = (int)Mathf.Clamp((Size2.Y - pad * 2f) * 0.6f, 10f, 32f);
        DrawTextCentered(new Vector2(pad, (Size2.Y - fontSize) * 0.5f), Size2.X - pad * 2f, text, color, fontSize);
    }

    // ------------------------------------------------------------------------------------------ Assault
    // Port extension (stock has no Assault m_modicons): draw the local player's objective role using the
    // shipped as_defend / as_destroy skin art, aspect-fit-centered, with a text fallback. 0 = blank.
    private void DrawAssault()
    {
        if (AssaultStatus == 0) return;
        DrawBackground();
        bool destroy = AssaultStatus == 2;
        string pic = destroy ? "as_destroy" : "as_defend";
        float pad = Padding;
        var box = new Rect2(pad, pad, Size2.X - pad * 2f, Size2.Y - pad * 2f);
        if (!DrawIconAspect(box, pic, LiveFgAlpha))
        {
            // QC AssaultDefend/Destroy waypoint color '1 0.5 0'.
            var col = new Color(1f, 0.5f, 0f, LiveFgAlpha);
            int fontSize = (int)Mathf.Clamp(box.Size.Y * 0.5f, 10f, 28f);
            DrawTextCentered(new Vector2(box.Position.X, box.Position.Y + (box.Size.Y - fontSize) * 0.5f),
                box.Size.X, destroy ? "Destroy" : "Defend", col, fontSize);
        }
    }

    // ------------------------------------------------------------------------------------------ Keepaway
    // Port of HUD_Mod_Keepaway (cl_keepaway.qc): one centered keepawayball_carrying icon shown only while the
    // LOCAL player holds the ball (KA_CARRYING). The carrying flag pulses (blink 0.85/0.15/5) and a status-change
    // expand transition (drawpic_aspect_skin_expanding) plays for the first ~0.5s after the carry state flips.
    private void DrawKeepaway()
    {
        double now = CurrentTime();

        // QC: kaball != kaball_prevstatus → record the change time (drives the expand transition's elapsed clock).
        if (KeepawayCarrying != _kaPrevCarrying)
        {
            _kaStatusChangeTime = now;
            _kaPrevCarrying = KeepawayCarrying;
        }

        if (!KeepawayCarrying)
            return; // QC mod_active is forced 1, but with no carry there's nothing to draw → blank panel

        DrawBackground();

        float kaAlpha = Blink(0.85f, 0.15f, 5f, now);
        float f = Mathf.Clamp((float)(now - _kaStatusChangeTime) * 2f, 0f, 1f); // QC bound(0, elapsed*2, 1)

        var box = new Rect2(Vector2.Zero, Size2);
        float a = LiveFgAlpha * kaAlpha * f;
        if (!DrawIconAspect(box, "keepawayball_carrying", a))
        {
            // Stand-in so the carry indicator is never invisible: a small ball glyph + "BALL" tag.
            Rect2 fit = AspectFit(box, 1f);
            var col = new Color(1f, 0.85f, 0.2f, a);
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.3f, fit.Position.Y + fit.Size.Y * 0.18f,
                fit.Size.X * 0.4f, fit.Size.Y * 0.4f), col);
            int sz = (int)Mathf.Clamp(fit.Size.Y * 0.28f, 8f, 16f);
            DrawTextCentered(new Vector2(fit.Position.X, fit.Position.Y + fit.Size.Y * 0.62f),
                fit.Size.X, "BALL", new Color(1f, 1f, 1f, a), sz);
        }
    }

    // -------------------------------------------------------------------------------------- TeamKeepaway
    // Port of HUD_Mod_TeamKeepaway (cl_tka.qc): always-active mod icon. Draws keepawayball_carrying while the
    // LOCAL player carries (TKA_BALL_CARRYING), else the tka_taken_<color> icon for the team that holds the ball
    // (TKA_BALL_TAKEN_*). The carrying flag pulses (blink 0.85/0.15/5) and a status-change expand transition
    // plays for the first ~0.5s after the carry state flips.
    private void DrawTeamKeepaway()
    {
        double now = CurrentTime();
        int stat = TkaBallStatus;
        int carrying = stat & TkaCarrying; // QC: tkaball = stat_items & TKA_BALL_CARRYING

        // QC: tkaball != tkaball_prevstatus → record the change time (drives the expand transition's clock).
        if (carrying != _tkaPrevCarrying)
        {
            _tkaStatusChangeTime = now;
            _tkaPrevCarrying = carrying;
        }

        // QC mod_active is forced 1, but with no carrying/taken bit there's nothing to draw → blank panel.
        string? icon = null;
        if (carrying != 0) icon = "keepawayball_carrying"; // QC TODO: unique team-based icon while carrying
        else if ((stat & TkaTakenRed) != 0) icon = "tka_taken_red";
        else if ((stat & TkaTakenBlue) != 0) icon = "tka_taken_blue";
        else if ((stat & TkaTakenYellow) != 0) icon = "tka_taken_yellow";
        else if ((stat & TkaTakenPink) != 0) icon = "tka_taken_pink";
        if (icon is null)
            return;

        DrawBackground();

        float tkaAlpha = Blink(0.85f, 0.15f, 5f, now);
        float f = Mathf.Clamp((float)(now - _tkaStatusChangeTime) * 2f, 0f, 1f); // QC bound(0, elapsed*2, 1)

        var box = new Rect2(Vector2.Zero, Size2);
        float a = LiveFgAlpha * tkaAlpha * f;
        if (!DrawIconAspect(box, icon, a))
        {
            // Stand-in so the indicator is never invisible: a small ball glyph + tag.
            Rect2 fit = AspectFit(box, 1f);
            var col = carrying != 0 ? new Color(1f, 0.85f, 0.2f, a) : new Color(0.85f, 0.85f, 0.85f, a);
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.3f, fit.Position.Y + fit.Size.Y * 0.18f,
                fit.Size.X * 0.4f, fit.Size.Y * 0.4f), col);
            int sz = (int)Mathf.Clamp(fit.Size.Y * 0.28f, 8f, 16f);
            DrawTextCentered(new Vector2(fit.Position.X, fit.Position.Y + fit.Size.Y * 0.62f),
                fit.Size.X, carrying != 0 ? "BALL" : "TKA", new Color(1f, 1f, 1f, a), sz);
        }
    }

    // ------------------------------------------------------------------------------------------ NexBall
    // Port of HUD_Mod_NexBall (common/gametypes/gametype/nexball/cl_nexball.qc): one centered nexball_carrying icon
    // shown only while the LOCAL player holds the ball. The carrying flag pulses (blink 0.85/0.15/5) and a
    // status-change expand transition (drawpic_aspect_skin_expanding) plays for the first ~0.5s after the carry
    // state flips — identical shape to HUD_Mod_Keepaway, with the nexball_carrying skin art (shipped in every skin).
    private void DrawNexBall()
    {
        double now = CurrentTime();

        // QC: nb != nb_prevstatus → record the change time (drives the expand transition's elapsed clock).
        if (NexBallCarrying != _nbPrevCarrying)
        {
            _nbStatusChangeTime = now;
            _nbPrevCarrying = NexBallCarrying;
        }

        if (!NexBallCarrying)
            return; // QC mod_active is forced 1, but with no carry there's nothing to draw → blank panel

        DrawBackground();

        float nbAlpha = Blink(0.85f, 0.15f, 5f, now);
        float f = Mathf.Clamp((float)(now - _nbStatusChangeTime) * 2f, 0f, 1f); // QC bound(0, elapsed*2, 1)

        var box = new Rect2(Vector2.Zero, Size2);
        float a = LiveFgAlpha * nbAlpha * f;
        if (!DrawIconAspect(box, "nexball_carrying", a))
        {
            // Stand-in so the carry indicator is never invisible: a small ball glyph + "BALL" tag.
            Rect2 fit = AspectFit(box, 1f);
            var col = new Color(1f, 0.85f, 0.2f, a);
            DrawRect(new Rect2(fit.Position.X + fit.Size.X * 0.3f, fit.Position.Y + fit.Size.Y * 0.18f,
                fit.Size.X * 0.4f, fit.Size.Y * 0.4f), col);
            int sz = (int)Mathf.Clamp(fit.Size.Y * 0.28f, 8f, 16f);
            DrawTextCentered(new Vector2(fit.Position.X, fit.Position.Y + fit.Size.Y * 0.62f),
                fit.Size.X, "BALL", new Color(1f, 1f, 1f, a), sz);
        }

        // QC HUD_Mod_NexBall power-meter bar (cl_nexball.qc): while the carrier is charging a throw, draw a
        // progress bar whose length is the nb_pb_period triangle wave — launch power ramps minpower→maxpower→
        // minpower across the meter period. NexBallMeterPhase carries the wave PHASE (0..255); -1 = not charging.
        int meterPhase = NexBallMeterPhase;
        if (meterPhase >= 0)
        {
            // QC nb_pb_period triangle wave: 0→127 ramps up, 128→255 ramps down (peak at the half period).
            int mp = Mathf.Clamp(meterPhase, 0, 255);
            float tri = mp < 128 ? mp / 127f : (255 - mp) / 127f;
            tri = Mathf.Clamp(tri, 0f, 1f);

            // Skin-tinted (hud_progressbar_nexball_color), QC default '1 0.85 0.2'. Sized to the panel box, sitting
            // along the bottom band so it reads under the carrying icon — reuse the shared progress-bar primitive.
            Color barCol = GlobalColor("hud_progressbar_nexball_color", new Color(1f, 0.85f, 0.2f));
            float pad = Padding;
            float barH = Mathf.Max(2f, Size2.Y * 0.18f);
            var barBox = new Rect2(pad, Size2.Y - pad - barH, Mathf.Max(0f, Size2.X - pad * 2f), barH);
            if (barBox.Size.X > 0f && barBox.Size.Y > 0f)
            {
                DrawRect(barBox, new Color(0f, 0f, 0f, 0.35f * LiveFgAlpha));
                DrawProgressBar(barBox, "progressbar", tri, vertical: false, baralign: 0, barCol, LiveFgAlpha);
            }
        }
    }

    // ------------------------------------------------------------------------------------------ LMS
    // Port of HUD_Mod_LMS_Draw (common/gametypes/gametype/lms/cl_lms.qc:27-64): a 2-item HUD_GetRowCount grid
    // (aspect 2) — item 1 = the leader-count icon (player_neutral, with a flag_stalemate overlay when the
    // leaders are inside their radar show-window) + the leader count; item 2 = the colored "+N" lives lead
    // (yellow / orange at diff==3 / red at diff>=4). Drawn only when there is at least one leader.
    private void DrawLms()
    {
        int leadersCount = LmsLeaderCount; // QC STAT(REDALIVE)
        if (leadersCount == 0)
            return; // QC: mod_active = 0; return; — nothing to show

        DrawBackground();

        // QC: rows = HUD_GetRowCount(2, mySize, 2); columns = ceil(2 / rows).
        const float aspectRatio = 2f;
        int rows = Mathf.Max(1, HudGetRowCount(2, Size2, aspectRatio));
        int columns = Mathf.Max(1, Mathf.CeilToInt(2f / rows));
        var itemSize = new Vector2(Size2.X / columns, Size2.Y / rows);

        // ---- item 1: the leader-count icon (left half = icon, right half = the count). ----
        var pos = Vector2.Zero;
        bool visibleLeaders = LmsLeadersVisible; // QC STAT(OBJECTIVE_STATUS)
        var iconBox = new Rect2(pos, new Vector2(0.5f * itemSize.X, itemSize.Y));

        // QC: a flag_stalemate overlay flashes under the player_neutral icon while the leaders are radar-visible.
        if (visibleLeaders)
            DrawIconAspect(iconBox, "flag_stalemate", LiveFgAlpha);
        if (!DrawIconAspect(iconBox, "player_neutral", LiveFgAlpha))
        {
            Rect2 ifit = AspectFit(iconBox, 1f);
            DrawRect(new Rect2(ifit.Position.X + ifit.Size.X * 0.12f, ifit.Position.Y + ifit.Size.Y * 0.12f,
                ifit.Size.X * 0.76f, ifit.Size.Y * 0.76f), new Color(0.85f, 0.85f, 0.85f, 0.85f * LiveFgAlpha));
        }
        int countFont = (int)Mathf.Clamp(itemSize.Y * 0.8f, 9f, 40f);
        DrawTextCentered(new Vector2(pos.X + 0.5f * itemSize.X, pos.Y + (itemSize.Y - countFont) * 0.5f),
            0.5f * itemSize.X, leadersCount.ToString(), new Color(1f, 1f, 1f, LiveFgAlpha), countFont);

        // QC: the second item is below (rows > 1) or to the right (single row).
        if (rows > 1) pos.Y += itemSize.Y;
        else pos.X += itemSize.X;

        // ---- item 2: the colored "+N" lives lead (QC color ladder: yellow, orange at 3, red at >=4). ----
        int livesDiff = LmsLivesDiff; // QC STAT(BLUEALIVE)
        Color color = livesDiff >= 4 ? new Color(1f, 0f, 0f, LiveFgAlpha)
                    : livesDiff == 3 ? new Color(1f, 0.5f, 0f, LiveFgAlpha)
                    : new Color(1f, 1f, 0f, LiveFgAlpha);
        const float scale = 0.75f; // QC drawstring_aspect(... itemSize * scale ...)
        int diffFont = (int)Mathf.Clamp(itemSize.Y * scale, 9f, 40f);
        DrawTextCentered(new Vector2(pos.X, pos.Y + (itemSize.Y - diffFont) * 0.5f),
            itemSize.X, "+" + livesDiff, color, diffFont);
    }

    // --------------------------------------------------------------------------------------------- Race/CTS
    // Port of HUD_Mod_Race (cl_race.qc:59-164): the Race/CTS mod-icon — personal best + server best times
    // + the race-award medal flash (race_new* art: fail / newtime PB / new-rank green or yellow / server record),
    // faded over 5s. Only shown when the score primary column is a TIME column and the game is NOT a team race
    // (QC: "if(!(scores_flags(ps_primary) & SFL_TIME) || teamplay) { mod_active = 0; return; }").
    //
    // The layout: when the panel is wider than tall (the default), text+time rows sit on the left half and the
    // medal icon on the right half (each in a centered squareSize×squareSize box). Tall panels use the upper
    // half for text+time and the lower half for the medal.
    //
    // QC race_showTime draws "Personal best" + TIME_ENCODED_TOSTRING(pb), "Server best" + TIME_ENCODED_TOSTRING(sr).
    // An expand transition (drawstring_aspect_expanding) fades in the row for ~1 s when the value changes. The port
    // approximates this with an alpha ramp from 0 → 1 over 1 s after the value changes.
    private void DrawRace()
    {
        double now = CurrentTime();

        // QC: if(!(scores_flags(ps_primary) & SFL_TIME) || teamplay) { mod_active = 0; return; }
        // The net layer sets Mode=Race only when the local gametype is Race/CTS with SFL_TIME primary; skip in
        // a team race (no records shown).  An empty pb+server-record is fine to show (draws "0:00.00").

        // ---- personal-best change detection → re-arm the expand animation ---
        float pb = RaceModIconPb;
        if (pb != _racePbPrev)
        {
            _racePbPrev = pb;
            _racePbChangeTime = now;
        }
        // ---- server-record change detection → re-arm the expand animation ---
        float sr = RaceModIconServerRecord;
        if (sr != _raceServerRecordPrev)
        {
            _raceServerRecordPrev = sr;
            _raceServerRecordChangeTime = now;
        }

        // ---- medal-status edge → set the 5-second fade window (QC race_status_time = time + 5) ----
        int status = RaceModIconStatus;
        string statusName = RaceModIconStatusName;
        if (status != _raceStatusPrev || statusName != _raceStatusNamePrev)
        {
            if (status >= 0)
                _raceStatusTime = now + 5.0; // QC: race_status_time = time + 5
            _raceStatusPrev = status;
            _raceStatusNamePrev = statusName;
        }
        // QC: if (race_status_time - time <= 0) reset
        if (_raceStatusTime - now <= 0.0 && status >= 0)
        {
            RaceModIconStatus = -1;
            RaceModIconStatusName = "";
            status = -1;
        }

        // Any data to show?  Suppress entirely when there is nothing (QC mod_active = 0 => blank).
        bool hasPb = pb > 0f;
        bool hasServer = sr > 0f;
        if (!hasPb && !hasServer && status < 0)
            return;

        DrawBackground();

        Vector2 size = Size2;
        float squareSize;
        Vector2 textPos, medalPos;
        if (size.X > size.Y)
        {
            // QC: text on left, medal on right.
            squareSize = Mathf.Min(size.Y, size.X / 2f);
            float ox = 0.5f * Mathf.Max(0f, size.X / 2f - squareSize);
            float oy = 0.5f * (size.Y - squareSize);
            textPos = new Vector2(ox, oy);
            medalPos = new Vector2(ox + size.X * 0.5f, oy);
        }
        else
        {
            // QC: text on top, medal below.
            squareSize = Mathf.Min(size.X, size.Y / 2f);
            float ox = 0.5f * (size.X - squareSize);
            float oy = 0.5f * Mathf.Max(0f, size.Y / 2f - squareSize);
            textPos = new Vector2(ox, oy);
            medalPos = new Vector2(ox, oy + size.Y * 0.5f);
        }

        // QC textSize = vec2(squareSize, 0.25 * squareSize); each row is 0.25*squareSize tall.
        float rowH = 0.25f * squareSize;
        int rowSz = (int)Mathf.Clamp(rowH * 0.75f, 8f, 20f);

        // QC race_showTime draws the label on the left and the time on the right of the row.
        // "Personal best" at textPos, "Server best" at textPos + eY * 0.5 * squareSize.
        float pbFade = Mathf.Clamp((float)(now - _racePbChangeTime), 0f, 1f);
        float srFade = Mathf.Clamp((float)(now - _raceServerRecordChangeTime), 0f, 1f);
        float baseAlpha = LiveFgAlpha;

        DrawRaceTimeRow(textPos, squareSize, rowH, rowSz, "Personal best", pb, baseAlpha * pbFade);
        DrawRaceTimeRow(new Vector2(textPos.X, textPos.Y + 0.5f * squareSize), squareSize, rowH, rowSz,
            "Server best", sr, baseAlpha * srFade);

        // ---- medal flash (QC race_status 0/1/2/3, faded over last second of the 5-s window) ----
        if (status < 0 || _raceStatusTime <= now) return;
        float a = Mathf.Clamp((float)(_raceStatusTime - now), 0f, 1f); // QC bound(0, race_status_time - time, 1)
        if (a <= 0f) return;

        string art = status switch
        {
            0 => "race_newfail",
            1 => "race_newtime",
            2 => RaceModIconStatusRankIsMine ? "race_newrankgreen" : "race_newrankyellow",
            3 => "race_newrecordserver",
            _ => "",
        };
        if (art == "") return;

        // QC: if(race_status == 0) drawpic at medalPos '1 1 0' * squareSize, else '1 1 0' * 0.8 * squareSize offset
        float iconSz = status == 0 ? squareSize : squareSize * 0.8f;
        float iconOff = status == 0 ? 0f : squareSize * 0.1f;
        var iconBox = new Rect2(medalPos.X + iconOff, medalPos.Y, iconSz, iconSz);
        if (!DrawIconAspect(iconBox, art, baseAlpha * a))
        {
            // Stand-in: a colored disc in the medal square.
            Color disc = status switch
            {
                0 => new Color(0.8f, 0.2f, 0.1f, baseAlpha * a),
                3 => new Color(1f, 0.55f, 0f, baseAlpha * a),
                2 => RaceModIconStatusRankIsMine
                    ? new Color(0.3f, 1f, 0.3f, baseAlpha * a)
                    : new Color(1f, 0.9f, 0.2f, baseAlpha * a),
                _ => new Color(0.6f, 0.8f, 1f, baseAlpha * a),
            };
            Rect2 fit = AspectFit(iconBox, 1f);
            DrawCircle(fit.GetCenter(), fit.Size.X * 0.4f, disc);
        }

        // QC: player name at medalPos + '0 0.8 0' * squareSize; ordinal at + '0 0.15 0' * squareSize.
        int nameSz = (int)Mathf.Clamp(squareSize * 0.1f, 7f, 14f);
        if (!string.IsNullOrEmpty(statusName))
            DrawTextCentered(new Vector2(medalPos.X, medalPos.Y + 0.8f * squareSize), squareSize,
                statusName, new Color(1f, 1f, 1f, baseAlpha * a), nameSz);
    }

    /// <summary>QC <c>race_showTime</c>: draw a label on the left half and the formatted time on the right half of
    /// a row, with the QC expand-transition (approximated as an alpha ramp from 0 → 1 over 1s after the value
    /// changes; <paramref name="fade"/> = elapsed since last change clamped to 0..1).</summary>
    private void DrawRaceTimeRow(Vector2 origin, float squareSize, float rowH, int sz,
        string label, float seconds, float fade)
    {
        float alpha = LiveFgAlpha * fade;
        if (alpha <= 0f) return;
        string timeStr = FormatRaceTime(seconds);
        float halfW = squareSize * 0.5f;
        var col = new Color(1f, 1f, 1f, alpha);
        DrawTextCentered(new Vector2(origin.X, origin.Y + (rowH - sz) * 0.5f), halfW, label, col, sz);
        DrawTextCentered(new Vector2(origin.X + halfW, origin.Y + (rowH - sz) * 0.5f), halfW, timeStr, col, sz);
    }

    /// <summary>QC <c>TIME_ENCODED_TOSTRING</c> M:SS.HH format for the mod-icon time rows.</summary>
    private static string FormatRaceTime(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f) return "";
        if (seconds > 5999.99f) seconds = 5999.99f;
        int h = Mathf.RoundToInt(seconds * 100f);
        int minutes = h / 6000;
        int rem = h - minutes * 6000;
        int s = rem / 100;
        int hh = rem % 100;
        return $"{minutes}:{s:D2}.{hh:D2}";
    }

    // -------------------------------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>_teams_available</c> bitfield (BIT(0)=red BIT(1)=blue BIT(2)=yellow BIT(3)=pink). The
    /// port feeds team membership through <see cref="TeamCount"/>, so reconstruct the contiguous low bits.</summary>
    private int TeamsAvailableBits()
    {
        int n = Mathf.Clamp(TeamCount, 2, 4);
        int bits = 0;
        for (int i = 0; i < n; i++) bits |= 1 << i;
        return bits;
    }

    /// <summary>QC <c>blink(base, amplitude, frequency)</c> from client/draw.qh: a sinusoidal pulse used for the
    /// carrying-flag flash (base 0.85, amplitude 0.15, freq 5).</summary>
    private static float Blink(float baseAlpha, float amplitude, float frequency, double now)
    {
        float a = baseAlpha + amplitude * Mathf.Sin((float)now * frequency);
        return float.IsFinite(a) ? Mathf.Clamp(a, 0f, 1f) : baseAlpha;
    }

    /// <summary>Resolve a global <c>hud_progressbar_*_color</c> skin cvar to a Color (QC '1 0.85 0.2' RGB triplet),
    /// falling back to <paramref name="fallback"/> when unset/unparseable. Same shape as the per-panel helper in
    /// HealthArmorPanel/PhysicsPanel.</summary>
    private static Color GlobalColor(string name, Color fallback)
        => TryParseRgb(GlobalStr(name), out Color c) ? c : fallback;
}
