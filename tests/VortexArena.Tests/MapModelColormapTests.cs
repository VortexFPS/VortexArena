using VortexArena.Common;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <c>g_model_setcolormaptoactivator</c> (common/mapobjects/models.qc:17-29) — the colormap a triggered
/// map model adopts from whoever activated it.
///
/// <para>These pin the WIRE-SAFETY invariant of <see cref="Entity.ColorMapOverride"/>, which rides the
/// snapshot as a 16-bit UNSIGNED field (<c>ServerNet</c> masks <c>&amp; 0xFFFF</c>; the codec uses
/// WriteUShort/ReadUShort). A negative or &gt;0xFFFF value would leave the server holding one int and every
/// client decoding a different one — invisible while consumers read only the low bits, but wrong the moment
/// anything compares the whole value (a <c>colormap &gt; 0</c> test, an <c>&amp; ~RenderColormapped</c>
/// unmask, a host-vs-client equality check).</para>
///
/// <para>A 2026-07-27 review flagged the teamplay branch as producing −17 for a team-less activator. It does
/// NOT: the <c>if (actor.Team != 0f)</c> guard (matching QC's <c>if(actor.team)</c>) sends that case to
/// <c>0x00</c>. These tests exist so that claim stays settled rather than being re-litigated — and so a
/// future edit that drops the guard, or admits a negative team, fails here instead of on the wire.</para>
/// </summary>
public class MapModelColormapTests
{
    private static void Boot()
    {
        MapMover.ClearIndex();
        var world = new CollisionWorld();
        world.BuildGrid();
        GameInit.Boot(new EngineServices(world));
    }

    /// <summary>A misc_models edict wired with the plain colormap-to-activator .use (models.qc:182).</summary>
    private static Entity Model()
    {
        Entity e = Api.Entities.Spawn();
        MapModels.ModelsSetup(e);
        return e;
    }

    private static Entity Activator(float team)
    {
        Entity a = Api.Entities.Spawn();
        a.Team = team;
        return a;
    }

    /// <summary>Every value this function can emit must survive the unsigned 16-bit snapshot field unchanged.</summary>
    private static void AssertWireSafe(int colormap)
    {
        Assert.True(colormap >= 0, $"colormap {colormap} is NEGATIVE — the 16-bit unsigned wire field would " +
                                   $"decode it as {colormap & 0xFFFF} on every client");
        Assert.True(colormap <= 0xFFFF, $"colormap {colormap} does not fit the 16-bit wire field");
        Assert.Equal(colormap, colormap & 0xFFFF); // the round-trip ServerNet/NetEntity actually perform
    }

    [Theory]
    [InlineData(Teams.Red)]     // 4  -> (4-1)*0x11 = 51
    [InlineData(Teams.Blue)]    // 13 -> 204
    [InlineData(Teams.Yellow)]  // 12 -> 187
    [InlineData(Teams.Pink)]    // 9  -> 136
    public void Teamplay_RealTeam_PacksBothNibbles_AndStaysWireSafe(int team)
    {
        Boot();
        Api.Cvars.Set("teamplay", "1");
        Entity model = Model();

        model.Use!(model, Activator(team));

        int expected = ((team - 1) * 0x11) | Entity.RenderColormapped;
        Assert.Equal(expected, model.ColorMapOverride);
        AssertWireSafe(model.ColorMapOverride);

        // 0x11 sets shirt and pants to the SAME index (16*n + n) — the two nibbles must agree.
        int packed = model.ColorMapOverride & ~Entity.RenderColormapped;
        Assert.Equal(packed & 0x0F, (packed >> 4) & 0x0F);
        Assert.Equal(team - 1, packed & 0x0F);
    }

    [Fact]
    public void Teamplay_TeamlessActivator_Is_Zero_Not_Negative()
    {
        // The regression guard for the reported (and refuted) −17. QC: `if(actor.team) ... else colormap = 0x00`.
        Boot();
        Api.Cvars.Set("teamplay", "1");
        Entity model = Model();

        model.Use!(model, Activator(Teams.None));

        Assert.Equal(Entity.RenderColormapped, model.ColorMapOverride); // 0x00 | BIT(10), nothing else
        AssertWireSafe(model.ColorMapOverride);
    }

    [Fact]
    public void NonTeamplay_RandomColormap_StaysInsideTheWireField()
    {
        // QC: colormap = floor(random() * 256) — the whole 0..255 byte, then | BIT(10). Loop so the assert
        // sees many draws rather than one lucky value.
        Boot();
        Api.Cvars.Set("teamplay", "0");

        for (int i = 0; i < 64; i++)
        {
            Entity model = Model();
            model.Use!(model, Activator(Teams.None));

            AssertWireSafe(model.ColorMapOverride);
            Assert.True((model.ColorMapOverride & Entity.RenderColormapped) != 0);
            int packed = model.ColorMapOverride & ~Entity.RenderColormapped;
            Assert.InRange(packed, 0, 255);
        }
    }

    [Fact]
    public void RenderColormappedBit_IsAlwaysSet_OnBothBranches()
    {
        Boot();
        foreach (string teamplay in new[] { "0", "1" })
        {
            Api.Cvars.Set("teamplay", teamplay);
            Entity model = Model();
            model.Use!(model, Activator(Teams.Red));
            Assert.True((model.ColorMapOverride & Entity.RenderColormapped) != 0,
                $"teamplay {teamplay}: RENDER_COLORMAPPED must be set (models.qc:28)");
        }
    }
}
