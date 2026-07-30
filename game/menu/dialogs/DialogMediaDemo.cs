using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Demos tab — a faithful C# port of <c>XonoticDemoBrowserTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_demo.qc). The QC builds a filter box + an
/// <c>XonoticDemoList</c> (engine-enumerated *.dem files) with "Auto record demos"
/// (<c>cl_autodemo</c>), Refresh, Timedemo and Play buttons.
///
/// Wired to the port's demo backend (T62/T63): the list enumerates the recorded <c>.xgd</c> files under
/// <c>user://demos/</c> (each row shows the demo's map + length from its header), the filter box narrows it
/// (QC <c>DemoList_Filter_Change</c> — client-side, no cvar), Refresh rescans, and Play / double-click boots
/// the selected demo through the same <see cref="Net.NetGame.PendingReplayDemo"/> one-shot the console
/// <c>playdemo</c> command and the <c>--playdemo</c> CLI flag use (via
/// <see cref="CreateGameScreen.RaiseStartGame"/>, the menu's standard start-a-match seam). Only Timedemo
/// remains representative (routed through <see cref="MenuCommand"/>, logged as having no backend — the port
/// has no benchmark playback mode). The <c>cl_autodemo</c> checkbox binds the same cvar the QC binds.
/// </summary>
public partial class DialogMediaDemo : Control
{
    /// <summary>One enumerated demo file: where it is + what its header says (display fields).</summary>
    private readonly struct DemoRow
    {
        public readonly string Path;      // absolute path, handed to PendingReplayDemo
        public readonly string FileName;  // bare name, the filter's subject (QC filters on the file name)
        public readonly string Display;   // list row text
        public readonly string MapName;   // from the header; "" when unreadable
        public readonly string Gametype;  // from the header (the recorded match's mode)
        public readonly bool Playable;

        public DemoRow(string path, string fileName, string display, string mapName, string gametype, bool playable)
        {
            Path = path;
            FileName = fileName;
            Display = display;
            MapName = mapName;
            Gametype = gametype;
            Playable = playable;
        }
    }

    private readonly List<DemoRow> _demos = new();      // full scan result
    private readonly List<int> _visible = new();        // indexes into _demos after the filter
    private ItemList _list = null!;
    private LineEdit _filter = null!;
    private Label _note = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        // Top row: filter (QC makeXonoticInputBox + DemoList_Filter_Change — no cvar; live client-side filter)
        // and "Auto record demos" + Refresh.
        _filter = new LineEdit { PlaceholderText = "Filter demos…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _filter.TextChanged += _ => ApplyFilter();
        box.AddChild(Ui.Row("Filter:", _filter, 80f));

        var topButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topButtons.AddThemeConstantOverride("separation", 10);
        // QC: makeXonoticCheckBox(0, "cl_autodemo", _("Auto record demos")) — live, binds the same cvar.
        var autodemo = Widgets.CheckBox("cl_autodemo", "Auto record demos");
        autodemo.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topButtons.AddChild(autodemo);
        // QC: makeXonoticButton(_("Refresh"), …) onClick=DemoList_Refresh_Click — rescan the demo dir.
        topButtons.AddChild(Ui.Button("Refresh", Rescan));
        box.AddChild(topButtons);

        // The demo list (QC: demolist = makeXonoticDemoList(); spans the middle). Double-click plays, like the
        // QC list's dblclick handler.
        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 240),
        };
        _list.ItemActivated += _ => PlaySelected();
        box.AddChild(_list);
        _note = Ui.Label("");
        box.AddChild(_note);

        // Bottom row: Timedemo (left half) + Play (right half), matching the QC two-column split.
        // QC handlers run demolist.timeDemo / demolist.startDemo. Play boots the real replay; Timedemo has no
        // benchmark backend — the representative DP command routes through MenuCommand and is logged honestly.
        var bottom = Ui.ButtonBar(
            Ui.Button("Timedemo", () => MenuCommand.Run("timedemo")),
            Ui.Button("Play", PlaySelected));
        box.AddChild(bottom);

        Rescan();
    }

    /// <summary>Enumerate <c>user://demos/*.xgd</c> and rebuild the rows (QC DemoList_Refresh_Click). Each
    /// row shows map + length from the demo header; an unreadable file is listed but not playable.</summary>
    private void Rescan()
    {
        _demos.Clear();
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "demos");
        if (System.IO.Directory.Exists(dir))
        {
            string[] files = System.IO.Directory.GetFiles(dir, "*.xgd");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                try
                {
                    var header = VortexArena.Net.Demo.DemoFormat.ReadHeader(path);
                    string len = header.DurationSeconds > 0f
                        ? header.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                        : "unfinished"; // crash-truncated: duration never patched (still playable via scan)
                    _demos.Add(new DemoRow(path, name, $"{name}  —  {header.MapName}, {len}",
                        header.MapName, header.Gametype, playable: true));
                }
                catch (Exception)
                {
                    _demos.Add(new DemoRow(path, name, $"{name}  —  (unreadable)", "", "", playable: false));
                }
            }
        }
        ApplyFilter();
    }

    /// <summary>Rebuild the visible rows from the filter text (QC DemoList_Filter_Change: case-insensitive
    /// substring on the file name).</summary>
    private void ApplyFilter()
    {
        _visible.Clear();
        _list.Clear();
        string needle = _filter.Text.Trim();
        for (int i = 0; i < _demos.Count; i++)
        {
            if (needle.Length > 0 && !_demos[i].FileName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            _visible.Add(i);
            _list.AddItem(_demos[i].Display);
        }
        _note.Text = _demos.Count == 0
            ? "(no demos yet — enable auto-record above, or use the `record` console command in a match)"
            : $"{_visible.Count} of {_demos.Count} demo(s)";
    }

    /// <summary>Boot the selected demo (QC demolist.startDemo): the same PendingReplayDemo one-shot handoff
    /// the console <c>playdemo</c> and the <c>--playdemo</c> CLI use — the replay host boots as a listen
    /// server on the demo's own map through the menu's standard start seam.</summary>
    private void PlaySelected()
    {
        int[] selected = _list.GetSelectedItems();
        if (selected.Length == 0 || selected[0] >= _visible.Count)
            return;
        DemoRow row = _demos[_visible[selected[0]]];
        if (!row.Playable)
        {
            VortexArena.Common.Diagnostics.Log.Info("[DialogMediaDemo] cannot play an unreadable demo file");
            return;
        }
        Net.NetGame.PendingReplayDemo = row.Path;
        CreateGameScreen.RaiseStartGame(new MatchConfig
        {
            Map = row.MapName,
            Gametype = string.IsNullOrEmpty(row.Gametype) ? "dm" : row.Gametype,
            BotCount = 0,
        });
    }
}
