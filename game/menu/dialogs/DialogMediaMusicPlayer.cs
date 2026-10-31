using System.Collections.Generic;
using Godot;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Music Player tab — <c>XonoticMusicPlayerTab_fill</c> (qcsrc/menu/xonotic/dialog_media_musicplayer.qc) on its
/// 17 x 6.5 grid, two halves of 6.3 columns less a 0.2 gap: on the left the filter box, the track list
/// (<see cref="QcSoundList"/>, sound/cdtracks), Add / Add all, "Set as menu track" and "Reset default menu track"; on
/// the right "Playlist:" and "Random order" (<c>music_playlist_random0</c>), the playlist (<see cref="QcPlayList"/>,
/// <c>music_playlist_list0</c>), Stop / Play / Pause / Prev / Next and Remove / Remove all.
///
/// Add and Remove edit <c>music_playlist_list0</c>, and the transport buttons drive the engine's playlist cvars
/// (<c>music_playlist_index</c> / <c>_current0</c> / <c>_sampleposition0</c>), as playlist.qc does.
/// </summary>
public partial class DialogMediaMusicPlayer : QcTab
{
    private const string PlaylistCvar = "music_playlist_list0";

    private SoundSource _sounds = null!;
    private QcSoundList _soundList = null!;
    private QcPlayList _playList = null!;
    private string? _filter;

    public DialogMediaMusicPlayer() : base(rows: 17f, columns: 6.5f, intendedWidth: 0.96f) { }

    protected override void Fill()
    {
        float columnsNoSpacing = Columns - 0.2f;
        _sounds = new SoundSource(MenuDataBridge.Files);
        _soundList = new QcSoundList();
        _soundList.ItemActivated += _ => AddSelected(); // doubleClickListBoxItem → addToPlayList
        _playList = new QcPlayList();
        _playList.ItemActivated += _ => StartSound(0);

        GotoRC(0.5f, 0); SetFirstColumn(CurrentColumn);
            TD(1, 0.5f, Qc.Label("Filter:"));
            var filter = TD(1, 2.5f, QcLook.InputBox(new LineEdit()));
                filter.TextChanged += t => { _filter = t.Length > 0 ? t : null; GetSounds(); };

        TR();
            TD(Rows - 4.5f, columnsNoSpacing / 2, _soundList);

        GotoRC(Rows - 3, 0);
            TD(1, columnsNoSpacing / 4, Qc.Button("Add", AddSelected));
            TD(1, columnsNoSpacing / 4, Qc.Button("Add all", AddAll));
        TR();
            TD(1, columnsNoSpacing / 2, Qc.Button("Set as menu track", SetMenuTrack));
        TR();
            TD(1, columnsNoSpacing / 2, Qc.Button("Reset default menu track", ResetMenuTrack));
        TR();
        TR();
        GotoRC(0.5f, columnsNoSpacing / 2 + 0.2f); SetFirstColumn(CurrentColumn);
            TD(1, columnsNoSpacing / 4, Qc.Label("Playlist:"));
            TD(1, columnsNoSpacing / 4, Qc.Check(0, "music_playlist_random0", "Random order"));
        TR();
            TD(Rows - 3.5f, columnsNoSpacing / 2, _playList);

        GotoRC(Rows - 2, columnsNoSpacing / 2 + 0.2f);
            TD(1, columnsNoSpacing / 10, Qc.Button("Stop", StopSound));
            TD(1, columnsNoSpacing / 10, Qc.Button("Play", () => StartSound(0)));
            TD(1, columnsNoSpacing / 10, Qc.Button("Pause", PauseSound));
            TD(1, columnsNoSpacing / 10, Qc.Button("Prev", () => StartSound(-1)));
            TD(1, columnsNoSpacing / 10, Qc.Button("Next", () => StartSound(+1)));
        TR();
            TD(1, columnsNoSpacing / 4, Qc.Button("Remove", RemoveSelected));
            TD(1, columnsNoSpacing / 4, Qc.Button("Remove all", RemoveAll));

        GetSounds();
    }

    // -------------------------------------------------------------------------------------------------
    //  Track list (soundlist.qc)
    // -------------------------------------------------------------------------------------------------

    /// <summary>XonoticSoundList_getSounds.</summary>
    private void GetSounds()
    {
        _sounds.Reload(_filter);
        _soundList.SetNames(_sounds.Names);
    }

    private string? SelectedSound()
        => _soundList.SelectedItem >= 0 && _soundList.SelectedItem < _sounds.Names.Count ? _sounds.Names[_soundList.SelectedItem] : null;

    private void AddSelected()
    {
        if (SelectedSound() is { } s) AddToPlayList(s);
    }

    private void AddAll()
    {
        foreach (string s in _sounds.Names) AddToPlayList(s);
    }

    /// <summary>SoundList_Menu_Track_Change: menu_cdtrack to the selected track.</summary>
    private void SetMenuTrack()
    {
        if (SelectedSound() is not { } s) return;
        MenuState.Cvars.Set("menu_cdtrack", s);
        MenuState.Cvars.MarkArchived("menu_cdtrack");
    }

    /// <summary>SoundList_Menu_Track_Reset: menu_cdtrack back to its default.</summary>
    private void ResetMenuTrack()
    {
        MenuState.Cvars.Set("menu_cdtrack", MenuState.Cvars.GetDefault("menu_cdtrack"));
        MenuState.Cvars.MarkArchived("menu_cdtrack");
    }

    // -------------------------------------------------------------------------------------------------
    //  Playlist (playlist.qc)
    // -------------------------------------------------------------------------------------------------

    private static string[] Tracks() => MenuState.Cvars.GetString(PlaylistCvar).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

    /// <summary>XonoticPlayList_addToPlayList: appended unless already in it.</summary>
    private static void AddToPlayList(string track)
    {
        string[] tracks = Tracks();
        if (System.Array.IndexOf(tracks, track) >= 0) return;
        SetPlaylist(tracks.Length == 0 ? track : string.Join(' ', tracks) + " " + track);
    }

    /// <summary>XonoticPlayList_removeSelectedFromPlayList: the selected track out. Removing the one playing
    /// restarts on the track now in its place (or stops when none is left).</summary>
    private void RemoveSelected()
    {
        string[] tracks = Tracks();
        int i = _playList.SelectedItem;
        if (tracks.Length == 0 || i < 0 || i >= tracks.Length) return;
        if (i == tracks.Length - 1) _playList.SetSelected(i - 1);
        bool playingRemoved = false;
        float index = MenuState.Cvars.GetFloat("music_playlist_index");
        if (index is 0 or 999)
        {
            float current = MenuState.Cvars.GetFloat("music_playlist_current0");
            if (current == i) playingRemoved = true;
            else if (current > i) MenuState.Cvars.Set("music_playlist_current0", Num(current - 1));
        }
        var kept = new List<string>(tracks);
        kept.RemoveAt(i);
        SetPlaylist(string.Join(' ', kept));
        if (playingRemoved)
        {
            if (kept.Count == 0) StopSound();
            else StartSound(0);
        }
    }

    /// <summary>PlayList_Remove_All.</summary>
    private void RemoveAll()
    {
        SetPlaylist("");
        StopSound();
        _playList.SetSelected(0);
    }

    private static void SetPlaylist(string value)
    {
        MenuState.Cvars.Set(PlaylistCvar, value);
        MenuState.Cvars.MarkArchived(PlaylistCvar);
    }

    private static string Num(float f) => f.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>XonoticPlayList_startSound: the selected track (or the one <paramref name="offset"/> from the playing
    /// one), from its start; the selected track resumes instead when it is the one paused.</summary>
    private void StartSound(int offset)
    {
        int n = Tracks().Length;
        float index = MenuState.Cvars.GetFloat("music_playlist_index");
        float current = MenuState.Cvars.GetFloat("music_playlist_current0");
        float f;
        if (offset != 0)
        {
            if (index == -1) return;
            f = Mathf.Clamp(current + offset, 0, n - 1);
            if (f == current) return;
        }
        else
        {
            f = _playList.SelectedItem;
            if (f == current && index == 999)
            {
                ResumeSound();
                return;
            }
        }
        // "START: list 0 is disabled by setting the index to 999 ... then we switch back to list 0"
        MenuState.Cvars.Set("music_playlist_index", "999");
        MenuState.Cvars.Set("music_playlist_current0", Num(f));
        MenuCommand.Run("wait; music_playlist_sampleposition0 0; wait; music_playlist_index 0");
    }

    /// <summary>XonoticPlayList_resumeSound.</summary>
    private static void ResumeSound()
    {
        if (MenuState.Cvars.GetFloat("music_playlist_index") == 999)
            MenuCommand.Run("music_playlist_index 0; wait; music_playlist_sampleposition0 0");
    }

    /// <summary>XonoticPlayList_pauseSound: list 0 off (the engine remembers the position), or resume when paused.</summary>
    private static void PauseSound()
    {
        if (MenuState.Cvars.GetFloat("music_playlist_index") == 0)
            MenuCommand.Run("music_playlist_index 999");
        else
            ResumeSound();
    }

    /// <summary>XonoticPlayList_stopSound: list 0 off, and the menu track again three seconds later.</summary>
    private static void StopSound()
    {
        float index = MenuState.Cvars.GetFloat("music_playlist_index");
        if (index is not (0 or 999)) return;
        MenuState.Cvars.Set("music_playlist_index", "-1");
        MenuCommand.Run("wait; music_playlist_sampleposition0 0");
        MenuCommand.Run("defer 3 \"cd play $menu_cdtrack\"");
    }
}
