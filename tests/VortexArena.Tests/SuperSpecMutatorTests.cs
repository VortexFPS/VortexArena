using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// M14: coverage for the SuperSpec spectator mutator (previously untested despite +694 lines of live hooks).
/// Pins the SSF_* flag bits (bit-identical to Base sv_superspec.qc:20-22) and round-trips the per-client item
/// filter through its spectator command — superspec_itemfilter → the live <see cref="SuperSpecMutator.FilterItem"/>
/// allowlist that gates the item-message channel — including the observer-only command gate.
/// </summary>
[Collection("GlobalState")]
public sealed class SuperSpecMutatorTests
{
    private static Entity Item(string cls) => new() { ClassName = cls };
    private static Player Spectator() => new() { IsObserver = true, Flags = EntFlags.Client };

    [Fact]
    public void SsfFlagBits_AreBitIdenticalToBase()
    {
        Assert.Equal(1 << 0, SuperSpecMutator.SSF_SILENT);
        Assert.Equal(1 << 1, SuperSpecMutator.SSF_VERBOSE);
        Assert.Equal(1 << 2, SuperSpecMutator.SSF_ITEMMSG);
    }

    [Fact]
    public void ItemFilter_RoundTripsThroughSpectatorCommand()
    {
        var m = new SuperSpecMutator();
        Player spec = Spectator();

        // superspec_itemfilter "" (unset) = match all: every pickup passes the filter.
        Assert.True(SuperSpecMutator.FilterItem(spec, Item("item_health_mega")));

        // Set the allowlist (a spectator-only verb: returns true = handled).
        Assert.True(m.HandleCommand(spec, new[] { "superspec_itemfilter", "item_health_mega item_armor_big" }));
        Assert.True(SuperSpecMutator.FilterItem(spec, Item("item_health_mega")));
        Assert.True(SuperSpecMutator.FilterItem(spec, Item("item_armor_big")));
        Assert.False(SuperSpecMutator.FilterItem(spec, Item("item_shells")));

        // `clear` → back to match-all.
        Assert.True(m.HandleCommand(spec, new[] { "superspec_itemfilter", "clear" }));
        Assert.True(SuperSpecMutator.FilterItem(spec, Item("item_shells")));
    }

    [Fact]
    public void SpectatorCommand_IsIgnoredForALivePlayer()
    {
        // QC: the superspec verbs are observer-only (if IS_PLAYER(player) return) — a live player is a no-op.
        var m = new SuperSpecMutator();
        var live = new Player { IsObserver = false, Flags = EntFlags.Client };
        Assert.False(m.HandleCommand(live, new[] { "superspec_itemfilter", "item_health_mega" }));
    }
}
