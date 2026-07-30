using System.Collections.Generic;

namespace VortexArena.Game.Client;

/// <summary>
/// Client-side map of a weapon's PRIMARY fire sound, for local fire prediction (cl_predictfire): the client
/// plays this the instant the local player fires (on the refire clock) and drops the matching networked copy of
/// its OWN shot so it isn't doubled. Mirrors the hardcoded <c>Api.Sound.Play(actor, …)</c> sample in each
/// weapon's <c>Attack()</c> (src/VortexArena.Common/Gameplay/Weapons/*.cs) — the server stays the single
/// authority that everyone ELSE hears; this is purely the local-feedback twin. A weapon absent here (Arc/Tuba
/// loops, Hook grapple) simply isn't predicted: its networked fire sound plays normally, unsuppressed.
///
/// Keep in lockstep with the weapons' fire sounds; a stale entry just means a slightly-late local sound for that
/// gun (the networked one still plays for everyone else).
/// </summary>
public static class WeaponFireSounds
{
    // weapon NetName -> primary fire sample (verbatim from each weapon's Attack()).
    private static readonly Dictionary<string, string> Primary = new()
    {
        ["blaster"]     = "weapons/lasergun_fire.wav",
        ["shotgun"]     = "weapons/shotgun_fire.wav",
        ["machinegun"]  = "weapons/uzi_fire.wav",
        ["vortex"]      = "weapons/nexfire.wav",
        ["devastator"]  = "weapons/rocket_fire.wav",
        ["mortar"]      = "weapons/grenade_fire.wav",
        ["electro"]     = "weapons/electro_fire.wav",
        ["crylink"]     = "weapons/crylink_fire.wav",
        ["hagar"]       = "weapons/hagar_fire.wav",
        ["vaporizer"]   = "weapons/minstanexfire.wav",
        ["rifle"]       = "weapons/campingrifle_fire.wav",
        ["hlac"]        = "weapons/lasergun_fire.wav",
        ["fireball"]    = "weapons/fireball_fire.wav",
        ["minelayer"]   = "weapons/mine_fire.wav",
        ["seeker"]      = "weapons/seeker_fire.wav",
        ["porto"]       = "porto/fire.wav",
        // Overkill variants (best-effort names; an unknown NetName just falls back to the networked sound).
        ["okmachinegun"] = "weapons/uzi_fire.wav",
        ["okhmg"]        = "weapons/uzi_fire.wav",
        ["oknex"]        = "weapons/nexfire.wav",
        ["okrpc"]        = "weapons/rocket_fire.wav",
        ["okshotgun"]    = "weapons/shotgun_fire.wav",
    };

    // The distinct sample set, for the "drop my own predicted shot" suppression filter (keyed by sample, since
    // several weapons share one — uzi/lasergun/nexfire/etc.). Built once from Primary's values.
    private static readonly HashSet<string> Samples = BuildSampleSet();

    private static HashSet<string> BuildSampleSet()
    {
        var s = new HashSet<string>();
        foreach (string v in Primary.Values)
            s.Add(v);
        return s;
    }

    /// <summary>The primary fire sample for a weapon NetName, or "" if it isn't predicted (loops/grapple/unknown).</summary>
    public static string PrimaryFor(string netName)
        => netName is not null && Primary.TryGetValue(netName, out string? s) ? s : "";

    /// <summary>True if <paramref name="sample"/> is a predicted primary fire sound (so the local player's own
    /// networked copy should be dropped — they played it locally).</summary>
    public static bool IsPredicted(string sample)
        => sample is not null && Samples.Contains(sample);
}
