using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="MasterAnnounce.RequestCatalogRefresh"/> — the hook <c>fs_rescan</c> uses so a server's map
/// catalog follows the packages it now has mounted, instead of describing the boot-time pool until the next
/// map change. The announce itself goes to a closed local port and fails, which is irrelevant here: the
/// catalog is built on its own worker either way.
/// </summary>
public class MasterAnnounceCatalogRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vx-catalog-refresh-" + Guid.NewGuid().ToString("N"));

    public MasterAnnounceCatalogRefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp dir; leave it */ }
    }

    private string Pack(string name, string bsp)
    {
        string path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry($"maps/{bsp}.bsp").Open());
            w.Write(bsp); // distinct bytes per pack, so each hashes to its own package
        }
        return path;
    }

    private static MasterAnnounce.AnnounceSnapshot Snapshot(IReadOnlyList<string> packages) => new(
        MasterUrl: "http://127.0.0.1:9", Port: 26000, Hostname: "test", Map: "dance", Gametype: "dm",
        Players: 0, Bots: 0, MaxPlayers: 8, GameVersion: "test", NetProtocol: 1, Mutators: Array.Empty<string>(),
        SvPublic: 1, PasswordProtected: false, AvailableForControl: false, ControlKeyFingerprint: null,
        ReportCatalog: true, PackagePaths: packages, CatalogDownloadUrl: null);

    private static bool WaitFor(Func<bool> condition, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void A_Requested_Refresh_Rescans_The_Packages_Mounted_Now()
    {
        var mounted = new List<string> { Pack("a.pk3", "a") };
        using var announce = new MasterAnnounce(() => Snapshot(mounted.ToArray()), _ => { });

        announce.Tick(); // first tick: announce due, and the startup scan
        Assert.True(WaitFor(() => announce.Catalog?.Entries.Count == 1), "startup scan never finished");

        // fs_rescan mounted another pack. Ticking alone must not rescan (that is the 180 s announce path's
        // whole point), so the catalog still describes the old pool...
        mounted.Add(Pack("b.pk3", "b"));
        announce.Tick();
        Thread.Sleep(200);
        Assert.Equal(1, announce.Catalog!.Entries.Count);

        // ...until the rescan asks, from whichever thread it ran on; the next tick starts the rebuild.
        announce.RequestCatalogRefresh();
        announce.Tick();
        Assert.True(WaitFor(() => announce.Catalog?.Entries.Count == 2), "the requested refresh never landed");
    }

    [Fact]
    public void A_Refresh_Requested_During_A_Scan_Runs_After_It()
    {
        var mounted = new List<string> { Pack("a.pk3", "a") };
        using var announce = new MasterAnnounce(() => Snapshot(mounted.ToArray()), _ => { });

        announce.Tick();                  // starts the startup scan of { a }
        mounted.Add(Pack("b.pk3", "b"));
        announce.RequestCatalogRefresh(); // may land while that scan is still running

        // Keep ticking the way the server's master pump does; the request must survive until it can run.
        Assert.True(WaitFor(() =>
        {
            announce.Tick();
            return announce.Catalog?.Entries.Count == 2;
        }), "the refresh was dropped while a scan was in flight");
    }
}
