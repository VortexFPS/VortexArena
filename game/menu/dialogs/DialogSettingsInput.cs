using System.Globalization;
using Godot;
using XonoticGodot.Game.Console;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Input settings tab — a faithful C# port of <c>XonoticInputSettingsTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings_input.qc, with the userbind edit dialog from
/// dialog_settings_input_userbind.qc). Follows <see cref="DialogSettingsAudio"/> as the reference pattern:
/// every control binds the same engine cvar the QC binds, with the same labels, order, and dependencies.
///
/// The QC lays this tab out in two side-by-side columns (left = the key-binding list and its buttons; right
/// = Mouse, then Other). We flatten that into one vertical list in reading order: Key Bindings, Mouse, Other.
///
/// Two pieces have no direct toolkit factory and are approximated (noted inline):
///   * the key-binding list (QC <c>makeXonoticKeyBinder</c>, a listbox) is rebuilt from the existing
///     <see cref="KeyCaptureButton"/> + <see cref="KeyBindings"/> + <see cref="MenuSettings"/> machinery —
///     a scrollable column of "action: [bind]" rows where clicking a row captures the next key. It writes
///     the bind table (persisted by <see cref="MenuSettings"/>); the gameplay controller does not consume
///     rebinds yet, so the capture is functional but currently inert downstream.
///   * "Invert aiming" (QC checkbox on <c>m_pitch</c>) toggles the *sign* of m_pitch rather than a fixed
///     on/off value, so it is a small dedicated checkbox instead of <see cref="Widgets.CheckBox"/>.
/// </summary>
public partial class DialogSettingsInput : SettingsTab
{
    // QC m_accelerate tooltip (shared by the mixed slider and the two speed-bound sliders).
    private const string MAccelerateTooltip =
        "In-game linear acceleration factor. \"Fully disabled\" also disables other acceleration types " +
        "that can be enabled via the m_accelerate_* cvars";

    protected override void Fill(VBoxContainer box)
    {
        // -------------------------------------------------------------------------------------------------
        //  Key Bindings  (QC left column: makeXonoticKeyBinder + Change/Edit/Clear + Reset all)
        // -------------------------------------------------------------------------------------------------
        box.AddChild(Ui.Header("Key Bindings"));
        BuildKeyBinder(box);

        box.AddChild(Ui.Spacer());

        // -------------------------------------------------------------------------------------------------
        //  Mouse  (QC right column, header _("Mouse"))
        // -------------------------------------------------------------------------------------------------
        box.AddChild(Ui.Header("Mouse"));

        // Sensitivity: a slider AND an input box, both bound to "sensitivity" (QC linkSensitivities keeps the
        // two in sync). Here both widgets bind the same cvar and re-read on the store's Changed event, so
        // they stay in lockstep without bespoke linking code.
        var sens = Widgets.Slider("sensitivity", 0.1f, 9.9f, 0.1f, "Mouse speed multiplier", format: v => v.ToString("0.0", CultureInfo.InvariantCulture));
        var sensBox = Widgets.InputBox("sensitivity", tooltip: "Mouse speed multiplier");
        sensBox.CustomMinimumSize = new Vector2(70, 0);
        var sensRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        sensRow.AddThemeConstantOverride("separation", 8);
        sens.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        sensBox.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        sensRow.AddChild(sens);
        sensRow.AddChild(sensBox);
        box.AddChild(Ui.Row("Sensitivity:", sensRow));

        // QC makeXonoticCheckBox_T(0, "m_filter", _("Smooth aiming"), …)
        box.AddChild(Widgets.CheckBox("m_filter", "Smooth aiming",
            "Smooths the mouse movement, but makes aiming slightly less responsive"));

        // QC makeXonoticCheckBox_T(1.022, "m_pitch", _("Invert aiming"), …). The checkbox flips m_pitch's
        // SIGN (positive = normal, negative = inverted), which has no on/off toolkit equivalent — dedicated
        // control below. (Approximate.)
        box.AddChild(new InvertMouseCheckBox("m_pitch", "Invert aiming")
        {
            TooltipText = "Invert mouse movement on the Y-axis",
        });

        // Acceleration factor — QC makeXonoticMixedSlider("m_accelerate"): two named entries then a numeric
        // range addRange(1.2, 4, 0.2). (TextSlider approximates the mixed slider.)
        var accel = Widgets.TextSlider("m_accelerate", MAccelerateTooltip)
            .Add("Fully disabled", 0)
            .Add("Linear disabled", 1);
        for (float v = 1.2f; v <= 4.0001f; v += 0.2f)
            accel.Add(v.ToString("0.0", CultureInfo.InvariantCulture), Mathf.Round(v * 10f) / 10f);
        box.AddChild(Ui.Row("Acceleration factor:", accel));

        // Speed bounds — two sliders, each QC setDependent(e,"m_accelerate",1,0). With min>max, QC's
        // setDependent enables while the value lies in [max,min] = [0,1]; the foundation's Bind does not swap
        // reversed bounds, so we pass the normalized range (0,1).
        var minSpeed = Widgets.Slider("m_accelerate_minspeed", 0f, 10000f, 500f, MAccelerateTooltip);
        var minRow = Ui.Row("Speed bounds:", minSpeed);
        box.AddChild(minRow);
        Dependent.Bind(minRow, "m_accelerate", 0, 1);

        var maxSpeed = Widgets.Slider("m_accelerate_maxspeed", 5000f, 20000f, 1000f, MAccelerateTooltip);
        var maxRow = Ui.Row("", maxSpeed);
        box.AddChild(maxRow);
        Dependent.Bind(maxRow, "m_accelerate", 0, 1);

        // "Disable system mouse acceleration" — QC chooses vid_dgamouse / apple_mouse_noaccel depending on
        // which the engine exposes. XonoticGodot's cvar store has no engine-type introspection, so we bind the
        // common one (vid_dgamouse). (Approximate.)
        box.AddChild(Widgets.CheckBox("vid_dgamouse", "Disable system mouse acceleration",
            "Make use of DGA mouse input"));

        // QC makeXonoticCheckBox_T(0, "menu_mouse_absolute", _("Use system mouse positioning"), …) — also
        // makeMulti'd onto hud_cursormode and re-displays the menu on click. We bind menu_mouse_absolute;
        // the cursor-mode mirror / menu redisplay are engine-side and inert here. (Approximate.)
        box.AddChild(Widgets.CheckBox("menu_mouse_absolute", "Use system mouse positioning"));

        box.AddChild(Ui.Spacer());

        // -------------------------------------------------------------------------------------------------
        //  Other  (QC header _("Other"))
        // -------------------------------------------------------------------------------------------------
        box.AddChild(Ui.Header("Other"));

        box.AddChild(Widgets.CheckBox("con_closeontoggleconsole", "Pressing \"enter console\" key also closes it",
            "Allow the console toggling bind to also close the console"));

        box.AddChild(Widgets.CheckBox("cl_movement_track_canjump", "Automatically repeat jumping if holding jump"));

        // Jetpack on jump — QC makeXonoticMixedSlider("cl_jetpack_jump"): Disabled/Air only/All.
        var jetpack = Widgets.TextSlider("cl_jetpack_jump")
            .Add("Disabled", 0)
            .Add("Air only", 1)
            .Add("All", 2);
        box.AddChild(Ui.Row("Jetpack on jump:", jetpack));

        // Joystick — QC chooses joy_enable / joystick by engine availability, gated on joy_detected != 0.
        // We bind joy_enable and reproduce setDependentNOT(e,"joy_detected",0). (Approximate cvar pick.)
        var joystick = Widgets.CheckBox("joy_enable", "Use joystick input");
        box.AddChild(joystick);
        Dependent.BindNot(joystick, "joy_detected", 0); // QC setDependentNOT(e, "joy_detected", 0)
    }

    // -----------------------------------------------------------------------------------------------------
    //  Key binder  (QC makeXonoticKeyBinder + its Change/Edit/Clear/Reset buttons)
    // -----------------------------------------------------------------------------------------------------

    private readonly System.Collections.Generic.List<KeyCaptureButton> _binders = new();

    /// <summary>
    /// Build the scrollable list of bindable actions, each a "label: [bind]" row whose button captures the
    /// next key (reusing <see cref="KeyCaptureButton"/>), plus the QC affordances underneath. Binds are read
    /// from / written to the CANONICAL runtime table (<see cref="XonoticGodot.Engine.Console.BindTable"/>) via
    /// <see cref="BindInput"/> — the same table binds-xonotic.cfg seeds and both gameplay input paths read —
    /// not the legacy <see cref="MenuSettings"/> store (quarantined). Captures persist through the cvar/config
    /// dump (<see cref="MenuState.SaveUserConfig"/>), mirroring DP's keybinder writing into config.cfg.
    /// </summary>
    private void BuildKeyBinder(VBoxContainer box)
    {
        // The list itself — a fixed-height scroll so the long action list doesn't push the rest off-screen,
        // standing in for the QC keybinder listbox.
        var listScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 260),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        listScroll.AddChild(list);
        box.AddChild(listScroll);

        foreach (var (id, label) in KeyBindings.Actions)
        {
            string current = BindInput.KeyForAction(id); // the key currently bound to this action's command
            var button = new KeyCaptureButton(id, current);
            button.BindCaptured += OnBindCaptured;
            _binders.Add(button);
            list.AddChild(Ui.Row(label + ":", button));
        }

        // QC row: "Change key..." / "Edit..." / "Clear". Clicking a row already enters capture mode; "Change
        // key" focuses a row so the keyboard can start a capture, and "Clear" unbinds the focused row.
        box.AddChild(Ui.ButtonBar(
            Ui.Button("Change key...", FocusFirstUnfocusedBind),
            Ui.Button("Edit...", OpenUserbindEdit),
            Ui.Button("Clear", ClearFocusedBind)));

        // QC: makeXonoticButton(_("Reset all"), …) -> opens the bindings-reset dialog; here it restores the
        // default binds directly (the same effect the reset dialog's "Reset all" performs).
        box.AddChild(Ui.ButtonBar(Ui.Button("Reset all", ResetAllBinds)));
    }

    /// <summary>A capture finished: bind the captured key to the action's command in the canonical
    /// <see cref="XonoticGodot.Engine.Console.BindTable"/> (keybinder.qc <c>bind KEY "func"</c>) and persist the
    /// table to the user config. Refreshes every row face since a re-bind may have stolen a key from another row.</summary>
    private void OnBindCaptured(string actionId, string bind)
    {
        BindInput.BindAction(actionId, bind);
        MenuState.SaveUserConfig(); // dump the bind table (+ archived cvars) so the rebind survives a restart
        RefreshBindFaces();
    }

    /// <summary>Push the canonical bind for each action onto its row face (after a capture/clear/reset).</summary>
    private void RefreshBindFaces()
    {
        foreach (var button in _binders)
            button.Bind = BindInput.KeyForAction(button.ActionId);
    }

    /// <summary>QC "Change key...": focus the currently-focused row, or the first row, so a capture can begin.</summary>
    private void FocusFirstUnfocusedBind()
    {
        foreach (var button in _binders)
            if (button.HasFocus())
                return; // a row is already armed/focused; nothing to do
        if (_binders.Count > 0)
            _binders[0].GrabFocus();
    }

    /// <summary>QC "Clear" (KeyBinder_Bind_Clear): unbind the focused row's action in the canonical table and
    /// persist (no-op if no row has focus).</summary>
    private void ClearFocusedBind()
    {
        foreach (var button in _binders)
        {
            if (button.HasFocus())
            {
                BindInput.UnbindAction(button.ActionId);
                MenuState.SaveUserConfig();
                RefreshBindFaces();
                return;
            }
        }
    }

    /// <summary>
    /// QC "Reset all" (keybinder.qc <c>KeyBinder_Bind_Reset_All</c>): <c>unbindall; exec binds-xonotic.cfg</c> —
    /// reload the bind table from the canonical cfg (NOT the thin KeyBindings.Defaults), then refresh the row
    /// faces + persist. Delegates to <see cref="MenuState.ReloadDefaultBinds"/>, which uses BindInput's translating
    /// bind sink so engine key names resolve correctly (the live console's bind handler does not translate).
    /// </summary>
    private void ResetAllBinds()
    {
        MenuState.ReloadDefaultBinds();
        MenuState.SaveUserConfig();
        RefreshBindFaces();
    }

    /// <summary>
    /// QC "Edit...": opens the userbind edit dialog (dialog_settings_input_userbind.qc) to author a custom
    /// command-bind. XonoticGodot has no userbind backend (custom press/release console commands), so this is
    /// inert and only logs — the standard action binds above are the functional path. (Inert.)
    /// </summary>
    private static void OpenUserbindEdit()
    {
        GD.Print("[Menu] Userbind editor (dialog_settings_input_userbind.qc) has no backend yet (inert).");
    }
}

/// <summary>
/// "Invert aiming" checkbox bound to <c>m_pitch</c> by SIGN: checked = inverted (negative m_pitch),
/// unchecked = normal (positive). Approximates QC's <c>makeXonoticCheckBox_T(1.022, "m_pitch", …)</c>, which
/// has no fixed-value on/off equivalent in the toolkit. Preserves the cvar's magnitude when flipping.
/// </summary>
public partial class InvertMouseCheckBox : CheckBox
{
    private const float DefaultMagnitude = 0.022f; // DP default |m_pitch|
    private readonly string _cvar;
    private bool _updating;

    public InvertMouseCheckBox(string cvar, string label)
    {
        _cvar = cvar;
        Text = label;
        Toggled += OnToggled;
    }

    public override void _EnterTree() { MenuState.Cvars.Changed += OnCvarChanged; Refresh(); }
    public override void _ExitTree() { MenuState.Cvars.Changed -= OnCvarChanged; }

    private void OnCvarChanged(string name) { if (name == _cvar) Refresh(); }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        ButtonPressed = MenuState.Cvars.GetFloat(_cvar) < 0f; // negative pitch == inverted
        _updating = false;
    }

    private void OnToggled(bool pressed)
    {
        if (_updating) return;
        float cur = MenuState.Cvars.GetFloat(_cvar);
        float mag = Mathf.Abs(cur);
        // 0 → don't lose the value; ≥0.5 → legacy sign-only ±1 from the old registration (DP magnitudes
        // are ~0.022) — normalize to the DP default instead of re-archiving the bogus scale.
        if (mag < 0.0001f || mag >= 0.5f) mag = DefaultMagnitude;
        float next = pressed ? -mag : mag;
        MenuState.Cvars.Set(_cvar, next.ToString(CultureInfo.InvariantCulture));
        MenuState.Cvars.MarkArchived(_cvar);
    }
}
