// Port of qcsrc/menu/xonotic/playermodel.qc (XonoticPlayerModelSelector).
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Vfs;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Menu;

/// <summary>
/// The player-model selector — a C# port of <c>XonoticPlayerModelSelector</c>
/// (qcsrc/menu/xonotic/playermodel.qc). Enumerates the player-model datafiles (the QC glob
/// <c>get_model_datafilename(null,-1,"txt")</c> → <c>models/player/*.iqm_N.txt</c> / <c>models/ok_player/*.dpm_N.txt</c>),
/// reads each model's <c>name</c> and <c>sex</c> (skipping hidden ones); the dialog's &lt;&lt; / &gt;&gt; buttons
/// call <see cref="Prev"/> / <see cref="Next"/>. Drawn as the QC draws it: the model's preview image (the sibling
/// <c>..._N.tga</c>) fitted to the box at its own aspect (XonoticImage, forcedAspect -1), the title in bold at
/// SKINFONTSIZE_TITLE centred at 80% of the height, and the description lines centred at the bottom.
///
/// As in QC <c>saveCvars</c>, cycling writes <c>_cl_playermodel</c> + <c>_cl_playerskin</c> but DOES NOT apply
/// the model live — the Profile dialog's "Apply" button does that (avoiding the engine's name/model flood
/// control). FAITHFUL-ENOUGH on the preview: QC renders the model image (and, with a real model viewer, a 3D
/// preview); here we show the model's preview .tga (the same currentModelImage QC draws when the engine lacks a
/// live 3D preview), cycled by &lt;/&gt;. Degrades to the model name + a "no preview" note without the asset.
/// </summary>
public partial class PlayerModelSelector : Control
{
    private sealed class ModelEntry
    {
        public string Title = "";
        public string Model = "";   // _cl_playermodel value, e.g. "models/player/erebus.iqm"
        public string Skin = "0";   // _cl_playerskin value
        public string ImageBase = ""; // VFS base (no ext) of the preview .tga
        public string Description = ""; // what draw prints under the title (XONVOTE186: the sex)
    }

    private static CvarService Cvars => MenuState.Cvars;

    private readonly List<ModelEntry> _models = new();
    private int _index;

    public PlayerModelSelector()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        LoadModels();
    }

    /// <summary>QC loadModels + loadCvars + go(0): enumerate the datafiles, match the current cvars, show it.</summary>
    private void LoadModels()
    {
        _models.Clear();
        VirtualFileSystem? vfs = MenuState.Vfs;
        if (vfs is not null)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string prefix in new[] { "models/player/", "models/ok_player/" })
            {
                var files = new List<string>(vfs.Find(prefix, "txt"));
                files.Sort(System.StringComparer.Ordinal); // QC buf_sort by name (stable, deterministic order)
                foreach (string fn in files)
                {
                    // datafile: "<model>_<skin>.txt" where <model> ends in .iqm/.dpm/.md3. e.g.
                    // "models/player/erebus.iqm_0.txt" → model "models/player/erebus.iqm", skin "0".
                    if (!TryParseDatafile(fn, out string model, out string skin)) continue;
                    if (!seen.Add(model + "\0" + skin)) continue;
                    (string? name, bool hidden, string sex) = ReadModelParams(vfs, fn);
                    if (hidden || string.IsNullOrEmpty(name)) continue;
                    _models.Add(new ModelEntry
                    {
                        Title = name!,
                        Model = model,
                        Skin = skin,
                        ImageBase = model + "_" + skin, // sibling preview: "<model>_<skin>.tga"
                        // XONVOTE186: "\n" then "\n<sex>", trailing newlines dropped.
                        Description = sex.Length > 0 ? "\n\n" + sex : "",
                    });
                }
            }
        }

        // loadCvars: select the entry matching _cl_playermodel + _cl_playerskin, else 0.
        string curModel = Cvars.GetString("_cl_playermodel");
        string curSkin = Cvars.GetString("_cl_playerskin");
        _index = 0;
        for (int i = 0; i < _models.Count; i++)
            if (_models[i].Model == curModel && _models[i].Skin == curSkin) { _index = i; break; }

        QueueRedraw();
    }

    private static bool TryParseDatafile(string fn, out string model, out string skin)
    {
        model = ""; skin = "0";
        if (!fn.EndsWith(".txt")) return false;
        string body = fn[..^4]; // drop ".txt"
        int us = body.LastIndexOf('_');
        if (us < 0) return false;
        string skinPart = body[(us + 1)..];
        if (!int.TryParse(skinPart, out _)) return false; // must be "<model>_<skinNumber>"
        model = body[..us];
        skin = skinPart;
        // sanity: the model base should carry a model extension
        return model.EndsWith(".iqm") || model.EndsWith(".dpm") || model.EndsWith(".md3");
    }

    /// <summary>Read the QC model-parameter file: the <c>name</c> line + the <c>hidden</c> flag.</summary>
    private static (string? Name, bool Hidden, string Sex) ReadModelParams(VirtualFileSystem vfs, string fn)
    {
        string? text = vfs.Exists(fn) ? vfs.ReadText(fn) : null;
        if (text is null) return (null, false, "");
        string? name = null, sex = "";
        bool hidden = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("name "))
                name = line[5..].Trim();
            else if (line.StartsWith("sex "))
                sex = line[4..].Trim();
            else if (line == "hidden" || line.StartsWith("hidden "))
                hidden = true;
        }
        return (name, hidden, sex);
    }

    public void Prev()
    {
        if (_models.Count == 0) return;
        _index = (_index - 1 + _models.Count) % _models.Count;
        QueueRedraw();
        SaveCvars();
    }

    public void Next()
    {
        if (_models.Count == 0) return;
        _index = (_index + 1) % _models.Count;
        QueueRedraw();
        SaveCvars();
    }

    public override void _Draw()
    {
        float fs = MenuMetrics.FontNormalU;
        if (_models.Count == 0)
        {
            DrawCentred(new Vector2(0.5f * Size.X, 0.5f * Size.Y), "<no model found>", MenuSkin.Fade(MenuSkin.Text, 0.6f), MenuMetrics.FontNormal, false);
            return;
        }
        ModelEntry e = _models[_index];
        // XonoticImage with forcedAspect -1: the picture at its own aspect, as large as fits, centred.
        Texture2D? image = MenuSkin.Image(e.ImageBase) ?? MenuSkin.SkinImage("nopreview_player");
        if (image is not null && image.GetHeight() > 0 && Size.Y > 0)
        {
            float asp = (float)image.GetWidth() / image.GetHeight();
            Vector2 size = Size.X > asp * Size.Y ? new Vector2(Size.Y * asp, Size.Y) : new Vector2(Size.X, Size.X / asp);
            DrawTextureRect(image, new Rect2((Size - size) * 0.5f, size), false);
        }
        // draw_CenterText('0.5 0.8 0', title, realFontSize * titleFontSize / fontSize, SKINCOLOR_MODELTITLE, ...)
        Color titleColor = MenuSkin.TextLift(MenuSkin.SkinColor("COLOR_MODELTITLE", 0.96f, 0.99f, 1f));
        DrawCentred(new Vector2(0.5f * Size.X, 0.8f * Size.Y), e.Title,
            MenuSkin.Fade(titleColor, MenuSkin.SkinNumber("ALPHA_MODELTITLE", 0.8f)), MenuMetrics.FontTitle, true);
        string[] lines = e.Description.Split('\n');
        float y = Size.Y - fs * (lines.Length + 0.5f);
        foreach (string line in lines)
        {
            DrawCentred(new Vector2(0.5f * Size.X, y), line, MenuSkin.TextLift(MenuSkin.SkinColor("COLOR_TEXT", 0.96f, 0.99f, 1f)), MenuMetrics.FontNormal, false);
            y += fs;
        }
    }

    // draw_CenterText: the text's top centre at the point.
    private void DrawCentred(Vector2 topCentre, string text, Color color, float menuPx, bool bold)
    {
        if (text.Length == 0) return;
        float w = QcDraw.TextWidth(text, menuPx, bold);
        QcDraw.Text(this, topCentre - new Vector2(0.5f * w, 0f), text, color, menuPx, bold);
    }

    public override Vector2 _GetMinimumSize() => Vector2.Zero;

    /// <summary>QC saveCvars: write the model/skin cvars but DON'T apply live (the Apply button does).</summary>
    private void SaveCvars()
    {
        if (_models.Count == 0) return;
        ModelEntry e = _models[_index];
        Cvars.Set("_cl_playermodel", e.Model);
        Cvars.Set("_cl_playerskin", e.Skin);
        Cvars.MarkArchived("_cl_playermodel");
        Cvars.MarkArchived("_cl_playerskin");
    }
}
