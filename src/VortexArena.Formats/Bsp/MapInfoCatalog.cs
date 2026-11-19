using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using VortexArena.Common.Menu;
using VortexArena.Common.Services;
using VortexArena.Formats.Vfs;
using VmapEntity = VortexArena.Formats.Vmap.VmapEntity;

namespace VortexArena.Formats.Bsp;

/// <summary>Metadata shared by the native catalog, map details and hosted rotation.</summary>
public sealed class MapInfoCatalog
{
    private readonly VirtualFileSystem? _vfs;
    private readonly int _q3Compat;
    private readonly MapInfoBackend _backend;
    private readonly ConcurrentDictionary<string, Lazy<MapInfo>> _entries = new(StringComparer.OrdinalIgnoreCase);

    public MapInfoCatalog(VirtualFileSystem? vfs, int q3Compat = 1)
    {
        _vfs = vfs;
        _q3Compat = q3Compat;
        _backend = new MapInfoBackend(ReadText, _ => false, Generate, q3Compat == 1 ? ReadArena : null);
    }

    // Concurrent callers share the same scan. Recreate the catalog after a VFS rescan.
    public MapInfo Get(string map) => _entries.GetOrAdd(map, name => new Lazy<MapInfo>(() => _backend.Parse(name))).Value;

    public bool IsLoaded(string map) => _entries.TryGetValue(map, out var entry) && entry.IsValueCreated;

    public IReadOnlyList<string> ForGametype(IEnumerable<string> maps, string? mode, ICvarService cvars, bool inMenu = false)
    {
        var settings = MapForcedSupport.FromCvars(cvars);
        var required = string.IsNullOrWhiteSpace(mode) ? MapFeatures.None : MapInfo.RequiredFeatures(mode, cvars);
        var kept = new List<string>();
        foreach (string map in maps)
        {
            MapInfo info = Get(map);
            if (info.CanList(cvars, inMenu) && (info.Features & required) == required &&
                (string.IsNullOrWhiteSpace(mode) || info.SupportsInCatalog(mode, settings)))
                kept.Add(map);
        }
        return kept;
    }

    private string? ReadText(string path)
    {
        if (_vfs?.Exists(path) != true)
            return null;
        try { return _vfs.ReadText(path); }
        catch (AssetParseException) { return null; }
    }

    private bool ReadArena(MapInfo info)
    {
        string path = $"maps/{info.BspName}.bsp";
        return _vfs?.Exists(path) == true && ArenaMapInfo.Read(_vfs, info, path);
    }

    public static bool HasQ3Companion(VirtualFileSystem vfs, string bspPath) => ArenaMapInfo.HasCompanion(vfs, bspPath);

    // _MapInfo_Generate and the gametype callbacks at Base 863cd3e84.
    private bool Generate(MapInfo info)
    {
        string path = $"maps/{info.BspName}.bsp";
        if (_vfs?.Exists(path) != true)
            return false;

        bool hasArena = _q3Compat >= 2 && ReadArena(info);
        if (hasArena && _q3Compat == 3)
            return true;

        IReadOnlyList<IReadOnlyDictionary<string, string>> entities;
        try { entities = BspMapSource.ReadEntities(_vfs, path); }
        catch (AssetParseException) { return hasArena; }

        bool foundWorldspawn = false;
        bool hasRacePlaces = false;
        bool isQ3Defrag = false;
        int spawnpoints = 0;
        Vector3 mins = Vector3.Zero;
        Vector3 maxs = Vector3.Zero;
        HashSet<string> modes = info.SupportedGametypes;

        foreach (var entity in entities)
        {
            string classname = entity.GetValueOrDefault("classname", "");
            if (!foundWorldspawn)
            {
                if (classname != "worldspawn")
                    continue;
                foundWorldspawn = true;
                if (info.Title.Length == 0)
                    info.Title = entity.GetValueOrDefault("message", "");
                info.Author = entity.GetValueOrDefault("author", info.Author);
                info.Description = entity.GetValueOrDefault("_description", info.Description);
                foreach (var (key, value) in entity)
                    if (key is "music" or "noise")
                        info.CdTrack = value;
                continue;
            }

            if (VmapEntity.TryParseVector(entity.GetValueOrDefault("origin"), out Vector3 origin)
                && float.IsFinite(origin.X) && float.IsFinite(origin.Y) && float.IsFinite(origin.Z))
            {
                mins = Vector3.Min(mins, origin);
                maxs = Vector3.Max(maxs, origin);
            }
            if (!hasRacePlaces && float.TryParse(entity.GetValueOrDefault("race_place"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float racePlace) && racePlace > 0)
                hasRacePlaces = true;

            switch (classname)
            {
                case "info_player_team1":
                case "info_player_team2":
                case "info_player_start":
                case "info_player_deathmatch":
                    spawnpoints++;
                    break;
                case "target_startTimer": modes.Add("cts"); break;
                case "target_stopTimer": isQ3Defrag = true; break;
                case "trigger_race_checkpoint": modes.Add("rc"); break;
                case "target_assault_roundend": modes.Add("as"); break;
                case "item_flag_team2":
                case "team_CTF_blueflag": modes.Add("ctf"); break;
                case "dom_controlpoint":
                case "team_dom_point": modes.Add("dom"); break;
                case "invasion_spawnpoint": modes.Add("inv"); break;
                case "onslaught_generator": modes.Add("ons"); break;
                case "trigger_music":
                case "target_music": info.CdTrack = ""; break;
                case "weapon_nex":
                case "weapon_railgun": break;
                default:
                    if (classname.StartsWith("ball", StringComparison.Ordinal) || classname.StartsWith("nexball_", StringComparison.Ordinal))
                        modes.Add("nb");
                    else if (classname.StartsWith("weapon_", StringComparison.Ordinal))
                        info.Features |= MapFeatures.Weapons;
                    else if (classname.StartsWith("turret_", StringComparison.Ordinal))
                        info.Features |= MapFeatures.Turrets;
                    else if (classname.StartsWith("vehicle_", StringComparison.Ordinal))
                        info.Features |= MapFeatures.Vehicles;
                    else if (classname.StartsWith("monster_", StringComparison.Ordinal))
                        info.Features |= MapFeatures.Monsters;
                    break;
            }
        }

        if (!foundWorldspawn)
            return hasArena;

        if (info.CdTrack.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            || info.CdTrack.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            info.CdTrack = info.CdTrack[..^4];

        // QC uses the diagonal of the entity-origin extents, including zero. Brush model bounds
        // may include distant geometry that has no effect on the generation predicates.
        float diameter = (maxs - mins).Length();
        info.Diameter = diameter;
        if (!isQ3Defrag && !modes.Contains("ctf") && !modes.Contains("as") && !modes.Contains("nb") && !modes.Contains("rc"))
        {
            modes.UnionWith(new[] { "dm", "ka", "lms", "mayhem", "surv", "tmayhem" });
            if (diameter < 3250)
                modes.Add("duel");
            if (spawnpoints >= 8 && diameter > 3250)
                modes.UnionWith(new[] { "ca", "freezetag", "tdm" });
            if (spawnpoints >= 8 && diameter > 4096)
                modes.Add("tka");
            if (spawnpoints >= 12 && diameter > 5120)
                modes.Add("kh");
        }

        // Race is a two-base mode before this demotion and still suppresses generic modes.
        if (!hasRacePlaces && modes.Remove("rc"))
            modes.Add("cts");
        return true;
    }
}
