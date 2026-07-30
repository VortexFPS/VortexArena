using System.Linq;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises <see cref="WeaponOrder"/> — the C# port of the QuakeC weapon-priority helpers
/// (fixPriorityList / swapInPriorityList / mapPriorityList / W_*WeaponOrder) the menu weapons list edits.
/// Uses the GlobalState collection because it reads the process-global weapon registry.
/// </summary>
[Collection("GlobalState")]
public class MenuWeaponOrderTests
{
    public MenuWeaponOrderTests() => GameRegistries.Bootstrap();

    [Fact]
    public void FixPriorityList_Keeps_In_Range_And_Completes()
    {
        int count = XonoticGodot.Common.Framework.Registry<Weapon>.Count;
        Assert.True(count > 3, "need a populated registry");

        // FixWeaponOrder("3 1 5", complete:true): 3,1,5 first, then every other id appended descending,
        // excluding any WEP_FLAG_SPECIALATTACK weapon. No special-attack weapons exist in this registry,
        // so the completed list is a permutation of all ids.
        string fixedOrder = WeaponOrder.FixWeaponOrder("3 1 5", complete: true);
        int[] ids = fixedOrder.Split(' ').Select(int.Parse).ToArray();

        Assert.Equal(new[] { 3, 1, 5 }, ids.Take(3).ToArray());           // explicit prefix preserved, in order
        Assert.Equal(ids.Length, ids.Distinct().Count());                 // no duplicates

        int specialAttacks = XonoticGodot.Common.Framework.Registry<Weapon>.All
            .Count(w => (w.SpawnFlags & WeaponFlags.SpecialAttack) != 0);
        Assert.Equal(count - specialAttacks, ids.Length);                 // count == registry size minus specials

        // The completion appends descending (to..from) for the missing ids; with 3,1,5 explicit the next
        // appended id is the highest remaining (count-1) down, skipping 5/3/1.
        Assert.Equal(count - 1, ids[3]);
    }

    [Fact]
    public void FixPriorityList_AllowIncomplete_Drops_OutOfRange_And_NonInteger()
    {
        int count = XonoticGodot.Common.Framework.Registry<Weapon>.Count;
        // 0 and count-1 are valid; count (one past the end), -1, and "x" are dropped; complete:false appends nothing.
        string s = WeaponOrder.FixWeaponOrder($"0 {count} -1 x {count - 1}", complete: false);
        Assert.Equal($"0 {count - 1}", s);
    }

    [Fact]
    public void SwapInPriorityList_Swaps_Two_Positions()
    {
        Assert.Equal("a c b d", WeaponOrder.SwapInPriorityList("a b c d", 1, 2));
        // i == j → unchanged (QC bounds guard)
        Assert.Equal("a b c d", WeaponOrder.SwapInPriorityList("a b c d", 1, 1));
        // out-of-range → unchanged
        Assert.Equal("a b c d", WeaponOrder.SwapInPriorityList("a b c d", 0, 9));
        Assert.Equal("a b c d", WeaponOrder.SwapInPriorityList("a b c d", -1, 2));
    }

    [Fact]
    public void NumberAndName_Roundtrip()
    {
        // Round-trip three known (non-Overkill) weapons through number↔name.
        const string names = "shotgun machinegun blaster";
        string numbered = WeaponOrder.NumberWeaponOrder(names);
        // every token is now an integer id
        Assert.All(numbered.Split(' '), t => Assert.True(int.TryParse(t, out _)));
        Assert.Equal(names, WeaponOrder.NameWeaponOrder(numbered));
    }

    [Fact]
    public void Unknown_Name_Survives_Number_Then_Dropped_By_Fix()
    {
        // A weapon name absent from the registry: NumberWeaponOrder passes it through unchanged (QC), then
        // FixWeaponOrder's integer-range filter drops it (it isn't an integer id). (The Overkill weapons ARE
        // registered now — T51 — so an actually-unknown token is used here.)
        const string bogus = "definitely_not_a_weapon";
        string numbered = WeaponOrder.NumberWeaponOrder($"{bogus} shotgun");
        Assert.StartsWith($"{bogus} ", numbered);                            // survived numbering unchanged
        string fixedOrder = WeaponOrder.FixWeaponOrder(numbered, complete: false);
        Assert.DoesNotContain(bogus, fixedOrder);                            // dropped by the range filter
        // shotgun's id remains
        int shotgunId = XonoticGodot.Common.Framework.Registry<Weapon>.ByName("shotgun")!.RegistryId;
        Assert.Equal(shotgunId.ToString(), fixedOrder);
    }

    [Fact]
    public void ShippedDefault_Normalizes_Dedup_And_Completes()
    {
        // The shipped cl_weaponpriority default (xonotic-client.cfg:796). The "ok*" Overkill tokens ARE
        // registered weapons (T51), so — faithful to QC fixPriorityList (common/util.qc), which only excludes
        // WEP_FLAG_SPECIALATTACK during completion, NOT hidden/mutatorblocked — they survive and the result is a
        // complete, dedup'd permutation of EVERY registry weapon.
        const string shipped =
            "vaporizer okhmg okrpc oknex vortex fireball mortar okmachinegun machinegun hagar rifle arc " +
            "electro devastator crylink minelayer okshotgun shotgun hlac tuba blaster porto seeker hook";

        string numbered = WeaponOrder.NumberWeaponOrder(shipped);
        string completed = WeaponOrder.FixWeaponOrder(numbered, complete: true);
        string named = WeaponOrder.NameWeaponOrder(completed);

        string[] outNames = named.Split(' ');
        // The Overkill weapons are registered → present in the completed order (QC keeps all non-specialattack).
        foreach (string ok in new[] { "okhmg", "okrpc", "oknex", "okmachinegun", "okshotgun" })
            Assert.Contains(ok, outNames);

        // complete + dedup → a permutation of every registry weapon name MINUS the WEP_FLAG_SPECIALATTACK
        // weapons that QC fixPriorityList drops during completion (e.g. the hidden Hunter-Killer player weapon
        // WEP_HK). The Ball-Stealer is MUTATORBLOCKED|TYPE_OTHER (not SPECIALATTACK), so completion keeps it.
        int count = XonoticGodot.Common.Framework.Registry<Weapon>.Count;
        int specialAttacks = XonoticGodot.Common.Framework.Registry<Weapon>.All
            .Count(w => (w.SpawnFlags & WeaponFlags.SpecialAttack) != 0);
        int completable = count - specialAttacks;
        Assert.Equal(completable, outNames.Length);
        Assert.Equal(completable, outNames.Distinct().Count());

        // The known names keep their shipped relative order at the front (vaporizer before vortex before
        // fireball before mortar before machinegun ...).
        int Idx(string n) => System.Array.IndexOf(outNames, n);
        Assert.True(Idx("vaporizer") < Idx("vortex"));
        Assert.True(Idx("vortex") < Idx("fireball"));
        Assert.True(Idx("fireball") < Idx("mortar"));
        Assert.True(Idx("mortar") < Idx("machinegun"));
        Assert.True(Idx("machinegun") < Idx("hagar"));
        Assert.True(Idx("hagar") < Idx("rifle"));
    }

    [Fact]
    public void MenuRegistryView_Excludes_BallStealer_Like_Bases_Menu_Program()
    {
        // Base compiles menu/client/server QC separately and the MENU program never registers WEP_NEXBALL —
        // so its weapons list can neither show nor append "ballstealer" (no real Base config.cfg contains
        // it), and a stray token wouldn't even resolve to an id there. The port's single registry models
        // that with Weapon.MenuRegistered; the menu widget passes menuRegistryView:true.
        Weapon? ballstealer = XonoticGodot.Common.Framework.Registry<Weapon>.ByName("ballstealer");
        Assert.NotNull(ballstealer);
        Assert.False(ballstealer!.MenuRegistered);
        string id = ballstealer.RegistryId.ToString();

        // The menu-view completion never appends it…
        string completed = WeaponOrder.FixWeaponOrder("", complete: true, menuRegistryView: true);
        Assert.DoesNotContain(id, completed.Split(' '));

        // …and a stray token from an already-dirty config is dropped (the cvar self-heals on the next
        // weapons-dialog open, like Base's menu dropping a name its registry can't resolve)…
        string dirty = WeaponOrder.NumberWeaponOrder("shotgun ballstealer");
        Assert.DoesNotContain(id, WeaponOrder.FixWeaponOrder(dirty, complete: false, menuRegistryView: true).Split(' '));

        // …while the default (client/server) view keeps it — Base's server program has the weapon
        // registered, and its sentcvar W_FixWeaponOrder_ForceComplete fix-up sees it too.
        Assert.Contains(id, WeaponOrder.FixWeaponOrder(dirty, complete: false).Split(' '));
    }

    [Fact]
    public void FixWeaponOrderForceComplete_Empty_Seeds_From_Default()
    {
        // QC W_FixWeaponOrder_ForceComplete: empty order → number the cvar default, then complete.
        const string defaultNames = "shotgun machinegun blaster";
        string result = WeaponOrder.FixWeaponOrderForceComplete("", defaultNames);
        int count = XonoticGodot.Common.Framework.Registry<Weapon>.Count;
        // Completion drops WEP_FLAG_SPECIALATTACK weapons (e.g. the hidden Hunter-Killer WEP_HK), so the
        // completed set is the registry minus those, not the bare registry count.
        int specialAttacks = XonoticGodot.Common.Framework.Registry<Weapon>.All
            .Count(w => (w.SpawnFlags & WeaponFlags.SpecialAttack) != 0);
        int completable = count - specialAttacks;
        int[] ids = result.Split(' ').Select(int.Parse).ToArray();
        Assert.Equal(completable, ids.Length);                            // completed to the full completable set
        Assert.Equal(completable, ids.Distinct().Count());                // dedup'd
        // The three default weapons lead.
        int Id(string n) => XonoticGodot.Common.Framework.Registry<Weapon>.ByName(n)!.RegistryId;
        Assert.Equal(new[] { Id("shotgun"), Id("machinegun"), Id("blaster") }, ids.Take(3).ToArray());
    }
}
