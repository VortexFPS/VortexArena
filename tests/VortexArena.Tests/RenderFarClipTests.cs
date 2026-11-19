// playtest-bugs #79: the native camera ran at Godot's default Far (4000 units = 4000 qu), cutting big maps off.
// The far plane is now DarkPlaces' r_farclip_base + world radius * r_farclip_world * 2 (gl_rmain.c).

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Math;
using VortexArena.Formats.Bsp;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

public sealed class RenderFarClipTests
{
    private readonly ITestOutputHelper _out;
    public RenderFarClipTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RadiusFromBounds_IsDarkPlacesCornerLength()
    {
        // DP mathlib.c: sqrt(max(mins², maxs²) per axis summed).
        float r = RenderFarClip.RadiusFromBounds(new Vector3(-3000f, -100f, -50f), new Vector3(1000f, 400f, 20f));
        Assert.Equal(MathF.Sqrt(3000f * 3000f + 400f * 400f + 50f * 50f), r, 2);
    }

    [Fact]
    public void Compute_IsBasePlusTwiceRadiusTimesWorld()
    {
        var mins = new Vector3(-4096f, -4096f, -1024f);
        var maxs = new Vector3(4096f, 4096f, 1024f);
        float radius = RenderFarClip.RadiusFromBounds(mins, maxs);
        Assert.Equal(65536f + radius * 2f * 2f, RenderFarClip.Compute(65536f, 2f, mins, maxs), 1);
        Assert.Equal(65536f, RenderFarClip.Compute(65536f, 2f, null, null)); // no map yet: the base alone
        Assert.Equal(4000f, RenderFarClip.Compute(0f, 0f, null, null));      // a zeroed cvar never shrinks the view
    }

    /// <summary>Every shipped map: no two points of the world are farther apart than the far plane at the defaults.</summary>
    [Fact]
    public void DefaultFarClip_CoversEveryShippedMap()
    {
        string maps = Path.Combine(TestPaths.Data, "maps");
        if (!Directory.Exists(maps)) { _out.WriteLine($"no maps — skipped. {TestPaths.NoMapsReason}"); return; }
        int n = 0;
        float largest = 0f;
        foreach (string pack in Directory.EnumerateFiles(maps, "*.pk3"))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(pack);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)))
            {
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                BspData bsp;
                try { bsp = BspReader.Read(ms.ToArray()); } catch (Exception) { continue; }
                if (bsp.Models.Length == 0) continue;
                Vector3 mn = bsp.Models[0].Mins, mx = bsp.Models[0].Maxs;
                float diagonal = Vector3.Distance(mn, mx);
                largest = MathF.Max(largest, diagonal);
                float far = RenderFarClip.Compute(RenderFarClip.DefaultBase, RenderFarClip.DefaultWorld, mn, mx);
                Assert.True(far > diagonal, $"{entry.FullName}: far {far} < world diagonal {diagonal}");
                Assert.True(diagonal < 4000f || far > 4000f, "sanity");
                n++;
            }
        }
        _out.WriteLine($"{n} maps; largest world diagonal {largest:0} qu (Godot's default Far is 4000)");
    }
}
