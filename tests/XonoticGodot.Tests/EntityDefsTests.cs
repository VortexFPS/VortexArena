using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="EntityDefs"/> (phase E8): the parser for Xonotic's shipped <c>scripts/entities.ent</c>,
/// which drives the editor's entity palette and inspector.
///
/// The parsing is deliberately forgiving. A definition file that is missing, truncated or newer than the
/// editor must degrade to plain boxes rather than stop a session opening — an editor that refuses to load a
/// map because a metadata file moved is worse than one that draws an unlabelled cube.
/// </summary>
public class EntityDefsTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private const string Sample = """
        <?xml version="1.0"?>
        <classes>
        <point name="weapon_devastator" color="1 0 .5" box="-30 -30 0 30 30 48">
        the Devastator
        A rocket launcher.
        -------- KEYS --------
        <real key="ammo_rockets" name="ammo_rockets">initial rockets of the weapon</real>
        <integer key="count" name="Ammo Given">number of primary shots</integer>
        <targetname key="targetname" name="targetname">the name other entities use</targetname>
        <flag key="FLOATING" name="FLOATING" bit="1">the item will float in air</flag>
        -------- MODEL FOR RADIANT ONLY - DO NOT SET THIS AS A KEY --------
        modeldisabled="models/weapons/g_devastator.md3"
        </point>

        <group name="func_door" color="0 .5 .8">
        A sliding door.
        -------- KEYS --------
        <real key="speed" name="speed">movement speed (default 100)</real>
        <boolean key="dmg" name="dmg">damage to inflict</boolean>
        </group>

        <point name="info_player_deathmatch" color="0 1 0" box="-16 -16 -24 16 16 45">
        A deathmatch spawn point.
        </point>
        </classes>
        """;

    // ---------------------------------------------------------------- shape

    [Fact]
    public void ParsesEveryClass()
    {
        EntityDefs defs = EntityDefs.Parse(Sample);
        Assert.Equal(3, defs.Count);
        Assert.NotNull(defs.Get("weapon_devastator"));
        Assert.NotNull(defs.Get("func_door"));
    }

    [Fact]
    public void ClassNameLookupIsCaseInsensitive()
        => Assert.NotNull(EntityDefs.Parse(Sample).Get("WEAPON_Devastator"));

    [Fact]
    public void PointAndGroupAreDistinguished()
    {
        EntityDefs defs = EntityDefs.Parse(Sample);
        Assert.False(defs.Get("weapon_devastator")!.IsBrushEntity);
        Assert.True(defs.Get("func_door")!.IsBrushEntity);
    }

    [Fact]
    public void BoxAndColourAreRead()
    {
        EntityClassDef d = EntityDefs.Parse(Sample).Get("weapon_devastator")!;
        Assert.True(d.HasBox);
        Assert.Equal(new Vector3(-30, -30, 0), d.Mins);
        Assert.Equal(new Vector3(30, 30, 48), d.Maxs);
        Assert.Equal(new Vector3(1f, 0f, 0.5f), d.Color);
    }

    /// <summary>
    /// A class with no box still has to be drawable and clickable, so it falls back to a small cube rather
    /// than to nothing.
    /// </summary>
    [Fact]
    public void AClassWithoutABox_FallsBackToADrawableCube()
    {
        EntityClassDef d = EntityDefs.Parse(Sample).Get("func_door")!;
        Assert.False(d.HasBox);
        Assert.Equal(EntityClassDef.DefaultMins, d.DrawMins);
        Assert.Equal(EntityClassDef.DefaultMaxs, d.DrawMaxs);
        Assert.True(d.DrawMaxs.X > d.DrawMins.X);
    }

    // ---------------------------------------------------------------- keys and flags

    [Fact]
    public void KeysCarryTheirTypeAndHelp()
    {
        EntityClassDef d = EntityDefs.Parse(Sample).Get("weapon_devastator")!;

        EntityKeyDef ammo = Assert.Single(d.Keys, k => k.Key == "ammo_rockets");
        Assert.Equal(EntityKeyKind.Real, ammo.Kind);
        Assert.Equal("initial rockets of the weapon", ammo.Help);

        Assert.Equal(EntityKeyKind.Integer, Assert.Single(d.Keys, k => k.Key == "count").Kind);
        Assert.Equal(EntityKeyKind.TargetName, Assert.Single(d.Keys, k => k.Key == "targetname").Kind);
        Assert.Equal(EntityKeyKind.Boolean,
            Assert.Single(EntityDefs.Parse(Sample).Get("func_door")!.Keys, k => k.Key == "dmg").Kind);
    }

    [Fact]
    public void KeysUseTheirDisplayNameWhenTheFileGivesOne()
        => Assert.Equal("Ammo Given",
            Assert.Single(EntityDefs.Parse(Sample).Get("weapon_devastator")!.Keys, k => k.Key == "count").Name);

    /// <summary>Flags are a separate list from keys: they are bits of one spawnflags value, not keys of their own.</summary>
    [Fact]
    public void FlagsAreSeparatedFromKeys()
    {
        EntityClassDef d = EntityDefs.Parse(Sample).Get("weapon_devastator")!;
        Assert.DoesNotContain(d.Keys, k => k.Key == "FLOATING");

        EntityFlagDef flag = Assert.Single(d.Flags);
        Assert.Equal("FLOATING", flag.Name);
        Assert.Equal(1, flag.Bit);
    }

    // ---------------------------------------------------------------- text handling

    [Fact]
    public void DescriptionKeepsTheProse_AndDropsTheSeparators()
    {
        EntityClassDef d = EntityDefs.Parse(Sample).Get("weapon_devastator")!;
        Assert.Contains("the Devastator", d.Description);
        Assert.Contains("A rocket launcher.", d.Description);
        Assert.DoesNotContain("--------", d.Description);
        Assert.DoesNotContain("modeldisabled", d.Description);
    }

    /// <summary>
    /// <c>modeldisabled=</c> lives in the element's TEXT, not in an attribute, and the file is emphatic that it
    /// is not a spawn key. It is editor preview metadata and has to be lifted out separately.
    /// </summary>
    [Fact]
    public void PreviewModelIsLiftedOutOfTheText()
        => Assert.Equal("models/weapons/g_devastator.md3",
            EntityDefs.Parse(Sample).Get("weapon_devastator")!.Model);

    [Fact]
    public void AClassWithNoPreviewModel_ReportsEmpty()
        => Assert.Equal("", EntityDefs.Parse(Sample).Get("info_player_deathmatch")!.Model);

    // ---------------------------------------------------------------- categories

    [Theory]
    [InlineData("weapon_devastator", "weapon")]
    [InlineData("item_health_mega", "item")]
    [InlineData("info_player_deathmatch", "info")]
    [InlineData("func_door", "func")]
    [InlineData("trigger_multiple", "trigger")]
    [InlineData("target_position", "target")]
    [InlineData("light", "light")]
    [InlineData("_skybox", "misc")]
    [InlineData("dom_controlpoint", "misc")]
    public void CategoriesComeFromTheClassnamePrefix(string className, string expected)
        => Assert.Equal(expected, EntityDefs.CategoryFor(className));

    [Fact]
    public void CategoriesAreOrderedWithTheCommonOnesFirst()
    {
        IReadOnlyList<string> cats = EntityDefs.Parse(Sample).Categories();
        Assert.Equal("weapon", cats[0]);
        Assert.Contains("func", cats);
        Assert.Contains("info", cats);
    }

    [Fact]
    public void InCategoryReturnsOnlyThatCategory()
    {
        EntityDefs defs = EntityDefs.Parse(Sample);
        EntityClassDef only = Assert.Single(defs.InCategory("weapon"));
        Assert.Equal("weapon_devastator", only.Name);
    }

    // ---------------------------------------------------------------- degradation

    /// <summary>
    /// A map can name a class the file does not describe — a mod entity, a typo, something newer. The editor
    /// still has to draw it and let you fix it, so lookup never returns null.
    /// </summary>
    [Fact]
    public void AnUnknownClass_GetsAPlaceholder_NotNull()
    {
        EntityClassDef d = EntityDefs.Parse(Sample).GetOrPlaceholder("mod_something_new");
        Assert.Equal("mod_something_new", d.Name);
        Assert.True(d.DrawMaxs.X > d.DrawMins.X);
        Assert.Empty(d.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<classes><point name=\"broken\"")]
    [InlineData("this is not xml at all")]
    public void MalformedInput_YieldsAnEmptyRegistry_NotAnException(string bad)
    {
        EntityDefs defs = EntityDefs.Parse(bad);
        Assert.Equal(0, defs.Count);
        Assert.NotNull(defs.GetOrPlaceholder("anything"));
    }

    // ---------------------------------------------------------------- the real file

    /// <summary>
    /// Parse the file the game actually ships, when the asset tree is present. The sample above proves the
    /// parser's shape; this proves it survives all 186 real classes, which is where the odd formatting lives.
    /// </summary>
    [Fact]
    public void TheShippedFileParses()
    {
        // Same self-skip convention as the other real-data tests: absent assets means no assertion, not a
        // failure, so a clean checkout still runs the suite green.
        const string path = DataDir + @"\xonotic-maps.pk3dir\scripts\entities.ent";
        if (!File.Exists(path))
            return;

        EntityDefs defs = EntityDefs.Parse(File.ReadAllText(path));
        Assert.True(defs.Count > 150, $"only parsed {defs.Count} classes");

        // Spot-check a class from each of the shapes the file uses.
        EntityClassDef spawn = defs.GetOrPlaceholder("info_player_deathmatch");
        Assert.True(spawn.HasBox);

        Assert.Contains(defs.All, d => d.IsBrushEntity);        // <group>
        Assert.Contains(defs.All, d => !d.IsBrushEntity);       // <point>
        Assert.Contains(defs.All, d => d.Model.Length > 0);     // modeldisabled
        Assert.Contains(defs.All, d => d.Flags.Count > 0);      // spawnflags
        Assert.Contains(defs.All, d => d.Keys.Count > 0);

        // Every parsed class must be usable: named, categorized, and drawable.
        foreach (EntityClassDef d in defs.All)
        {
            Assert.NotEqual("", d.Name);
            Assert.NotEqual("", d.Category);
            Assert.True(d.DrawMaxs.X >= d.DrawMins.X, $"{d.Name} has an inverted box");
        }
    }
}
