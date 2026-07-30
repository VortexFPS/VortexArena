namespace XonoticGodot.Common.Framework;

/// <summary>
/// Anything enumerated by a registry (weapons, items, mutators, gametypes, stats, net messages…).
/// The C# successor to QuakeC's REGISTER_* + [[accumulate]] system (planning/decisions/ADR-0003).
/// </summary>
public interface IRegistered
{
    int RegistryId { get; set; }
    /// <summary>Stable identifier used for ordering and the client/server content hash.</summary>
    string RegistryName { get; }
}

/// <summary>Base for the attributes a source generator (or reflection bootstrap) scans for.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public abstract class GameRegistryAttribute : Attribute { }

public sealed class WeaponAttribute : GameRegistryAttribute { }
public sealed class ItemAttribute : GameRegistryAttribute { }
public sealed class MutatorAttribute : GameRegistryAttribute { }
public sealed class GameTypeAttribute : GameRegistryAttribute { }
public sealed class MonsterAttribute : GameRegistryAttribute { }

/// <summary>
/// A typed, ordered catalog. Population is pluggable: reflection now (<see cref="GameRegistries"/>),
/// a Roslyn source generator later. Order is made deterministic via <see cref="Sort"/> so the
/// client and server agree (the analogue of registry_net.qh's hash handshake).
/// </summary>
public static class Registry<T> where T : class, IRegistered
{
    private static readonly List<T> _all = new();
    private static readonly Dictionary<string, T> _byName = new(StringComparer.Ordinal);

    public static IReadOnlyList<T> All => _all;
    public static int Count => _all.Count;

    public static T? ByName(string name) => _byName.TryGetValue(name, out var v) ? v : null;
    public static T ById(int id) => _all[id];

    /// <summary>Idempotent registration (by RegistryName).</summary>
    public static void Register(T item)
    {
        if (string.IsNullOrEmpty(item.RegistryName) || _byName.ContainsKey(item.RegistryName)) return;
        item.RegistryId = _all.Count;
        _all.Add(item);
        _byName[item.RegistryName] = item;
    }

    /// <summary>Sort by RegistryName (ordinal) and renumber, for deterministic CL/SV ordering.</summary>
    public static void Sort()
    {
        _all.Sort(static (a, b) => string.CompareOrdinal(a.RegistryName, b.RegistryName));
        for (int i = 0; i < _all.Count; i++) _all[i].RegistryId = i;
    }

    /// <summary>FNV-1a hash over registry names in order; client and server must match.</summary>
    public static uint ContentHash()
    {
        uint h = 2166136261u;
        foreach (var it in _all)
            foreach (char c in it.RegistryName) { h ^= c; h *= 16777619u; }
        return h;
    }

    internal static void Clear()
    {
        _all.Clear();
        _byName.Clear();
    }
}
