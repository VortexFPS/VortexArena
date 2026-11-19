using VortexArena.Common.Gameplay;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for §4.5: the persistent race/CTS record database (QC ServerProgsDB race_readTime/writeTime/setTime
/// ranking semantics) — insert, improve, break, fail, and the export/import round-trip.
/// </summary>
[Collection("GlobalState")]
public class RaceRecordsTests
{
    private const string Map = "stormkeep";
    private static string Type => RaceRecords.RaceRecord;

    public RaceRecordsTests() => RaceRecords.Clear();

    [Fact]
    public void FirstTime_SetsRank1()
    {
        var r = RaceRecords.SetTime(Map, Type, 42.0f, "uidA", "Alice");
        Assert.Equal(RaceRecordKind.NewSet, r.Kind);
        Assert.Equal(1, r.NewPos);
        Assert.True(r.IsServerRecord);
        Assert.Equal(42.0f, RaceRecords.ServerRecord(Map, Type));
        Assert.Equal("Alice", RaceRecords.ServerRecordHolder(Map, Type));
    }

    [Fact]
    public void FasterTime_BreaksRecordAndShiftsRanks()
    {
        RaceRecords.SetTime(Map, Type, 42.0f, "uidA", "Alice");      // rank 1
        var r = RaceRecords.SetTime(Map, Type, 40.0f, "uidB", "Bob"); // beats Alice
        Assert.Equal(RaceRecordKind.NewBroken, r.Kind);
        Assert.Equal(1, r.NewPos);
        Assert.Equal("Alice", r.OldRecordHolder);
        Assert.Equal(40.0f, RaceRecords.ReadTime(Map, Type, 1));
        Assert.Equal(42.0f, RaceRecords.ReadTime(Map, Type, 2));     // Alice shifted to rank 2
        Assert.Equal("uidB", RaceRecords.ReadUid(Map, Type, 1));
        Assert.Equal("uidA", RaceRecords.ReadUid(Map, Type, 2));
    }

    [Fact]
    public void SamePlayerImproves_KeepsSingleEntry()
    {
        RaceRecords.SetTime(Map, Type, 42.0f, "uidA", "Alice"); // rank 1
        var r = RaceRecords.SetTime(Map, Type, 39.0f, "uidA", "Alice"); // improves own
        Assert.Equal(RaceRecordKind.NewImproved, r.Kind);
        Assert.Equal(1, r.NewPos);
        Assert.Equal(39.0f, RaceRecords.ReadTime(Map, Type, 1));
        Assert.Equal(0f, RaceRecords.ReadTime(Map, Type, 2)); // no duplicate Alice entry
    }

    [Fact]
    public void SlowerThanOwnRecord_Fails()
    {
        RaceRecords.SetTime(Map, Type, 40.0f, "uidA", "Alice");
        var r = RaceRecords.SetTime(Map, Type, 45.0f, "uidA", "Alice"); // worse than own
        Assert.Equal(RaceRecordKind.Fail, r.Kind);
        Assert.Equal(40.0f, RaceRecords.ReadTime(Map, Type, 1)); // unchanged
    }

    [Fact]
    public void AnonymousPlayer_CannotRank()
    {
        var r = RaceRecords.SetTime(Map, Type, 30.0f, "", "Anon");
        Assert.Equal(RaceRecordKind.Fail, r.Kind);
        Assert.Equal(0f, RaceRecords.ReadTime(Map, Type, 1));
    }

    [Fact]
    public void RaceAndCts_AreSeparateTables()
    {
        RaceRecords.SetTime(Map, RaceRecords.RaceRecord, 42.0f, "uidA", "Alice");
        RaceRecords.SetTime(Map, RaceRecords.CtsRecord, 50.0f, "uidB", "Bob");
        Assert.Equal(42.0f, RaceRecords.ServerRecord(Map, RaceRecords.RaceRecord));
        Assert.Equal(50.0f, RaceRecords.ServerRecord(Map, RaceRecords.CtsRecord));
    }

    [Fact]
    public void ExportImport_RoundTrips()
    {
        RaceRecords.SetTime(Map, Type, 42.0f, "uidA", "Alice");
        RaceRecords.SetTime(Map, Type, 40.0f, "uidB", "Bob");
        string data = RaceRecords.Export();

        RaceRecords.Clear();
        Assert.Equal(0f, RaceRecords.ServerRecord(Map, Type));

        RaceRecords.Import(data);
        Assert.Equal(40.0f, RaceRecords.ReadTime(Map, Type, 1));
        Assert.Equal("Bob", RaceRecords.ReadName(Map, Type, 1));
        Assert.Equal("Alice", RaceRecords.ReadName(Map, Type, 2));
    }
    [Fact]
    public void Profile_ReloadsExistingRecordsAndPersistsScopedDeletionAcrossWorlds()
    {
        using var profile = new TemporaryProfile();
        string path = profile.File;
        File.WriteAllText(path, "R\tstormkeep/race100record/time1\t12.5\n"
            + "R\tstormkeep/race100record/uid1\tuidA\nN\tuidA\tOld name\n");
        var first = RaceRecordFile.Open(path);
        try
        {
            Assert.Equal(12.5f, RaceRecords.ServerRecord(Map, Type));
            RaceRecords.SetName("uidA", "^1New\tname");
            RaceRecords.SetTime(Map, Type, 14f, "uidB", "Runner");
            RaceRecords.SetTime(Map, RaceRecords.CtsRecord, 18f, "uidA", "^1New\tname");
            RaceRecords.SetTime(Map, RaceRecords.CtfRecord, 6f, "uidB", "Runner");
            RaceRecords.SetTime("other-map", Type, 8f, "uidC", "Other map");
            RaceRecords.WriteSpeedAwardBest(Map, Type, 700f, "uidA");
            RaceRecords.WriteCaptureBest(Map, 5.25f, "Cap holder");
        }
        finally { first.Close(cheated: false); }

        var rotated = RaceRecordFile.Open(path);
        try
        {
            Assert.Equal("^1New\tname", RaceRecords.ReadName(Map, Type, 1));
            Assert.Equal("^1New\tname", RaceRecords.ReadSpeedAwardBestHolder(Map, Type));
            Assert.Equal((5.25f, "Cap holder"), RaceRecords.ReadCaptureBest(Map));
            RaceRecords.DeleteTime(Map, Type, 1);
        }
        finally { rotated.Close(cheated: false); }

        var restarted = RaceRecordFile.Open(path);
        try
        {
            Assert.Equal(14f, RaceRecords.ServerRecord(Map, Type));
            Assert.Equal(0f, RaceRecords.ReadTime(Map, Type, 2));
            Assert.Equal(18f, RaceRecords.ServerRecord(Map, RaceRecords.CtsRecord));
            Assert.Equal(6f, RaceRecords.ServerRecord(Map, RaceRecords.CtfRecord));
            Assert.Equal(8f, RaceRecords.ServerRecord("other-map", Type));
            Assert.Equal(700f, RaceRecords.ReadSpeedAwardBest(Map, Type));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { restarted.Close(cheated: true); }
    }

    [Fact]
    public void Profile_CheatedWorldLeavesLastCleanSnapshotIntact()
    {
        using var profile = new TemporaryProfile();
        var clean = RaceRecordFile.Open(profile.File);
        RaceRecords.SetTime(Map, Type, 12f, "runner", "Runner");
        clean.Close(cheated: false);
        string saved = File.ReadAllText(profile.File);

        var cheated = RaceRecordFile.Open(profile.File);
        RaceRecords.SetTime(Map, Type, 1f, "runner", "Runner");
        cheated.Close(cheated: true);

        Assert.Equal(saved, File.ReadAllText(profile.File));
        var reloaded = RaceRecordFile.Open(profile.File);
        try { Assert.Equal(12f, RaceRecords.ServerRecord(Map, Type)); }
        finally { reloaded.Close(cheated: true); }
    }

    [Fact]
    public void Profile_InvalidExistingFileRemainsIntact()
    {
        using var profile = new TemporaryProfile();
        File.WriteAllText(profile.File, "foreign format");
        var session = RaceRecordFile.Open(profile.File);
        try { RaceRecords.SetTime(Map, Type, 12f, "runner", "Runner"); }
        finally { session.Close(cheated: false); }
        Assert.Equal("foreign format", File.ReadAllText(profile.File));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_InvalidUtf8InOrdinaryOrEncodedRowsPreservesExistingBytes(bool encoded)
    {
        using var profile = new TemporaryProfile();
        byte[] bytes = encoded
            ? "R\tknown\tvalid\nN2\tdWlk\t/w==\n"u8.ToArray()
            : [.. System.Text.Encoding.UTF8.GetPreamble(), .. "R\tknown\tvalid\nN\tuid\t"u8, 0xff, 10];
        File.WriteAllBytes(profile.File, bytes);
        var session = RaceRecordFile.Open(profile.File);
        try { RaceRecords.SetTime(Map, Type, 12f, "runner", "Runner"); }
        finally { session.Close(cheated: false); }
        Assert.Equal(bytes, File.ReadAllBytes(profile.File));
    }

    [Fact]
    public void Profile_ReplacementPreservesTheExistingModeAndAdjacentFiles()
    {
        using var profile = new TemporaryProfile();
        File.WriteAllText(profile.File, "N\tuid\tOld\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(profile.File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(profile.File + ".tmp", "existing file");
        var session = RaceRecordFile.Open(profile.File);
        try { RaceRecords.SetName("uid", "New"); }
        finally { session.Close(cheated: false); }
        Assert.Contains("N\tuid\tNew\n", File.ReadAllText(profile.File));
        Assert.Equal("existing file", File.ReadAllText(profile.File + ".tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(profile.File));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_HonorsUtf8PreambleAndPreservesForeignEncoding(bool utf8)
    {
        using var profile = new TemporaryProfile();
        var encoding = utf8 ? System.Text.Encoding.UTF8 : System.Text.Encoding.Unicode;
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("N\tuid\tBefore\n")];
        File.WriteAllBytes(profile.File, original);
        var session = RaceRecordFile.Open(profile.File);
        try { RaceRecords.SetName("uid", "After"); }
        finally { session.Close(cheated: false); }
        if (utf8)
            Assert.Equal("N\tuid\tAfter\n", File.ReadAllText(profile.File));
        else
            Assert.Equal(original, File.ReadAllBytes(profile.File));
    }

    [Fact]
    public void SessionId_SelectsASafeDistinctProfileFile()
    {
        Assert.Equal("race-records.tsv", RaceRecordFile.ProfileFileName(""));
        string path = RaceRecordFile.ProfileFileName("../another/profile");
        Assert.Equal(path, Path.GetFileName(path));
        Assert.NotEqual(path, RaceRecordFile.ProfileFileName("../another/Profile"));
    }

    private sealed class TemporaryProfile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("vortex-record-tests-").FullName;
        public string File => Path.Combine(_directory, "race-records.tsv");
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
