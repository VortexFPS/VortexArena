using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The notice shown when the player tries to join a server that turns out to be running stock Xonotic.
///
/// <para>The server browser queries the same dpmaster instances Xonotic itself uses, so almost every row in
/// the internet list is a Darkplaces server running the original QuakeC game. VortexArena's netcode is a
/// ground-up reimplementation — different handshake, different snapshot format, different entity protocol —
/// so connecting to one could not do anything but fail, and it would fail as a timeout rather than as
/// anything the player could act on. Those servers are still listed (they exist, and dropping them would
/// read as a broken browser), so this is where the truth gets told instead.</para>
///
/// <para>Presented like <see cref="QuitDialog"/>: full-rect with a dimmed backdrop over the browser, so it
/// reads as a modal rather than another screen on the stack.</para>
/// </summary>
public partial class DialogIncompatibleServer : MenuScreen, ISelfFramedDialog
{
    private readonly string _serverName;
    private readonly string _address;

    /// <param name="serverName">The server's hostname, for the player to recognise which row this was.</param>
    /// <param name="address">Its "ip:port", shown when the hostname is missing or unhelpful.</param>
    public DialogIncompatibleServer(string serverName, string address)
    {
        _serverName = MenuColorCodes.Strip(serverName ?? "").Trim();
        _address = address ?? "";
    }

    protected override void BuildUi()
    {
        Name = "DialogIncompatibleServer";

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

        column.AddChild(MakeTitle("Xonotic server"));

        // Which row this was: the hostname if the server gave one, the address otherwise.
        string who = _serverName.Length > 0 ? _serverName : _address;
        if (who.Length > 0)
        {
            var target = MakeLabel(who);
            target.HorizontalAlignment = HorizontalAlignment.Center;
            target.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(target);
        }

        var body = MakeLabel(
            "This is a Xonotic server, and Vortex Arena is not compatible with Xonotic yet.\n\n"
            + "The server list comes from the shared Xonotic master servers, so most of what it shows are "
            + "Xonotic servers rather than Vortex Arena ones. Backwards-compatible support is coming.");
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(body);

        column.AddChild(Ui.Spacer(4));
        column.AddChild(MakeButtonBar(MakeButton("OK", GoBack)));
    }
}
