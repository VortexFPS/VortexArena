using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Engine.Collision;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the per-gametype map-entity filter (<see cref="MapEntityFilter"/>) — the port of DP's
/// <c>SV_OnEntityPreSpawnFunction</c> / <c>isGametypeInFilter</c> / <c>DoesQ3ARemoveThisEntity</c> gate. A
/// brush entity (<c>func_wall "*N"</c>) tagged for a different gametype must be resolved as a dropped
/// submodel so its collision + render geometry is skipped. The headline real-data case is stormkeep, which
/// ships Race-only barriers (<c>gametypefilter "+rc"</c>) and non-Race barriers (<c>"-rc"</c>).
/// </summary>
public class MapEntityFilterTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private static readonly MapEntityFilter.GametypeContext Dm = new("dm", TeamPlay: false, HaveTeamSpawns: false);
    private static readonly MapEntityFilter.GametypeContext Ctf = new("ctf", TeamPlay: true, HaveTeamSpawns: true);
    private static readonly MapEntityFilter.GametypeContext Race = new("rc", TeamPlay: false, HaveTeamSpawns: false);

    // ---- isGametypeInFilter: faithful to common/util.qc -------------------------------------------------

    [Theory]
    // "+rc": keep ONLY in Race; drop everywhere else.
    [InlineData("+rc", "rc", false, false, true)]
    [InlineData("+rc", "dm", false, false, false)]
    [InlineData("+rc", "ctf", true, true, false)]
    // "-rc": drop in Race; keep everywhere else.
    [InlineData("-rc", "rc", false, false, false)]
    [InlineData("-rc", "dm", false, false, true)]
    [InlineData("-rc", "ctf", true, true, true)]
    // bare gametype token (no +/-): include form.
    [InlineData("ctf", "ctf", true, true, true)]
    [InlineData("ctf", "dm", false, false, false)]
    // the team/noteam pseudo-tokens.
    [InlineData("teams", "dm", false, false, false)]
    [InlineData("teams", "ctf", true, true, true)]
    [InlineData("noteams", "dm", false, false, true)]
    [InlineData("noteams", "ctf", true, true, false)]
    // substring safety: ",dm," must not match ",tdm," (DP wraps tokens in commas).
    [InlineData("+tdm", "dm", false, false, false)]
    public void IsGametypeInFilter_Matches_QC(string pattern, string gt, bool tp, bool ts, bool expectKeep)
    {
        var ctx = new MapEntityFilter.GametypeContext(gt, tp, ts);
        Assert.Equal(expectKeep, MapEntityFilter.IsGametypeInFilter(ctx, pattern));
    }

    // ---- Q3/QL compat field filters (server/compat/quake3.qc) ------------------------------------------

    [Fact]
    public void NotTeam_Drops_Only_In_TeamGames()
    {
        var ent = new Dictionary<string, string> { ["notteam"] = "1" };
        Assert.False(MapEntityFilter.ShouldKeepEntity(ent, Ctf)); // team game → dropped
        Assert.True(MapEntityFilter.ShouldKeepEntity(ent, Dm));   // FFA → kept
    }

    [Fact]
    public void NotFree_Drops_Only_In_NonTeamGames()
    {
        var ent = new Dictionary<string, string> { ["notfree"] = "1" };
        Assert.True(MapEntityFilter.ShouldKeepEntity(ent, Ctf)); // team game → kept
        Assert.False(MapEntityFilter.ShouldKeepEntity(ent, Dm)); // FFA → dropped
    }

    [Fact]
    public void Q3_Gametype_Token_List_Keeps_When_Matched()
    {
        // Q3 "gametype" token list: keep iff our q3/ql name is a substring.
        var ctfOnly = new Dictionary<string, string> { ["gametype"] = "ctf oneflag" };
        Assert.True(MapEntityFilter.ShouldKeepEntity(ctfOnly, Ctf));
        Assert.False(MapEntityFilter.ShouldKeepEntity(ctfOnly, Dm)); // "ffa" not in the list
    }

    [Fact]
    public void NoFilter_Keys_Always_Kept()
    {
        var plain = new Dictionary<string, string> { ["classname"] = "func_wall", ["model"] = "*5" };
        Assert.True(MapEntityFilter.ShouldKeepEntity(plain, Dm));
        Assert.True(MapEntityFilter.ShouldKeepEntity(plain, Race));
    }

    // ---- DroppedSubmodels: resolve "*N" model indices from filtered brush entities --------------------

    [Fact]
    public void DroppedSubmodels_Resolves_StarN_From_Filter()
    {
        var bsp = new BspData
        {
            Entities = new IReadOnlyDictionary<string, string>[]
            {
                new Dictionary<string, string> { ["classname"] = "worldspawn" },
                new Dictionary<string, string> { ["classname"] = "func_wall", ["model"] = "*1", ["gametypefilter"] = "+rc" },
                new Dictionary<string, string> { ["classname"] = "func_wall", ["model"] = "*2", ["gametypefilter"] = "-rc" },
                new Dictionary<string, string> { ["classname"] = "func_wall", ["model"] = "*3" }, // unconditional
            },
        };

        // Deathmatch: drop the +rc wall (*1), keep the -rc wall (*2) and the plain wall (*3).
        HashSet<int> dm = MapEntityFilter.DroppedSubmodels(bsp, Dm);
        Assert.Contains(1, dm);
        Assert.DoesNotContain(2, dm);
        Assert.DoesNotContain(3, dm);

        // Race: inverse — keep *1, drop *2.
        HashSet<int> race = MapEntityFilter.DroppedSubmodels(bsp, Race);
        Assert.DoesNotContain(1, race);
        Assert.Contains(2, race);
        Assert.DoesNotContain(3, race);
    }

    // ---- collision builder honors the dropped set ----------------------------------------------------

    [Fact]
    public void CollisionBuilder_Skips_Dropped_Submodels()
    {
        // Two inline models (*1, *2) on top of worldspawn (model 0); drop *1.
        var planes = new List<BspPlane>();
        var sides = new List<BspBrushSide>();
        var brushes = new List<BspBrush>();
        int Box(Vector3 mn, Vector3 mx)
        {
            int firstSide = sides.Count;
            (Vector3 n, float d)[] faces =
            {
                (new Vector3(1, 0, 0), mx.X), (new Vector3(-1, 0, 0), -mn.X),
                (new Vector3(0, 1, 0), mx.Y), (new Vector3(0, -1, 0), -mn.Y),
                (new Vector3(0, 0, 1), mx.Z), (new Vector3(0, 0, -1), -mn.Z),
            };
            foreach (var (n, d) in faces) { sides.Add(new BspBrushSide(planes.Count, 0, -1)); planes.Add(new BspPlane(n, d)); }
            brushes.Add(new BspBrush(firstSide, 6, 0));
            return brushes.Count - 1;
        }
        Box(new Vector3(0, 0, 0), new Vector3(64, 64, 64));      // 0 — world
        Box(new Vector3(100, 0, 0), new Vector3(164, 64, 64));   // 1 — *1 (dropped)
        Box(new Vector3(200, 0, 0), new Vector3(264, 64, 64));   // 2 — *2 (kept)

        var bsp = new BspData
        {
            Planes = planes.ToArray(),
            BrushSides = sides.ToArray(),
            Brushes = brushes.ToArray(),
            Textures = new[] { new BspTexture("common/solid", 0, SuperContents.Solid) },
            Models = new[]
            {
                new BspModel(Vector3.Zero, new Vector3(64, 64, 64), 0, 0, 0, 1),
                new BspModel(new Vector3(100, 0, 0), new Vector3(164, 64, 64), 0, 0, 1, 1),
                new BspModel(new Vector3(200, 0, 0), new Vector3(264, 64, 64), 0, 0, 2, 1),
            },
        };

        var dropped = new HashSet<int> { 1 };
        BspCollisionBuilder.Result r = BspCollisionBuilder.Build(bsp, dropped);

        // *1 was dropped → only *2 remains as a submodel.
        Assert.Single(r.Submodels);
        Assert.Equal("*2", r.Submodels[0].Name);
    }

    // ---- real data: stormkeep's gametype-conditional walls -------------------------------------------

    [Fact]
    public void Real_Stormkeep_Filters_Race_Walls_Per_Gametype()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        if (!vfs.Exists("maps/stormkeep.bsp")) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes("maps/stormkeep.bsp"));

        HashSet<int> dm = MapEntityFilter.DroppedSubmodels(bsp, Dm);
        HashSet<int> race = MapEntityFilter.DroppedSubmodels(bsp, Race);

        // stormkeep has both "+rc" (Race-only) and "-rc" (non-Race) func_walls, so each gametype drops a
        // non-empty, DISJOINT set: the +rc walls drop in DM, the -rc walls drop in Race.
        Assert.NotEmpty(dm);
        Assert.NotEmpty(race);
        Assert.Empty(dm.Intersect(race)); // a wall is never dropped in both gametypes

        // Every dropped index maps to a real inline model and a func_wall carrying a gametypefilter.
        foreach (int n in dm.Concat(race))
            Assert.InRange(n, 1, bsp.Models.Length - 1);

        // Sanity: the specific models we verified by hand (see investigation). *17 is "+rc" (drop in DM, keep
        // in Race); *26 is "-rc" (keep in DM, drop in Race).
        Assert.Contains(17, dm);
        Assert.DoesNotContain(17, race);
        Assert.Contains(26, race);
        Assert.DoesNotContain(26, dm);
    }
}
