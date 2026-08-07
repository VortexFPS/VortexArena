using System;
using System.IO;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// How a texture stem resolves to a pre-compressed <c>.dds</c> — the read half of the texture cache
/// (<c>r_texture_dds_load</c> / <c>r_texture_dds_save</c>).
///
/// <para>These exist because the cache silently failed to converge for months and nothing noticed. Writing it
/// worked, reading it back did not, and the only symptom was a slower load — no error, no warning, and a
/// summary line that said "cached 287, next launch skips this" every single launch. On stormkeep 61 of 287
/// textures re-encoded on EVERY start: ~1.5 s at S3TC and ~5.0 s of an 11.0 s warm load at BC7.</para>
///
/// <para>Both causes were ordering/naming mistakes invisible to any test that only asks "did something
/// resolve?" — which is what the existing coverage asked. So these assert the STRONGER property: which of
/// several present candidates wins.</para>
/// </summary>
public class DdsCacheResolutionTests : IDisposable
{
    private readonly bool _preferDds = VirtualFileSystem.PreferDds;
    private readonly string _cacheDir = VirtualFileSystem.DdsCacheDir;

    /// <summary>The statics are process-wide render settings, so every case restores them.</summary>
    public void Dispose()
    {
        VirtualFileSystem.PreferDds = _preferDds;
        VirtualFileSystem.DdsCacheDir = _cacheDir;
    }

    /// <summary>A throwaway content tree of loose files — the cache is written as loose files, not packs.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "vortex-dds-" + Path.GetRandomFileName());

        public TempTree(params string[] relPaths)
        {
            foreach (string rel in relPaths)
            {
                string full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, new byte[16]);   // content is irrelevant; only the NAME resolves
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// THE BUG (P6). A bare model-shader name — <c>a_shells.md3</c>'s "shellsammo" surface, which has no
    /// <c>.shader</c> entry — resolves through the <c>textures/&lt;stem&gt;</c> fallback. The cache is written
    /// under the path the bytes were FOUND at (<c>dds/textures/shellsammo.dds</c>) but the next launch looks
    /// the stem up under the name that was ASKED for, and the fallback branch probed the raster forms first.
    /// So the .png won every time and the texture re-encoded for ever.
    /// </summary>
    [Fact]
    public void Bare_Model_Shader_Name_Prefers_The_Dds_Cache_Over_The_Raster_Original()
    {
        using var tree = new TempTree("textures/shellsammo.png", "dds/textures/shellsammo.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));

        VirtualFileSystem.DdsCacheDir = string.Empty;
        VirtualFileSystem.PreferDds = true;
        Assert.Equal("dds/textures/shellsammo.dds", vfs.ResolveImage("shellsammo"));
    }

    /// <summary>With the cache OFF, the same stem must fall back to the uncompressed original — otherwise
    /// <c>r_texture_dds_load 0</c> would be the no-op the cvar exists to avoid being.</summary>
    [Fact]
    public void With_Dds_Loading_Off_The_Raster_Original_Wins()
    {
        using var tree = new TempTree("textures/shellsammo.png", "dds/textures/shellsammo.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));

        VirtualFileSystem.DdsCacheDir = string.Empty;
        VirtualFileSystem.PreferDds = false;
        Assert.Equal("textures/shellsammo.png", vfs.ResolveImage("shellsammo"));
    }

    /// <summary>The path-rooted form of the same rule, which already worked — pinned so a fix to the fallback
    /// branch cannot regress the branch that was fine.</summary>
    [Fact]
    public void Path_Rooted_Stem_Prefers_The_Dds_Cache()
    {
        using var tree = new TempTree("textures/wall.tga", "dds/textures/wall.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));

        VirtualFileSystem.DdsCacheDir = string.Empty;
        VirtualFileSystem.PreferDds = true;
        Assert.Equal("dds/textures/wall.dds", vfs.ResolveImage("textures/wall"));
    }

    /// <summary>
    /// (P7) Our own cache is written to a directory named for the compression mode that produced it, and that
    /// directory outranks the shared <c>dds/</c> tree the game ships. Without this, <c>gl_texturecompression</c>
    /// was inert after the first load: a player switching from S3TC to BC7 kept being served the DXT blocks the
    /// previous run banked, because a DDS records its block format but not the setting that chose it.
    /// </summary>
    [Fact]
    public void Mode_Tagged_Cache_Outranks_The_Shipped_Dds_Tree()
    {
        using var tree = new TempTree(
            "textures/wall.tga", "dds/textures/wall.dds", "dds2/textures/wall.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));
        VirtualFileSystem.PreferDds = true;

        VirtualFileSystem.DdsCacheDir = "dds2";
        Assert.Equal("dds2/textures/wall.dds", vfs.ResolveImage("textures/wall"));
    }

    /// <summary>
    /// The other half of P7, and the one that makes switching modes actually cost something: when the current
    /// mode's cache has no entry, resolution must NOT silently fall through to the other mode's blocks. It may
    /// still use the SHIPPED <c>dds/</c> tree — those are the game's own files and DarkPlaces uses them
    /// whatever the setting says; re-encoding shipped DXT1 to BC7 would cost a fortune to make it worse.
    /// </summary>
    [Fact]
    public void A_Mode_With_No_Cache_Entry_Does_Not_Fall_Through_To_Another_Modes_Cache()
    {
        using var tree = new TempTree("textures/wall.tga", "dds1/textures/wall.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));
        VirtualFileSystem.PreferDds = true;

        // Mode 2 asked for; only mode 1's cache exists and there is no shipped dds/ — so the raster original
        // must win and the texture re-encodes into dds2/, which is exactly the intended cost of the switch.
        VirtualFileSystem.DdsCacheDir = "dds2";
        Assert.Equal("textures/wall.tga", vfs.ResolveImage("textures/wall"));

        // ...and mode 1 still finds its own.
        VirtualFileSystem.DdsCacheDir = "dds1";
        Assert.Equal("dds1/textures/wall.dds", vfs.ResolveImage("textures/wall"));
    }

    /// <summary>The bare-name fallback has to honour the mode tag too — it is the branch P6 just fixed, so it
    /// is the one most likely to be forgotten again.</summary>
    [Fact]
    public void Mode_Tagged_Cache_Also_Wins_For_A_Bare_Model_Shader_Name()
    {
        using var tree = new TempTree(
            "textures/shellsammo.png", "dds/textures/shellsammo.dds", "dds1/textures/shellsammo.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));
        VirtualFileSystem.PreferDds = true;

        VirtualFileSystem.DdsCacheDir = "dds1";
        Assert.Equal("dds1/textures/shellsammo.dds", vfs.ResolveImage("shellsammo"));
    }

    /// <summary>Flipping either static must invalidate the per-stem resolve cache, or the first lookup of a
    /// session would pin the answer for the whole process — which is how a "live" setting becomes a lie.</summary>
    [Fact]
    public void Changing_The_Settings_Reresolves_Already_Cached_Stems()
    {
        using var tree = new TempTree("textures/wall.tga", "dds/textures/wall.dds", "dds1/textures/wall.dds");
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(tree.Root));

        VirtualFileSystem.PreferDds = true;
        VirtualFileSystem.DdsCacheDir = string.Empty;
        Assert.Equal("dds/textures/wall.dds", vfs.ResolveImage("textures/wall"));   // now cached

        VirtualFileSystem.DdsCacheDir = "dds1";
        Assert.Equal("dds1/textures/wall.dds", vfs.ResolveImage("textures/wall"));

        VirtualFileSystem.PreferDds = false;
        Assert.Equal("textures/wall.tga", vfs.ResolveImage("textures/wall"));
    }
}
