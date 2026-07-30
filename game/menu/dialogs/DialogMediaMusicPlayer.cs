using Godot;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// Music Player tab — a faithful C# port of <c>XonoticMusicPlayerTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_musicplayer.qc). The QC is a two-pane layout: a filterable
/// <c>XonoticSoundList</c> (engine cdtracks/*.ogg) on the left with Add / Add all / Set as menu
/// track / Reset default menu track, and an <c>XonoticPlayList</c> on the right with a "Random
/// order" toggle (<c>music_playlist_random0</c>) and Stop/Play/Pause/Prev/Next + Remove/Remove all
/// transport.
///
/// The two LISTS are now live data-source backends: the sound list is a <see cref="SoundSource"/> (the C#
/// port of soundlist.qc, enumerating <c>sound/cdtracks/*.ogg</c> via the asset VFS, with the QC [C]/[D]
/// current/default-track markers), and the playlist is a <see cref="CvarStringSource"/> over
/// <c>music_playlist_list0</c> (the C# port of playlist.qc, re-tokenized from the cvar every refresh —
/// exactly as the QC playlist tokenizes the cvar each draw). Add / Remove edit that cvar directly
/// (addToPlayList / removeSelectedFromPlayList). The actual playback transport (cd play/pause/…) has no
/// audio backend, so those buttons route through <see cref="MenuCommand"/> and are inert until then.
/// Live bindings: the "Random order" checkbox (<c>music_playlist_random0</c>) and the Music volume slider.
/// </summary>
public partial class DialogMediaMusicPlayer : Control
{
    private const string PlaylistCvar = "music_playlist_list0";

    private SoundSource _sounds = null!;
    private CvarStringSource _playlist = null!;
    private ItemList _soundList = null!;
    private ItemList _playlistList = null!;
    private Label _soundNote = null!;
    private Label _playlistNote = null!;
    private LineEdit _filter = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _sounds = new SoundSource(MenuDataBridge.Files);
        // playlist.qc: nItems = tokenize_console(cvar_string("music_playlist_list0")) — space-separated.
        _playlist = new CvarStringSource(PlaylistCvar, " ", MenuDataBridge.CvarString);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        // Two side-by-side panes (QC: soundList in the left half, playList in the right half).
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        box.AddChild(columns);

        columns.AddChild(BuildSoundPane());
        columns.AddChild(BuildPlaylistPane());

        // Music volume — not in the QC media dialog (it lives in audio settings) but requested for this port;
        // binds the same engine cvar (bgmvolume, linear 0..1 in DP) with a percent readout.
        box.AddChild(Ui.Spacer(6f));
        box.AddChild(Ui.Row("Music volume:", Widgets.Slider("bgmvolume", 0f, 1f, 0.05f, format: Percent)));

        ReloadSounds();
        ReloadPlaylist();
    }

    /// <summary>Left pane: filter + sound list + Add / Add all / Set as menu track / Reset default menu track.</summary>
    private VBoxContainer BuildSoundPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 8);

        // QC: makeXonoticInputBox + SoundList_Filter_Change → getSounds with the filter substring.
        _filter = new LineEdit { PlaceholderText = "Filter tracks…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _filter.TextChanged += _ => ReloadSounds();
        pane.AddChild(Ui.Row("Filter:", _filter, 70f));

        _soundList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _soundList.ItemActivated += i => AddTrackAt((int)i); // QC doubleClickListBoxItem → addToPlayList
        pane.AddChild(_soundList);

        _soundNote = Ui.Label("");
        _soundNote.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        pane.AddChild(_soundNote);

        // QC: Add / Add all (SoundList_Add / SoundList_Add_All) — append to the playlist cvar.
        var addRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        addRow.AddThemeConstantOverride("separation", 10);
        addRow.AddChild(Ui.Button("Add", AddSelectedTrack));
        addRow.AddChild(Ui.Button("Add all", AddAllTracks));
        pane.AddChild(addRow);

        // QC: Set as menu track / Reset default menu track (SoundList_Menu_Track_Change / _Reset) — live cvar edit.
        pane.AddChild(Ui.Button("Set as menu track", SetMenuTrack));
        pane.AddChild(Ui.Button("Reset default menu track", ResetMenuTrack));

        return pane;
    }

    /// <summary>Right pane: "Random order" toggle + playlist + Stop/Play/Pause/Prev/Next + Remove/Remove all.</summary>
    private VBoxContainer BuildPlaylistPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 8);

        // QC: TextLabel _("Playlist:") + makeXonoticCheckBox(0, "music_playlist_random0", _("Random order")) — live.
        var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 10);
        var label = Ui.Label("Playlist:");
        label.CustomMinimumSize = new Vector2(70, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(label);
        var random = Widgets.CheckBox("music_playlist_random0", "Random order");
        random.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(random);
        pane.AddChild(header);

        _playlistList = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _playlistList.ItemActivated += _ => MenuCommand.Run("cd play"); // QC doubleClickListBoxItem → startSound
        pane.AddChild(_playlistList);

        _playlistNote = Ui.Label("");
        _playlistNote.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        pane.AddChild(_playlistNote);

        // QC transport: Stop / Play / Pause / Prev / Next (StopSound_Click … NextSound_Click) — playback inert.
        var transport = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        transport.AddThemeConstantOverride("separation", 6);
        transport.AddChild(Ui.Button("Stop", () => MenuCommand.Run("cd stop")));
        transport.AddChild(Ui.Button("Play", () => MenuCommand.Run("cd play")));
        transport.AddChild(Ui.Button("Pause", () => MenuCommand.Run("cd pause")));
        transport.AddChild(Ui.Button("Prev", () => MenuCommand.Run("cd prev")));
        transport.AddChild(Ui.Button("Next", () => MenuCommand.Run("cd next")));
        pane.AddChild(transport);

        // QC: Remove / Remove all (PlayList_Remove / PlayList_Remove_All) — edit the playlist cvar.
        var removeRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        removeRow.AddThemeConstantOverride("separation", 10);
        removeRow.AddChild(Ui.Button("Remove", RemoveSelected));
        removeRow.AddChild(Ui.Button("Remove all", RemoveAll));
        pane.AddChild(removeRow);

        return pane;
    }

    // -------------------------------------------------------------------------------------------------
    //  Sound list (left)
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC getSounds: rescan sound/cdtracks/*.ogg honoring the filter, marking the [C]urrent / [D]efault track.</summary>
    private void ReloadSounds()
    {
        string text = _filter.Text;
        int n = _sounds.Reload(string.IsNullOrEmpty(text) ? null : text);
        _soundList.Clear();

        // QC drawListBoxItem prefixes the current menu track with [C] and the default with [D] (soundlist.qc:81-84).
        string current = MenuState.Cvars.GetString("menu_cdtrack");
        string def = MenuState.Cvars.GetDefault("menu_cdtrack");
        foreach (string name in _sounds.Names)
        {
            string marker = name == current ? "[C] " : (name == def ? "[D] " : "");
            _soundList.AddItem($"{marker}{name}");
        }

        _soundNote.Text = n > 0
            ? $"({n} track{(n == 1 ? "" : "s")} in sound/cdtracks/)"
            : "(no cdtracks found — none installed, or the asset VFS isn't mounted)";
    }

    /// <summary>QC SoundList_Add: append the selected track to the playlist (skipping duplicates — addToPlayList).</summary>
    private void AddSelectedTrack()
    {
        int[] sel = _soundList.GetSelectedItems();
        if (sel.Length > 0)
            AddTrackAt(sel[0]);
    }

    private void AddTrackAt(int index)
    {
        if (index < 0 || index >= _sounds.Names.Count)
            return;
        AddToPlayList(_sounds.Names[index]);
        ReloadPlaylist();
    }

    /// <summary>QC SoundList_Add_All: append every listed track to the playlist.</summary>
    private void AddAllTracks()
    {
        foreach (string name in _sounds.Names)
            AddToPlayList(name);
        ReloadPlaylist();
    }

    /// <summary>QC SoundList_Menu_Track_Change: set menu_cdtrack to the selected track.</summary>
    private void SetMenuTrack()
    {
        int[] sel = _soundList.GetSelectedItems();
        if (sel.Length == 0 || sel[0] >= _sounds.Names.Count)
            return;
        MenuState.Cvars.Set("menu_cdtrack", _sounds.Names[sel[0]]);
        MenuState.Cvars.MarkArchived("menu_cdtrack");
        ReloadSounds(); // refresh the [C] marker
    }

    /// <summary>QC SoundList_Menu_Track_Reset: menu_cdtrack ← its default.</summary>
    private void ResetMenuTrack()
    {
        MenuState.Cvars.Set("menu_cdtrack", MenuState.Cvars.GetDefault("menu_cdtrack"));
        MenuState.Cvars.MarkArchived("menu_cdtrack");
        ReloadSounds();
    }

    // -------------------------------------------------------------------------------------------------
    //  Playlist (right) — backed by the music_playlist_list0 cvar (CvarStringSource)
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC XonoticPlayList_draw: re-tokenize music_playlist_list0 every refresh and repopulate.</summary>
    private void ReloadPlaylist()
    {
        int n = _playlist.Reload(null);
        _playlistList.Clear();
        for (int i = 0; i < n; i++)
            if (_playlist.TryGetEntry(i, out DataSourceEntry e))
                _playlistList.AddItem($"{i + 1}.  {e.Name}"); // QC draws the 1-based index + track name

        _playlistNote.Text = n > 0
            ? $"({n} track{(n == 1 ? "" : "s")} queued — playback backend pending)"
            : "(playlist empty — add tracks from the left; playback backend pending)";
    }

    /// <summary>
    /// QC XonoticPlayList_addToPlayList (playlist.qc:34-49): append <paramref name="track"/> to
    /// <c>music_playlist_list0</c> unless it's already present; an empty list is set to just the track.
    /// </summary>
    private void AddToPlayList(string track)
    {
        string cur = MenuState.Cvars.GetString(PlaylistCvar);
        var tokens = cur.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            SetPlaylistCvar(track);
            return;
        }
        foreach (string t in tokens)
            if (t == track)
                return; // already in playlist
        SetPlaylistCvar(cur + " " + track);
    }

    /// <summary>QC PlayList_Remove → removeSelectedFromPlayList: drop the selected entry from the cvar.</summary>
    private void RemoveSelected()
    {
        int[] sel = _playlistList.GetSelectedItems();
        if (sel.Length == 0)
            return;
        int idx = sel[0];
        var tokens = MenuState.Cvars.GetString(PlaylistCvar).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (idx < 0 || idx >= tokens.Length)
            return;
        var kept = new System.Collections.Generic.List<string>(tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
            if (i != idx)
                kept.Add(tokens[i]);
        SetPlaylistCvar(string.Join(' ', kept));
        ReloadPlaylist();
    }

    /// <summary>QC PlayList_Remove_All: clear music_playlist_list0.</summary>
    private void RemoveAll()
    {
        SetPlaylistCvar("");
        MenuCommand.Run("cd stop");
        ReloadPlaylist();
    }

    private static void SetPlaylistCvar(string value)
    {
        MenuState.Cvars.Set(PlaylistCvar, value);
        MenuState.Cvars.MarkArchived(PlaylistCvar);
    }

    /// <summary>Render a linear 0..1 volume as a percentage (matches the audio-settings readout).</summary>
    private static string Percent(float v) => $"{Mathf.RoundToInt(v * 100f)}%";
}
