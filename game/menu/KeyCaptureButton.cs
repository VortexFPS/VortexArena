using System;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A keybind button: shows the currently bound key and, when clicked, enters "press a key" capture mode —
/// the next key or mouse-button press becomes the new bind. C# successor to QC's
/// <c>KeyBinder_Bind_Change</c> / the "press a key…" prompt in keybinder.qc.
///
/// While capturing, the button grabs keyboard focus and consumes input so the press doesn't leak to the
/// rest of the menu. Escape cancels (keeps the old bind); any other key/mouse press is encoded via
/// <see cref="KeyBindings.Encode"/>, stored, and reported through <see cref="BindCaptured"/>.
/// </summary>
public partial class KeyCaptureButton : Button
{
    private string _bind;
    private bool _capturing;

    /// <summary>True while ANY keybind button is in "press a key" capture mode. A global hotkey owner (the
    /// screenshot service's F12 handler) checks this so that rebinding an action TO that key is captured by the
    /// menu instead of being eaten by the live hotkey.</summary>
    public static bool Capturing { get; private set; }

    /// <summary>The action id this button binds (config key, e.g. "forward").</summary>
    public string ActionId { get; }

    /// <summary>The current bind string (e.g. "W", "MOUSE1"). Set updates the label.</summary>
    public string Bind
    {
        get => _bind;
        set
        {
            _bind = value ?? "";
            UpdateLabel();
        }
    }

    /// <summary>Raised after a successful capture with (actionId, newBind).</summary>
    public event Action<string, string>? BindCaptured;

    public KeyCaptureButton(string actionId, string initialBind)
    {
        ActionId = actionId;
        _bind = initialBind ?? "";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ToggleMode = false;
        UpdateLabel();
        Pressed += BeginCapture;
    }

    private void BeginCapture()
    {
        if (_capturing)
            return;
        _capturing = true;
        Capturing = true;
        Text = "< press a key >";
        // Take focus so _GuiInput receives the next press, and so the user sees which row is arming.
        GrabFocus();
    }

    private void EndCapture()
    {
        _capturing = false;
        Capturing = false;
        UpdateLabel();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (!_capturing)
            return;

        // Escape cancels the capture and restores the previous bind.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            AcceptEvent();
            EndCapture();
            return;
        }

        string? captured = KeyBindings.Encode(@event);
        if (captured is null)
            return; // ignore releases / echoes / non-bindable events; stay armed

        AcceptEvent(); // swallow it so the press doesn't also click something else
        _bind = captured;
        EndCapture();
        BindCaptured?.Invoke(ActionId, _bind);
    }

    private void UpdateLabel() => Text = string.IsNullOrEmpty(_bind) ? "(unbound)" : _bind;
}
