using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Demos tab — <c>XonoticDemoBrowserTab_fill</c> (qcsrc/menu/xonotic/dialog_media_demo.qc) on its 17 x 6.5 grid:
/// the filter box and "Auto record demos" (<c>cl_autodemo</c>) + Refresh half a row down, the demo list
/// (<see cref="QcNameList"/>, demolist.qc) from row 2, Timedemo and Play under it.
///
/// The list is demolist.qc getDemos: <c>demos/*/*.dem</c> and <c>demos/*.dem</c> through the game's file system,
/// a subdirectory's demos first with their directory in SKINCOLOR_DEMOLIST_SUBDIR, the lot sorted. Play and Timedemo
/// start playback or benchmarking through the legacy viewer.
/// </summary>
public partial class DialogMediaDemo : QcTab
{
    private QcNameList _list = null!;
    private List<(string Name, string Path)> _demos = new();
    private string? _filter;

    public DialogMediaDemo() : base(rows: 17f, columns: 6.5f, intendedWidth: 0.96f) { }

    protected override void Fill()
    {
        _list = new QcNameList(skinColor: true);
        _list.ItemActivated += _ => StartDemo();

        GotoRC(0.5f, 0); SetFirstColumn(CurrentColumn);
            TD(1, 0.6f, Qc.Label("Filter:", 1f));
            var filter = TD(1, 2.9f, QcLook.InputBox(new LineEdit()));
                filter.TextChanged += t => { _filter = Glob(t); GetDemos(); };

        GotoRC(0.5f, 3.7f);
            TD(1, 1.5f, Qc.Check(0, "cl_autodemo", "Auto record demos"));
            TD(1, 1, Qc.Button("Refresh", GetDemos));

        GotoRC(2, 0);
            TD(Rows - 3, Columns, _list);

        GotoRC(Rows - 1, 0);
            TD(1, Columns / 2, Qc.Button("Timedemo", TimeDemo, "Benchmark how fast your computer can run the highlighted demo"));
            TD(1, Columns / 2, Qc.Button("Play", StartDemo));

        GetDemos();
    }

    /// <summary>DemoList_Filter_Change: a plain word matches anywhere in the name.</summary>
    internal static string? Glob(string text)
        => text.Length == 0 ? null : text.Contains('*') || text.Contains('?') ? text : "*" + text + "*";

    internal static bool Matches(string name, string? glob)
        => glob is null || Regex.IsMatch(name, "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>XonoticDemoList_getDemos.</summary>
    private void GetDemos()
    {
        _demos = ScanEntries("demos/", new[] { ".dem" }, _filter, "COLOR_DEMOLIST_SUBDIR");
        _list.SetNames(_demos.Select(demo => demo.Name).ToList());
    }

    /// <summary>
    /// getDemos_for_ext / getScreenshots_for_ext for each extension: <c>dir/*/filter.ext</c>, then
    /// <c>dir/filter.ext</c>, the directory and extension cut off and the name decolorized; a subdirectory's file
    /// gets a leading "/" (it sorts first) and its directory drawn in <paramref name="subdirColor"/>. buf_sort.
    /// </summary>
    internal static List<string> Scan(string dir, string[] extensions, string? glob, string subdirColor)
        => ScanEntries(dir, extensions, glob, subdirColor).Select(entry => entry.Name).ToList();

    private static List<(string Name, string Path)> ScanEntries(
        string dir, string[] extensions, string? glob, string subdirColor)
    {
        var entries = new List<(string Name, string Path)>();
        Color sub = MenuSkin.SkinColor(subdirColor, 0.5f, 0.5f, 0.5f);
        string code = Hex(sub);
        foreach (string ext in extensions)
        {
            foreach (string path in MenuDataBridge.Files.Find(dir, ext))
            {
                string s = path[dir.Length..^ext.Length];
                int slash = s.IndexOf('/');
                if (slash != s.LastIndexOf('/')) continue; // one directory level, as dir/*/ matches
                if (!Matches(slash >= 0 ? s[(slash + 1)..] : s, glob)) continue;
                s = MenuTextFormat.Decolorize(s);
                string name = slash >= 0 ? "/" + code + s.Replace("/", "^7/") : s;
                entries.Add((name, path));
            }
        }
        entries.Sort(static (left, right) =>
        {
            int byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.Path, right.Path);
        });
        return entries;
    }

    /// <summary>rgb_to_hexcolor.</summary>
    private static string Hex(Color c)
    {
        static char D(float v) => "0123456789ABCDEF"[(int)Math.Floor(Math.Clamp(v, 0f, 1f) * 15f + 0.5f)];
        return $"^x{D(c.R)}{D(c.G)}{D(c.B)}";
    }

    private string? SelectedDemo()
    {
        int i = _list.SelectedItem;
        return i >= 0 && i < _demos.Count ? _demos[i].Path : null;
    }

    // XonoticDemoList_startDemo / timeDemo, behind DemoConfirm_ListClick_Check_Gamestatus: in a match the player
    // confirms first (DialogDemoConfirm).
    private void StartDemo()
    {
        if (SelectedDemo() is { } s)
            Confirm(timing: false, () => MenuCommand.PlayDemo?.Invoke(s, false));
    }

    private void TimeDemo()
    {
        if (SelectedDemo() is { } s)
            Confirm(timing: true, () => MenuCommand.PlayDemo?.Invoke(s, true));
    }

    private void Confirm(bool timing, System.Action start)
    {
        if (MenuCommand.InMatch?.Invoke() ?? false)
            Menu?.Push(new DialogDemoConfirm(timing, start));
        else
            start();
    }
}
