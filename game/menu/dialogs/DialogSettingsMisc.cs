using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Misc settings tab — a faithful C# port of <c>XonoticMiscSettingsTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings_misc.qc). Binds the same engine cvars the QC binds, in the same
/// order/grouping (Network → HTTP downloads → Other), with the same dependency
/// (<c>cl_movement_errorcompensation</c> greys out unless <c>cl_movement</c>==1) and the same trailing
/// "Apply immediately" command button (<c>menu_restart</c>).
///
/// The two sub-dialogs the QC opens via buttons are represented here:
///   * "Advanced settings..." (the cvar-list editor, dialog_settings_misc_cvars.qc) — that full
///     filterable cvar editor is a separate dialog XonoticGodot does not yet host, so this is a noted/inert
///     button (it logs an unknown command via <see cref="MenuCommand"/>).
///   * "Factory reset" (dialog_settings_misc_reset.qc) — wired to the engine reset command
///     <c>cvar_resettodefaults_all</c> (MenuCommand handles it), per task spec.
/// </summary>
public partial class DialogSettingsMisc : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // QC: makeXonoticCommandButton(_("Apply immediately"), "menu_restart", COMMANDBUTTON_APPLY)
        var applyButton = Widgets.CommandButton("Apply immediately", "menu_restart");

        // --- Network ---------------------------------------------------------------------------------
        box.AddChild(Ui.Header("Network"));

        box.AddChild(Widgets.CheckBox("shownetgraph", "Show netgraph",
            "Show a graph of packet sizes and other information"));

        box.AddChild(Widgets.CheckBox("cl_netrepeatinput", "Packet loss compensation",
            "Each packet includes a copy of the previous message"));

        // QC: setDependent(e, "cl_movement", 1, 1) — only enabled while client-side prediction is on.
        var errorComp = Widgets.CheckBox("cl_movement_errorcompensation", "Movement prediction error compensation");
        box.AddChild(errorComp);
        Dependent.Bind(errorComp, "cl_movement", 1, 1);

        // QC: makeXonoticCheckBoxEx(2, 1, "crypto_aeslevel", …) — checked stores 2 (encryption REQUESTED),
        // unchecked 1 (supported-but-not-requested; 0 = refuse / 3 = require have no menu rows).
        // (QC gates this row on the engine providing crypto_aeslevel; we include it faithfully.)
        box.AddChild(Widgets.ValueCheckBox("crypto_aeslevel", 2f, 1f, "Use encryption (AES) when available"));

        box.AddChild(Ui.Spacer());

        // --- HTTP downloads --------------------------------------------------------------------------
        box.AddChild(Ui.Header("HTTP downloads"));

        // QC: makeXonoticSlider(1, 5, 1, "cl_curl_maxdownloads")
        box.AddChild(Ui.Row("Simultaneous:",
            Widgets.Slider("cl_curl_maxdownloads", 1, 5, 1,
                "Maximum number of concurrent HTTP downloads")));

        // QC: makeXonoticMixedSlider("cl_curl_maxspeed") — KiB/s, MiB/s, then Unlimited (0).
        var bandwidth = Widgets.TextSlider("cl_curl_maxspeed")
            .Add("64 KiB/s", 64).Add("128 KiB/s", 128).Add("256 KiB/s", 256).Add("512 KiB/s", 512)
            .Add("1 MiB/s", 1024).Add("2 MiB/s", 2048).Add("4 MiB/s", 4096).Add("8 MiB/s", 8192)
            .Add("Unlimited", 0);
        box.AddChild(Ui.Row("Bandwidth limit:", bandwidth));

        box.AddChild(Ui.Spacer());

        // --- Other -----------------------------------------------------------------------------------
        box.AddChild(Ui.Header("Other"));

        // QC: makeXonoticMixedSlider("menu_tooltips") — Disabled / Standard / Advanced.
        var tooltips = Widgets.TextSlider("menu_tooltips",
                "Menu tooltips: disabled, standard or advanced (also shows cvar or console command bound to the menu item)")
            .Add("Disabled", 0).Add("Standard", 1).Add("Advanced", 2);
        box.AddChild(Ui.Row("Menu tooltips:", tooltips));

        // QC: makeXonoticMixedSlider("menu_animations"), formatString "S" (seconds): Disabled (0),
        // then addRange(0.05, 0.5, 0.05). Enumerated as the equivalent discrete entries.
        var animations = Widgets.TextSlider("menu_animations", "How fast animations in the menu occur")
            .Add("Disabled", 0)
            .Add("0.05 s", 0.05f).Add("0.1 s", 0.1f).Add("0.15 s", 0.15f).Add("0.2 s", 0.2f)
            .Add("0.25 s", 0.25f).Add("0.3 s", 0.3f).Add("0.35 s", 0.35f).Add("0.4 s", 0.4f)
            .Add("0.45 s", 0.45f).Add("0.5 s", 0.5f);
        box.AddChild(Ui.Row("Menu animations:", animations));

        // QC: r_textshadow checkbox (value takes effect on apply → menu_restart button below).
        box.AddChild(Widgets.CheckBox("r_textshadow", "Text shadow",
            "Draw a shadow behind all text to improve readability"));

        // QC: showtime checkbox, makeMulti(e, "showdate") — drives both showtime and showdate together.
        var showTime = Widgets.CheckBox("showtime", "Show current date and time",
            "Show current date and time of day, useful on screenshots");
        showTime.Toggled += pressed =>
        {
            MenuState.Cvars.Set("showdate", pressed ? "1" : "0");
            MenuState.Cvars.MarkArchived("showdate");
        };
        box.AddChild(showTime);

        box.AddChild(Ui.Spacer());

        // QC: makeXonoticButton(_("Advanced settings..."), …) → opens main.cvarsDialog (the cvar editor).
        // That filterable cvar-list editor (dialog_settings_misc_cvars.qc) is a separate dialog XonoticGodot
        // does not yet host — inert/noted button (MenuCommand logs the unknown command).
        box.AddChild(Widgets.CommandButton("Advanced settings...", "menu_showcvarsdialog",
            "Advanced settings where you can tweak every single variable of the game"));

        // QC: makeXonoticButton(_("Factory reset")) → opens main.resetDialog. Per task spec, wire the
        // engine reset command directly (MenuCommand handles cvar_resettodefaults_all).
        box.AddChild(Widgets.CommandButton("Reset all settings to defaults", "cvar_resettodefaults_all",
            "Reset all settings, creating a backup config in your data directory"));

        box.AddChild(Ui.Spacer());
        box.AddChild(applyButton); // "Apply immediately" — menu_restart
    }
}
