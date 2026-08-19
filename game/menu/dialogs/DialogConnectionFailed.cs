using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The notice shown when a match's connection fails before the player ever spawns — the server rejected the
/// handshake, the link dropped mid-join, or the connect timed out with no response.
///
/// <para>Before this existed, any of those left the DP-style loading screen stuck at "Connecting…" forever
/// (the screen only dismisses on spawn), with no way back but killing the process. <see cref="Shell"/> now
/// tears the dead match down, returns to the main menu, and puts this in front of the player with the reason
/// <see cref="VortexArena.Game.Net.NetGame"/> reported.</para>
///
/// <para>Presented like <see cref="DialogIncompatibleServer"/>: full-rect with a dimmed backdrop, so it reads
/// as a modal over the menu rather than another screen on the stack.</para>
/// </summary>
public partial class DialogConnectionFailed : MenuScreen, ISelfFramedDialog
{
    private readonly string _target;
    private readonly string _reason;

    /// <param name="target">What we were connecting to — a server address, or the map name for a listen server —
    /// so the player recognises which attempt this was.</param>
    /// <param name="reason">The short, player-facing failure reason from the netcode.</param>
    public DialogConnectionFailed(string target, string reason)
    {
        _target = MenuColorCodes.Strip(target ?? "").Trim();
        _reason = MenuColorCodes.Strip(reason ?? "").Trim();
    }

    protected override void BuildUi()
    {
        Name = "DialogConnectionFailed";

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 24);
        panel.AddChild(margin);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        column.AddThemeConstantOverride("separation", 16);
        margin.AddChild(column);

        column.AddChild(MakeTitle("Connection failed"));

        // Which attempt this was: the address/map, when we have one.
        if (_target.Length > 0)
        {
            var who = MakeLabel(_target);
            who.HorizontalAlignment = HorizontalAlignment.Center;
            who.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(who);
        }

        var body = MakeLabel(_reason.Length > 0 ? _reason : "The connection could not be completed.");
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(body);

        column.AddChild(Ui.Spacer(4));
        column.AddChild(MakeButtonBar(MakeButton("OK", GoBack)));
    }
}
