using VortexArena.Common.Services;

namespace VortexArena.Common.Menu;

/// <summary>Mode-specific point sliders and the match defaults that their overrides replace.</summary>
public sealed record PointLimitSettings(
    string PointCvar, string LeadCvar, int SliderMax, int SliderStep, int DefaultPoints, int DefaultMinutes,
    string? MapTeamsCvar = null, int DefaultTeams = 0)
{
    public const int SliderMin = 200;
    public string? TeamsCvar => MapTeamsCvar is null ? null : MapTeamsCvar + "_override";

    public static PointLimitSettings Mayhem { get; } = new(
        "g_mayhem_point_limit", "g_mayhem_point_leadlimit", 2000, 100, 1000, 15);
    public static PointLimitSettings TeamMayhem { get; } = new(
        "g_tmayhem_point_limit", "g_tmayhem_point_leadlimit", 3000, 100, 1500, 20, "g_tmayhem_teams", 2);
    public static PointLimitSettings KeyHunt { get; } = new(
        "g_keyhunt_point_limit", "g_keyhunt_point_leadlimit", 1500, 50, 1000, 20, "g_keyhunt_teams", 3);

    public static PointLimitSettings? For(string? mode) => mode?.ToLowerInvariant() switch
    {
        "mayhem" => Mayhem,
        "tmayhem" => TeamMayhem,
        "kh" => KeyHunt,
        _ => null,
    };

    /// <summary>Carry the same cvars the menu edits to either listen-server cvar store, including -1 and 0.</summary>
    public void CopyHostOverrides(ICvarService cvars, IDictionary<string, string> overrides)
    {
        Copy(PointCvar);
        Copy(LeadCvar);
        if (TeamsCvar is not null)
            Copy(TeamsCvar);

        void Copy(string name)
        {
            if (cvars.Has(name))
                overrides[name] = cvars.GetString(name);
        }
    }
}
