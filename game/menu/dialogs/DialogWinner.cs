using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// "Winner" popup — a faithful C# port of <c>XonoticWinnerDialog</c>
/// (qcsrc/menu/xonotic/dialog_singleplayer_winner.qc). Shown after winning a single-player campaign level: it
/// is just the <c>/gfx/winner</c> banner image filling the dialog, with an "OK" button beneath
/// (QC <c>Dialog_Close</c> — here the universal Back). The QC also plays MENU_SOUND_WINNER on focus
/// (<c>XonoticWinnerDialog_focusEnter</c>); XonoticGodot's menu has no focus-sound hook wired here, so that cue is
/// omitted (noted).
///
/// The banner is a content texture from the asset repo; we load <c>/gfx/winner</c> if a Godot-importable
/// resource for it exists, otherwise we show an honest placeholder note rather than fabricating the artwork.
/// Binds no cvars (a static image dialog). QC title "Winner".
/// </summary>
public partial class DialogWinner : MenuScreen
{
    // Candidate paths for the QC "/gfx/winner" banner, in QC's fall-through order.
    //
    // The first is a bare, extension-agnostic VFS name, which is how the rest of the game reaches art:
    // ResolveImage probes .tga/.png/.jpg, so it survives the TGA->PNG conversion without an edit. The
    // res:// entry stays only as a fallback for art shipped as a Godot resource.
    //
    // It used to be three res:// paths under res://assets/data/, and none of them ever resolved — the
    // content root holds .pk3dir packages, never a loose gfx/, so the banner has silently never drawn.
    // Repointing them at res://data/ would not have fixed it either: the content tree carries a
    // .gdignore (G4), so Godot does not import it and ResourceLoader cannot see anything under it. The
    // content is reachable only through the VFS, which is what this now uses.
    private static readonly string[] WinnerImagePaths =
    {
        "gfx/winner",
        "res://gfx/winner.png",
    };

    protected override void BuildUi()
    {
        Name = "DialogWinner"; // QC XonoticWinnerDialog

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        root.AddChild(MakeTitle("Winner"));

        // QC: a single makeXonoticImage("/gfx/winner", -1) spanning the dialog above the OK button.
        Texture2D? banner = LoadWinnerBanner();
        if (banner is not null)
        {
            var image = new TextureRect
            {
                Texture = banner,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            root.AddChild(image);
        }
        else
        {
            // Honest placeholder: the banner artwork isn't available as an importable resource here.
            var placeholder = new CenterContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            var note = MakeLabel("(winner banner image — /gfx/winner asset pending)");
            note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
            note.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.AddChild(note);
            root.AddChild(placeholder);
        }

        // QC OK button (Dialog_Close) — the universal Back.
        root.AddChild(MakeButtonBar(MakeButton("OK", GoBack)));
    }

    /// <summary>Load the "/gfx/winner" banner if a resource for it exists; otherwise null (show the note).</summary>
    private static Texture2D? LoadWinnerBanner()
        // TextureCache routes a bare name through the VFS resolver and a res:// path through the resource
        // loader, caching both outcomes including the miss. Null still means "no banner", as before.
        => XonoticGodot.Game.Hud.TextureCache.GetFirst(WinnerImagePaths);
}
