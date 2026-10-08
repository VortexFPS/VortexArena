using System;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// <see cref="LegacyQcHost"/> is where a server-supplied QuakeC program's reach ends: what it can read,
/// where it can write, which cvars it can see. Tested from the program's side, as an adversary.
/// </summary>
public class LegacyQcHostTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "va-legacyhost-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string Write => Path.Combine(Root, "user");
        public CvarService Cvars { get; } = new();
        public VirtualFileSystem Vfs { get; } = new();
        public LegacyQcHost Host { get; }

        public Fixture(long writeBudget = 1 << 20)
        {
            Directory.CreateDirectory(Path.Combine(Data, "gfx", "hud"));
            File.WriteAllText(Path.Combine(Data, "gfx", "hud", "panel.tga"), "tga");
            File.WriteAllText(Path.Combine(Data, "gfx", "hud", "border.tga"), "tga");
            File.WriteAllText(Path.Combine(Data, "effectinfo.txt"), "effect one\n");
            File.WriteAllText(Path.Combine(Root, "secret.txt"), "outside the data area");
            Assert.True(Vfs.Mount(Data));
            Host = new LegacyQcHost(Cvars, Vfs) { WriteRoot = Write, WriteBudgetBytes = writeBudget };
        }

        public void Dispose()
        {
            Vfs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("gfx/../../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("gfx\\hud\\panel.tga")]
    [InlineData("gfx//hud/panel.tga")]
    [InlineData("./effectinfo.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("")]
    [InlineData("a\0b")]
    public void UnsafePaths_AreRefusedForReadingWritingAndSearching(string path)
    {
        using Fixture f = new();
        Assert.False(LegacyQcHost.IsSafePath(path));
        Assert.Null(f.Host.OpenRead(path));
        Assert.Null(f.Host.OpenWrite(path, append: false));
        Assert.Empty(f.Host.Search(path, caseInsensitive: true, packFile: null));
        Assert.False(Directory.Exists(f.Write) && Directory.EnumerateFileSystemEntries(f.Write).Any());
    }

    [Fact]
    public void Reads_ComeFromMountedData_AndWritesStayUnderTheWriteRoot()
    {
        using Fixture f = new();
        using (Stream packaged = f.Host.OpenRead("effectinfo.txt")!)
            Assert.Equal("effect one\n", new StreamReader(packaged).ReadToEnd());
        Assert.Null(f.Host.OpenRead("no/such/file.txt"));

        using (Stream output = f.Host.OpenWrite("data/client.db", append: false)!)
            output.Write(Encoding.UTF8.GetBytes("saved"));
        Assert.Equal("saved", File.ReadAllText(Path.Combine(f.Write, "data", "client.db")));

        // What the program wrote is what it reads back, ahead of packaged data of the same name.
        using (Stream output = f.Host.OpenWrite("effectinfo.txt", append: false)!)
            output.Write(Encoding.UTF8.GetBytes("mine"));
        using (Stream mine = f.Host.OpenRead("effectinfo.txt")!)
            Assert.Equal("mine", new StreamReader(mine).ReadToEnd());
        Assert.Equal("effect one\n", File.ReadAllText(Path.Combine(f.Data, "effectinfo.txt"))); // the packaged file is untouched
    }

    [Fact]
    public void Writes_AreRefusedWithoutAWriteRoot_AndStopAtTheBudget()
    {
        using Fixture f = new(writeBudget: 100);
        LegacyQcHost readOnly = new(f.Cvars, f.Vfs);
        Assert.Null(readOnly.OpenWrite("data/x.txt", append: false));

        using Stream output = f.Host.OpenWrite("data/big.txt", append: false)!;
        output.Write(new byte[80]);
        Assert.Throws<IOException>(() => output.Write(new byte[80]));
    }

    [Fact]
    public void Search_MatchesGlobsOverMountedFiles()
    {
        using Fixture f = new();
        Assert.Equal(new[] { "gfx/hud/border.tga", "gfx/hud/panel.tga" }, f.Host.Search("gfx/hud/*.tga", true, null));
        Assert.Equal(new[] { "gfx/hud/panel.tga" }, f.Host.Search("gfx/hud/p?nel.tga", true, null));
        Assert.Empty(f.Host.Search("gfx/hud/*.png", true, null));
        // A wildcard does not cross a directory separator, as in DarkPlaces.
        Assert.Empty(f.Host.Search("gfx/*.tga", true, null));
        Assert.Empty(f.Host.Search("gfx?hud/panel.tga", true, null));
        Assert.Equal(2, f.Host.Search("gfx/*/*.tga", true, null).Count);
    }

    [Fact]
    public void PrivateCvars_AreInvisibleAndUnwritable()
    {
        using Fixture f = new();
        f.Cvars.Register("rcon_password", "hunter2");
        f.Cvars.Register("hud_fontsize", "11");
        string? warning = null;
        LegacyQcHost host = new(f.Cvars, f.Vfs) { WarningSink = w => warning = w };

        Assert.False(host.CvarExists("rcon_password"));
        Assert.Equal("", host.CvarString("rcon_password"));
        Assert.Equal(0, host.CvarTypeFlags("rcon_password"));
        Assert.DoesNotContain("rcon_password", host.CvarNames("", ""));
        host.CvarSet("rcon_password", "owned");
        Assert.Equal("hunter2", f.Cvars.GetString("rcon_password"));
        Assert.NotNull(warning);
        Assert.False(host.RegisterCvar("rcon_address", "1.2.3.4", 0));

        Assert.Equal("11", host.CvarString("hud_fontsize"));
        host.CvarSet("hud_fontsize", "14");
        Assert.Equal(14f, host.CvarFloat("hud_fontsize"));
    }

    [Fact]
    public void RegisterCvar_CreatesOnce_AndSettingAnUnknownCvarDoesNothing()
    {
        using Fixture f = new();
        Assert.True(f.Host.RegisterCvar("_cl_mod_thing", "3", 0));
        Assert.False(f.Host.RegisterCvar("_cl_mod_thing", "9", 0));
        Assert.Equal("3", f.Host.CvarString("_cl_mod_thing"));
        Assert.Equal(1, f.Host.CvarTypeFlags("_cl_mod_thing") & 9); // exists, and not an engine cvar

        f.Host.CvarSet("never_registered", "1");
        Assert.False(f.Cvars.Has("never_registered"));
    }

    [Fact]
    public void LocalCommands_AreQueuedForTheHost_NotExecuted()
    {
        using Fixture f = new();
        f.Host.LocalCommand("alias foo bar\n");
        f.Host.LocalCommand("cl_cmd settemp x 1\n");
        Assert.Equal(new[] { "alias foo bar\n", "cl_cmd settemp x 1\n" }, f.Host.TakePendingCommands());
        Assert.Empty(f.Host.TakePendingCommands());
    }
}
