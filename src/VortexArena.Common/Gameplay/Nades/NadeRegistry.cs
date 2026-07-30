// Port of qcsrc/common/mutators/mutator/nades/nades.qh + all.inc + nade/*.qh (the Nade CLASS + the 12
// REGISTER_NADE definitions) and the nade-type selection helpers in sv_nades.qc:419-528
// (nades_CheckTypes / Nades_FromString / nade_choose_random / Nades_GetType).
//
// QC enrolls nades into a dedicated REGISTRY(Nades, BITS(4)) keyed by m_id. The port keeps this
// self-contained (a plain static catalog like StatusEffectsCatalog) rather than touching the shared
// Registry<T>/GameRegistries bootstrap: a Nade is content-table data (color/projectile/netname/alpha),
// not a hooked mutator, and the per-type behaviour lives in the boom files + NadeBoomRegistry seam.
//
// NADE_TYPE_Null (m_id 0) is the "random nade" sentinel; the random/unknown fallbacks resolve to
// NADE_TYPE_NORMAL exactly as QC does.

using System.Numerics;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay.Nades;

/// <summary>
/// One nade type — the C# successor to QuakeC's <c>CLASS(Nade)</c> (nades.qh) + the per-type subclass
/// ATTRIBs (nade/normal.qh etc.). Carries the id, netname, display name, explosion color, held-nade alpha,
/// the two CSQC projectile model ids, the legacy selection impulse, and the per-type g_nades_&lt;type&gt;
/// enable cvar (used by <see cref="NadeRegistry.CheckType"/>). Behaviour (the boom) is NOT here — it is
/// registered per-type into <see cref="NadeBoomRegistry"/> by the boom files.
/// </summary>
public sealed class NadeDef
{
    /// <summary>QC <c>m_id</c> — the Nades registry id (0 = Null/random sentinel). Stable across the run.</summary>
    public int Id;
    /// <summary>QC <c>netname</c> — the stable string name (e.g. "normal", "napalm", "ice"; Null = "random").</summary>
    public string NetName = "random";
    /// <summary>QC <c>m_name</c> — the localized display name ("Grenade", "Napalm grenade", …).</summary>
    public string Name = "Grenade";
    /// <summary>QC <c>m_color</c> — the explosion / colormod tint vector.</summary>
    public Vector3 Color = Vector3.Zero;
    /// <summary>QC <c>m_alpha</c> — the held-nade alpha (1 for most; the veil nade is 0.45).</summary>
    public float Alpha = 1f;
    /// <summary>QC <c>m_projectile[2]</c> — the CSQC projectile model ids (flying, burning). Render-only.</summary>
    public int Projectile0;
    public int Projectile1;
    /// <summary>QC <c>impulse</c> — legacy numeric selection id (1=normal, 2=napalm, …).</summary>
    public int Impulse;
    /// <summary>The <c>g_nades_&lt;type&gt;</c> enable cvar name (null for normal/null which are always allowed).</summary>
    public string? EnableCvar;
    /// <summary>QC <c>NADE_TYPE_MONSTER</c> also requires <c>g_monsters</c>; this flags that extra gate.</summary>
    public bool RequiresMonsters;
}

/// <summary>
/// The Nade catalog — the C# successor to the QC Nades registry (nades.qh) populated from all.inc. A static
/// list built once by <see cref="RegisterAll"/> (invoked from <see cref="NadesMutator"/> so it stays
/// self-contained). Mirrors the QC selection helpers used by the throw + bonus paths.
/// </summary>
public static class NadeRegistry
{
    private static readonly List<NadeDef> _all = new();
    private static readonly Dictionary<string, NadeDef> _byName = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, NadeDef> _byId = new();
    private static bool _registered;

    // QC nades.qh: PROJECTILE_NADE = 71, PROJECTILE_NADE_BURN = 72; the rest are in the per-type .qh.
    private const int ProjNade = 71, ProjNadeBurn = 72;
    private const int ProjNapalm = 73, ProjNapalmBurn = 74;
    private const int ProjIce = 76, ProjIceBurn = 77;
    private const int ProjTranslocate = 78;
    private const int ProjSpawn = 79;
    private const int ProjHeal = 80, ProjHealBurn = 81;
    private const int ProjMonster = 82, ProjMonsterBurn = 83;
    private const int ProjEntrap = 84, ProjEntrapBurn = 85;
    private const int ProjVeil = 86, ProjVeilBurn = 87;
    private const int ProjAmmo = 88, ProjAmmoBurn = 89;
    private const int ProjDarkness = 90, ProjDarknessBurn = 91;

    public static IReadOnlyList<NadeDef> All => _all;

    // Well-known nade types (resolved after RegisterAll), the analogue of QC's NADE_TYPE_* globals.
    public static NadeDef Null => ById(0) ?? _fallbackNull;
    public static NadeDef? Normal => ByName("normal");
    public static NadeDef? Napalm => ByName("napalm");
    public static NadeDef? Ice => ByName("ice");
    public static NadeDef? Translocate => ByName("translocate");
    public static NadeDef? Spawn => ByName("spawn");
    public static NadeDef? Heal => ByName("heal");
    public static NadeDef? Monster => ByName("pokenade");
    public static NadeDef? Entrap => ByName("entrap");
    public static NadeDef? Veil => ByName("veil");
    public static NadeDef? Ammo => ByName("ammo");
    public static NadeDef? Darkness => ByName("darkness");

    private static readonly NadeDef _fallbackNull = new() { Id = 0, NetName = "random", Name = "Grenade" };

    public static NadeDef? ByName(string name) => _byName.TryGetValue(name, out var d) ? d : null;
    public static NadeDef? ById(int id) => _byId.TryGetValue(id, out var d) ? d : null;

    /// <summary>
    /// Mirror of the QC Nades registry (REGISTER_NADE across all.inc). Idempotent. Ids are assigned in the
    /// QC declaration order (Null=0, NORMAL=1, NAPALM=2, …) so STAT(NADE_BONUS_TYPE) and the impulse mapping
    /// agree with the originals.
    /// </summary>
    public static void RegisterAll()
    {
        if (_registered) return;
        _registered = true;

        // NADE_TYPE_Null — the random sentinel (REGISTER(Nades, NADE_TYPE, Null, …) in nades.qh).
        Add(new NadeDef { Id = 0, NetName = "random", Name = "Grenade" });

        // The 11 real nade types, in all.inc declaration order (their m_id is 1..11).
        // colors/alphas/impulses copied from the per-type .qh ATTRIBs.
        Add(new NadeDef
        {
            Id = 1, NetName = "normal", Name = "Grenade", Color = new Vector3(1, 1, 1),
            Projectile0 = ProjNade, Projectile1 = ProjNadeBurn, Impulse = 1, EnableCvar = null,
        });
        Add(new NadeDef
        {
            Id = 2, NetName = "napalm", Name = "Napalm grenade", Color = new Vector3(1, 0.5f, 0),
            Projectile0 = ProjNapalm, Projectile1 = ProjNapalmBurn, Impulse = 2, EnableCvar = "g_nades_napalm",
        });
        Add(new NadeDef
        {
            Id = 3, NetName = "ice", Name = "Ice grenade", Color = new Vector3(0, 0.66f, 1),
            Projectile0 = ProjIce, Projectile1 = ProjIceBurn, Impulse = 3, EnableCvar = "g_nades_ice",
        });
        Add(new NadeDef
        {
            Id = 4, NetName = "translocate", Name = "Translocate grenade", Color = new Vector3(1, 0, 1),
            Projectile0 = ProjTranslocate, Projectile1 = ProjTranslocate, Impulse = 4, EnableCvar = "g_nades_translocate",
        });
        Add(new NadeDef
        {
            Id = 5, NetName = "spawn", Name = "Spawn grenade", Color = new Vector3(1, 0.9f, 0),
            Projectile0 = ProjSpawn, Projectile1 = ProjSpawn, Impulse = 5, EnableCvar = "g_nades_spawn",
        });
        Add(new NadeDef
        {
            Id = 6, NetName = "heal", Name = "Heal grenade", Color = new Vector3(1, 0, 0),
            Projectile0 = ProjHeal, Projectile1 = ProjHealBurn, Impulse = 6, EnableCvar = "g_nades_heal",
        });
        // QC netname is "pokenade" (monster nade); requires both g_nades_pokenade and g_monsters.
        Add(new NadeDef
        {
            Id = 7, NetName = "pokenade", Name = "Monster grenade", Color = new Vector3(0.1f, 0.65f, 0),
            Projectile0 = ProjMonster, Projectile1 = ProjMonsterBurn, Impulse = 7,
            EnableCvar = "g_nades_pokenade", RequiresMonsters = true,
        });
        Add(new NadeDef
        {
            Id = 8, NetName = "entrap", Name = "Entrap grenade", Color = new Vector3(0.4f, 0.85f, 0.15f),
            Projectile0 = ProjEntrap, Projectile1 = ProjEntrapBurn, Impulse = 8, EnableCvar = "g_nades_entrap",
        });
        Add(new NadeDef
        {
            Id = 9, NetName = "veil", Name = "Veil grenade", Color = new Vector3(0.65f, 0.85f, 0.65f),
            Alpha = 0.45f, Projectile0 = ProjVeil, Projectile1 = ProjVeilBurn, Impulse = 9, EnableCvar = "g_nades_veil",
        });
        Add(new NadeDef
        {
            Id = 10, NetName = "ammo", Name = "Ammo grenade", Color = new Vector3(0.33f, 0.33f, 1),
            Projectile0 = ProjAmmo, Projectile1 = ProjAmmoBurn, Impulse = 10, EnableCvar = "g_nades_ammo",
        });
        Add(new NadeDef
        {
            Id = 11, NetName = "darkness", Name = "Darkness grenade", Color = new Vector3(0.23f, 0, 0.23f),
            Projectile0 = ProjDarkness, Projectile1 = ProjDarknessBurn, Impulse = 11, EnableCvar = "g_nades_darkness",
        });
    }

    private static void Add(NadeDef d)
    {
        if (_byName.ContainsKey(d.NetName)) return;
        _all.Add(d);
        _byName[d.NetName] = d;
        _byId[d.Id] = d;
    }

    // ===================================================================================================
    //  Type selection (sv_nades.qc:419-528)
    // ===================================================================================================

    /// <summary>
    /// Port of <c>nades_CheckTypes</c> (sv_nades.qc:419): if <paramref name="ntype"/> is allowed by its
    /// per-type g_nades cvar, return it; the Null sentinel passes through (random); anything disabled or
    /// unknown falls back to NORMAL.
    /// </summary>
    public static NadeDef CheckType(NadeDef? ntype)
    {
        if (ntype is null) return Normal ?? Null;
        if (ntype.Id == 0) return Null; // NADE_TYPE_Null — the random sentinel passes through
        if (IsAllowed(ntype)) return ntype;
        return Normal ?? Null; // default to NORMAL for disabled/unknown types
    }

    /// <summary>True when this nade type's enable cvar(s) are set (QC the NADE_TYPE_CHECK macro body).</summary>
    public static bool IsAllowed(NadeDef d)
    {
        if (d.Id == 0) return true;          // Null is always "allowed" (random sentinel)
        if (d.EnableCvar is null) return true; // normal: no gate
        bool on = Cvar(d.EnableCvar) != 0f;
        if (d.RequiresMonsters) on = on && Cvar("g_monsters") != 0f;
        return on;
    }

    /// <summary>
    /// Port of <c>Nades_FromString</c> (sv_nades.qc:513): resolve a nade by netname OR by its impulse number
    /// as a string ("2" → napalm). Returns the Null sentinel when unmatched.
    /// </summary>
    public static NadeDef FromString(string? ntype)
    {
        if (string.IsNullOrEmpty(ntype)) return Null;
        foreach (var it in _all)
        {
            if (it.Id == 0) continue;
            if (it.NetName == ntype || it.Impulse.ToString(System.Globalization.CultureInfo.InvariantCulture) == ntype)
                return it;
        }
        return Null;
    }

    /// <summary>Port of <c>nade_choose_random</c> (sv_nades.qc:502): pick a random ALLOWED non-Null nade id.</summary>
    public static int ChooseRandom()
    {
        var allowed = new List<NadeDef>();
        foreach (var it in _all)
            if (it.Id != 0 && CheckType(it) == it) // this nade type is allowed
                allowed.Add(it);
        if (allowed.Count == 0) return Normal?.Id ?? 0;
        return allowed[Prandom.RangeInt(0, allowed.Count)].Id;
    }

    /// <summary>
    /// Port of <c>Nades_GetType</c> (sv_nades.qc:519): "random"/"0" → a random allowed type; otherwise the
    /// named/impulse type; an unmatched name falls back to NORMAL.
    /// </summary>
    public static NadeDef GetType(string? ntype)
    {
        NadeDef def = (ntype == "random" || ntype == "0")
            ? (ById(ChooseRandom()) ?? Null)
            : FromString(ntype);
        return def.Id == 0 ? (Normal ?? Null) : def;
    }

    private static float Cvar(string name) => Api.Services is null ? 0f : Api.Cvars.GetFloat(name);
}

/// <summary>
/// The nade deathtype tags (QC DEATH_NADE / DEATH_NADE_NAPALM / DEATH_NADE_ICE / DEATH_NADE_HEAL /
/// DEATH_NADE_DARKNESS, common/mutators/mutator/nades/_mod / the per-type .qh). In this port a deathtype is
/// a plain string tag (see <see cref="Damage.DeathTypes"/>), so these are string constants compatible with
/// the <c>DeathTypes.BaseOf</c>/obituary scheme. Kept in the Nades namespace (rather than editing the shared
/// DeathTypes.cs) so the nade subsystem — including part B's boom files — owns its own deathtype constants.
/// </summary>
public static class NadeDeathTypes
{
    /// <summary>QC DEATH_NADE — a normal nade explosion / the held-nade throw deathtype.</summary>
    public const string Nade = "nade";
    /// <summary>QC DEATH_NADE_NAPALM — napalm-nade fire damage.</summary>
    public const string Napalm = "nade_napalm";
    /// <summary>QC DEATH_NADE_ICE — ice-nade explode (when g_nades_ice_explode) / ice-field damage.</summary>
    public const string Ice = "nade_ice";
    /// <summary>QC DEATH_NADE_HEAL — heal-nade harm-to-foe damage.</summary>
    public const string Heal = "nade_heal";
    /// <summary>QC DEATH_NADE_DARKNESS — darkness-nade explode (when g_nades_darkness_explode).</summary>
    public const string Darkness = "nade_darkness";
}
