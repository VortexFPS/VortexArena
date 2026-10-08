// NOT BUILT IN THE GODOT HOST as of 2026-10-08, and never run. It type-checks against GodotSharp 4.6.3 in a
// scratch project with stand-ins for the host classes it uses; the real build (source generators) is untried.
using System;
using System.Text;
using Godot;
using VortexArena.Game.Hud;
using VortexArena.Modding;

namespace VortexArena.Game.Modding;

/// <summary>
/// The question a player is asked before anything of a server's mod is downloaded
/// (planning/specs/modding.md, section 9): who offers what, what it may do, how big it is, and what
/// refusing costs.
///
/// Deliberately minimal and keyboard-only. The game owns the mouse while a match is running
/// (<c>MouseCapture</c>), and this layer must not fight it; four function keys answer the prompt, and
/// the console commands <c>mod_allow</c> / <c>mod_deny</c> do the same. While the prompt is up it
/// swallows those four keys and nothing else.
///
/// Every string shown comes from a stranger's server. The manifest parser has already refused control
/// characters and capped the lengths; here the text is additionally stripped of colour codes and put in
/// plain <see cref="Label"/>s, which interpret no markup.
/// </summary>
public partial class ModConsentPrompt : PanelContainer
{
    private Label? _title, _body, _keys;

    /// <summary>Raised once per prompt with the player's answer.</summary>
    public event Action<ModConsentDecision>? Answered;

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.CenterTop);
        GrowHorizontal = GrowDirection.Both;
        OffsetTop = 48f;
        CustomMinimumSize = new Vector2(560f, 0f);

        MarginContainer margin = new() { MouseFilter = MouseFilterEnum.Ignore };
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 14);
        AddChild(margin);

        VBoxContainer rows = new() { MouseFilter = MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 8);
        margin.AddChild(rows);

        _title = NewLabel(18);
        _body = NewLabel(14);
        _keys = NewLabel(14);
        rows.AddChild(_title);
        rows.AddChild(_body);
        rows.AddChild(_keys);
    }

    private static Label NewLabel(int fontSize)
    {
        Label label = new()
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(532f, 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        if (HudPanel.HudFont is { } font) label.AddThemeFontOverride("font", font);
        return label;
    }

    public void Open(ModConsentRequest request)
    {
        if (_title is null || _body is null || _keys is null) return;
        ModManifest manifest = request.Manifest;

        _title.Text = $"Server mod: {Clean(manifest.Consent.Title, manifest.ModId)} {Clean(manifest.ModVersion, "")}";

        StringBuilder body = new();
        body.Append(request.ServerKey.Length > 0 ? request.ServerKey : "This server").Append(" offers a mod");
        if (manifest.Consent.Author.Length > 0) body.Append(" by ").Append(Clean(manifest.Consent.Author, "?"));
        body.Append(".\n");
        if (manifest.Consent.Description.Length > 0) body.Append(Clean(manifest.Consent.Description, "")).Append('\n');

        body.Append("\nIt runs in a sandbox. It can draw on your screen");
        if (manifest.HasCapability(ModCapabilities.Sound)) body.Append(", play sounds");
        if (manifest.HasCapability(ModCapabilities.Net)) body.Append(", exchange messages with this server");
        body.Append(" and read the game state your HUD shows. It cannot read or write files, open network connections or run programs.\n");

        body.Append(request.DownloadBytes > 0
            ? $"Download: {request.DownloadBytes / (1024.0 * 1024.0):0.0} MB in {request.DownloadFiles} file(s).\n"
            : "Nothing to download: its files are already on this computer.\n");
        body.Append(manifest.Required
            ? "This server REQUIRES the mod: if you refuse, the server will disconnect you."
            : "You can play here without it.");
        _body.Text = body.ToString();

        _keys.Text = "[F1] Allow once    [F2] Always allow this mod on this server\n[F3] Not now    [F4] Never for this server\n(console: mod_allow, mod_deny)";
        Visible = true;
    }

    public void Close() => Visible = false;

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey { Pressed: true, Echo: false } key) return;
        ModConsentDecision? decision = key.Keycode switch
        {
            Key.F1 => ModConsentDecision.AllowOnce,
            Key.F2 => ModConsentDecision.AllowOnThisServer,
            Key.F3 => ModConsentDecision.DenyOnce,
            Key.F4 => ModConsentDecision.DenyThisServer,
            _ => null,
        };
        if (decision is not { } answer) return;
        GetViewport().SetInputAsHandled();
        Close();
        Answered?.Invoke(answer);
    }

    // Colour codes out, and never an empty string where a name should be.
    private static string Clean(string text, string fallback)
    {
        string stripped = HudText.Strip(text).Trim();
        return stripped.Length > 0 ? stripped : fallback;
    }
}
