using System.Collections.Generic;

namespace VortexArena.Game.Client;

/// <summary>
/// Client-side map of the sound a weapon's PRIMARY press makes, for local fire prediction (cl_predictfire): the
/// client plays this the instant the local player fires (on the refire clock) and drops the matching networked
/// copy of that same shot (<see cref="VortexArena.Net.PredictedFireSoundLedger"/>) so it isn't doubled. Mirrors
/// the <c>Api.Sound.Play(actor, …)</c> sample and volume in each weapon's attack
/// (src/VortexArena.Common/Gameplay/Weapons/*.cs) — the server stays the single authority that everyone ELSE
/// hears; this is purely the local-feedback twin. A weapon absent here (Arc/Tuba loops, Hook grapple) simply
/// isn't predicted: its networked fire sound plays normally, unsuppressed.
///
/// Keep in lockstep with the weapons' fire sounds. A wrong entry is audible: the predicted sample plays on
/// every press, and the real one then plays too because nothing pairs with it.
/// </summary>
public static class WeaponFireSounds
{
    // weapon NetName -> the sample its primary press plays first (verbatim from each weapon's attack).
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
        // The Fireball's primary press plays the charge-up cue (fireball.qc W_Fireball_Attack1_Frame0,
        // SND_FIREBALL_PREFIRE2); the launch (SND_FIREBALL_FIRE2) follows four animtimes later from the server.
        // fireball_fire is the SECONDARY (firemine) sound — predicting it here played the secondary's sound on
        // every primary press and silenced the real secondary.
        ["fireball"]    = "weapons/fireball_prefire2.wav",
        ["minelayer"]   = "weapons/mine_fire.wav",
        // Seeker: see PrimaryFor — the sample depends on g_balance_seeker_type.
        ["seeker"]      = "weapons/tag_fire.wav",
        ["porto"]       = "porto/fire.wav",
        // Overkill variants (best-effort names; an unknown NetName just falls back to the networked sound).
        ["okmachinegun"] = "weapons/uzi_fire.wav",
        ["okhmg"]        = "weapons/uzi_fire.wav",
        ["oknex"]        = "weapons/nexfire.wav",
        ["okrpc"]        = "weapons/rocket_fire.wav",
        ["okshotgun"]    = "weapons/shotgun_fire.wav",
    };

    // Weapons whose fire sound is played at other than the default volume, so the predicted copy sounds the
    // same as the server's copy of the same shot.
    private static readonly Dictionary<string, float> Volume = new();

    // Every sample a primary press can predict, for the "is this one of my predicted sounds" filter (keyed by
    // sample, since several weapons share one — uzi/lasergun/nexfire/etc.).
    private static readonly HashSet<string> Samples = BuildSampleSet();

    private static HashSet<string> BuildSampleSet()
    {
        var s = new HashSet<string>();
        foreach (string v in Primary.Values)
            s.Add(v);
        s.Add("weapons/seeker_fire.wav"); // the g_balance_seeker_type 1 primary (missiles)
        return s;
    }

    /// <summary>The primary fire sample for a weapon, or "" if it isn't predicted (loops/grapple/unknown).</summary>
    public static string PrimaryFor(VortexArena.Common.Gameplay.Weapon w)
    {
        if (w is null)
            return "";
        // seeker.qc wr_think: type 0 (the stock balance) fires the tag dart on primary (SND_TAG_FIRE) and the
        // missiles follow from the tag; type 1 fires the missiles (SND_SEEKER_FIRE) on primary.
        if (w is VortexArena.Common.Gameplay.Seeker seeker)
            return seeker.Type == 1 ? "weapons/seeker_fire.wav" : "weapons/tag_fire.wav";
        return PrimaryFor(w.NetName);
    }

    /// <summary>The primary fire sample for a weapon NetName, or "" if it isn't predicted (loops/grapple/unknown).</summary>
    public static string PrimaryFor(string netName)
        => netName is not null && Primary.TryGetValue(netName, out string? s) ? s : "";

    /// <summary>The volume the server plays this weapon's primary fire sound at (1 unless the weapon passes its own).</summary>
    public static float VolumeFor(string netName)
        => netName is not null && Volume.TryGetValue(netName, out float v) ? v : 1f;

    /// <summary>True if <paramref name="sample"/> is one a primary press can predict — only such sounds from the
    /// local player are checked against the prediction ledger; everything else always plays.</summary>
    public static bool IsPredicted(string sample)
        => sample is not null && Samples.Contains(sample);
}
