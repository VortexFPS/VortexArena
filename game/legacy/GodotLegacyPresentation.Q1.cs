// Quake 1 format maps (BSP 29, "BSP2", "2PSB", Half-Life 30) in a legacy session: the dispatch from LoadWorld
// and what the session keeps of such a level. The reader is VortexArena.Formats.Bsp.Q1BspReader, the collision
// VortexArena.Engine.Collision.Q1HullCollision (through BspLegacyWorld.UseQ1Map); the drawing is Q1MapLoader.
using System;
using VortexArena.Formats.Bsp;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // The level's map when it is a Quake 1 format one (then _levelBsp is null).
    private Q1BspData? _levelQ1;

    // True when the file starts like a Quake 1 format map. (Four bytes would do; the file system reads whole
    // files, and LoadQ1World reads it again - a map is read twice on the Quake 3 path as well.)
    private bool IsQ1Map(string map)
    {
        try { return Q1BspReader.IsQ1Format(_vfs.ReadBytes(map)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    // LoadWorld for a Quake 1 format map. An exception goes to LoadWorld's handler, which records it as the
    // reason the level is not entered.
    private bool LoadQ1World(string map, string levelName)
    {
        _levelQ1 = null;
        Q1BspData q1 = BspLegacyWorld.ReadQ1(_vfs, map, _vfs.ReadBytes(map));
        Map.UseQ1Map(map, q1);
        if (Map.Collision is { } collision) _effects.SetCollisionWorld(collision);
        // Not drawn yet: a level that cannot be seen is not entered.
        _worldError = $"map \"{map}\" is a Quake 1 format map ({q1.Format}), which this build can collide with but not draw";
        _note(_worldError);
        return false;
    }
}
