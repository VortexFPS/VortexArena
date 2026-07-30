// Named particle effect descriptors — the C# successor to QuakeC's EFFECT_* registry
// (common/effects/all.qh + all.inc). Each Effect maps a stable EFFECT_NAME to an effectinfo.txt
// effect name; the network/render layer (CSQC today, Godot later) resolves that name to a particle
// number. On the server side these are emitted by id via EffectEmitter (the analogue of Send_Effect).
//
// Source of truth for the names: Base/.../qcsrc/common/effects/all.inc — the full EFFECT() registry is
// ported in EffectsList.cs (the order/hash must match the engine). The richer effectinfo.txt particle
// *parameters* (color/size/type) are a client-render concern resolved by name, not part of this registry.

using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// One named particle effect, enrolled into <see cref="Effects"/>. Mirrors QuakeC's
/// <c>EFFECT(istrail, NAME, "effectinfo_string")</c> registrable: <see cref="NetName"/> is the
/// effectinfo.txt name, <see cref="IsTrail"/> distinguishes trail effects (which sweep between two
/// points and ignore a count) from point effects (which spawn <c>count</c> particles at one spot).
///
/// <see cref="IRegistered.RegistryName"/> is the stable EFFECT_* identifier (e.g. "EXPLOSION_BIG"),
/// not the effectinfo name, so renaming an effectinfo string doesn't shift the content hash.
/// </summary>
public sealed class Effect : IRegistered
{
    public int RegistryId { get; set; }

    /// <summary>Stable EFFECT_* identifier, e.g. "EXPLOSION_BIG". Drives ordering / content hash.</summary>
    public string Name = "";

    /// <summary>The effectinfo.txt effect name this maps to, e.g. "explosion_big". May be null/empty for EFFECT_Null.</summary>
    public string NetName = "";

    /// <summary>True for trail effects (trailparticles, swept between two points); false for point effects (pointparticles).</summary>
    public bool IsTrail;

    public string RegistryName => Name;

    public Effect() { }

    public Effect(string name, string effectInfoName, bool isTrail)
    {
        Name = name;
        NetName = effectInfoName;
        IsTrail = isTrail;
    }

    public override string ToString() => $"EFFECT_{Name}(\"{NetName}\"{(IsTrail ? ", trail" : "")})";
}

/// <summary>
/// The named-effect catalog — the C# successor to QC's <c>REGISTRY(Effects)</c> / <c>FOREACH(Effects, …)</c>.
/// Self-registering (no attribute bootstrap): definitions are added by <see cref="EffectsList.RegisterAll"/>,
/// which the lead calls once from GameInit. Lookups are by stable EFFECT_* name or by effectinfo name.
/// </summary>
public static class Effects
{
    public static IReadOnlyList<Effect> All => Registry<Effect>.All;
    public static int Count => Registry<Effect>.Count;

    /// <summary>Look up by stable EFFECT_* identifier (e.g. "EXPLOSION_BIG").</summary>
    public static Effect? ByName(string name) => Registry<Effect>.ByName(name);

    /// <summary>Look up by id (network/ordering index).</summary>
    public static Effect ById(int id) => Registry<Effect>.ById(id);

    /// <summary>Content hash over EFFECT_* names in order — client and server must agree (QC registry handshake).</summary>
    public static uint Hash => Registry<Effect>.ContentHash();

    /// <summary>Register one effect (idempotent by name). Used by <see cref="EffectsList"/>.</summary>
    public static Effect Register(Effect e)
    {
        Registry<Effect>.Register(e);
        return e;
    }

    /// <summary>Convenience overload mirroring the QC <c>EFFECT(istrail, name, realname)</c> macro.</summary>
    public static Effect Register(string name, string effectInfoName, bool isTrail = false)
        => Register(new Effect(name, effectInfoName, isTrail));

    /// <summary>
    /// Populate the catalog with every implemented effect (delegates to <see cref="EffectsList.RegisterAll"/>).
    /// The lead calls this once from GameInit. Idempotent (registration is by name).
    /// </summary>
    public static void RegisterAll() => EffectsList.RegisterAll();

    /// <summary>Find the first effect whose effectinfo name matches (QC <c>Send_Effect_</c> path). Linear scan.</summary>
    public static Effect? ByEffectInfoName(string effectInfoName)
    {
        var all = Registry<Effect>.All;
        for (int i = 0; i < all.Count; i++)
            if (string.Equals(all[i].NetName, effectInfoName, StringComparison.Ordinal))
                return all[i];
        return null;
    }

    /// <summary>Deterministic CL/SV ordering (call after RegisterAll, before hashing). QC Registry sort.</summary>
    public static void Sort() => Registry<Effect>.Sort();

    /// <summary>Test support.</summary>
    public static void Clear() => Registry<Effect>.Clear();

    // --- team-variant resolvers (QC EFFECT_FLAG_TOUCH / EFFECT_PASS / EFFECT_CAP switch helpers) ---
    // The QC switches on NUM_TEAM_1..4; in this port those are the Teams.* color codes (Teams.cs).
    // Anything not one of the four known teams falls back to the NEUTRAL variant.

    public static Effect? FlagTouch(int team) => TeamVariant(team, "FLAG_TOUCH");
    public static Effect? Pass(int team) => TeamVariant(team, "PASS");
    public static Effect? Cap(int team) => TeamVariant(team, "CAP");

    private static Effect? TeamVariant(int team, string prefix)
    {
        string suffix = team switch
        {
            Teams.Red => "RED",
            Teams.Blue => "BLUE",
            Teams.Yellow => "YELLOW",
            Teams.Pink => "PINK",
            _ => "NEUTRAL",
        };
        return ByName($"{prefix}_{suffix}");
    }
}
