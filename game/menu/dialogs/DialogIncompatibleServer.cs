using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The notice shown when the player tries to join a server that turns out to be running stock Xonotic.
///
/// <para>The server browser queries the same dpmaster instances Xonotic itself uses, so almost every row in
/// the internet list is a Darkplaces server running the original QuakeC game, listed under its own
/// "Xonotic Servers (legacy mode)" heading. The native netcode shares no wire format with them; legacy
/// compatibility mode joins them, but only with Xonotic's own data configured (<c>legacy_xonotic_data</c>).
/// Without it, connecting could not do anything but fail — as a timeout rather than as anything the player
/// could act on — so this is where the player is told what to set up instead.</para>
///
/// <para>Presented as the old QuitDialog was: full-rect with a dimmed backdrop over the browser, so it
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
            "This is a Xonotic server. Vortex Arena can join it in legacy compatibility mode, which is "
            + "experimental and needs Xonotic's own game data.\n\n"
            + "To turn it on, open the console and set legacy_xonotic_data to the \"data\" folder of a "
            + "Xonotic install, for example:\n"
            + "legacy_xonotic_data \"C:/Games/Xonotic/data\"\n\n"
            + "The server list comes from the shared Xonotic master servers, so most of what it shows are "
            + "Xonotic servers rather than Vortex Arena ones.");
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(body);

        column.AddChild(Ui.Spacer(4));
        column.AddChild(MakeButtonBar(MakeButton("OK", GoBack)));
    }
}
