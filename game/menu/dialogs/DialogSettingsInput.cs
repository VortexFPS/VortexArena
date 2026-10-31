using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Input settings tab — <c>XonoticInputSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_input.qc), TD for
/// TD on its 15.5 x 6.2 grid: the key binder (<see cref="QcKeyBinder"/>) and its buttons on the left, Mouse and
/// Other on the right.
///
/// <para>Where the QC picks a cvar by which one the engine has, the joystick row asks DarkPlaces' own cvar table
/// (<c>joy_enable</c>, else <c>joystick</c>); the system-acceleration row is the QC's greyed fallback, as on Windows,
/// because <c>vid_dgamouse</c> and <c>apple_mouse_noaccel</c> belong to other platforms' video drivers.
/// "Edit..." needs the user-bind editor (dialog_settings_input_userbind.qc), which is not ported: it is enabled on a
/// user-bind row as in Xonotic, and does nothing yet.</para>
/// </summary>
public partial class DialogSettingsInput : QcTab
{
    private const string MAccelerateTooltip =
        "In-game linear acceleration factor. \"Fully disabled\" also disables other acceleration types that can be enabled via the m_accelerate_* cvars";

    public DialogSettingsInput() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    private static bool IsEngineCvar(string name) => VortexArena.Legacy.Menu.MenuEngineCvarInfo.IsEngineCvar(name);

    protected override void Fill()
    {
        var kb = new QcKeyBinder();

        TR();
            TD(1, 3, Qc.Header("Key Bindings"));
        TR();
            TD(Rows - 3, 3, kb);
        GotoRC(Rows - 2, 0);
            var change = QcLook.Button(new Button { Text = Localization.Tr("Change key...") }, forcePressed: () => kb.Grabbing);
            change.Pressed += kb.Change;
            kb.KeyGrabButton = TD(1, 1, change);
            kb.UserbindEditButton = TD(1, 1, Qc.Button("Edit...",
                () => GD.Print("[Menu] Userbind editor (dialog_settings_input_userbind.qc) is not ported yet.")));
            kb.ClearButton = TD(1, 1, Qc.Button("Clear", kb.Clear));
        TR();
            TD(1, 3, Qc.Button("Reset all", () => Menu?.Push(new DialogBindingsReset())));

        GotoRC(0, 3.2f); SetFirstColumn(CurrentColumn);
            TD(1, 3, Qc.Header("Mouse"));
        TR();
            TD(1, 1, Qc.Label("Sensitivity:"));
            TD(1, 1, Qc.Slider(0.1f, 9.9f, 0.1f, "sensitivity", "Mouse speed multiplier", valueSpace: 0));
            // linkSensitivities: both edit "sensitivity" and each re-reads it when the other changes it.
            var sensBox = QcLook.InputBox(Widgets.InputBox("sensitivity", tooltip: "Mouse speed multiplier"));
            sensBox.TextChanged += t =>
            {
                // allowedCharacters "0.123456789"
                string kept = string.Concat(System.Linq.Enumerable.Where(t, ch => ch == '.' || char.IsAsciiDigit(ch)));
                if (kept != t)
                {
                    int caret = sensBox.CaretColumn - (t.Length - kept.Length);
                    sensBox.Text = kept;
                    sensBox.CaretColumn = System.Math.Max(0, caret);
                }
            };
            TD(1, 1, sensBox);
        TR();
            TD(1, 3, Qc.Check(0, "m_filter", "Smooth aiming", "Smooths the mouse movement, but makes aiming slightly less responsive"));
        TR();
            TD(1, 3, Qc.Check(1.022f, "m_pitch", "Invert aiming", "Invert mouse movement on the Y-axis"));
        TR();
            TD(1, 1, Qc.Label("Acceleration factor:"));
            TD(1, 2, Qc.Mixed("m_accelerate", MAccelerateTooltip)
                .Add("Fully disabled", 0).Add("Linear disabled", 1).AddRange(1.2f, 4, 0.2f).Done(1.5f));
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Speed bounds:")).Dep("m_accelerate", 1, 0);
            TD(1, 1, Qc.Slider(0, 10000, 500, "m_accelerate_minspeed", MAccelerateTooltip)).Dep("m_accelerate", 1, 0);
            TD(1, 1, Qc.Slider(5000, 20000, 1000, "m_accelerate_maxspeed", MAccelerateTooltip)).Dep("m_accelerate", 1, 0);
        TR();
            TDempty(0.2f);
            // vid_dgamouse (GLX) and apple_mouse_noaccel (macOS) are platform cvars this client has neither of, so
            // this is the QC's last branch, as on Windows: "just there for show".
            TD(1, 2.8f, Qc.Check(0, null, "Disable system mouse acceleration"));
        TR();
            TD(1, 3, Qc.Check(0, "menu_mouse_absolute", "Use system mouse positioning"))
                .Multi("menu_mouse_absolute", "hud_cursormode");

        TR();
        TR();
            TD(1, 3, Qc.Header("Other"));
        TR();
            TD(1, 3, Qc.Check(0, "con_closeontoggleconsole", "Pressing \"enter console\" key also closes it",
                "Allow the console toggling bind to also close the console"));
        TR();
            TD(1, 3, Qc.Check(1, "cl_movement_track_canjump", "Automatically repeat jumping if holding jump"));
        TR();
            TD(1, 1, Qc.Label("Jetpack on jump:"));
            TD(1, 2, Qc.Mixed("cl_jetpack_jump").Add("Disabled", 0).Add("Air only", 1).Add("All", 2).Done(1.25f));
        TR();
            if (IsEngineCvar("joy_enable"))
                TD(1, 3, Qc.Check(0, "joy_enable", "Use joystick input")).DepNot("joy_detected", 0);
            else if (IsEngineCvar("joystick"))
                TD(1, 3, Qc.Check(0, "joystick", "Use joystick input")).DepNot("joy_detected", 0);
            else
                TD(1, 3, Qc.Check(0, null, "Use joystick input"));
    }
}
