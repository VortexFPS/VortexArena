using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// HUD "Chat" panel config dialog — a faithful C# port of <c>XonoticHUDChatDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_hudpanel_chat.qc). Like every HUD-panel dialog it opens with the common
/// panel block (the Enable mode + the Background group, built by <see cref="HudPanelCommon"/>, the C#
/// successor to QC <c>dialog_hudpanel_main_checkbox</c> + <c>dialog_hudpanel_main_settings</c>), then the
/// chat-specific rows the .qc declares: chat size, chat lifetime, and the chat-beep checkbox.
///
/// FAITHFUL UI NOW: this dialog drives the in-game HUD editor / chat panel backend VortexArena has not wired up
/// yet, but every cvar binding here is REAL — the widgets read/write the shared <see cref="MenuState.Cvars"/>
/// store the running game reads. There is no live HUD preview to render, so we don't fabricate one.
/// </summary>
public partial class DialogHudPanelChat : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogHudPanelChat";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Chat Panel"));

        // Long form → scroll it so it never overflows. (NEVER nest a TabContainer in a ScrollContainer; this
        // dialog has none.)
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);

        // QC: dialog_hudpanel_main_checkbox(me, "chat") + dialog_hudpanel_main_settings(me, "chat").
        HudPanelCommon.BuildCommon(box, "chat");

        // QC: makeXonoticSlider(6, 20, 1, "con_chatsize").
        box.AddChild(Ui.Row("Chat size:", Widgets.Slider("con_chatsize", 6f, 20f, 1f)));

        // QC: makeXonoticMixedSlider("con_chattime") — a "s"-suffixed mixed slider: ranges 5..60 step 5 and
        // 120..300 step 60, plus an "Infinite" (LIFETIME^Infinite) entry at value 0.
        var lifetime = Widgets.TextSlider("con_chattime").Add("Infinite", 0);
        for (int s = 5; s <= 60; s += 5) lifetime.Add($"{s}s", s);
        for (int s = 120; s <= 300; s += 60) lifetime.Add($"{s}s", s);
        box.AddChild(Ui.Row("Chat lifetime:", lifetime));

        // QC: makeXonoticCheckBox(0, "con_chatsound", _("Chat beep sound")).
        box.AddChild(Widgets.CheckBox("con_chatsound", "Chat beep sound"));

        // QC bottom button: the HUD-panel dialogs close with Dialog_Close → the universal Back here.
        root.AddChild(MakeButtonBar(MakeButton("Back", GoBack)));
    }
}
