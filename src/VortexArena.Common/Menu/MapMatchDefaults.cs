using System.Globalization;

namespace VortexArena.Common.Menu;

/// <summary>The match limit fields from one modern <c>gametype</c> directive.</summary>
public readonly record struct MapMatchDefaults(float? Time, float? Score, float? Lead, float? TeamScore, float? QualifyingTime)
{
    public float? Teams { get; init; }

    internal static MapMatchDefaults Parse(string options)
    {
        MapMatchDefaults limits = default;
        foreach (string option in options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = option.IndexOf('=');
            if (equals < 0 || !float.TryParse(option.AsSpan(equals + 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                continue;
            limits = option[..equals] switch
            {
                "timelimit" => limits with { Time = value },
                "leadlimit" => limits with { Lead = value },
                "pointlimit" or "fraglimit" or "lives" or "laplimit" or "caplimit" => limits with { Score = value },
                "teampointlimit" or "teamlaplimit" => limits with { TeamScore = value },
                "qualifying_timelimit" => limits with { QualifyingTime = value },
                "teams" => limits with { Teams = value },
                _ => limits,
            };
        }
        return limits;
    }
}
