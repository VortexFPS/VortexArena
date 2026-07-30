using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.SourceGen;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the registry source generator (T26 — <see cref="RegistryGenerator"/>, ADR-0003, the C# successor
/// to QC's REGISTER_* / [[accumulate]] compile-time registration in lib/registry.qh) and the
/// <see cref="GameRegistries.Bootstrap"/> swap from reflection scanning to the generated
/// <c>GeneratedRegistrations.RegisterAll()</c>.
///
/// Two layers:
///   1. Generator-driver tests: run <see cref="RegistryGenerator"/> over an in-memory compilation whose stub
///      attributes/bases mirror the REAL metadata names (the generator matches by name, never by symbol
///      identity), asserting per-category emission (all 7 catalogs incl. Monster/Turret/Vehicle), the
///      skip rules (abstract/static/generic/private-ctor/untagged), and byte-identical determinism.
///   2. Registration-parity test: after a real Reset()+Bootstrap(), an INDEPENDENT reflection scan over
///      XonoticGodot.Common (the old Bootstrap algorithm) must produce exactly the same per-catalog name
///      sets and the same FNV-1a content hash — proving generated == reflection, so the swap changed the
///      mechanism but not the registries (registry ids, WepSet bits, deathtypes, wire fields all unchanged).
/// </summary>
[Collection("GlobalState")]
public class SourceGenTests
{
    // ---------------------------------------------------------------- generator-driver harness ----

    /// <summary>
    /// Stub declarations mirroring the real marker-attribute metadata names and catalog base classes.
    /// Weapon/Item/Mutator/GameType/Monster attributes live in XonoticGodot.Common.Framework
    /// (Framework/Registry.cs); Turret/Vehicle were declared later next to their descriptor bases in
    /// XonoticGodot.Common.Gameplay (Gameplay/Turrets/TurretAI.cs, Gameplay/Vehicles/VehicleCommon.cs).
    /// </summary>
    private const string StubDecls = @"
namespace XonoticGodot.Common.Framework
{
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public abstract class GameRegistryAttribute : System.Attribute { }
    public sealed class WeaponAttribute : GameRegistryAttribute { }
    public sealed class ItemAttribute : GameRegistryAttribute { }
    public sealed class MutatorAttribute : GameRegistryAttribute { }
    public sealed class GameTypeAttribute : GameRegistryAttribute { }
    public sealed class MonsterAttribute : GameRegistryAttribute { }
}
namespace XonoticGodot.Common.Gameplay
{
    public sealed class TurretAttribute : XonoticGodot.Common.Framework.GameRegistryAttribute { }
    public sealed class VehicleAttribute : XonoticGodot.Common.Framework.GameRegistryAttribute { }
    public abstract class Weapon { }
    public abstract class Pickup { }
    public abstract class MutatorBase { }
    public abstract class GameType { }
    public abstract class Monster { }
    public abstract class Turret { }
    public abstract class Vehicle { }
}
";

    /// <summary>One tagged, registrable class per category (mirrors real usage: short attribute names).</summary>
    private const string AllCategoriesSource = StubDecls + @"
namespace Test.Stub
{
    using XonoticGodot.Common.Framework;
    using XonoticGodot.Common.Gameplay;

    [Weapon] public sealed class StubWeapon : Weapon { }
    [Item] public sealed class StubItem : Pickup { }
    [Mutator] public sealed class StubMutator : MutatorBase { }
    [GameType] public sealed class StubGameType : GameType { }
    [Monster] public sealed class StubMonster : Monster { }
    [Turret] public sealed class StubTurret : Turret { }
    [Vehicle] public sealed class StubVehicle : Vehicle { }
}
";

    private static CSharpCompilation Compile(string source)
    {
        var refs = new List<MetadataReference>();
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in tpa.Split(Path.PathSeparator))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name is "System.Private.CoreLib" or "System.Runtime" or "netstandard")
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        var compilation = CSharpCompilation.Create(
            "SourceGenStubAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A broken stub would silently produce a wrong/empty generator result — fail loudly instead.
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "stub compilation has errors: " + string.Join("; ", errors));
        return compilation;
    }

    /// <summary>Runs the generator over <paramref name="source"/> and returns the generated text.</summary>
    private static string RunGenerator(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RegistryGenerator());
        driver = driver.RunGenerators(Compile(source));

        GeneratorRunResult run = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(run.Diagnostics);
        GeneratedSourceResult generated = Assert.Single(run.GeneratedSources);
        Assert.Equal("GeneratedRegistrations.g.cs", generated.HintName);
        return generated.SourceText.ToString();
    }

    private static string RegisterLine(string catalog, string type)
        => $"global::XonoticGodot.Common.Framework.Registry<global::XonoticGodot.Common.Gameplay.{catalog}>"
           + $".Register(new global::{type}());";

    private static string SortLine(string catalog)
        => $"global::XonoticGodot.Common.Framework.Registry<global::XonoticGodot.Common.Gameplay.{catalog}>.Sort();";

    // ---------------------------------------------------------------------- generator behavior ----

    [Fact]
    public void Generator_EmitsTypedRegistration_ForAllSevenCategories()
    {
        string text = RunGenerator(AllCategoriesSource);

        // Each tagged stub routes to the catalog its ATTRIBUTE names (the generator infers the registry
        // from the marker, not from the base type), incl. the newly covered Monster/Turret/Vehicle.
        Assert.Contains(RegisterLine("Weapon", "Test.Stub.StubWeapon"), text);
        Assert.Contains(RegisterLine("Pickup", "Test.Stub.StubItem"), text);
        Assert.Contains(RegisterLine("MutatorBase", "Test.Stub.StubMutator"), text);
        Assert.Contains(RegisterLine("GameType", "Test.Stub.StubGameType"), text);
        Assert.Contains(RegisterLine("Monster", "Test.Stub.StubMonster"), text);
        Assert.Contains(RegisterLine("Turret", "Test.Stub.StubTurret"), text);
        Assert.Contains(RegisterLine("Vehicle", "Test.Stub.StubVehicle"), text);

        // Every touched catalog gets a Sort() (deterministic CL/SV order), AFTER all Register() calls —
        // the order GameRegistries.Bootstrap guaranteed (sort, then Weapons.ConfigureAll on the sorted set).
        foreach (string catalog in new[] { "Weapon", "Pickup", "MutatorBase", "GameType", "Monster", "Turret", "Vehicle" })
        {
            int sortAt = text.IndexOf(SortLine(catalog), StringComparison.Ordinal);
            Assert.True(sortAt >= 0, $"missing Sort() for {catalog}");
            Assert.True(text.LastIndexOf(".Register(new", StringComparison.Ordinal) < sortAt,
                $"Sort() for {catalog} must come after every Register()");
        }
    }

    [Fact]
    public void Generator_SkipRules_AbstractStaticGenericPrivateCtorUntagged()
    {
        string source = StubDecls + @"
namespace Test.Stub
{
    using XonoticGodot.Common.Framework;
    using XonoticGodot.Common.Gameplay;

    [Weapon] public abstract class AbstractWeapon : Weapon { }
    [Weapon] public static class StaticTagged { }
    [Weapon] public sealed class GenericWeapon<T> : Weapon { }
    [Weapon] public sealed class PrivateCtorWeapon : Weapon { private PrivateCtorWeapon() { } }
    public sealed class UntaggedWeapon : Weapon { }
    [Weapon] internal sealed class InternalWeapon : Weapon { }
    [Weapon] public sealed class GoodWeapon : Weapon { }
}
";
        string text = RunGenerator(source);

        Assert.Contains(RegisterLine("Weapon", "Test.Stub.GoodWeapon"), text);
        // internal is fine: the generated code lives in the same assembly as the tagged types.
        Assert.Contains(RegisterLine("Weapon", "Test.Stub.InternalWeapon"), text);

        Assert.DoesNotContain("AbstractWeapon", text);
        Assert.DoesNotContain("StaticTagged", text);
        Assert.DoesNotContain("GenericWeapon", text);
        Assert.DoesNotContain("PrivateCtorWeapon", text);
        Assert.DoesNotContain("UntaggedWeapon", text);
    }

    [Fact]
    public void Generator_EmptyCompilation_EmitsCallableNoOp()
    {
        string text = RunGenerator(StubDecls); // attributes + bases, but no tagged types

        Assert.Contains("public static void RegisterAll()", text);
        Assert.Contains("-tagged types were found", text);
        Assert.DoesNotContain(".Register(new", text);
        Assert.DoesNotContain(".Sort();", text);
    }

    [Fact]
    public void Generator_IsDeterministic_ByteIdenticalAcrossRuns()
    {
        // Fresh driver + fresh compilation each run: the emitted file must be byte-identical (the file
        // participates in deterministic builds; ordering is (category, FQN), never hash/dictionary order).
        string first = RunGenerator(AllCategoriesSource);
        string second = RunGenerator(AllCategoriesSource);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Generator_CategorySuffixes_AreNotSuffixesOfEachOther()
    {
        // The generator routes attributes by trailing-name match (tolerating subclassed markers like
        // "SuperWeaponAttribute"). That is only sound while no category suffix ends with another's —
        // this pins the invariant for anyone adding an eighth registry.
        string[] suffixes =
        {
            "WeaponAttribute", "ItemAttribute", "MutatorAttribute", "GameTypeAttribute",
            "MonsterAttribute", "TurretAttribute", "VehicleAttribute",
        };
        for (int i = 0; i < suffixes.Length; i++)
            for (int j = 0; j < suffixes.Length; j++)
                if (i != j)
                    Assert.False(suffixes[i].EndsWith(suffixes[j], StringComparison.Ordinal),
                        $"'{suffixes[i]}' ends with '{suffixes[j]}' — greedy suffix matching would misroute");
    }

    // ------------------------------------------------------------------------ registration parity ----

    /// <summary>
    /// The old Bootstrap algorithm, re-run independently: reflect over XonoticGodot.Common for
    /// [GameRegistry]-derived markers, instantiate, dispatch by runtime base type. Returns per-catalog
    /// RegistryName sets (ordinal-sorted, matching Registry&lt;T&gt;.Sort()).
    /// </summary>
    private static Dictionary<string, SortedSet<string>> ReflectionScan()
    {
        var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["Weapon"] = new(StringComparer.Ordinal), ["Pickup"] = new(StringComparer.Ordinal),
            ["MutatorBase"] = new(StringComparer.Ordinal), ["GameType"] = new(StringComparer.Ordinal),
            ["Monster"] = new(StringComparer.Ordinal), ["Turret"] = new(StringComparer.Ordinal),
            ["Vehicle"] = new(StringComparer.Ordinal),
        };

        // SafeGetTypes, like the old Bootstrap: a type that fails to load (mid-wave mixed binaries,
        // ReflectionTypeLoadException) yields null slots rather than aborting the scan.
        Type?[] types;
        try { types = typeof(GameRegistries).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }

        foreach (Type? t in types)
        {
            if (t is null || t.IsAbstract) continue;
            if (t.GetCustomAttribute<GameRegistryAttribute>() is null) continue;

            object? inst;
            try { inst = Activator.CreateInstance(t); }
            catch { continue; }

            switch (inst)
            {
                case Weapon w: found["Weapon"].Add(w.RegistryName); break;
                case Pickup p: found["Pickup"].Add(p.RegistryName); break;
                case MutatorBase m: found["MutatorBase"].Add(m.RegistryName); break;
                case GameType g: found["GameType"].Add(g.RegistryName); break;
                case Monster mo: found["Monster"].Add(mo.RegistryName); break;
                case Turret tu: found["Turret"].Add(tu.RegistryName); break;
                case Vehicle ve: found["Vehicle"].Add(ve.RegistryName); break;
            }
        }

        return found;
    }

    /// <summary>FNV-1a over names in order — the exact <c>Registry&lt;T&gt;.ContentHash()</c> algorithm.</summary>
    private static uint Fnv1a(IEnumerable<string> namesInOrder)
    {
        uint h = 2166136261u;
        foreach (string name in namesInOrder)
            foreach (char c in name) { h ^= c; h *= 16777619u; }
        return h;
    }

    [Fact]
    public void Bootstrap_GeneratedRegistration_MatchesIndependentReflectionScan()
    {
        GameRegistries.Reset();
        GameRegistries.Bootstrap();

        Dictionary<string, SortedSet<string>> expected = ReflectionScan();

        // Per-catalog: the generated tables register exactly the types the old reflection scan found,
        // and the live registry order is the ordinal name order (Registry<T>.Sort()) the wire relies on.
        Assert.Equal(expected["Weapon"], Weapons.All.Select(x => x.RegistryName).ToList());
        Assert.Equal(expected["Pickup"], Items.All.Select(x => x.RegistryName).ToList());
        Assert.Equal(expected["MutatorBase"], Mutators.All.Select(x => x.RegistryName).ToList());
        Assert.Equal(expected["GameType"], GameTypes.All.Select(x => x.RegistryName).ToList());
        Assert.Equal(expected["Monster"], Monsters.All.Select(x => x.RegistryName).ToList());
        Assert.Equal(expected["Turret"], Turrets.All.Select(x => x.RegistryName).ToList());
        Assert.Equal(expected["Vehicle"], Vehicles.All.Select(x => x.RegistryName).ToList());

        // Content hash parity with the pre-swap (reflection) pipeline: same names, same order, same FNV-1a
        // — i.e. the client/server registry handshake value is bit-identical across the swap.
        Assert.Equal(Fnv1a(expected["Weapon"]), Weapons.Hash);
        Assert.Equal(Fnv1a(expected["Pickup"]), Items.Hash);
        Assert.Equal(Fnv1a(expected["MutatorBase"]), Mutators.Hash);
        Assert.Equal(Fnv1a(expected["GameType"]), GameTypes.Hash);

        // Tripwire: parity alone would pass if BOTH sides were empty. Pin non-trivial floors (current
        // counts; other waves only add content). Weapons also need their balance seeded (ConfigureAll).
        Assert.True(Weapons.Count >= 24, $"weapons={Weapons.Count}");
        Assert.True(Items.Count >= 19, $"items={Items.Count}");
        Assert.True(Mutators.Count >= 41, $"mutators={Mutators.Count}");
        Assert.True(GameTypes.Count >= 20, $"gametypes={GameTypes.Count}");
        Assert.True(Monsters.Count >= 5, $"monsters={Monsters.Count}");
        Assert.True(Turrets.Count >= 12, $"turrets={Turrets.Count}");
        Assert.True(Vehicles.Count >= 4, $"vehicles={Vehicles.Count}");
    }

    [Fact]
    public void Bootstrap_IsIdempotent_AndHashStableAcrossResetCycles()
    {
        GameRegistries.Reset();
        GameRegistries.Bootstrap();
        int weapons = Weapons.Count, turrets = Turrets.Count, vehicles = Vehicles.Count;
        uint hash = Weapons.Hash;

        GameRegistries.Bootstrap(); // _done flag: second call is a no-op
        Assert.Equal(weapons, Weapons.Count);
        Assert.Equal(turrets, Turrets.Count);
        Assert.Equal(vehicles, Vehicles.Count);

        GameRegistries.Reset();     // full Reset+Bootstrap cycle reproduces the same catalog
        GameRegistries.Bootstrap();
        Assert.Equal(weapons, Weapons.Count);
        Assert.Equal(hash, Weapons.Hash);
    }
}
