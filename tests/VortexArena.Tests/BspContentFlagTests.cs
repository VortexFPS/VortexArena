using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression tests for the BSP "invisible walls" bug: the texture lump stores RAW Q3 native content flags
/// (<see cref="Q3Contents"/>), which <see cref="BspCollisionBuilder"/> must convert to the engine's
/// <see cref="SuperContents"/> bitspace (DP's <c>Mod_Q3BSP_SuperContentsFromNativeContents</c>). Before the
/// fix the raw bits were stamped straight onto the brush, so Q3 <c>TRANSLUCENT</c> (0x20000000) aliased
/// <see cref="SuperContents.Corpse"/> and every translucent non-solid brush — <c>common/hint</c>,
/// <c>common/donotenter</c>, lava/water surfaces — became a wall, while <c>common/clip</c> blocked only by
/// that same accident (so it must keep blocking through the proper PlayerClip path once the alias is gone).
/// </summary>
public class BspContentFlagTests
{
    // Real Q3 native content values seen on shipped Xonotic maps (see .tmp-bsp/analyze_contents.py output).
    private const int Q3SolidTex      = Q3Contents.Solid;                          // 0x00000001 base wall / caulk
    private const int Q3PlayerClipTex = Q3Contents.PlayerClip | Q3Contents.Translucent; // 0x20010000 common/clip
    private const int Q3HintTex       = Q3Contents.Structural | Q3Contents.Translucent; // 0x30000000 common/hint
    private const int Q3LavaTex       = Q3Contents.Lava | Q3Contents.Translucent;       // 0x20000008 liquids_lava

    // Each box lives in its own 64-unit cube along +X so a horizontal trace hits exactly one brush.
    private const int SolidX = 0, ClipX = 200, HintX = 400, LavaX = 600;

    /// <summary>Append an axis-aligned [mn,mx] box (6 sides + 1 brush) referencing texture <paramref name="tex"/>.</summary>
    private static void AddBox(List<BspPlane> planes, List<BspBrushSide> sides, List<BspBrush> brushes,
                               Vector3 mn, Vector3 mx, int tex)
    {
        int firstSide = sides.Count;
        (Vector3 n, float d)[] faces =
        {
            (new Vector3(1, 0, 0), mx.X), (new Vector3(-1, 0, 0), -mn.X),
            (new Vector3(0, 1, 0), mx.Y), (new Vector3(0, -1, 0), -mn.Y),
            (new Vector3(0, 0, 1), mx.Z), (new Vector3(0, 0, -1), -mn.Z),
        };
        foreach ((Vector3 n, float d) in faces)
        {
            sides.Add(new BspBrushSide(planes.Count, tex, -1));
            planes.Add(new BspPlane(n, d));
        }
        brushes.Add(new BspBrush(firstSide, 6, tex));
    }

    /// <summary>Four cubes — solid / playerclip / hint / lava — each textured with its raw Q3 content value.</summary>
    private static CollisionWorld BuildFourBoxWorld()
    {
        var planes = new List<BspPlane>();
        var sides = new List<BspBrushSide>();
        var brushes = new List<BspBrush>();
        Vector3 size = new(64, 64, 64);

        AddBox(planes, sides, brushes, new Vector3(SolidX, 0, 0), new Vector3(SolidX, 0, 0) + size, 0);
        AddBox(planes, sides, brushes, new Vector3(ClipX, 0, 0),  new Vector3(ClipX, 0, 0) + size,  1);
        AddBox(planes, sides, brushes, new Vector3(HintX, 0, 0),  new Vector3(HintX, 0, 0) + size,  2);
        AddBox(planes, sides, brushes, new Vector3(LavaX, 0, 0),  new Vector3(LavaX, 0, 0) + size,  3);

        var bsp = new BspData
        {
            Planes = planes.ToArray(),
            BrushSides = sides.ToArray(),
            Brushes = brushes.ToArray(),
            Textures = new[]
            {
                new BspTexture("textures/common/caulk", 0, Q3SolidTex),
                new BspTexture("textures/common/clip", 0, Q3PlayerClipTex),
                new BspTexture("textures/common/hint", 0, Q3HintTex),
                new BspTexture("textures/liquids_lava/lava0", 0, Q3LavaTex),
            },
            // No Models lump → BspCollisionBuilder's legacy path puts every brush in the static world.
        };

        return BspCollisionBuilder.Build(bsp).World;
    }

    private static Vector3 Center(int x) => new(x + 32, 32, 32);

    [Fact]
    public void Q3_Native_Contents_Are_Converted_To_SuperContents()
    {
        var trace = new TraceService(BuildFourBoxWorld());

        // Solid wall → SOLID set (and nothing exotic).
        int solid = trace.PointContents(Center(SolidX));
        Assert.NotEqual(0, solid & SuperContents.Solid);

        // common/clip → PlayerClip, NOT solid (so it blocks players via the mask, not as a wall).
        int clip = trace.PointContents(Center(ClipX));
        Assert.NotEqual(0, clip & SuperContents.PlayerClip);
        Assert.Equal(0, clip & SuperContents.Solid);

        // common/hint (STRUCTURAL|TRANSLUCENT) → nothing: a vis-only brush carries no content at all.
        int hint = trace.PointContents(Center(HintX));
        Assert.Equal(0, hint);

        // lava (LAVA|TRANSLUCENT) → liquid Lava, NOT solid (and the TRANSLUCENT bit no longer aliases Corpse).
        int lava = trace.PointContents(Center(LavaX));
        Assert.NotEqual(0, lava & SuperContents.Lava);
        Assert.Equal(0, lava & SuperContents.Solid);
        Assert.Equal(0, lava & SuperContents.Corpse);
    }

    /// <summary>Sweep a player-sized box straight through the cube at <paramref name="boxX"/>.</summary>
    private static TraceResult SweepThrough(TraceService trace, int boxX, Entity? mover)
    {
        Vector3 hull = new(8, 8, 8);
        Vector3 start = new(boxX - 30, 32, 32);
        Vector3 end = new(boxX + 64 + 30, 32, 32);
        return trace.Trace(start, -hull, hull, end, MoveFilter.Normal, mover);
    }

    [Fact]
    public void Player_Is_Blocked_By_Solid_And_Clip_But_Passes_Hint_And_Lava()
    {
        var trace = new TraceService(BuildFourBoxWorld());
        var player = new Entity { Solid = Solid.SlideBox };   // SOLID_SLIDEBOX, not FL_MONSTER → mask gains PlayerClip

        Assert.True(SweepThrough(trace, SolidX, player).Fraction < 1f, "solid wall must block the player");
        Assert.True(SweepThrough(trace, ClipX, player).Fraction < 1f, "common/clip must still block the player");

        // The invisible-wall regression: hint and lava must NOT stop a player.
        Assert.Equal(1f, SweepThrough(trace, HintX, player).Fraction, 3);
        Assert.Equal(1f, SweepThrough(trace, LavaX, player).Fraction, 3);
    }

    [Fact]
    public void Generic_Move_Without_PlayerClip_Passes_Through_Clip()
    {
        // A null mover uses DP's generic mask (Solid|Body|Corpse). With the conversion in place a clip brush
        // is pure PlayerClip, so it no longer blocks here — proving the old TRANSLUCENT→Corpse alias is gone
        // and that PlayerClip blocking is now driven by the per-entity mask, not by the brush being "translucent".
        var trace = new TraceService(BuildFourBoxWorld());

        Assert.Equal(1f, SweepThrough(trace, ClipX, null).Fraction, 3);
        Assert.Equal(1f, SweepThrough(trace, HintX, null).Fraction, 3);
        Assert.True(SweepThrough(trace, SolidX, null).Fraction < 1f, "solid still blocks any move");
    }
}
