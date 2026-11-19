using System;
using VortexArena.Common.Config;

namespace VortexArena.Common.Menu;

/// <summary>Stock map-vote picture search paths from server/mapvoting.qc and client/mapvoting.qc.</summary>
public static class MapVotePictures
{
    public static string Map(string name, string directories, Func<string, bool> exists)
    {
        if (name.Length == 0)
            return "";
        var candidates = ConfigInterpreter.Tokenize(directories);
        if (candidates.Count == 0)
            candidates.AddRange(new[] { "maps", "levelshots" });

        // MapVote_AddVotable checks at most four directories and selects the first when none has art.
        for (int i = 0; i < System.Math.Min(candidates.Count, 4); ++i)
        {
            string picture = $"{candidates[i]}/{name}";
            if (exists(picture))
                return picture;
        }
        return $"{candidates[0]}/{name}";
    }

    public static string Gametype(string name, string baseType, string menuSkin, Func<string, bool> exists)
    {
        string picture = $"gfx/menu/{menuSkin}/gametype_{name}";
        if (exists(picture))
            return picture;
        picture = $"gfx/menu/wickedx/gametype_{name}";
        if (exists(picture) || baseType.Length == 0)
            return picture;
        picture = $"gfx/menu/{menuSkin}/gametype_{baseType}";
        return exists(picture) ? picture : $"gfx/menu/wickedx/gametype_{baseType}";
    }
}
