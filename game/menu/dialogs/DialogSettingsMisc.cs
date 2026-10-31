using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Misc settings tab — <c>XonoticMiscSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_misc.qc), TD for TD
/// on its 15.5 x 6.2 grid: Network and HTTP downloads on the left, Other on the right, "Apply immediately"
/// (<c>menu_restart</c>, live once the text shadow setting changes) along the bottom. "Advanced settings..." opens
/// the cvar list (<see cref="DialogCvarList"/>) and "Factory reset" its confirmation (<see cref="DialogSettingsReset"/>),
/// as the QC opens main.cvarsDialog and main.resetDialog.
/// </summary>
public partial class DialogSettingsMisc : QcTab
{
    public DialogSettingsMisc() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var apply = Qc.Command("Apply immediately", "menu_restart");
        QcApplyOnChange.Bind(apply, "r_textshadow");

        TR();
            TD(1, 3, Qc.Header("Network"));
        TR();
            TD(1, 3, Qc.Check(0, "shownetgraph", "Show netgraph", "Show a graph of packet sizes and other information"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_netrepeatinput", "Packet loss compensation", "Each packet includes a copy of the previous message"));
        TR();
            TD(1, 3, Qc.Check(0, "cl_movement_errorcompensation", "Movement prediction error compensation")).Dep("cl_movement", 1, 1);
        TR();
            if (VortexArena.Legacy.Menu.MenuEngineCvarInfo.IsEngineCvar("crypto_aeslevel"))
                TD(1, 3, Qc.CheckEx(2, 1, "crypto_aeslevel", "Use encryption (AES) when available"));
        TR();
        TR();
            TD(1, 3, Qc.Header("HTTP downloads"));
        TR();
            TD(1, 1, Qc.Label("Simultaneous:"));
            TD(1, 2, Qc.Slider(1, 5, 1, "cl_curl_maxdownloads", "Maximum number of concurrent HTTP downloads"));
        TR();
            TD(1, 1, Qc.Label("Bandwidth limit:"));
            var speed = Qc.Mixed("cl_curl_maxspeed");
            foreach (int kib in new[] { 64, 128, 256, 512 })
                speed.Add(string.Format(Localization.Tr("{0} KiB/s"), kib), kib);
            foreach (int mib in new[] { 1, 2, 4, 8 })
                speed.Add(string.Format(Localization.Tr("{0} MiB/s"), mib), mib * 1024);
            TD(1, 2, speed.Add("Unlimited", 0).Done());

        GotoRC(0, 3.2f); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Other"));
        TR();
            TD(1, 1, Qc.Label("Menu tooltips:"));
            TD(1, 2, Qc.Mixed("menu_tooltips",
                    "Menu tooltips: disabled, standard or advanced (also shows cvar or console command bound to the menu item)")
                .Add("Disabled", 0).Add("Standard", 1).Add("Advanced", 2).Done(1.2f));
        TR();
            TD(1, 1, Qc.Label("Menu animations:"));
            TD(1, 2, Qc.Mixed("menu_animations", "How fast animations in the menu occur", "S")
                .Add("Disabled", 0).AddRange(0.05f, 0.5f, 0.05f).Done(1.2f));
        TR();
            TD(1, 3, Qc.Check(0, "r_textshadow", "Text shadow", "Draw a shadow behind all text to improve readability"));
        TR();
            TD(1, 3, Qc.Check(0, "showtime", "Show current date and time", "Show current date and time of day, useful on screenshots"))
                .Multi("showtime", "showdate");
        TR();
        TR();
            TDempty(0.5f);
            TD(1, 2, Qc.Button("Advanced settings...", () => Menu?.Push(new DialogCvarList()),
                "Advanced settings where you can tweak every single variable of the game"));
        TR();
        TR();
            TDempty(0.5f);
            TD(1, 2, Qc.Button("Factory reset", () => Menu?.Push(new DialogSettingsReset()), tint: new Color(1, 0, 0)));

        GotoRC(Rows - 1, 0);
            TD(1, Columns, apply);
    }
}
