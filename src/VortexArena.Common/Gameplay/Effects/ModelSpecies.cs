namespace VortexArena.Common.Gameplay;

public enum ModelSpecies
{
    Human = 0,
    RobotSolid = 1,
    Alien = 2,
    Animal = 3,
    RobotRusty = 4,
    RobotShiny = 5,
    Reserved = 15,
}

public enum GibEffectKind
{
    Splash,
    Hit,
    Chunk,
}

public static class ModelSpeciesEffects
{
    public const string GibSplash = "model_gib_splash";
    private const string SplashPrefix = GibSplash + "_";
    private const string HitPrefix = "model_gib_hit_";
    private const string ChunkPrefix = "model_gib_chunk_";
    private static readonly string[] HitNames = BuildNames(HitPrefix);
    private static readonly string[] ChunkNames = BuildNames(ChunkPrefix);

    public static ModelSpecies Parse(string? species) => species switch
    {
        "robot_solid" => ModelSpecies.RobotSolid,
        "alien" => ModelSpecies.Alien,
        "animal" => ModelSpecies.Animal,
        "robot_rusty" => ModelSpecies.RobotRusty,
        "robot_shiny" => ModelSpecies.RobotShiny,
        "reserved" => ModelSpecies.Reserved,
        _ => ModelSpecies.Human,
    };

    public static string Prefix(ModelSpecies species) => species switch
    {
        ModelSpecies.Alien => "alien_",
        ModelSpecies.RobotSolid or ModelSpecies.RobotRusty or ModelSpecies.RobotShiny => "robot_",
        ModelSpecies.Animal => "animal_",
        ModelSpecies.Reserved => "reserved_",
        _ => "",
    };

    // The by-name effect path carries species without changing the registered human event or the wire layout.
    public static string GibSplashName(ModelSpecies species) => $"{GibSplash}_{(int)species}";
    public static string GibHitName(ModelSpecies species) => ImpactName(HitNames, species);
    public static string GibChunkName(ModelSpecies species) => ImpactName(ChunkNames, species);

    public static bool TryGetGibSpecies(string name, out ModelSpecies species)
    {
        if (string.Equals(name, GibSplash, StringComparison.OrdinalIgnoreCase))
        {
            species = ModelSpecies.Human;
            return true;
        }
        return TryGetSpecies(name, SplashPrefix, out species);
    }

    public static bool TryGetGibEvent(string name, out GibEffectKind kind, out ModelSpecies species)
    {
        if (TryGetSpecies(name, HitPrefix, out species))
        {
            kind = GibEffectKind.Hit;
            return true;
        }
        if (TryGetSpecies(name, ChunkPrefix, out species))
        {
            kind = GibEffectKind.Chunk;
            return true;
        }
        kind = GibEffectKind.Splash;
        return TryGetGibSpecies(name, out species);
    }

    private static bool TryGetSpecies(string name, string prefix, out ModelSpecies species)
    {
        species = ModelSpecies.Human;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(name.AsSpan(prefix.Length), out int value)
            || !Enum.IsDefined((ModelSpecies)value))
            return false;

        species = (ModelSpecies)value;
        return true;
    }

    private static string[] BuildNames(string prefix)
    {
        var names = new string[(int)ModelSpecies.Reserved + 1];
        foreach (ModelSpecies species in Enum.GetValues<ModelSpecies>())
            names[(int)species] = $"{prefix}{(int)species}";
        return names;
    }

    private static string ImpactName(string[] names, ModelSpecies species)
        => (uint)species < (uint)names.Length && names[(int)species] is { } name ? name : names[0];
}
