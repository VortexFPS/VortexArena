using System;
using System.Collections.Generic;
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
/// send <c>playdemo</c> / <c>timedemo</c> as XonoticDemoList_startDemo / timeDemo do.
/// </summary>
public partial class DialogMediaDemo : QcTab
{
    private QcNameList _list = null!;
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
    private void GetDemos() => _list.SetNames(Scan("demos/", new[] { ".dem" }, _filter, "COLOR_DEMOLIST_SUBDIR"));

    /// <summary>
    /// getDemos_for_ext / getScreenshots_for_ext for each extension: <c>dir/*/filter.ext</c>, then
    /// <c>dir/filter.ext</c>, the directory and extension cut off and the name decolorized; a subdirectory's file
    /// gets a leading "/" (it sorts first) and its directory drawn in <paramref name="subdirColor"/>. buf_sort.
    /// </summary>
    internal static List<string> Scan(string dir, string[] extensions, string? glob, string subdirColor)
    {
        var names = new List<string>();
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
                names.Add(slash >= 0 ? "/" + code + s.Replace("/", "^7/") : s);
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
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
        if (i < 0 || i >= _list.Names.Count) return null;
        string s = _list.Names[i];
        if (s.StartsWith('/')) s = s[1..];
        return MenuTextFormat.Decolorize(s);
    }

    // XonoticDemoList_startDemo / timeDemo.
    private void StartDemo()
    {
        if (SelectedDemo() is { } s)
            MenuCommand.Run($"playdemo \"demos/{s}.dem\"; wait; togglemenu");
    }

    private void TimeDemo()
    {
        if (SelectedDemo() is { } s)
            MenuCommand.Run($"timedemo \"demos/{s}.dem\"; wait; togglemenu");
    }
}
