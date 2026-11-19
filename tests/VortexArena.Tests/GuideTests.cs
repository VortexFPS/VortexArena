using System;
using VortexArena.Common.Menu;
using Xunit;

namespace VortexArena.Tests;

public class GuideTests
{
    [Fact]
    public void SelectionSurvivesUnavailableResultsAndRemembersEachTopic()
    {
        var selection = new GuideSelection();
        static string Id(string entry) => entry;

        Assert.Equal(-1, selection.Resolve("Introduction", Array.Empty<string>(), Id));
        Assert.Equal(0, selection.Resolve("Introduction", new[] { "about", "community", "getting-good" }, Id));
        selection.Remember("Introduction", "community");

        Assert.Equal(0, selection.Resolve("Introduction", new[] { "about", "getting-good" }, Id));
        Assert.Equal(-1, selection.Resolve("Introduction", Array.Empty<string>(), Id));
        Assert.Equal(0, selection.Resolve("Introduction", new[] { "community" }, Id));

        Assert.Equal(0, selection.Resolve("Movement", new[] { "intro", "bunny" }, Id));
        selection.Remember("Movement", "bunny");
        Assert.Equal(1, selection.Resolve("Introduction", new[] { "about", "community", "getting-good" }, Id));

        Assert.Equal(0, selection.Resolve("Introduction", new[] { "getting-good" }, Id));
        selection.RememberInitial("Introduction", "about");
        Assert.Equal(1, selection.Resolve("Introduction", new[] { "about", "community", "getting-good" }, Id));

        selection.Remember("Introduction", "getting-good");
        Assert.Equal(-1, selection.Resolve("Introduction", Array.Empty<string>(), Id));
        Assert.Equal(1, selection.Resolve("Movement", new[] { "intro", "bunny" }, Id));
        Assert.Equal(2, selection.Resolve("Introduction", new[] { "about", "community", "getting-good" }, Id));
    }

    [Fact]
    public void SelectionUsesIdWhenLabelsChangeOrCollide()
    {
        var selection = new GuideSelection();
        selection.Remember("Introduction", "community");
        var localized = new[] { (Id: "about", Label: "同名"), (Id: "community", Label: "同名") };

        Assert.Equal(1, selection.Resolve("Introduction", localized, entry => entry.Id));
    }

    [Theory]
    [InlineData("Gametypes", "editor,tdm,tmayhem,ca,ctf,dm", "ca,ctf,dm,tdm,tmayhem,editor")]
    [InlineData("Items", "armor_small,health_small,bullets,health_mega,health_medium,cells", "bullets,cells,health_medium,health_mega,health_small,armor_small")]
    [InlineData("Weapons", "native_weapon,okhmg,okshotgun,okmachinegun,rifle,blaster", "blaster,rifle,okmachinegun,okshotgun,okhmg,native_weapon")]
    [InlineData("Turrets", "plasma_dual,walker,native_z,plasma,native_a,ewheel", "ewheel,plasma,plasma_dual,walker,native_z,native_a")]
    [InlineData("Mutators", "nix,new_toys,offhand_blaster,bloodloss,instagib,dodging,nades", "nades,dodging,instagib,bloodloss,offhand_blaster,new_toys,nix")]
    public void CatalogOrdersSharedEntriesAndAppendsNativeEntries(string topic, string candidates, string expected)
    {
        Assert.Equal(expected.Split(','), GuideCatalogOrder.Order(topic, candidates.Split(','), entry => entry));
    }
}
