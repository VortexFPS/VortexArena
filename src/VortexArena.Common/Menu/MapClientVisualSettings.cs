namespace VortexArena.Common.Menu;

/// <summary>Map-local defaults for the native visual settings that explicitly support mapinfo.</summary>
public readonly record struct MapClientVisualSettings(bool? Bloom, bool? Sky, bool? Fog)
{
    public const string ServerSuggestionsCvar = "cl_accept_server_map_visuals";
    public const string AclCvar = "g_mapinfo_settemp_acl";

    public static MapClientVisualSettings Resolve(MapInfo info, string mode, string acl, Func<string, string?>? readInclude)
    {
        bool? bloom = null, sky = null, fog = null;
        foreach (MapTempDirective directive in info.ClientTempDirectives)
        {
            if (directive.Mode != "all" && directive.Mode != mode)
                continue;

            foreach (var (name, value) in directive.ReadAssignments(readInclude))
            {
                if (name is not ("r_bloom" or "r_sky" or "r_drawfog") || value is not ("0" or "1")
                    || !MapTempDirective.Allows(MapTempDirective.SystemAcl, name) || !MapTempDirective.Allows(acl, name))
                    continue;

                bool enabled = value == "1";
                switch (name)
                {
                    case "r_bloom": bloom = enabled; break;
                    case "r_sky": sky = enabled; break;
                    case "r_drawfog": fog = enabled; break;
                }
            }
        }
        return new MapClientVisualSettings(bloom, sky, fog);
    }

    public MapClientVisualSettings Filter(string acl) => new(
        MapTempDirective.Allows(acl, "r_bloom") ? Bloom : null,
        MapTempDirective.Allows(acl, "r_sky") ? Sky : null,
        MapTempDirective.Allows(acl, "r_drawfog") ? Fog : null);

    public MapClientVisualSettings WithServerSuggestions(MapClientVisualSettings suggestions, bool accept) =>
        accept ? new(suggestions.Bloom ?? Bloom, suggestions.Sky ?? Sky, suggestions.Fog ?? Fog) : this;

    public static bool EffectiveValue(bool? mapDefault, bool preference, bool explicitPreference) =>
        explicitPreference ? preference : mapDefault ?? preference;
}
