using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Common.Localization;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// xonotic/skinlist.qc: four text rows per menu skin — its 4:3 preview, the title a character after it and the
/// author right-aligned a line and a half below. Double-click or "Set skin" switches to the selected skin
/// (<c>menu_skin</c>, then <c>menu_restart; menu_cmd skinselect</c>).
/// </summary>
public partial class QcSkinList : MenuListBox
{
    private readonly SkinSource _source;

    public QcSkinList()
    {
        ItemHeight = 4f * MenuMetrics.FontNormalU; // rowsPerItem 4
        string defaultSkin = MenuState.Cvars.GetDefault("menu_skin");
        _source = new SkinSource(MenuDataBridge.Files, MenuDataBridge.ReadText, MenuDataBridge.ImageExists,
            string.IsNullOrEmpty(defaultSkin) ? "default" : defaultSkin);
        _source.Reload(null);
        ItemCount = _source.Skins.Count;
        ItemActivated += _ => SetSkin();
    }

    public override void _Ready()
    {
        base._Ready();
        // loadCvars: the row whose name is menu_skin.
        int i = _source.IndexOf(MenuState.Cvars.GetString("menu_skin"));
        if (i >= 0) SetSelected(i);
    }

    /// <summary>XonoticSkinList_setSkin.</summary>
    public void SetSkin()
    {
        int i = SelectedItem;
        if (i < 0 || i >= _source.Skins.Count) return;
        MenuState.Cvars.Set("menu_skin", _source.Skins[i].Name);
        MenuState.Cvars.MarkArchived("menu_skin");
        MenuCommand.Run("menu_restart");
        MenuCommand.Run("menu_cmd skinselect");
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _source.Skins.Count) return;
        SkinInfo skin = _source.Skins[index];
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        float previewW = h * 4f / 3f;
        Texture2D? preview = skin.Preview.Contains('/') ? MenuSkin.Image(skin.Preview) : MenuSkin.SkinImage(skin.Preview);
        if (preview is not null)
            DrawTextureRect(preview, new Rect2(rect.Position, new Vector2(previewW, h)), false);

        float nameOrigin = previewW + ch, nameSize = rect.Size.X - previewW - 2f * ch;
        float y1 = 0.5f * (h - 2.5f * ch), y2 = y1 + 1.5f * ch;
        float alpha = MenuSkin.Text.A;
        Color title = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_SKINLIST_TITLE", 1f, 1f, 1f), alpha));
        Color author = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_SKINLIST_AUTHOR", 0.5f, 0.5f, 0.5f), alpha));
        string s = QcDraw.ShortenToWidth(skin.Title, nameSize);
        QcDraw.Text(this, rect.Position + new Vector2(nameOrigin, y1), s, title);
        s = QcDraw.ShortenToWidth(skin.Author, nameSize);
        QcDraw.Text(this, rect.Position + new Vector2(nameOrigin + nameSize - QcDraw.TextWidth(s), y2), s, author);
    }
}

/// <summary>
/// xonotic/languagelist.qc: one row per language in languages.txt — its own name for itself (condensed to fit),
/// and on the right how complete its translation is: a check for a complete one, else the percentage, dimmed below
/// 90% and more below 50%. Selecting a row stages <c>_menu_prvm_language</c>; double-click or "Set language"
/// switches to it.
/// </summary>
public partial class QcLanguageList : MenuListBox
{
    private readonly List<LanguageEntry> _languages;

    public QcLanguageList()
    {
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
        _languages = Localization.LoadLanguages();
        ItemCount = _languages.Count;
        ItemActivated += _ => SetLanguage();
        ItemSelected += _ => SaveCvars();
    }

    public override void _Ready()
    {
        base._Ready();
        // loadCvars: English unless _menu_prvm_language names another; then save it back (unknown becomes "en").
        string s = MenuState.Cvars.GetString("_menu_prvm_language");
        int en = _languages.FindIndex(l => l.Id == "en"), found = _languages.FindIndex(l => l.Id == s);
        int i = found >= 0 ? found : en;
        if (i >= 0) SetSelected(i);
        SaveCvars();
    }

    private void SaveCvars()
    {
        int i = SelectedItem;
        if (i >= 0 && i < _languages.Count) MenuState.Cvars.Set("_menu_prvm_language", _languages[i].Id);
    }

    /// <summary>XonoticLanguageList_setLanguage: switch when the staged language differs from the running one.</summary>
    public void SetLanguage()
    {
        if (MenuState.Cvars.GetString("prvm_language") == MenuState.Cvars.GetString("_menu_prvm_language")) return;
        MenuCommand.Run("prvm_language \"$_menu_prvm_language\"; menu_restart; menu_cmd languageselect");
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _languages.Count) return;
        LanguageEntry l = _languages[index];
        float w = rect.Size.X, h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        string p = l.Percentage;
        float alphaFactor = 1f;
        if (p.Length > 0)
        {
            float.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float percent);
            alphaFactor = percent >= 90 ? 1f : percent >= 50 ? 0.65f : 0.3f;
            p = QcFormat.Decimals(percent, 0) + "%";
        }
        float percentSize = 3f * ch, percentOrigin = w - percentSize - 0.5f * ch, nameOrigin = 0.5f * ch;
        float y = rect.Position.Y + 0.5f * (h - ch);

        // draw_CondensedFontFactor(s, false, realFontSize, 1): squeezed only if wider than the whole row.
        string s = l.Localized;
        float sw = QcDraw.TextWidth(s), f = sw > w ? w / sw : 1f;
        Color text = MenuSkin.Text;
        // SKINCOLOR_TEXT * alpha_factor: the name's colour is darkened (before DrawQ_String's lift), not faded.
        Color raw = MenuSkin.SkinColor("COLOR_TEXT", 0.96f, 0.99f, 1f);
        Color name = MenuSkin.TextLift(new Color(raw.R * alphaFactor, raw.G * alphaFactor, raw.B * alphaFactor, text.A));
        QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin, y), s, name, condense: f);

        if (p.Length == 0)
        {
            if (MenuSkin.Image("gfx/scoreboard/player_ready") is { } icon && icon.GetHeight() > 0)
            {
                float iconW = h * icon.GetWidth() / icon.GetHeight();
                DrawTextureRect(icon, new Rect2(rect.Position.X + percentOrigin + percentSize - iconW, y, iconW, h), false,
                    MenuSkin.Fade(Colors.White, MenuSkin.SelectionAlpha));
            }
        }
        else
        {
            float pw = QcDraw.TextWidth(p), pf = pw > w ? w / pw : 1f;
            QcDraw.Text(this, new Vector2(rect.Position.X + percentOrigin + percentSize - pw * pf, y), p,
                MenuSkin.Fade(text, text.A * alphaFactor), condense: pf);
        }
    }
}
