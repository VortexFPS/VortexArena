using System.Reflection;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

// Per-category catalog accessors (the C# successors to QC's FOREACH(Weapons, …) targets).

public static class Weapons
{
    public static IReadOnlyList<Weapon> All => Registry<Weapon>.All;
    public static int Count => Registry<Weapon>.Count;
    public static Weapon? ByName(string name) => Registry<Weapon>.ByName(name);
    public static Weapon ById(int id) => Registry<Weapon>.ById(id); // RegistryId == index; caller guards 0 ≤ id < Count
    public static uint Hash => Registry<Weapon>.ContentHash();

    /// <summary>
    /// Re-seed every weapon's balance block from the live <c>g_balance_*</c> cvars (QC re-reading WEP_CVAR
    /// autocvars). Call after the config interpreter loads a balance set so weapons pick up the real values
    /// (and again if the balance changes at runtime, e.g. a ruleset vote that exec's a different bal-wep cfg).
    /// </summary>
    public static void ConfigureAll()
    {
        foreach (Weapon w in Registry<Weapon>.All)
            w.Configure();
    }

    // ---- central NetName→ammo-type / superweapon registry (QC the weapon ATTRIBs) ----

    /// <summary>QC <c>w.ammo_type</c>: the resource a weapon consumes (RES_NONE for ammo-less weapons / unknown name).</summary>
    public static ResourceType AmmoTypeOf(string netName) => ByName(netName)?.AmmoType ?? ResourceType.None;

    /// <summary>QC <c>w.m_wepset &amp; WEPSET_SUPERWEAPONS</c>: is this weapon a timed superweapon?</summary>
    public static bool IsSuperWeapon(string netName) => ByName(netName)?.IsSuperWeapon ?? false;

    /// <summary>The superweapon set (QC WEPSET_SUPERWEAPONS) — every weapon with WEP_FLAG_SUPERWEAPON.</summary>
    public static IEnumerable<Weapon> Superweapons
    {
        get { foreach (var w in Registry<Weapon>.All) if (w.IsSuperWeapon) yield return w; }
    }

    /// <summary>QC <c>STAT(WEAPONS) &amp; WEPSET_SUPERWEAPONS</c>: does this owned-weapon set contain any superweapon?</summary>
    public static bool OwnsAnySuperWeapon(IEnumerable<string> ownedNetNames)
    {
        foreach (string n in ownedNetNames)
            if (IsSuperWeapon(n)) return true;
        return false;
    }
}

public static class Items
{
    public static IReadOnlyList<Pickup> All => Registry<Pickup>.All;
    public static int Count => Registry<Pickup>.Count;
    public static Pickup? ByName(string name) => Registry<Pickup>.ByName(name);
    public static uint Hash => Registry<Pickup>.ContentHash();
}

public static class Mutators
{
    public static IReadOnlyList<MutatorBase> All => Registry<MutatorBase>.All;
    public static int Count => Registry<MutatorBase>.Count;
    public static MutatorBase? ByName(string name) => Registry<MutatorBase>.ByName(name);
    public static uint Hash => Registry<MutatorBase>.ContentHash();
}

public static class GameTypes
{
    public static IReadOnlyList<GameType> All => Registry<GameType>.All;
    public static int Count => Registry<GameType>.Count;
    public static GameType? ByName(string name) => Registry<GameType>.ByName(name);
    public static uint Hash => Registry<GameType>.ContentHash();
}

/// <summary>
/// Populates the registries. The body of <see cref="Bootstrap"/> is the source-generated
/// <c>GeneratedRegistrations.RegisterAll()</c> (emitted into this assembly by <c>XonoticGodot.SourceGen</c>
/// from the <c>[Weapon]</c>/<c>[Item]</c>/<c>[Mutator]</c>/<c>[GameType]</c>/<c>[Monster]</c>/<c>[Turret]</c>/
/// <c>[Vehicle]</c> markers — ADR-0003, the C# successor to QC's REGISTER_* / [[accumulate]] compile-time
/// registration, lib/registry.qh). A reflection scan remains ONLY for explicitly-passed extra (mod)
/// assemblies; registrable content in the port itself must live in <c>XonoticGodot.Common</c>.
/// Bootstrap is idempotent (the <c>_done</c> flag; use <see cref="Reset"/> in tests to re-run it).
/// </summary>
public static class GameRegistries
{
    private static bool _done;

    public static void Bootstrap(params Assembly[] extraAssemblies)
    {
        if (_done) return;
        _done = true;

        // Compile-time registration tables (no reflection). RegisterAll() also Sort()s every touched
        // catalog, so after this call the registries are already in their deterministic CL/SV order
        // (ordinal RegistryName — same final order the old AppDomain reflection scan produced).
        GeneratedRegistrations.RegisterAll();

        // The sound catalog (SoundsList → Sounds registry). NOT one of the [attribute] marker catalogs, so the
        // generator doesn't emit it — register it here so Sounds.All is populated at process boot like the others
        // (GameInit also calls this; it's idempotent). Without this it was empty until a world booted, so anything
        // reading Sounds.All at the MENU — e.g. the game-load asset warm — saw zero sounds.
        Sounds.RegisterAll();

        // Extension hook: mod/expansion assemblies passed explicitly still register via reflection
        // (the generator only sees Common's compilation). No caller in the port passes any today.
        if (extraAssemblies.Length > 0)
        {
            foreach (var asm in extraAssemblies)
            {
                foreach (var t in SafeGetTypes(asm))
                {
                    if (t is null || t.IsAbstract) continue;
                    if (t.GetCustomAttribute<GameRegistryAttribute>() is null) continue;

                    object? inst;
                    try { inst = Activator.CreateInstance(t); }
                    catch { continue; }

                    switch (inst)
                    {
                        case Weapon w: Registry<Weapon>.Register(w); break;
                        case Pickup p: Registry<Pickup>.Register(p); break;
                        case MutatorBase m: Registry<MutatorBase>.Register(m); break;
                        case GameType g: Registry<GameType>.Register(g); break;
                        case Monster mo: Registry<Monster>.Register(mo); break;
                        case Turret tu: Registry<Turret>.Register(tu); break;
                        case Vehicle ve: Registry<Vehicle>.Register(ve); break;
                    }
                }
            }

            // Re-sort: the extras were appended after the generated tables' Sort().
            Registry<Weapon>.Sort();
            Registry<Pickup>.Sort();
            Registry<MutatorBase>.Sort();
            Registry<GameType>.Sort();
            Registry<Monster>.Sort();
            Registry<Turret>.Sort();
            Registry<Vehicle>.Sort();
        }

        // Seed each weapon's balance block from its g_balance_* cvars (QC W_PROPS at progs init). At this point
        // no .cfg is loaded yet, so this stamps the stock fallbacks; Weapons.ConfigureAll() re-runs it after the
        // config interpreter loads the real balance table. Without this, every weapon's balance struct stays at
        // its zero default and weapons fire with no damage/speed. Must stay AFTER the Sort()s.
        Weapons.ConfigureAll();
    }

    /// <summary>Reset (test support).</summary>
    public static void Reset()
    {
        _done = false;
        // Unsubscribe any active mutator hooks before dropping the instances, so the global hook chains don't
        // retain handlers bound to about-to-be-discarded mutators (QC: a progs reload tears down the chains too).
        MutatorActivation.DeactivateAll();
        // Same for the active gametype: a booted gametype subscribes its own handlers to the global hook chains
        // (e.g. CTS's Shotgun-only PlayerSpawn/PlayerPreThink), and those are NOT mutator hooks so the loop above
        // misses them. Without this, a discarded gametype's handlers leaked into the next world (a CTS test world
        // forced Shotgun-only spawns onto a later DM/arena world). Deactivate is idempotent per gametype.
        foreach (GameType gt in Registry<GameType>.All)
            gt.Deactivate();
        Registry<Weapon>.Clear();
        Registry<Pickup>.Clear();
        Registry<MutatorBase>.Clear();
        Registry<GameType>.Clear();
        Registry<Monster>.Clear();
        Registry<Turret>.Clear();
        Registry<Vehicle>.Clear();
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types; }
        catch { return Array.Empty<Type?>(); }
    }
}
