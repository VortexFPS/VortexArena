using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The menu's console-command dispatcher — the C# stand-in for the engine command layer the QuakeC menu
/// pokes through <c>cmd</c> / command buttons (<c>makeXonoticCommandButton(label, "vid_restart")</c>,
/// <c>"snd_restart"</c>, <c>"disconnect"</c>, <c>"quit"</c>, <c>set</c>/<c>toggle</c>, …). A command string may
/// hold several <c>;</c>-separated statements and embed <c>$cvar</c> / <c>${cvar}</c> expansions, exactly like
/// the stock configs; we split, expand, tokenize, and dispatch each.
///
/// cvar statements (<c>set</c>/<c>seta</c>/<c>toggle</c>/<c>inc</c>/<c>dec</c>/<c>reset…</c>) act on the shared
/// <see cref="MenuState.Cvars"/> store directly. Engine actions that need the host (quit, disconnect, video/
/// audio restart, starting a map) are exposed as hooks the <see cref="Shell"/> wires once at boot. Anything we
/// don't recognise is logged rather than silently dropped — honest about what has no client backend yet.
/// </summary>
public static class MenuCommand
{
    // ---- host hooks (wired once by Shell) ----------------------------------------------------------------

    /// <summary><c>quit</c> — leave the application.</summary>
    public static Action? Quit;

    /// <summary><c>disconnect</c> — leave the current match and return to the main menu.</summary>
    public static Action? Disconnect;

    /// <summary><c>togglemenu</c> — the DP engine command bound to Escape: toggle the in-game pause menu.
    /// Arg 0 = force close; any other value (or no arg) = toggle (open if closed, close/pop if open).</summary>
    public static Action<int>? ToggleMenu;

    /// <summary><c>vid_restart</c> — re-apply the video mode (resolution/fullscreen/vsync) to the window.</summary>
    public static Action? VideoRestart;

    /// <summary><c>snd_restart</c> — re-apply audio settings to the buses.</summary>
    public static Action? AudioRestart;

    /// <summary><c>map NAME</c> / <c>menu_cmd map NAME</c> — start a local match on the given map.</summary>
    public static Action<string>? StartMap;

    /// <summary><c>connect ADDR</c> — connect to a server.</summary>
    public static Action<string>? Connect;

    /// <summary>
    /// QC <c>menu_restart</c> — rebuild the menu (re-runs <c>m_init_delayed</c>: re-apply the skin + rebuild the
    /// window). The faithful effect of a skin or language change: re-translate / re-style the live front-end.
    /// Wired by <see cref="Shell"/> to rebuild the <see cref="MenuRoot"/>'s theme + current screen. Inert (logged)
    /// when unwired (e.g. headless tests).
    /// </summary>
    public static Action? MenuRestart;

    // ---- nav verbs + the live-match gameplay channel (T50) -------------------------------------------------

    /// <summary>
    /// QC <c>m_goto(name, true)</c> — open the named dialog AND hide the menu when that dialog is closed (so a
    /// pause-menu "Servers"/"Profile"/"Input" drops the player back INTO the match on Back). Wired by
    /// <see cref="Shell"/> to push the registered dialog with resume-on-close bookkeeping.
    /// </summary>
    public static Action<string>? OpenDialog;

    /// <summary>
    /// QC <c>m_goto(name, false)</c> — open the named dialog OVER the menu, keeping the menu open when the dialog
    /// is closed (the <c>nexposee</c>/<c>servers</c>/<c>profile</c>/<c>settings</c> family). Wired by
    /// <see cref="Shell"/>.
    /// </summary>
    public static Action<string>? OpenDialogOverlay;

    /// <summary>QC <c>closemenu &lt;name&gt;</c> — close the named dialog if it's open/focused.</summary>
    public static Action<string>? CloseDialog;

    /// <summary>
    /// The menu → live-match gameplay-command channel: the C# stand-in for the engine command layer that the QC
    /// command buttons (<c>"join"</c>/<c>"spec"</c>/<c>"ready"</c>/<c>"cmd ..."</c>) poke. Wired by
    /// <see cref="Shell"/> to the SAME path the console uses (local listen-server world, else the remote
    /// string-command channel). This is the canonical way a menu button reaches the running match — without it
    /// the join/spectate/leave buttons are inert (logged only).
    /// </summary>
    public static Action<string>? SendGameCommand;

    /// <summary>
    /// QC gamestatus test (<c>gamestatus &amp; (GAME_ISSERVER|GAME_CONNECTED)</c>): is a match currently live?
    /// Drives the Leave-match button's disabled state (leavematchbutton.qc). Wired by <see cref="Shell"/>;
    /// defaults to false (no match) when unwired.
    /// </summary>
    public static Func<bool>? InMatch;

    /// <summary>
    /// Run a command string (possibly several <c>;</c>-separated statements with <c>$cvar</c> expansions).
    /// Safe to call with user-authored text; never throws.
    /// </summary>
    public static void Run(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
        try
        {
            foreach (string statement in SplitStatements(command))
            {
                List<string> tokens = Tokenize(Expand(statement));
                if (tokens.Count > 0)
                    Dispatch(tokens);
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[MenuCommand] '{command}' failed: {ex.Message}");
        }
    }

    private static void Dispatch(List<string> t)
    {
        var cvars = MenuState.Cvars;
        string cmd = t[0];
        switch (cmd)
        {
            case "quit":
            case "menu_cmd" when t.Count > 1 && t[1] == "quit":
                Quit?.Invoke();
                break;

            case "disconnect":
                Disconnect?.Invoke();
                break;

            case "vid_restart":
                VideoRestart?.Invoke();
                break;

            case "snd_restart":
                AudioRestart?.Invoke();
                break;

            case "map":
            case "devmap":
                if (t.Count > 1) StartMap?.Invoke(t[1]);
                break;

            case "connect":
                if (t.Count > 1) Connect?.Invoke(t[1]);
                break;

            case "set":
            case "seta":
                if (t.Count >= 3)
                {
                    cvars.Set(t[1], t[2]);
                    cvars.MarkArchived(t[1]); // menu-issued sets are user prefs → persist
                }
                break;

            case "toggle":
                if (t.Count >= 2)
                    Toggle(t);
                break;

            case "inc":
            case "dec":
                if (t.Count >= 2)
                {
                    float step = t.Count >= 3 && float.TryParse(t[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : 1f;
                    if (cmd == "dec") step = -step;
                    cvars.Set(t[1], (cvars.GetFloat(t[1]) + step).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    cvars.MarkArchived(t[1]);
                }
                break;

            case "cvar_resettodefaults_all":
            case "cvar_resettodefaults_saved":
            case "cvar_resettodefaults_nosave":
                foreach (string name in new List<string>(cvars.Names))
                    cvars.ResetToDefault(name);
                break;

            // ---- nav verbs (menu_cmd.qc GameCommand) -------------------------------------------------------

            // "menu_cmd <verb> ..." — strip the menu_cmd prefix and re-dispatch on the verb (the QC command is
            // `menu_cmd directmenu servers`; a bare `directmenu servers` also reaches us, so handle both).
            case "menu_cmd" when t.Count > 1:
                Dispatch(t.GetRange(1, t.Count - 1));
                break;

            // directmenu <name> [args] → m_goto(name, true): open + HIDE-menu-on-close (drops back into match).
            case "directmenu":
                if (t.Count > 1) OpenDialog?.Invoke(t[1]);
                break;

            // directpanelhudmenu <panel> → the HUD-prefixed variant: QC menu_cmd.qc sets filter = "HUD" and
            // resolves the dialog named "HUD<panel>" (each dialog_hudpanel_<p>.qh registers e.g. "HUDweapons").
            // The registry carries the same names, so the HUD editor's double-click/Enter opens the RIGHT dialog.
            case "directpanelhudmenu":
                if (t.Count > 1) OpenDialog?.Invoke("HUD" + t[1]);
                break;

            // QC the DP `togglemenu [0]` command (bound to Escape; also issued by QC HUDSetup_Start to drop the
            // menu before enabling the HUD editor). Arg 0 = force close; no arg / non-zero = toggle.
            case "togglemenu":
                ToggleMenu?.Invoke(t.Count > 1 && int.TryParse(t[1], out int tmArg) ? tmArg : -1);
                break;

            // closemenu <name> → close that dialog if open/focused (QC close_mode).
            case "closemenu":
                if (t.Count > 1) CloseDialog?.Invoke(t[1]);
                break;

            // The m_goto(name, false) family: open OVER the menu, keep the menu open on close. QC remaps
            // skinselect→skinselector and languageselect→languageselector.
            case "nexposee":        OpenDialogOverlay?.Invoke("nexposee"); break;
            case "servers":         OpenDialogOverlay?.Invoke("servers"); break;
            case "profile":         OpenDialogOverlay?.Invoke("profile"); break;
            case "settings":        OpenDialogOverlay?.Invoke("settings"); break;
            case "inputsettings":   OpenDialogOverlay?.Invoke("inputsettings"); break;
            case "videosettings":   OpenDialogOverlay?.Invoke("videosettings"); break;
            case "skinselect":      OpenDialogOverlay?.Invoke("skinselector"); break;
            case "languageselect":  OpenDialogOverlay?.Invoke("languageselector"); break;

            // QC dialog_gamemenu.qc "Quit" → "menu_showquitdialog" opens the quit confirmation.
            case "menu_showquitdialog":
                OpenDialog?.Invoke("quitdialog");
                break;

            // QC commands.cfg aliases `menu_showsandboxtools "menu_cmd directmenu SandboxTools"` (bound to F7 in
            // binds-xonotic.cfg). The alias normally expands through the directmenu verb above, but route the bare
            // command here too so the keybind opens the Sandbox Tools dialog even if the cfg alias isn't loaded.
            case "menu_showsandboxtools":
                OpenDialog?.Invoke("SandboxTools");
                break;

            // QC commands.cfg `alias menu_showhudoptions "menu_cmd directpanelhudmenu ${* ?}"`: with a panel
            // argument (the HUD editor's double-click / Enter on a panel) open that panel's own dialog
            // ("HUD<panel>", e.g. HUDweapons); bare, open the port's panel-dialog directory.
            case "menu_showhudpanels":
            case "menu_showhudoptions":
                if (t.Count > 1 && MenuDialogRegistry.Has("HUD" + t[1]))
                    OpenDialog?.Invoke("HUD" + t[1]);
                else
                    OpenDialogOverlay?.Invoke("hudpanels");
                break;

            // QC commands.cfg `alias menu_showhudexit "menu_cmd directmenu HUDExit"`: ESC in the live HUD
            // editor opens the hudsetup-exit dialog (grid/dock/panel-bg defaults + the Exit-setup button).
            case "menu_showhudexit":
                OpenDialog?.Invoke("HUDExit");
                break;

            // ---- live-match gameplay commands (dialog_gamemenu / teamselect COMMANDBUTTON strings) ----------

            // "cmd <rest>" — DP forwards the rest to the server as a client command (clc_stringcmd). Strip the
            // leading "cmd" and send the remainder (e.g. "cmd selectteam red", "cmd spectate").
            case "cmd":
                if (t.Count > 1) SendGame(string.Join(' ', t.GetRange(1, t.Count - 1)));
                break;

            // "sandbox <rest>" — DP cfg alias `sandbox "cmd g_sandbox ${* ?}"` (commands.cfg). The Sandbox Tools
            // dialog buttons issue these. Expand the alias here (prepend the g_sandbox server verb, route through
            // the live-match channel) so the buttons drive the SandboxMutator backend (Commands.CmdSandbox →
            // HandleCommand) instead of falling through to the inert default.
            case "sandbox":
                SendGame(t.Count > 1 ? "g_sandbox " + string.Join(' ', t.GetRange(1, t.Count - 1)) : "g_sandbox");
                break;

            // Direct gameplay verbs the pause-menu / team-select buttons issue. These are real server commands
            // (Commands.cs: ready/join/spectate; selectteam handles spec/spectator/spectate). resetmatch is the
            // campaign "Restart level" command.
            case "join":
            case "spec":
            case "spectate":
            case "ready":
            case "resetmatch":
                SendGame(string.Join(' ', t));
                break;

            // ---- i18n + skin + config (T28) ----------------------------------------------------------------

            // QC `prvm_language <id>`: the engine cvar that drives the .po load. Set it (it's on the
            // ConfigInterpreter NonCvarCommands denylist, so it's normally a command — here we both store the
            // value and swap the live catalog) and load the matching catalog so the NEXT menu_restart re-translates
            // the UI. menu.qc's "Set language" issues `prvm_language "$x"; menu_restart` so the pair takes effect.
            case "prvm_language":
                if (t.Count > 1)
                {
                    cvars.Set("prvm_language", t[1]);
                    cvars.MarkArchived("prvm_language");
                    Localization.SetLanguage(t[1], MenuState.Vfs);
                }
                break;

            // QC `menu_restart`: rebuild the menu so a language/skin change is visible (re-translate + restyle).
            case "menu_restart":
                if (MenuRestart is not null) MenuRestart();
                else GD.Print("[MenuCommand] menu_restart: no menu host wired (inert).");
                break;

            // QC `saveconfig`: persist the user's archived cvars + binds (DP's config.cfg archive dump).
            case "saveconfig":
                MenuState.SaveUserConfig();
                break;

            default:
                // DP console semantics: a line starting with a registered cvar name is a bare cvar set/print —
                // exactly what ConfigInterpreter.ExecuteLine does. The QC HUD-setup command strings depend on it
                // (`_hud_configure 1` from HUDSetup_Start / DialogHudConfirm, `_hud_configure 0` from the
                // hudsetup-exit "Exit setup" button). Plain `set` semantics — deliberately NO MarkArchived, so a
                // transient like _hud_configure never lands in config.cfg (its Save flag decides persistence).
                if (cvars.Has(cmd))
                {
                    if (t.Count >= 2) cvars.Set(cmd, t[1]);
                    else GD.Print($"\"{cmd}\" is \"{cvars.GetString(cmd)}\"");
                    break;
                }
                GD.Print($"[MenuCommand] '{string.Join(' ', t)}' has no client backend yet (inert).");
                break;
        }
    }

    /// <summary>Send a gameplay command to the live match, or log it inert when there's no channel wired.</summary>
    private static void SendGame(string line)
    {
        if (SendGameCommand is not null)
            SendGameCommand(line);
        else
            GD.Print($"[MenuCommand] gameplay command '{line}' dropped — no live match channel (inert).");
    }

    /// <summary>QC <c>toggle cvar [v1 v2 …]</c>: cycle through the given values, or flip 0↔1 if none given.</summary>
    private static void Toggle(List<string> t)
    {
        var cvars = MenuState.Cvars;
        string name = t[1];
        if (t.Count <= 2)
        {
            cvars.Set(name, cvars.GetFloat(name) != 0f ? "0" : "1");
        }
        else
        {
            string cur = cvars.GetString(name);
            int at = t.IndexOf(cur, 2);
            int next = (at < 0 || at + 1 >= t.Count) ? 2 : at + 1;
            cvars.Set(name, t[next]);
        }
        cvars.MarkArchived(name);
    }

    // ---- parsing -----------------------------------------------------------------------------------------

    /// <summary>Split on <c>;</c> at top level (quotes protect a literal semicolon).</summary>
    private static IEnumerable<string> SplitStatements(string command)
    {
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (char c in command)
        {
            if (c == '"') { inQuote = !inQuote; sb.Append(c); }
            else if (c == ';' && !inQuote) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(c);
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    /// <summary>Replace <c>$name</c> / <c>${name}</c> with the cvar's current value (DP-style expansion).</summary>
    private static string Expand(string s)
    {
        if (s.IndexOf('$') < 0)
            return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '$') { sb.Append(s[i]); continue; }
            int j = i + 1;
            bool braced = j < s.Length && s[j] == '{';
            if (braced) j++;
            int start = j;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
            string name = s[start..j];
            if (braced && j < s.Length && s[j] == '}') j++;
            sb.Append(name.Length > 0 ? MenuState.Cvars.GetString(name) : "$");
            i = j - 1;
        }
        return sb.ToString();
    }

    /// <summary>Whitespace tokenizer honoring <c>"double quoted"</c> tokens.</summary>
    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false, has = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuote = !inQuote; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuote) { if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; } }
            else { sb.Append(c); has = true; }
        }
        if (has) tokens.Add(sb.ToString());
        return tokens;
    }
}
