// Port of qcsrc/menu/command/menu_cmd.qc (the directmenu/m_goto "by-name dialog" lookup) — the C# stand-in for
// FOREACH_ENTITY_ORDERED(it.name != "", ...): a name→dialog map so `menu_cmd directmenu <name>` and the
// nexposee/servers/profile/settings nav verbs can resolve a dialog the way the QC menu resolves an entity by
// its .name field.
using System;
using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Maps the QuakeC dialog <c>name</c> strings (the identifiers <c>menu_cmd directmenu &lt;name&gt;</c> and the
/// <c>nexposee</c>/<c>servers</c>/<c>profile</c>/<c>settings</c>/… verbs reference) to a factory that builds the
/// corresponding port screen. In QC every dialog registers a <c>.name</c> and <c>m_goto</c> finds it by that
/// name; here we keep an explicit table because the port's screens are concrete Godot controls rather than a
/// uniform entity tree. Names absent from the table return <c>null</c> (the caller logs, mirroring QC's
/// <c>LOG_INFO "Invalid command"</c>).
///
/// A few QC names take a sub-tab: <c>inputsettings</c>/<c>videosettings</c> open the Settings dialog focused on
/// the Input/Video tab (QC has dedicated <c>inputsettings</c>/<c>videosettings</c> dialog entities; the port
/// folds them into the one Settings dialog + a tab select, which is the faithful visible result).
/// </summary>
public static class MenuDialogRegistry
{
    // QC dialog .name → screen factory. Names match the strings menu_cmd.qc / dialog_gamemenu.qc use.
    private static readonly Dictionary<string, Func<Control>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nexposee"]    = () => new MainMenu(),                       // the main-menu fan (QC nexposee)
        ["servers"]     = () => new MultiplayerScreen(),             // the server browser host (QC dialog_multiplayer w/ Servers tab)
        ["profile"]     = () => new DialogMultiplayerProfile(),      // QC dialog_multiplayer_profile
        ["settings"]    = () => new DialogSettings(),                // QC dialog_settings
        ["inputsettings"]  = () => MakeSettings("Input"),           // QC inputsettings → Settings on the Input tab
        ["videosettings"]  = () => MakeSettings("Video"),           // QC videosettings → Settings on the Video tab
        ["guide"]       = () => new DialogMediaGuide(),             // QC dialog_multiplayer_media_screenshot's guide → the entry guide
        ["quitdialog"]  = () => new QuitDialog(),                   // QC menu_showquitdialog
        ["hudpanels"]   = () => new DialogHudPanels(),              // HUD-panel config host (menu_showhudoptions)
        // QC dialog_sandboxtools registers .name "SandboxTools"; binds-xonotic.cfg `bind F7 menu_showsandboxtools`
        // aliases (commands.cfg) to `menu_cmd directmenu SandboxTools`, which flows MenuCommand→OpenDialog→
        // Shell.OpenMenuDialog→Create(name). Without this entry that path resolved to null ("no dialog named
        // 'SandboxTools'") and Sandbox Tools was reachable ONLY via the dev `--menu-screen sandbox` flag (Shell's
        // separate lowercase "sandbox" key). Registering the QC name restores the Base in-match entry point — the
        // dialog opens regardless (exactly like Base; g_sandbox must be enabled for its buttons to do anything).
        ["SandboxTools"] = () => new DialogSandboxTools(),          // QC dialog_sandboxtools (menu_showsandboxtools)
        // QC `menu_cmd skinselect`/`languageselect` remap to the skinselector/languageselector dialogs. Those QC
        // dialogs are the skin list / language list; the port folds both into the Settings dialog's User tab
        // (dialog_settings_user.qc — the faithful home of both pickers), so the *select overlay verbs resolve there.
        ["skinselector"]     = () => MakeSettings("User"),
        ["languageselector"] = () => MakeSettings("User"),

        // ---- HUD editor dialogs (QC names). dialog_hudsetup_exit.qh registers "HUDExit" (the editor's ESC →
        // `menu_showhudexit` → `menu_cmd directmenu HUDExit`); each dialog_hudpanel_<p>.qh registers
        // "HUD<panelname>" and `menu_cmd directpanelhudmenu <p>` prefixes the "HUD" filter — so the editor's
        // double-click/Enter (`menu_showhudoptions <p>`) resolves the per-panel dialog by the same name here.
        // The confirm popup has no QC .name (opened by object ref, main.hudconfirmDialog); "hudconfirm" is the
        // port's name for the same dialog. ----
        ["HUDExit"]          = () => new DialogHudSetupExit(),      // QC XonoticHUDExitDialog (menu_showhudexit)
        ["hudconfirm"]       = () => new DialogHudConfirm(),        // QC XonoticHUDConfirmDialog (no QC name)
        ["HUDweapons"]       = () => new DialogHudPanelWeapons(),
        ["HUDammo"]          = () => new DialogHudPanelAmmo(),
        ["HUDpowerups"]      = () => new DialogHudPanelPowerups(),
        ["HUDhealtharmor"]   = () => new DialogHudPanelHealthArmor(),
        ["HUDnotify"]        = () => new DialogHudPanelNotification(),
        ["HUDtimer"]         = () => new DialogHudPanelTimer(),
        ["HUDradar"]         = () => new DialogHudPanelRadar(),
        ["HUDscore"]         = () => new DialogHudPanelScore(),
        ["HUDracetimer"]     = () => new DialogHudPanelRaceTimer(),
        ["HUDvote"]          = () => new DialogHudPanelVote(),
        ["HUDmodicons"]      = () => new DialogHudPanelModIcons(),
        ["HUDpressedkeys"]   = () => new DialogHudPanelPressedKeys(),
        ["HUDchat"]          = () => new DialogHudPanelChat(),
        ["HUDengineinfo"]    = () => new DialogHudPanelEngineInfo(),
        ["HUDinfomessages"]  = () => new DialogHudPanelInfoMessages(),
        ["HUDphysics"]       = () => new DialogHudPanelPhysics(),
        ["HUDcenterprint"]   = () => new DialogHudPanelCenterPrint(),
        ["HUDitemstime"]     = () => new DialogHudPanelItemsTime(),
        ["HUDpickup"]        = () => new DialogHudPanelPickup(),
        ["HUDquickmenu"]     = () => new DialogHudPanelQuickMenu(),
        ["HUDcheckpoints"]   = () => new DialogHudPanelCheckpoints(),
        ["HUDstrafehud"]     = () => new DialogHudPanelStrafeHUD(),
    };

    /// <summary>
    /// Build the screen registered under <paramref name="name"/>, or <c>null</c> when the name isn't registered
    /// (the caller logs, like QC's invalid-command path). Case-insensitive on the name.
    /// </summary>
    public static Control? Create(string name)
        => Factories.TryGetValue(name, out Func<Control>? f) ? f() : null;

    /// <summary>True when <paramref name="name"/> resolves to a screen.</summary>
    public static bool Has(string name) => Factories.ContainsKey(name);

    private static Control MakeSettings(string tab)
    {
        var s = new DialogSettings();
        // SelectTab walks the TabContainer once it's in the tree; defer so _Ready has built the tabs.
        s.Ready += () => s.SelectTab(tab);
        return s;
    }
}
