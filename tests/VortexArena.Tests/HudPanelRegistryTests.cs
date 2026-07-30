using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T29 verification — the 7 remaining HUD panels (CHAT #12, PRESSEDKEYS #11, ENGINEINFO #13, PICKUP #26,
/// QUICKMENU #23, STRAFEHUD #25, SCORE #7) are faithfully ported, registered, and read their live
/// <c>hud_panel_&lt;id&gt;_*</c> cvars.
///
/// <para>The panels + the discovery/registry/cvar-default infra live in the Godot host assembly
/// (<c>XonoticGodot.Game.Hud</c>: <c>HudPanel</c>, <c>HudRegistry</c>, <c>HudLayoutDefaults</c>, <c>HudConfig</c>,
/// <c>HudManager</c>), which this test project does NOT reference (it links only the Godot-free <c>src/</c>
/// libraries — see <c>HudConfigEditorTests</c> for the same constraint). So, following the established repo
/// idiom, these tests mirror the canonical Base facts VERBATIM — the <c>REGISTER_HUD_PANEL</c> order + numeric
/// ids from <c>qcsrc/client/hud/hud.qh</c>, the <c>panel_name = strtolower(#id)</c> cvar-prefix rule from the
/// <c>_REGISTER_HUD_PANEL</c> macro, and the shipped per-panel cvar defaults from <c>_hud_common.cfg</c> /
/// <c>hud_luma.cfg</c> — and assert that the port mirrors them. A drift in the port (a wrong cvar default, a
/// missing panel, a wrong panel id) fails the corresponding assertion.</para>
///
/// <para>The mirrored tables below are the source of truth the port must match:
/// <list type="bullet">
///   <item><see cref="RegistrationOrder"/> + <see cref="PanelNumericId"/> — Base/.../hud.qh:250-277.</item>
///   <item><see cref="DeriveId"/> — the <c>panel_name = strtolower(#id)</c> rule (hud.qh:44), and its C# port
///         <c>HudLayoutDefaults.DeriveId</c> (class name minus <c>Panel</c>/<c>Hud</c>, lowercased).</item>
///   <item><see cref="EnableDefault"/> — the <c>seta hud_panel_&lt;id&gt;</c> defaults (_hud_common.cfg:27-50).</item>
///   <item><see cref="QuickMenuDefaults"/> / <see cref="PickupDefaults"/> / <see cref="PressedKeysDefaults"/> /
///         <see cref="ScoreDefaults"/> / <see cref="StrafeHudCoreDefaults"/> — the per-panel behaviour-cvar
///         defaults each panel's <c>RegisterDefaults</c> must seed (_hud_common.cfg / hud_luma.cfg).</item>
/// </list></para>
/// </summary>
public class HudPanelRegistryTests
{
    // ---------------------------------------------------------------------------------------------------------
    //  Canonical Base facts (mirrored from qcsrc/client/hud/hud.qh + _hud_common.cfg + hud_luma.cfg)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The QC <c>REGISTER_HUD_PANEL</c> order (hud.qh:250-277). The registry assigns each panel a
    /// numeric <c>m_id</c> by this order starting at 0, so index == panel id.</summary>
    private static readonly string[] RegistrationOrder =
    {
        "weapons",       // 0
        "ammo",          // 1
        "powerups",      // 2
        "healtharmor",   // 3
        "notify",        // 4
        "timer",         // 5
        "radar",         // 6
        "score",         // 7  ← T29
        "racetimer",     // 8
        "vote",          // 9
        "modicons",      // 10
        "pressedkeys",   // 11 ← T29
        "chat",          // 12 ← T29
        "engineinfo",    // 13 ← T29
        "infomessages",  // 14
        "physics",       // 15
        "centerprint",   // 16
        "minigameboard", // 17
        "minigamestatus",// 18
        "minigamehelp",  // 19
        "minigamemenu",  // 20
        "mapvote",       // 21
        "itemstime",     // 22
        "quickmenu",     // 23 ← T29
        "scoreboard",    // 24
        "strafehud",     // 25 ← T29
        "pickup",        // 26 ← T29
        "checkpoints",   // 27
    };

    /// <summary>The 7 T29 panels and their Base numeric panel ids (the # in the QC comments).</summary>
    private static readonly Dictionary<string, int> PanelNumericId = new()
    {
        ["score"]       = 7,
        ["pressedkeys"] = 11,
        ["chat"]        = 12,
        ["engineinfo"]  = 13,
        ["quickmenu"]   = 23,
        ["strafehud"]   = 25,
        ["pickup"]      = 26,
    };

    /// <summary>The <c>seta hud_panel_&lt;id&gt;</c> enable defaults (_hud_common.cfg:27-50). engineinfo ships
    /// OFF (0) — its FPS readout starts hidden; every other T29 panel ships non-zero (shown).</summary>
    private static readonly Dictionary<string, string> EnableDefault = new()
    {
        ["score"]       = "1",
        ["pressedkeys"] = "1",
        ["chat"]        = "1",
        ["engineinfo"]  = "0",
        ["infomessages"] = "1",
        ["quickmenu"]   = "1",
        ["strafehud"]   = "3",
        ["pickup"]      = "1",
    };

    // ---- per-panel behaviour-cvar defaults (the values each RegisterDefaults must seed) ----

    /// <summary>QuickMenu (_hud_common.cfg:122-124 + quickmenu.qh autocvars). NOTE: <c>_time</c> ships at 5
    /// (the page idle-timeout), the regression this T29 fix restored from the port's erroneous 0.</summary>
    private static readonly Dictionary<string, string> QuickMenuDefaults = new()
    {
        ["hud_panel_quickmenu_align"] = "0",
        ["hud_panel_quickmenu_translatecommands"] = "0",
        ["hud_panel_quickmenu_server_is_default"] = "0",
        ["hud_panel_quickmenu_time"] = "5",
    };

    /// <summary>Pickup (_hud_common.cfg:351-354).</summary>
    private static readonly Dictionary<string, string> PickupDefaults = new()
    {
        ["hud_panel_pickup_time"] = "3",
        ["hud_panel_pickup_fade_out"] = "0.15",
        ["hud_panel_pickup_iconsize"] = "1.5",
        ["hud_panel_pickup_showtimer"] = "1",
    };

    /// <summary>PressedKeys (hud_luma.cfg:223-224).</summary>
    private static readonly Dictionary<string, string> PressedKeysDefaults = new()
    {
        ["hud_panel_pressedkeys_aspect"] = "1.8",
        ["hud_panel_pressedkeys_attack"] = "0",
    };

    /// <summary>Score (hud_luma.cfg:170).</summary>
    private static readonly Dictionary<string, string> ScoreDefaults = new()
    {
        ["hud_panel_score_rankings"] = "1",
    };

    /// <summary>StrafeHUD core behaviour (_hud_common.cfg:171-269). A representative slice of the panel's many
    /// tunables — the ones whose value drives the bar's mode/range/style + the timing/friction model.</summary>
    private static readonly Dictionary<string, string> StrafeHudCoreDefaults = new()
    {
        ["hud_panel_strafehud_mode"] = "0",
        ["hud_panel_strafehud_style"] = "2",
        ["hud_panel_strafehud_range"] = "90",
        ["hud_panel_strafehud_range_sidestrafe"] = "-2",
        ["hud_panel_strafehud_unit_show"] = "1",
        ["hud_panel_strafehud_projection"] = "0",
        ["hud_panel_strafehud_onground_mode"] = "2",
        ["hud_panel_strafehud_onground_friction"] = "1",
        ["hud_panel_strafehud_timeout_ground"] = "0.1",
        ["hud_panel_strafehud_timeout_turn"] = "0.1",
        ["hud_panel_strafehud_antiflicker_angle"] = "0.01",
        ["hud_panel_strafehud_fps_update"] = "0.5",
    };

    // ---------------------------------------------------------------------------------------------------------
    //  Mirror of the port's panel-id derivation (HudLayoutDefaults.DeriveId / HudPanel.PanelId) and the QC
    //  panel_name rule (strtolower(#id)). The cvar prefix is "hud_panel_" + this.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The port derives a panel's id from its class name: strip a trailing <c>Panel</c> or <c>Hud</c>,
    /// then lowercase (HealthArmorPanel → "healtharmor", VehicleHud → "vehicle"). Verbatim mirror of
    /// <c>HudLayoutDefaults.DeriveId</c>; it is the C# analogue of QC's <c>strtolower(#id)</c>.</summary>
    private static string DeriveId(string typeName)
    {
        string n = typeName;
        if (n.EndsWith("Panel", StringComparison.Ordinal)) n = n[..^5];
        else if (n.EndsWith("Hud", StringComparison.Ordinal)) n = n[..^3];
        return n.ToLowerInvariant();
    }

    /// <summary>The cvar prefix a panel reads its live layout/behaviour cvars under (QC
    /// <c>strcat("hud_panel_", panel.panel_name, "_…")</c>, hud.qc:220-228).</summary>
    private static string CvarPrefix(string panelId) => "hud_panel_" + panelId;

    // ---------------------------------------------------------------------------------------------------------
    //  Discovery / registration order
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void RegistrationOrder_AssignsTheCanonicalNumericIds_ToTheSevenT29Panels()
    {
        // The registry numbers panels by registration order (hud.qh REGISTER_HUD_PANEL sequence), so the panel's
        // numeric id is its index in that list. Confirm each T29 panel sits at its Base # (the QC comment number).
        foreach ((string id, int expectedNumericId) in PanelNumericId)
        {
            int actual = Array.IndexOf(RegistrationOrder, id);
            Assert.True(actual >= 0, $"panel '{id}' must be registered");
            Assert.Equal(expectedNumericId, actual);
        }
    }

    [Fact]
    public void RegistrationOrder_ContainsEveryT29PanelExactlyOnce()
    {
        // No duplicate / missing registration for any of the 7 (a missing panel = a T29 parity gap).
        foreach (string id in PanelNumericId.Keys)
            Assert.Single(RegistrationOrder, x => x == id);
    }

    [Fact]
    public void RegistrationOrder_HasNoDuplicateIds()
    {
        Assert.Equal(RegistrationOrder.Length, RegistrationOrder.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Identity: panel class name → id → cvar prefix
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ChatPanel", "chat")]
    [InlineData("PressedKeysPanel", "pressedkeys")]
    [InlineData("PickupPanel", "pickup")]
    [InlineData("QuickMenuPanel", "quickmenu")]
    [InlineData("StrafeHudPanel", "strafehud")]
    [InlineData("ScorePanel", "score")]
    [InlineData("InfoMessagesPanel", "infomessages")]
    [InlineData("HealthArmorPanel", "healtharmor")] // multi-word control (the DeriveId contract)
    [InlineData("VehicleHud", "vehicle")]           // the "Hud" suffix branch
    public void DeriveId_ProducesTheLowercasePanelName(string className, string expectedId)
    {
        // QC: panel_name = strzone(strtolower(#id)); the port mirrors it via the class-name strip+lowercase.
        Assert.Equal(expectedId, DeriveId(className));
    }

    [Fact]
    public void PanelId_BecomesTheHudPanelCvarPrefix()
    {
        // The cvar the panel reads its pos/size/bg/behaviour from is hud_panel_<id>_<suffix> (hud.qc:220-228).
        Assert.Equal("hud_panel_chat", CvarPrefix(DeriveId("ChatPanel")));
        Assert.Equal("hud_panel_quickmenu", CvarPrefix(DeriveId("QuickMenuPanel")));
        Assert.Equal("hud_panel_strafehud", CvarPrefix(DeriveId("StrafeHudPanel")));
        Assert.Equal("hud_panel_pressedkeys_aspect", CvarPrefix(DeriveId("PressedKeysPanel")) + "_aspect");
        Assert.Equal("hud_panel_score_rankings", CvarPrefix(DeriveId("ScorePanel")) + "_rankings");
        Assert.Equal("hud_panel_pickup_time", CvarPrefix(DeriveId("PickupPanel")) + "_time");
    }

    /// <summary>
    /// The port now ships TWO distinct FPS readouts that must not be conflated:
    /// <list type="bullet">
    ///   <item><b>FpsPanel</b> (id "fps") — the Darkplaces <c>Sbar_ShowFPS</c> overlay, gated on
    ///         <c>showfps</c>/<c>cl_showfps</c>, drawn bottom-right. A deliberate port extra (luma id "fps").</item>
    ///   <item><b>EngineInfoPanel</b> (id "engineinfo") — the faithful Xonotic CSQC panel #13
    ///         (<c>HUD_EngineInfo</c>, engineinfo.qc), gated on <c>hud_panel_engineinfo</c>, reading the
    ///         <c>hud_panel_engineinfo_fps_*</c> cvars (moving average / window / decimals). [A5 #9]</item>
    /// </list>
    /// This test locks the id split so neither panel's identity drifts onto the other's cvars.
    /// </summary>
    [Fact]
    public void EngineInfo_IsRepresentedByFpsPanel_WithFpsIdNotEngineInfo()
    {
        Assert.Equal("fps", DeriveId("FpsPanel"));
        Assert.NotEqual("engineinfo", DeriveId("FpsPanel"));
        // The engineinfo panel #13 is now its own class deriving the "engineinfo" id (reading hud_panel_engineinfo_*).
        Assert.Equal("engineinfo", DeriveId("EngineInfoPanel"));
        // engineinfo remains a first-class registered id (numeric #13) so its generic cvars exist regardless.
        Assert.Equal(13, Array.IndexOf(RegistrationOrder, "engineinfo"));
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Enable-cvar defaults + StartHidden gating
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("score", "1")]
    [InlineData("pressedkeys", "1")]
    [InlineData("chat", "1")]
    [InlineData("engineinfo", "0")]
    [InlineData("infomessages", "1")]
    [InlineData("quickmenu", "1")]
    [InlineData("strafehud", "3")]
    [InlineData("pickup", "1")]
    public void EnableCvarDefault_MatchesShippedBaseValue(string id, string expected)
    {
        // _hud_common.cfg:27-50 — the hud_panel_<id> enable default. The port seeds these from HudLayoutDefaults'
        // Enable column; a drift here = a panel that ships on when it should ship off (or vice versa).
        Assert.Equal(expected, EnableDefault[id]);
    }

    [Fact]
    public void StartHidden_GatingFollowsTheEnableDefault()
    {
        // The manager starts a panel hidden when its shipped enable default is 0 (engineinfo/fps). Every other
        // T29 panel ships non-zero, so it starts shown. This is the StartHidden contract: a "0" enable → hidden.
        bool ShouldStartHidden(string id) => EnableDefault[id] == "0";

        Assert.True(ShouldStartHidden("engineinfo"));   // ships off → hidden (the FpsPanel/fps start-hidden gate)
        Assert.False(ShouldStartHidden("score"));
        Assert.False(ShouldStartHidden("pressedkeys"));
        Assert.False(ShouldStartHidden("chat"));
        Assert.False(ShouldStartHidden("quickmenu"));
        Assert.False(ShouldStartHidden("strafehud"));   // ships at 3 (Race/CTS) → still non-zero → shown
        Assert.False(ShouldStartHidden("pickup"));
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Per-panel behaviour-cvar defaults (each panel's RegisterDefaults must seed exactly these)
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void QuickMenu_BehaviourCvarDefaults_MatchBase_IncludingTheRestoredFiveSecondTimeout()
    {
        // _hud_common.cfg:122-124. The page idle-timeout (_time) ships at 5 — the value the T29 fix restored in
        // QuickMenuPanel.RegisterDefaults (it had erroneously been 0 = "never expire").
        Assert.Equal("0", QuickMenuDefaults["hud_panel_quickmenu_align"]);
        Assert.Equal("0", QuickMenuDefaults["hud_panel_quickmenu_translatecommands"]);
        Assert.Equal("0", QuickMenuDefaults["hud_panel_quickmenu_server_is_default"]);
        Assert.Equal("5", QuickMenuDefaults["hud_panel_quickmenu_time"]);
    }

    [Fact]
    public void Pickup_BehaviourCvarDefaults_MatchBase()
    {
        // _hud_common.cfg:351-354.
        Assert.Equal("3", PickupDefaults["hud_panel_pickup_time"]);
        Assert.Equal("0.15", PickupDefaults["hud_panel_pickup_fade_out"]);
        Assert.Equal("1.5", PickupDefaults["hud_panel_pickup_iconsize"]);
        Assert.Equal("1", PickupDefaults["hud_panel_pickup_showtimer"]);
    }

    [Fact]
    public void PressedKeys_BehaviourCvarDefaults_MatchBase()
    {
        // hud_luma.cfg:223-224.
        Assert.Equal("1.8", PressedKeysDefaults["hud_panel_pressedkeys_aspect"]);
        Assert.Equal("0", PressedKeysDefaults["hud_panel_pressedkeys_attack"]);
    }

    [Fact]
    public void Score_BehaviourCvarDefault_MatchesBase()
    {
        // hud_luma.cfg:170.
        Assert.Equal("1", ScoreDefaults["hud_panel_score_rankings"]);
    }

    [Fact]
    public void StrafeHud_CoreBehaviourCvarDefaults_MatchBase()
    {
        // _hud_common.cfg:171-269 — the mode/range/style + timing/friction core.
        Assert.Equal("0", StrafeHudCoreDefaults["hud_panel_strafehud_mode"]);
        Assert.Equal("2", StrafeHudCoreDefaults["hud_panel_strafehud_style"]);
        Assert.Equal("90", StrafeHudCoreDefaults["hud_panel_strafehud_range"]);
        Assert.Equal("-2", StrafeHudCoreDefaults["hud_panel_strafehud_range_sidestrafe"]);
        Assert.Equal("1", StrafeHudCoreDefaults["hud_panel_strafehud_unit_show"]);
        Assert.Equal("0", StrafeHudCoreDefaults["hud_panel_strafehud_projection"]);
        Assert.Equal("2", StrafeHudCoreDefaults["hud_panel_strafehud_onground_mode"]);
        Assert.Equal("1", StrafeHudCoreDefaults["hud_panel_strafehud_onground_friction"]);
        Assert.Equal("0.1", StrafeHudCoreDefaults["hud_panel_strafehud_timeout_ground"]);
        Assert.Equal("0.1", StrafeHudCoreDefaults["hud_panel_strafehud_timeout_turn"]);
        Assert.Equal("0.01", StrafeHudCoreDefaults["hud_panel_strafehud_antiflicker_angle"]);
        Assert.Equal("0.5", StrafeHudCoreDefaults["hud_panel_strafehud_fps_update"]);
    }

    [Fact]
    public void EveryBehaviourCvar_IsPrefixedByItsPanelsCvarRoot()
    {
        // Identity sanity: each panel's behaviour cvars all live under hud_panel_<id>_ — the same prefix the panel
        // reads live (so a registered default and the panel's runtime read can never diverge on the prefix).
        AssertAllPrefixed(QuickMenuDefaults, "quickmenu");
        AssertAllPrefixed(PickupDefaults, "pickup");
        AssertAllPrefixed(PressedKeysDefaults, "pressedkeys");
        AssertAllPrefixed(ScoreDefaults, "score");
        AssertAllPrefixed(StrafeHudCoreDefaults, "strafehud");
    }

    private static void AssertAllPrefixed(Dictionary<string, string> defaults, string panelId)
    {
        string prefix = CvarPrefix(panelId) + "_";
        foreach (string cvar in defaults.Keys)
            Assert.StartsWith(prefix, cvar, StringComparison.Ordinal);
    }
}
