using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.5: the persistent race/CTS record database (QC ServerProgsDB race_readTime/writeTime/setTime
/// ranking semantics) — insert, improve, break, fail, and the export/import round-trip.
/// </summary>
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
}
