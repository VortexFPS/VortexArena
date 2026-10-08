// Port of Base/darkplaces/cl_parse.c CL_BeginDownloads as far as its order goes: the world model and the
// precaches are loaded when the curl downloads for the map are done ("if(Curl_Have_forthismap()) ... come
// back later"), not when svc_serverinfo names them. A level whose server announced package downloads, or
// whose map arrived only after svc_serverinfo, gets its world and its precache here.
using System;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // Set when a package was mounted on the session's file system after files had been looked for: what the
    // worker threads read (or failed to find) before that is not to be trusted.
    private bool _dataChanged;

    /// <summary>A downloaded package has been mounted on the session's file system.</summary>
    public void GameDataChanged() => _dataChanged = true;

    /// <summary>True when the level's map is loaded: there is a world to draw and to collide with.</summary>
    public bool WorldLoaded => _levelBsp is not null && _mapRoot is not null;

    // The tail of BeginLevel: the map's files and the level's precache lists.
    private void LoadLevelFiles(CsqcClientState state)
    {
        string map = state.WorldModel;
        // The name is the server's. It has to be a plain path inside the game data and nothing else.
        if (!LegacyQcHost.IsSafePath(map) || !map.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) || !_vfs.Exists(map))
        {
            Map.LoadMap(map);   // records why (LoadError) and leaves an empty world
            _note($"map \"{map}\" is not in the Xonotic data: the world is empty ({Map.LoadError})");
            return;
        }

        // The level's models and sounds start loading on worker threads now, under the map build and CSQC_Init.
        BeginPrecache(state);
        LoadWorld(map, state.WorldNameNoExtension);
    }

    /// <summary>
    /// The level's downloads are done (or there were none after all) and its program is about to be loaded:
    /// the world is loaded now if BeginLevel left it, and whatever the worker threads read before a package
    /// was mounted is read again.
    /// </summary>
    public void LevelFilesArrived(CsqcClientState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        bool changed = _dataChanged;
        _dataChanged = false;
        if (changed)
        {
            // A model the last level's lists named may live in a package that was not mounted when a worker
            // looked for it: start the file work over on the data as it is now.
            CancelPrecache();
            ReleaseAllProxies();
            ReleasePrebuilt();
            ModelData.ClearCache();
            _note("downloaded packages were added to the game data: the level's files are read from it now");
        }
        if (!WorldLoaded) LoadLevelFiles(state);
        else if (changed) BeginPrecache(state);
    }
}
