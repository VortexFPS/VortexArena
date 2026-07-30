using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Terms of Service dialog — a faithful C# port of <c>XonoticToSDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_termsofservice.qc). On first launch (or when the ToS version bumps) the engine
/// fetches the current Terms of Service text from a server URL and shows it in a scrollable text box with
/// Accept / "Don't accept (quit the game)" buttons. Accepting records the accepted ToS version in
/// <c>_termsofservice_accepted</c> and saves the config; declining quits the game.
///
/// INERT (faithful UI, no backend): QC loads the body via <c>url_single_fopen(termsofservice_url, …)</c> and
/// fills <c>me.textBox</c> from the HTTP response (<c>XonoticToS_OnGet</c>). VortexArena has no ToS-fetch backend,
/// so the scrollable body shows a short honest placeholder instead of live ToS text. The surrounding controls
/// are faithful:
///   * Accept writes <c>_termsofservice_accepted</c> (QC stores the version int <c>_Nex_ExtResponseSystem_NewToS</c>;
///     with no live version we record "1") + <c>saveconfig</c>, then returns;
///   * Don't accept issues the SAME console command QC does: <c>quit</c>.
/// </summary>
public partial class DialogTermsOfService : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogTermsOfService";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Terms of Service"));

        // QC subtitle: "...updated..." if already accepted a prior version, else the welcome variant. With no
        // live accepted-version backend we show the first-launch variant (faithful default copy).
        var subtitle = MakeLabel("Welcome to Xonotic! Please read the following Terms of Service:");
        subtitle.AutowrapMode = TextServer.AutowrapMode.Word;
        root.AddChild(subtitle);

        // QC me.textBox = makeXonoticTextBox() (scrollable, allowColors). INERT: body is server-fetched; we
        // render a scrollable placeholder. Never put a TabContainer in here — this is a plain scrollable label.
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var body = MakeLabel(
            "(Terms of Service text is fetched from the Xonotic server at runtime — fetch backend pending.)\n\n" +
            "When connected, the current Terms of Service would appear here. Use Accept to record agreement " +
            "and continue, or Don't accept to quit the game.");
        body.AutowrapMode = TextServer.AutowrapMode.Word;
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(body);
        root.AddChild(scroll);

        // QC bottom row: Accept ('0 1 0') -> Close_Clicked; "Don't accept (quit the game)" ('1 0 0') ->
        // DontAccept_Clicked (localcmd "quit").
        root.AddChild(MakeButtonBar(
            MakeButton("Accept", OnAccept),
            Widgets.CommandButton("Don't accept (quit the game)", "quit")));
    }

    // QC Close_Clicked: cvar_set("_termsofservice_accepted", ftos(_Nex_ExtResponseSystem_NewToS)); saveconfig.
    private void OnAccept()
    {
        // No live ToS version (QC uses _Nex_ExtResponseSystem_NewToS); record a non-zero accepted version.
        MenuState.Cvars.Set("_termsofservice_accepted", "1");
        MenuState.Cvars.MarkArchived("_termsofservice_accepted");
        MenuState.SaveUserConfig(); // QC localcmd("saveconfig\n")
        GoBack();
    }
}
