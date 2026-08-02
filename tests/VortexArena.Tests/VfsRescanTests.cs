using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The per-user gamedir layering and <c>fs_rescan</c> (<see cref="VirtualFileSystem.Rescan"/>).
///
/// <para>Every fixture here is synthetic — a temp tree of tiny .pk3s — so these run on a checkout that has
/// never fetched content, unlike the real-data tests that guard on <see cref="TestPaths.Data"/>. What is
/// being tested is search-path mechanics, and a three-entry zip exercises those exactly as a 500 MB pack
/// does.</para>
/// </summary>
public class VfsRescanTests
{
    // -------------------------------------------------------------------------------------------------
    //  Fixture
    // -------------------------------------------------------------------------------------------------

    /// <summary>A throwaway content tree under the temp dir, deleted on dispose.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; }

        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "vortex-vfs-rescan-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Root);
        }

        /// <summary>An absolute path under the tree; the directory is created.</summary>
        public string Dir(string rel)
        {
            string p = Path.Combine(Root, rel);
            Directory.CreateDirectory(p);
            return p;
        }

        /// <summary>Write a .pk3 at <paramref name="relPath"/> (parents created) holding <paramref name="entries"/>.
        /// Overwrites, and stamps a distinct mtime so "was this pack replaced?" is decided by the fixture
        /// rather than by the filesystem's timestamp granularity.</summary>
        public string Pk3(string relPath, params (string Name, string Body)[] entries)
        {
            string full = Path.Combine(Root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach ((string name, string body) in entries)
                {
                    ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    using Stream s = e.Open();
                    byte[] bytes = Encoding.ASCII.GetBytes(body);
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            File.SetLastWriteTimeUtc(full, _stamp);
            _stamp = _stamp.AddMinutes(1); // every write is distinguishable from the one before it
            return full;
        }

        private DateTime _stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp dir; leave it */ }
        }
    }

    private static string Text(VirtualFileSystem vfs, string vpath) =>
        Encoding.ASCII.GetString(vfs.ReadBytes(vpath));

    // -------------------------------------------------------------------------------------------------
    //  Content-root layering (the per-user gamedir)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ContentRoot_Mounts_Packs_In_The_Maps_Subdir()
    {
        using var tree = new TempTree();
        tree.Dir("data");
        tree.Pk3("data/maps/stormkeep.pk3", ("maps/stormkeep.bsp", "BSP"));

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(Path.Combine(tree.Root, "data")));

        // The pack sits one level down in maps/, which is the layout both the shipped tree and the user
        // gamedir use — MountContentRoot is what reaches it.
        Assert.True(vfs.Exists("maps/stormkeep.bsp"));
    }

    [Fact]
    public void Later_ContentRoot_Outranks_Earlier()
    {
        using var tree = new TempTree();
        tree.Pk3("shipped/core.pk3", ("textures/wall.tga", "SHIPPED"));
        tree.Pk3("user/override.pk3", ("textures/wall.tga", "USER"));

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "shipped"));
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));

        // DP's FS_Init order: basedir gamedir first, userdir gamedir second and therefore winning. This is
        // what makes a player's own pack able to override shipped content.
        Assert.Equal("USER", Text(vfs, "textures/wall.tga"));
    }

    [Fact]
    public void User_Map_Pack_Is_Visible_Alongside_Shipped_Maps()
    {
        using var tree = new TempTree();
        tree.Pk3("shipped/maps/stormkeep.pk3", ("maps/stormkeep.bsp", "BSP"));
        tree.Pk3("user/maps/mymap.pk3", ("maps/mymap.bsp", "BSP"), ("maps/mymap.mapinfo", "title My Map"));

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "shipped"));
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));

        // Find() unions across mounts, which is what puts a user-dropped map in the create-game list next to
        // the shipped ones rather than shadowing or being shadowed by them.
        Assert.Contains("maps/stormkeep.bsp", vfs.Find("maps/", "bsp"));
        Assert.Contains("maps/mymap.bsp", vfs.Find("maps/", "bsp"));
        Assert.True(vfs.Exists("maps/mymap.mapinfo"));
    }

    // -------------------------------------------------------------------------------------------------
    //  Rescan
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Rescan_Finds_A_Pack_Dropped_In_After_Boot()
    {
        using var tree = new TempTree();
        tree.Dir("user/maps");

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));

        // Probe FIRST, so the negative result is in the Exists cache. A rescan that rebuilt the search path
        // but left that cache would still answer false here.
        Assert.False(vfs.Exists("maps/late.bsp"));

        tree.Pk3("user/maps/late.pk3", ("maps/late.bsp", "BSP"));
        VirtualFileSystem.RescanResult r = vfs.Rescan();

        Assert.True(vfs.Exists("maps/late.bsp"));
        Assert.True(r.Added >= 1);
    }

    [Fact]
    public void Rescan_Drops_A_Pack_Deleted_From_Disk()
    {
        using var tree = new TempTree();
        string pack = tree.Pk3("user/maps/gone.pk3", ("maps/gone.bsp", "BSP"));

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));
        Assert.True(vfs.Exists("maps/gone.bsp"));

        // Deleting a MOUNTED pack has to work, or a player can never remove a map without quitting first.
        // It only does because Pk3Mount opens with FileShare.Delete — see the note there.
        File.Delete(pack);
        VirtualFileSystem.RescanResult r = vfs.Rescan();

        Assert.False(vfs.Exists("maps/gone.bsp"));
        Assert.True(r.Removed >= 1);
    }

    [Fact]
    public void Rescan_Reuses_Unchanged_Packs_And_Rebuilds_Directories()
    {
        using var tree = new TempTree();
        tree.Pk3("shipped/core.pk3", ("textures/wall.tga", "SHIPPED"));
        tree.Pk3("shipped/maps/stormkeep.pk3", ("maps/stormkeep.bsp", "BSP"));
        tree.Pk3("user/maps/mymap.pk3", ("maps/mymap.bsp", "BSP"));
        tree.Dir("user"); // so user/ and user/maps/ both exist

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "shipped"));
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));

        VirtualFileSystem.RescanResult r = vfs.Rescan();

        // Four directory mounts (each root plus each root's maps/) are always rebuilt — a directory index is
        // a re-walk that holds no handles. The three packs are carried over untouched, which is the whole
        // point: re-opening them is the expensive half of a mount.
        Assert.Equal(3, r.Reused);
        Assert.Equal(4, r.Added);
        Assert.Equal(4, r.Removed); // the four directory mounts the rebuild replaced
        Assert.Equal(7, r.Mounts);
    }

    [Fact]
    public void Rescan_Serves_The_New_Bytes_After_A_Pack_Is_Replaced()
    {
        using var tree = new TempTree();
        string pack = tree.Pk3("user/skin.pk3", ("textures/wall.tga", "OLD"));

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));
        Assert.Equal("OLD", Text(vfs, "textures/wall.tga"));

        // Delete, rescan, write the replacement, rescan — which is the sequence that actually works on
        // Windows. An in-place overwrite of a mounted pack cannot: the handle denies write sharing, and even
        // after an unlink the name stays reserved until the mount holding it is disposed. The first rescan is
        // what disposes it. On POSIX the two steps could be one; doing it the portable way keeps this test
        // meaningful on the platform where the constraint is real.
        File.Delete(pack);
        vfs.Rescan();
        Assert.False(vfs.Exists("textures/wall.tga"));

        tree.Pk3("user/skin.pk3", ("textures/wall.tga", "REPLACED"));
        vfs.Rescan();

        Assert.Equal("REPLACED", Text(vfs, "textures/wall.tga"));
    }

    [Fact]
    public void Rescan_Picks_Up_A_Content_Root_Created_After_Boot()
    {
        using var tree = new TempTree();
        string userRoot = Path.Combine(tree.Root, "not-there-yet");

        using var vfs = new VirtualFileSystem();
        // Reports false — nothing to mount — but the root is remembered, so a player who creates the folder
        // mid-session does not have to restart to use it.
        Assert.False(vfs.MountContentRoot(userRoot));

        tree.Pk3("not-there-yet/maps/mymap.pk3", ("maps/mymap.bsp", "BSP"));
        vfs.Rescan();

        Assert.True(vfs.Exists("maps/mymap.bsp"));
    }

    [Fact]
    public void Rescan_Preserves_Root_Precedence()
    {
        using var tree = new TempTree();
        tree.Pk3("shipped/core.pk3", ("textures/wall.tga", "SHIPPED"));
        tree.Pk3("user/override.pk3", ("textures/wall.tga", "USER"));

        using var vfs = new VirtualFileSystem();
        vfs.MountContentRoot(Path.Combine(tree.Root, "shipped"));
        vfs.MountContentRoot(Path.Combine(tree.Root, "user"));
        Assert.Equal("USER", Text(vfs, "textures/wall.tga"));

        // A rescan replays the recorded mount calls in order, so the layering survives it. Rebuilding the
        // path in any other order would silently hand precedence back to the shipped tree.
        tree.Pk3("user/extra.pk3", ("textures/floor.tga", "USER"));
        vfs.Rescan();

        Assert.Equal("USER", Text(vfs, "textures/wall.tga"));
        Assert.Equal("USER", Text(vfs, "textures/floor.tga"));
    }

    [Fact]
    public void Rescan_Keeps_A_Singly_Mounted_Pack()
    {
        using var tree = new TempTree();
        string pack = tree.Pk3("loose/extra.pk3", ("textures/wall.tga", "SINGLE"));

        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.Mount(pack));

        vfs.Rescan();

        // Mount() records its source too, so a rescan replays it instead of dropping the mount on the floor.
        Assert.Equal("SINGLE", Text(vfs, "textures/wall.tga"));
    }

    [Fact]
    public void Rescan_On_An_Empty_Vfs_Is_A_NoOp()
    {
        using var vfs = new VirtualFileSystem();
        VirtualFileSystem.RescanResult r = vfs.Rescan();
        Assert.Equal(0, r.Mounts);
        Assert.Equal(0, r.Added);
        Assert.Equal(0, r.Removed);
    }

    [Fact]
    public void Rescan_After_Dispose_Throws()
    {
        var vfs = new VirtualFileSystem();
        vfs.Dispose();
        Assert.Throws<ObjectDisposedException>(() => vfs.Rescan());
    }
}
