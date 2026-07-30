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
/// reads each model's <c>name</c> (skipping hidden ones), and lets the user cycle them with the
/// <c>&lt;</c>/<c>&gt;</c> buttons. The selected model's preview image (the sibling <c>..._N.tga</c>) is shown
/// with the model title beneath it (QC draw at '0.5 0.8 0').
///
/// As in QC <c>saveCvars</c>, cycling writes <c>_cl_playermodel</c> + <c>_cl_playerskin</c> but DOES NOT apply
/// the model live — the Profile dialog's "Apply" button does that (avoiding the engine's name/model flood
/// control). FAITHFUL-ENOUGH on the preview: QC renders the model image (and, with a real model viewer, a 3D
/// preview); here we show the model's preview .tga (the same currentModelImage QC draws when the engine lacks a
/// live 3D preview), cycled by &lt;/&gt;. Degrades to the model name + a "no preview" note without the asset.
/// </summary>
public partial class PlayerModelSelector : VBoxContainer
{
    private sealed class ModelEntry
    {
        public string Title = "";
        public string Model = "";   // _cl_playermodel value, e.g. "models/player/erebus.iqm"
        public string Skin = "0";   // _cl_playerskin value
        public string ImageBase = ""; // VFS base (no ext) of the preview .tga
    }

    private static CvarService Cvars => MenuState.Cvars;

    private readonly List<ModelEntry> _models = new();
    private int _index;

    private TextureRect _preview = null!;
    private Label _title = null!;

    public PlayerModelSelector()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 6);
    }

    public override void _Ready()
    {
        var top = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddThemeConstantOverride("separation", 8);

        top.AddChild(Ui.Button("<", Prev));

        var center = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 160),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        center.AddChild(_preview);
        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeColorOverride("font_color", MenuSkin.Bright);
        if (MenuSkin.BoldFont is { } bold) _title.AddThemeFontOverride("font", bold);
        center.AddChild(_title);
        top.AddChild(center);

        top.AddChild(Ui.Button(">", Next));
        AddChild(top);

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
                    (string? name, bool hidden) = ReadModelParams(vfs, fn);
                    if (hidden || string.IsNullOrEmpty(name)) continue;
                    _models.Add(new ModelEntry
                    {
                        Title = name!,
                        Model = model,
                        Skin = skin,
                        ImageBase = model + "_" + skin, // sibling preview: "<model>_<skin>.tga"
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

        Show();
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
    private static (string? Name, bool Hidden) ReadModelParams(VirtualFileSystem vfs, string fn)
    {
        string? text = vfs.Exists(fn) ? vfs.ReadText(fn) : null;
        if (text is null) return (null, false);
        string? name = null;
        bool hidden = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("name "))
                name = line[5..].Trim();
            else if (line == "hidden" || line.StartsWith("hidden "))
                hidden = true;
        }
        return (name, hidden);
    }

    private void Prev()
    {
        if (_models.Count == 0) return;
        _index = (_index - 1 + _models.Count) % _models.Count;
        Show();
        SaveCvars();
    }

    private void Next()
    {
        if (_models.Count == 0) return;
        _index = (_index + 1) % _models.Count;
        Show();
        SaveCvars();
    }

    private void Show()
    {
        if (_models.Count == 0)
        {
            _title.Text = "<no model found>";
            _preview.Texture = null;
            return;
        }
        ModelEntry e = _models[_index];
        _title.Text = e.Title;
        _preview.Texture = MenuSkin.Image(e.ImageBase); // null → blank preview (the QC "nopreview_player" fallback)
    }

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
