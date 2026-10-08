using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Legacy.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// Runs the DP7 parser over real recordings: the two demos shipped in the reference Xonotic checkout
/// (Base/data/xonotic-data.pk3dir/demos). They are the byte stream a DarkPlaces client received from a
/// Xonotic server, so every message in them is one the parser must cope with.
///
/// Two payloads in that stream are defined by the game's QuakeC and have no length (svc_csqcentities,
/// and the Xonotic temp entities, ids 80 and up). Without a QuakeC VM a message cannot be read past
/// one of those, so such a message counts as "cut short", not as an error. Everything before the cut
/// is engine protocol, and an error there is a decoder bug.
/// </summary>
public class DpDemoTests
{
    private readonly ITestOutputHelper _output;

    public DpDemoTests(ITestOutputHelper output) => _output = output;

    private static string DemoPath(string name) => Path.Combine(TestPaths.BaseCorePk3Dir, "demos", name);

    /// <summary>Counts what the parser saw, and stops at anything only QuakeC can measure.</summary>
    private sealed class StatsHandler : IDpClientHandler
    {
        public DpServerInfo? ServerInfo;
        public int ServerInfos;
        public readonly List<int> Signons = new();
        public readonly DpStuffTextBuffer Stuff = new();
        public readonly List<string> StuffLines = new();
        public readonly List<string> Prints = new();
        public int EntityFrames, EntitiesChanged, Baselines, Statics, StaticSounds, Sounds, EngineTempEntities, DownloadBlocks;
        public long DownloadBytes;
        public int FirstEntityFrameMessage = -1;
        public int MessageIndex;
        public readonly DpDownload Download = new();
        public bool DownloadCorrupt;

        public void OnServerInfo(DpServerInfo info) { ServerInfo ??= info; ServerInfos++; }
        public void OnSignonNum(int stage) => Signons.Add(stage);
        public void OnStuffText(string text) => Stuff.Add(text, StuffLines);
        public void OnPrint(string text) { if (Prints.Count < 4) Prints.Add(text); }
        public void OnSpawnBaseline(int entity, in EntityState baseline) => Baselines++;
        public void OnSpawnStatic(in EntityState state) => Statics++;
        public void OnSpawnStaticSound(in DpStaticSound sound) => StaticSounds++;
        public void OnSound(in DpSound sound) => Sounds++;
        public void OnEngineTempEntity(in DpTempEntity tempEntity) => EngineTempEntities++;

        public void OnDownloadData(int start, ReadOnlySpan<byte> data)
        {
            DownloadBlocks++;
            DownloadBytes += data.Length;
            if (!Download.OnData(start, data))
                DownloadCorrupt = true;
        }

        public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities)
        {
            if (FirstEntityFrameMessage < 0)
                FirstEntityFrameMessage = MessageIndex;
            EntityFrames++;
            EntitiesChanged += frame.Changed.Count;
        }

        // Xonotic numbers its own temp entities from 80 (qcsrc/lib/net.qh: REGISTRY(TempEntities,
        // BITS(8) - 80), m_id = 80 + index) and its CSQC_Parse_TempEntity returns false for anything
        // else, so an id below 80 is the engine's to decode.
        public DpPayloadResult OnTempEntity(DpMessageReader reader)
        {
            int type = reader.ReadByte();
            return type >= 80 ? DpPayloadResult.Abort : DpPayloadResult.NotHandled;
        }
        // OnCsqcEntityUpdate is left at its default: Abort.
    }

    private sealed class DemoStats
    {
        public string Name = "";
        public int Messages, Complete, CutAtCsqcEntities, CutAtTempEntity, Errors;
        public long Bytes, BytesDecoded;
        public readonly SortedDictionary<int, int> SvcCounts = new();
        public readonly List<string> ErrorDetails = new();
        public StatsHandler Handler = new();
        public string? ReaderError;
        public int ForceTrack;
        public int SkippedClientMessages;
        public int CsprogsSize = -1, CsprogsCrc = -1;
        public string CsprogsName = "";
        public DpDownloadResult? EmbeddedCsprogs;
        public int ErrorsBeforeFirstEntityFrame;
    }

    private static DemoStats Run(string path)
    {
        var stats = new DemoStats { Name = Path.GetFileName(path) };
        var handler = stats.Handler;
        var parser = new DpServerMessageParser(handler);
        parser.CommandTrace = (svc, _) => stats.SvcCounts[svc] = stats.SvcCounts.GetValueOrDefault(svc) + 1;
        var signon = new DpSignon(new DpSignonConfig(), handler.Download);

        using var stream = File.OpenRead(path);
        var demo = new DpDemoReader(stream);
        stats.ForceTrack = demo.ForceTrack;
        while (demo.TryReadMessage(out DpDemoMessage message))
        {
            handler.MessageIndex = stats.Messages;
            var reader = new DpMessageReader(message.Data);
            DpParseResult result = parser.Parse(reader);
            stats.Bytes += message.Data.Length;
            switch (result.Status)
            {
                case DpParseStatus.Complete:
                    stats.Complete++;
                    stats.BytesDecoded += message.Data.Length;
                    break;
                case DpParseStatus.Aborted:
                    if (result.Svc == (int)Svc.CsqcEntities) stats.CutAtCsqcEntities++;
                    else stats.CutAtTempEntity++;
                    stats.BytesDecoded += result.Offset;
                    break;
                default:
                    stats.Errors++;
                    if (handler.FirstEntityFrameMessage < 0)
                        stats.ErrorsBeforeFirstEntityFrame++;
                    if (stats.ErrorDetails.Count < 20)
                        stats.ErrorDetails.Add($"message {stats.Messages} (file offset {message.FileOffset}, {message.Data.Length} bytes): svc {result.Svc} at offset {result.Offset}: {result.Message}");
                    break;
            }

            // Stuffed commands run after the message, as in the client.
            foreach (string line in handler.StuffLines)
            {
                bool finishing = line.TrimStart().StartsWith("cl_downloadfinished", StringComparison.Ordinal);
                signon.HandleCommand(line);
                if (finishing && signon.LastDownload is not null)
                    stats.EmbeddedCsprogs = signon.LastDownload;
            }
            handler.StuffLines.Clear();
            signon.Commands.Clear();
            stats.Messages++;
        }
        stats.ReaderError = demo.Error;
        stats.SkippedClientMessages = demo.SkippedClientMessages;
        stats.CsprogsName = signon.CsqcProgName;
        stats.CsprogsSize = signon.CsqcProgSize;
        stats.CsprogsCrc = signon.CsqcProgCrc;
        return stats;
    }

    private static string Describe(DemoStats s)
    {
        var sb = new StringBuilder();
        var h = s.Handler;
        sb.AppendLine($"=== {s.Name} ===");
        sb.AppendLine($"header track {s.ForceTrack}; reader error: {s.ReaderError ?? "none"}; client-to-server blocks skipped: {s.SkippedClientMessages}");
        sb.AppendLine($"messages: {s.Messages} ({s.Bytes} bytes); fully decoded: {s.Complete}; cut short at svc_csqcentities: {s.CutAtCsqcEntities}; cut short at a QuakeC temp entity: {s.CutAtTempEntity}; parse errors: {s.Errors}");
        sb.AppendLine($"bytes decoded before any cut: {s.BytesDecoded} of {s.Bytes} ({(s.Bytes == 0 ? 0 : 100.0 * s.BytesDecoded / s.Bytes):F1}%)");
        if (h.ServerInfo is { } info)
            sb.AppendLine($"serverinfo x{h.ServerInfos}: protocol {info.Protocol}, maxclients {info.MaxClients}, gametype {info.GameType}, world \"{info.WorldMessage}\", map model \"{info.WorldModel}\", {info.Models.Count - 1} models, {info.Sounds.Count - 1} sounds");
        sb.AppendLine($"signon stages seen: [{string.Join(",", h.Signons)}]; first entity frame in message {h.FirstEntityFrameMessage}");
        sb.AppendLine($"csqc_progname {s.CsprogsName}, csqc_progsize {s.CsprogsSize}, csqc_progcrc {s.CsprogsCrc}");
        sb.AppendLine($"download blocks {h.DownloadBlocks} ({h.DownloadBytes} bytes); embedded csprogs: {(s.EmbeddedCsprogs is { } d ? $"{d.Status}, {d.Data.Length} bytes, crc {d.Crc}" : "none")}");
        sb.AppendLine($"entity frames {h.EntityFrames} ({h.EntitiesChanged} entity updates), baselines {h.Baselines}, statics {h.Statics}, static sounds {h.StaticSounds}, sounds {h.Sounds}, engine temp entities {h.EngineTempEntities}");
        sb.AppendLine("per-svc counts (id name: count): " + string.Join(", ",
            s.SvcCounts.Select(kv => $"{kv.Key} {(Enum.IsDefined(typeof(Svc), (byte)kv.Key) ? ((Svc)kv.Key).ToString() : "?")}: {kv.Value}")));
        foreach (string e in s.ErrorDetails)
            sb.AppendLine("  ERROR " + e);
        return sb.ToString();
    }

    [Theory]
    [InlineData("little-bot-orchestra.dem")]
    [InlineData("the-big-keybench.dem")]
    public void Real_Demo_Decodes_Without_Parse_Errors(string name)
    {
        string path = DemoPath(name);
        if (!File.Exists(path))
            return; // reference checkout not present on this machine

        DemoStats s = Run(path);
        string report = Describe(s);
        _output.WriteLine(report);
        // Also kept on disk so the numbers can be quoted without re-running with a verbose logger.
        try
        {
            // DP_DEMO_STATS_DIR is for runs whose binaries live outside the repo (a private artifacts
            // path), where there is no repo root to find.
            string scratch = Environment.GetEnvironmentVariable("DP_DEMO_STATS_DIR") ?? Path.Combine(TestPaths.RepoRoot, "_scratch");
            if (Directory.Exists(scratch))
                File.WriteAllText(Path.Combine(scratch, $"dp-demo-stats-{Path.GetFileNameWithoutExtension(name)}.txt"), report);
        }
        catch (IOException) { }

        Assert.Null(s.ReaderError);
        Assert.True(s.Messages > 100, "a real demo has many messages");

        // The level description.
        Assert.NotNull(s.Handler.ServerInfo);
        DpServerInfo info = s.Handler.ServerInfo!;
        Assert.Equal(DpProtocol.ProtocolNumberDp7, info.Protocol);
        Assert.InRange(info.MaxClients, 1, 255);
        Assert.StartsWith("maps/", info.WorldModel);
        Assert.EndsWith(".bsp", info.WorldModel);
        Assert.True(info.Models.Count > 2, "model precache list");
        Assert.True(info.Sounds.Count > 2, "sound precache list");
        Assert.All(info.Models.Skip(1), m => Assert.False(string.IsNullOrEmpty(m)));

        // Signon ran to the end and entity frames followed.
        // Stages 1 and 2 are decodable. Stage 3 is not asserted: in both recordings the reliable message
        // that carries it also carries Xonotic temp entities ahead of it, so without QuakeC the parser
        // never gets that far into the message.
        Assert.Contains(1, s.Handler.Signons);
        Assert.Contains(2, s.Handler.Signons);
        Assert.True(s.Handler.EntityFrames > 0, "entity frames");

        // The recording embeds the client program it was made with (CL_VM_Init writes it into the demo
        // as a cl_downloadbegin / svc_downloaddata / cl_downloadfinished sequence). Reassembling it and
        // matching the size and CRC16 the server announced exercises the download path, the stufftext
        // handling and CRC_Block against bytes DarkPlaces itself produced.
        Assert.NotNull(s.EmbeddedCsprogs);
        Assert.Equal(DpDownloadStatus.Completed, s.EmbeddedCsprogs!.Status);
        Assert.Equal(s.CsprogsSize, s.EmbeddedCsprogs.Data.Length);
        Assert.Equal(s.CsprogsCrc, s.EmbeddedCsprogs.Crc);
        Assert.Equal(0, s.ErrorsBeforeFirstEntityFrame);

        // Any error at all is inside engine-defined protocol (QuakeC payloads abort, they do not
        // error), so none is acceptable.
        Assert.True(s.Errors == 0, report);
        Assert.False(s.Handler.DownloadCorrupt);
    }

    [Fact]
    public void Demo_Reader_Reads_Header_Skips_Client_Blocks_And_Reports_Truncation()
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("-1\n"));
        void Block(uint length, byte[] data)
        {
            ms.Write(BitConverter.GetBytes(length));
            ms.Write(BitConverter.GetBytes(10f));
            ms.Write(BitConverter.GetBytes(20f));
            ms.Write(BitConverter.GetBytes(30f));
            ms.Write(data);
        }
        Block(3, new byte[] { 1, 1, 1 });
        Block(0x80000000u | 2, new byte[] { 9, 9 }); // client-to-server: skipped
        Block(2, new byte[] { 7, 8 });
        ms.Write(BitConverter.GetBytes(50u));       // a block that promises more than the file has
        ms.Write(new byte[12 + 5]);
        ms.Position = 0;

        var demo = new DpDemoReader(ms);
        Assert.Equal(-1, demo.ForceTrack);
        Assert.True(demo.TryReadMessage(out DpDemoMessage a));
        Assert.Equal(new byte[] { 1, 1, 1 }, a.Data);
        Assert.Equal(new System.Numerics.Vector3(10, 20, 30), a.ViewAngles);
        Assert.Equal(3, a.FileOffset);
        Assert.True(demo.TryReadMessage(out DpDemoMessage b));
        Assert.Equal(new byte[] { 7, 8 }, b.Data);
        Assert.Equal(1, demo.SkippedClientMessages);
        Assert.False(demo.TryReadMessage(out _));
        Assert.NotNull(demo.Error);
        Assert.False(demo.TryReadMessage(out _)); // stays ended
    }

    [Fact]
    public void Demo_Reader_Ends_Cleanly_And_Rejects_Oversized_Block()
    {
        var clean = new DpDemoReader(new MemoryStream(Encoding.ASCII.GetBytes("12\n")));
        Assert.Equal(12, clean.ForceTrack);
        Assert.False(clean.TryReadMessage(out _));
        Assert.Null(clean.Error);

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("0\n"));
        ms.Write(BitConverter.GetBytes(DpProtocol.NetMaxMessage + 1));
        ms.Write(new byte[64]);
        ms.Position = 0;
        var big = new DpDemoReader(ms);
        Assert.False(big.TryReadMessage(out _));
        Assert.Contains("maxsize", big.Error);

        var notADemo = new DpDemoReader(new MemoryStream(new byte[64]));
        Assert.NotNull(notADemo.Error);
        Assert.False(notADemo.TryReadMessage(out _));
    }
}
