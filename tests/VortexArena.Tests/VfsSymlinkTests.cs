using System.IO;
using System.IO.Compression;
using System.Text;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Regression tests for pk3 (zip) SYMLINK resolution. Xonotic's build-time dedup
/// (<c>symlink-deduplicate.sh</c>) replaces duplicate assets with Unix symlinks; in the packed <c>.pk3</c>
/// such an entry carries the <c>S_IFLNK</c> mode in its external attributes and stores the TARGET PATH as its
/// body. The VFS must follow the link and return the target's bytes — not the path string — so e.g. a
/// deduped <c>*_norm.dds</c> resolves to the real DXT texture it points at. See
/// <see cref="VirtualFileSystem"/>'s Pk3Mount.
/// </summary>
public class VfsSymlinkTests
{
    // S_IFLNK | 0777 in the high 16 bits of the zip external attributes — what a Unix symlink entry carries.
    private const int SymlinkAttr = unchecked((int)0xA1FF0000);

    private static string WriteZip(params (string name, byte[] body, bool symlink)[] entries)
    {
        string path = Path.Combine(Path.GetTempPath(), "rebirth-vfs-" + Path.GetRandomFileName() + ".pk3");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, body, symlink) in entries)
            {
                ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.NoCompression);
                if (symlink)
                    e.ExternalAttributes = SymlinkAttr;
                using Stream s = e.Open();
                s.Write(body, 0, body.Length);
            }
        }
        return path;
    }

    [Fact]
    public void Symlink_Entry_Reads_Through_To_Target()
    {
        byte[] real = Encoding.ASCII.GetBytes("REAL-DDS-BYTES");
        // link -> sibling "real.dds"; the link body is the (relative) target path, as Xonotic packs it.
        string pk3 = WriteZip(
            ("textures/base/real.dds", real, false),
            ("textures/base/link.dds", Encoding.ASCII.GetBytes("real.dds"), true));
        try
        {
            using var vfs = new VirtualFileSystem();
            Assert.True(vfs.Mount(pk3));

            Assert.True(vfs.Exists("textures/base/link.dds"));
            // The link must read the TARGET's bytes, not the "real.dds" path-string body.
            Assert.Equal(real, vfs.ReadBytes("textures/base/link.dds"));
            // The real entry is of course unaffected.
            Assert.Equal(real, vfs.ReadBytes("textures/base/real.dds"));
        }
        finally { File.Delete(pk3); }
    }

    [Fact]
    public void Symlink_With_DotDot_Resolves_Relative_To_Link_Dir()
    {
        byte[] real = Encoding.ASCII.GetBytes("SHARED-PARENT-FILE");
        string pk3 = WriteZip(
            ("textures/shared.dds", real, false),
            ("textures/base/up.dds", Encoding.ASCII.GetBytes("../shared.dds"), true));
        try
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(pk3);
            Assert.Equal(real, vfs.ReadBytes("textures/base/up.dds"));
        }
        finally { File.Delete(pk3); }
    }

    [Fact]
    public void Symlink_Chain_Is_Followed()
    {
        byte[] real = Encoding.ASCII.GetBytes("END-OF-CHAIN");
        string pk3 = WriteZip(
            ("a/real.dds", real, false),
            ("a/mid.dds", Encoding.ASCII.GetBytes("real.dds"), true),
            ("a/head.dds", Encoding.ASCII.GetBytes("mid.dds"), true));
        try
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(pk3);
            Assert.Equal(real, vfs.ReadBytes("a/head.dds"));
        }
        finally { File.Delete(pk3); }
    }

    [Fact]
    public void NonSymlink_File_That_Looks_Like_A_Path_Is_Left_Alone()
    {
        // A regular file whose body happens to be a path string must NOT be treated as a symlink.
        byte[] body = Encoding.ASCII.GetBytes("real.dds");
        string pk3 = WriteZip(
            ("textures/base/real.dds", Encoding.ASCII.GetBytes("X"), false),
            ("textures/base/plain.dds", body, false));
        try
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(pk3);
            Assert.Equal(body, vfs.ReadBytes("textures/base/plain.dds"));
        }
        finally { File.Delete(pk3); }
    }

    /// <summary>
    /// The bug this whole path exists for: <c>shared.pk3</c> carries ~974 entries that WERE symlinks until the
    /// pack was zipped without preserving the S_IFLNK bit, 903 of them pointing at a real DDS. Every one used
    /// to read back as its 20-byte path string, fail to decode, and fall back to the uncompressed TGA beside
    /// it — or to nothing at all where no TGA existed.
    /// </summary>
    [Fact]
    public void StrippedSymlink_Bit_Still_Follows_When_The_Target_Is_A_Real_Image()
    {
        byte[] real = new byte[256];                       // plausibly an image: bigger than any header
        for (int i = 0; i < real.Length; i++) real[i] = (byte)i;
        byte[] stub = Encoding.ASCII.GetBytes("base_b_norm.dds");

        // NOTE the `false`: neither entry carries the symlink bit, which is exactly the shipped situation.
        string pk3 = WriteZip(
            ("dds/textures/x/base_b_norm.dds", real, false),
            ("dds/textures/x/base_a_norm.dds", stub, false));
        try
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(pk3);
            Assert.Equal(real, vfs.ReadBytes("dds/textures/x/base_a_norm.dds"));

            // And both names must resolve to ONE vpath, or the texture cache keys them separately and
            // decodes + uploads the same pixels twice — which is what made this worth fixing in the VFS
            // rather than in the image loader.
            Assert.Equal(vfs.ResolveImage("textures/x/base_b_norm"),
                         vfs.ResolveImage("textures/x/base_a_norm"));
        }
        finally { File.Delete(pk3); }
    }

    /// <summary>
    /// The discriminator that keeps the above from swallowing ordinary files. A stripped link and a small
    /// regular file whose body happens to be a path are byte-identical, so the only thing separating them is
    /// what they point AT: a real link points at a real image. A DECLARED symlink is trusted regardless —
    /// the archive said it is a link.
    /// </summary>
    [Fact]
    public void StrippedSymlink_Is_Not_Followed_When_The_Target_Is_Too_Small_To_Be_An_Image()
    {
        byte[] tiny = Encoding.ASCII.GetBytes("X");        // 1 byte: cannot be any image
        byte[] body = Encoding.ASCII.GetBytes("real.dds");
        string pk3 = WriteZip(
            ("textures/base/real.dds", tiny, false),
            ("textures/base/plain.dds", body, false));
        try
        {
            using var vfs = new VirtualFileSystem();
            vfs.Mount(pk3);
            Assert.Equal(body, vfs.ReadBytes("textures/base/plain.dds"));
        }
        finally { File.Delete(pk3); }
    }

    [Fact]
    public void RealData_DedupSymlink_NowReadsAsRealDds()
    {
        string dataDir = TestPaths.Data;
        if (!Directory.Exists(dataDir)) return;

        using var vfs = new VirtualFileSystem();
        vfs.MountGameDir(dataDir);

        // A known deduped normal map in the shipped maps pk3 (a symlink -> base_base1c_norm.dds).
        const string link = "dds/textures/trak5x/base/base_base1b_norm.dds";
        if (!vfs.Exists(link)) return; // maps pack not present in this checkout

        byte[] bytes = vfs.ReadBytes(link);
        // Following the symlink yields the real DXT-compressed DDS (begins with the "DDS " magic), not the
        // ~20-byte target-path stub it used to return.
        Assert.True(bytes.Length > 100, $"expected real DDS bytes, got {bytes.Length}");
        Assert.True(bytes[0] == (byte)'D' && bytes[1] == (byte)'D' && bytes[2] == (byte)'S' && bytes[3] == (byte)' ',
            "expected the DDS magic after following the dedup symlink");
    }
}
