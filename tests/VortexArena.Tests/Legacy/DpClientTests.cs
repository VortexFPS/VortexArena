using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The client-to-server writers, the stufftext/signon/download state machines, and the whole
/// <see cref="DpClient"/> against a scripted server built from the same netchan.
/// </summary>
public class DpClientTests
{
    // ---------------------------------------------------------------- clc writers

    [Fact]
    public void Simple_Clc_Messages()
    {
        var w = new DpMessageWriter();
        DpClientMessages.WriteNop(w);
        DpClientMessages.WriteDisconnect(w);
        DpClientMessages.WriteStringCommand(w, "say hi");
        DpClientMessages.WriteAckFrame(w, 0x01020304);
        DpClientMessages.WriteAckDownloadData(w, 70000, 1400);
        Assert.Equal(new byte[]
        {
            1,
            2,
            4, (byte)'s', (byte)'a', (byte)'y', (byte)' ', (byte)'h', (byte)'i', 0,
            50, 4, 3, 2, 1,
            51, 0x70, 0x11, 0x01, 0x00, 0x78, 0x05,
        }, w.ToArray());
    }

    [Fact]
    public void Move_Is_56_Bytes_In_The_DP7_Layout()
    {
        var cmd = new DpUserCmd
        {
            Sequence = 0x0A0B0C0D,
            Predicted = true,
            Time = 2.5f,
            ViewAngles = new Vector3(90, -90, 0),
            ForwardMove = 400, SideMove = -350, UpMove = 0.5f,
            Buttons = 0x00010203,
            Impulse = 9,
            CursorScreen = new Vector2(1, -1),
            CursorStart = new Vector3(1, 2, 3),
            CursorImpact = new Vector3(4, 5, 6),
            CursorEntity = 0x1234,
        };
        var w = new DpMessageWriter();
        DpClientMessages.WriteMove(w, cmd);
        byte[] b = w.ToArray();
        Assert.Equal(DpClientMessages.MoveSize, b.Length);
        Assert.Equal(56, b.Length);

        var r = new DpMessageReader(b);
        Assert.Equal(3, r.ReadByte());                 // clc_move
        Assert.Equal(0x0A0B0C0D, r.ReadLong());        // sequence
        Assert.Equal(2.5f, r.ReadFloat());             // time
        Assert.Equal(new Vector3(90, -90, 0), r.ReadAngles());
        Assert.Equal(400, r.ReadShort());
        Assert.Equal(-350, r.ReadShort());
        Assert.Equal(1, r.ReadShort());                // 0.5 rounds away from zero
        Assert.Equal(0x00010203, r.ReadLong());        // buttons
        Assert.Equal(9, r.ReadByte());                 // impulse
        Assert.Equal(32767, r.ReadShort());            // cursor x
        Assert.Equal(-32767, r.ReadShort());           // cursor y
        Assert.Equal(new Vector3(1, 2, 3), r.ReadVector());
        Assert.Equal(new Vector3(4, 5, 6), r.ReadVector());
        Assert.Equal(0x1234, r.ReadUShort());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Unpredicted_Move_Sends_Sequence_Zero_And_Wild_Cursor_Saturates()
    {
        var w = new DpMessageWriter();
        DpClientMessages.WriteMove(w, new DpUserCmd { Sequence = 77, Predicted = false, CursorScreen = new Vector2(50, float.NaN) });
        var r = new DpMessageReader(w.ToArray());
        r.ReadByte();
        Assert.Equal(0, r.ReadLong());
        Assert.Equal(56, w.Length);
    }

    [Fact]
    public void Input_Packet_Is_Moves_Then_Frame_Acks_Then_Download_Acks_And_Skips_Stale_Moves()
    {
        var moves = new[]
        {
            new DpUserCmd { Sequence = 8, Predicted = true },   // already applied by the server: stale
            new DpUserCmd { Sequence = 10, Predicted = true },
            new DpUserCmd { Sequence = 11, Predicted = true },
        };
        var w = new DpMessageWriter();
        DpClientMessages.WriteInputPacket(w, moves, serverMoveSequence: 10, new[] { 500, 501 }, new[] { new DpDownloadAck(2800, 1400), new DpDownloadAck(4200, 0) });
        byte[] b = w.ToArray();
        Assert.Equal(2 * 56 + 2 * 5 + 2 * 7, b.Length);
        var parsed = FakeServer.ParseClientMessage(b);
        Assert.Equal(new[] { "move 10", "move 11", "ackframe 500", "ackframe 501", "ackdl 2800 1400", "ackdl 4200 0" }, parsed);
    }

    // ---------------------------------------------------------------- stufftext

    private static List<string> Lines(DpStuffTextBuffer buffer, params string[] pieces)
    {
        var lines = new List<string>();
        foreach (string p in pieces)
            buffer.Add(p, lines);
        return lines;
    }

    [Fact]
    public void StuffText_Splits_On_Newline_Return_And_Unquoted_Semicolon()
    {
        var b = new DpStuffTextBuffer();
        Assert.Equal(new[] { "a 1", "b 2", "c 3", "say \"x;y\"", "d" }, Lines(b, "a 1\nb 2;c 3\r\nsay \"x;y\"\n\n\nd\n"));
        Assert.Equal("", b.Pending);
    }

    [Fact]
    public void StuffText_Line_Split_Across_Messages_Is_Reassembled()
    {
        var b = new DpStuffTextBuffer();
        var lines = new List<string>();
        b.Add("\ncl_downloadbegin 12", lines);
        Assert.Empty(lines);
        Assert.Equal("cl_downloadbegin 12", b.Pending);
        b.Add("345 csprogs.dat deflate\ncsqc_prog", lines);
        Assert.Equal(new[] { "cl_downloadbegin 12345 csprogs.dat deflate" }, lines);
        b.Add("", lines);
        b.Add("\n", lines); // "all I got was this lousy \n"
        Assert.Equal(new[] { "cl_downloadbegin 12345 csprogs.dat deflate", "csqc_prog" }, lines);
    }

    [Fact]
    public void StuffText_Comments_Are_Dropped_But_Urls_Survive()
    {
        var b = new DpStuffTextBuffer();
        Assert.Equal(new[] { "a ", "curl --pak http://example.org/x.pk3", "b" },
            Lines(b, "a // comment; still comment\ncurl --pak http://example.org/x.pk3\n// whole line\nb\n"));
    }

    [Fact]
    public void StuffText_Unterminated_Flood_Is_Bounded()
    {
        var b = new DpStuffTextBuffer();
        var lines = new List<string>();
        string chunk = new string('x', 10000);
        for (int i = 0; i < 100; i++)
            b.Add(chunk, lines);
        Assert.Empty(lines);
        Assert.True(b.Pending.Length <= DpStuffTextBuffer.MaxPending);
    }

    [Fact]
    public void Tokenize_Args_And_Expand()
    {
        Assert.Equal(new[] { "name", "a \"b\" \\c", "x" }, DpStuffText.Tokenize("  name \"a \\\"b\\\" \\\\c\"\tx // rest"));
        Assert.Empty(DpStuffText.Tokenize("   "));
        Assert.Equal(new[] { "unterminated quote" }, DpStuffText.Tokenize("\"unterminated quote"));
        Assert.Equal("clientversion $gameversion", DpStuffText.ArgsAfterFirst("cmd   clientversion $gameversion"));
        Assert.Equal("", DpStuffText.ArgsAfterFirst("cmd"));
        Assert.Equal("b c", DpStuffText.ArgsAfterFirst("\"a a\" b c"));

        var vars = new Dictionary<string, string> { ["gameversion"] = "806", ["x"] = "1" };
        Assert.Equal("clientversion 806", DpStuffText.Expand("clientversion $gameversion", vars));
        Assert.Equal("a 806b $nope $ 1$", DpStuffText.Expand("a ${gameversion}b $nope $$ $x$", vars));
        Assert.Equal("${open", DpStuffText.Expand("${open", vars));
        Assert.Equal("plain", DpStuffText.Expand("plain", vars));
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("  -45xyz", -45)]
    [InlineData("+7", 7)]
    [InlineData("abc", 0)]
    [InlineData("", 0)]
    [InlineData("99999999999999999999", int.MaxValue)]
    [InlineData("-99999999999999999999", int.MinValue)]
    public void Atoi_Behaves_Like_C(string text, int expected) => Assert.Equal(expected, DpStuffText.Atoi(text));

    // ---------------------------------------------------------------- download

    private static byte[] FileBytes(int n)
    {
        var b = new byte[n];
        var rng = new Random(7);
        // compressible but not trivial
        for (int i = 0; i < n; i++)
            b[i] = (byte)((i / 7) ^ (rng.Next(4)));
        return b;
    }

    [Fact]
    public void Download_Plain_Round_Trip_With_Acks()
    {
        byte[] file = FileBytes(3000);
        var d = new DpDownload();
        Assert.True(d.Begin(file.Length, "csprogs.dat", deflate: false));
        Assert.True(d.Active);
        Assert.True(d.OnData(0, file.AsSpan(0, 1400)));
        Assert.True(d.OnData(1400, file.AsSpan(1400, 1400)));
        Assert.True(d.OnData(2800, file.AsSpan(2800, 200)));
        Assert.True(d.OnData(3000, ReadOnlySpan<byte>.Empty)); // the empty end block is acked too
        var acks = new List<DpDownloadAck>();
        d.TakeAcks(acks);
        Assert.Equal(new[] { new DpDownloadAck(0, 1400), new DpDownloadAck(1400, 1400), new DpDownloadAck(2800, 200), new DpDownloadAck(3000, 0) }, acks);
        Assert.Equal(0, d.PendingAcks);

        DpDownloadResult r = d.Finish(file.Length, Crc16.Block(file));
        Assert.Equal(DpDownloadStatus.Completed, r.Status);
        Assert.Equal(file, r.Data);
        Assert.Equal("csprogs.dat", r.Name);
        Assert.Equal(Crc16.Block(file), r.Crc);
        Assert.False(d.Active);
    }

    [Fact]
    public void Download_Deflate_Round_Trip_Checks_The_Wire_Bytes_Then_Inflates()
    {
        byte[] file = FileBytes(50000);
        byte[] wire = DpDownload.DeflateBytes(file);
        Assert.True(wire.Length < file.Length);
        // raw DEFLATE: no zlib header (0x78 ..) and no gzip magic
        Assert.False(wire[0] == 0x1F && wire[1] == 0x8B);
        Assert.Equal(file, DpDownload.Inflate(wire, 1 << 20));

        var d = new DpDownload();
        Assert.True(d.Begin(wire.Length, "csprogs.dat", deflate: true));
        for (int at = 0; at < wire.Length; at += 1400)
            Assert.True(d.OnData(at, wire.AsSpan(at, Math.Min(1400, wire.Length - at))));
        Assert.Equal(wire.Length, d.ReceivedSize);
        // the server's size and crc describe the deflated bytes
        DpDownloadResult r = d.Finish(wire.Length, Crc16.Block(wire));
        Assert.Equal(DpDownloadStatus.Completed, r.Status);
        Assert.True(r.WasDeflated);
        Assert.Equal(wire.Length, r.WireSize);
        Assert.Equal(file, r.Data);
        Assert.Equal(Crc16.Block(file), r.Crc);
    }

    [Fact]
    public void Download_Crc_Or_Size_Mismatch_Is_Corrupt()
    {
        byte[] file = FileBytes(2000);
        var d = new DpDownload();
        d.Begin(file.Length, "a.dat", false);
        d.OnData(0, file);
        Assert.Equal(DpDownloadStatus.Corrupt, d.Finish(file.Length, Crc16.Block(file) ^ 1).Status);
        Assert.False(d.Active);

        d.Begin(file.Length, "a.dat", false);
        d.OnData(0, file.AsSpan(0, 1000)); // the rest never arrived
        Assert.Equal(DpDownloadStatus.Corrupt, d.Finish(file.Length, Crc16.Block(file)).Status);

        // one flipped bit in transit
        byte[] damaged = (byte[])file.Clone();
        damaged[777] ^= 0x10;
        d.Begin(file.Length, "a.dat", false);
        d.OnData(0, damaged);
        Assert.Equal(DpDownloadStatus.Corrupt, d.Finish(file.Length, Crc16.Block(file)).Status);

        Assert.Equal(DpDownloadStatus.NotActive, d.Finish(1, 1).Status);
    }

    [Fact]
    public void Download_That_Matches_But_Does_Not_Inflate_Fails_Cleanly()
    {
        var junk = new byte[500];
        new Random(3).NextBytes(junk);
        junk[0] = 0x07; // reserved block type: invalid DEFLATE
        var d = new DpDownload();
        d.Begin(junk.Length, "csprogs.dat", deflate: true);
        d.OnData(0, junk);
        Assert.Equal(DpDownloadStatus.InflateFailed, d.Finish(junk.Length, Crc16.Block(junk)).Status);
        Assert.Null(DpDownload.Inflate(junk, 1 << 20));
    }

    [Fact]
    public void Download_Inflate_Bomb_Is_Capped()
    {
        byte[] bomb = DpDownload.DeflateBytes(new byte[8 << 20]);
        Assert.True(bomb.Length < 20000);
        Assert.Null(DpDownload.Inflate(bomb, 1 << 20));
        Assert.NotNull(DpDownload.Inflate(bomb, 8 << 20));
    }

    [Fact]
    public void Download_Blocks_Outside_The_Announced_File_Are_Rejected()
    {
        var d = new DpDownload();
        d.Begin(1000, "a.dat", false);
        Assert.False(d.OnData(-1, new byte[10]));
        Assert.False(d.OnData(995, new byte[10]));
        Assert.False(d.OnData(int.MaxValue, new byte[10]));   // start + size must not overflow
        Assert.False(d.OnData(0, new byte[1001]));
        Assert.True(d.OnData(990, new byte[10]));
        Assert.True(d.OnData(1000, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Download_Data_With_No_Download_Active_Is_Acked_And_Ignored_And_Acks_Are_Capped_At_Four()
    {
        var d = new DpDownload();
        for (int i = 0; i < 9; i++)
            Assert.True(d.OnData(i * 10, new byte[10]));
        Assert.Equal(DpProtocol.MaxDownloadAcks, d.PendingAcks);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("csprogs.dat", false)]
    [InlineData("maps/x.bsp", false)]
    [InlineData("../x", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData("c:/x", true)]
    [InlineData("a\\b", true)]
    [InlineData("a//b", true)]
    [InlineData("a/./b", true)]
    [InlineData("a/.hidden", true)]
    public void Download_Nasty_Paths(string path, bool nasty)
    {
        Assert.Equal(nasty, DpDownload.IsNastyPath(path));
        if (nasty)
            Assert.False(new DpDownload().Begin(10, path, false));
    }

    [Fact]
    public void Download_Size_Limits()
    {
        var d = new DpDownload { MaxSize = 1000 };
        Assert.False(d.Begin(-1, "a", false));
        Assert.False(d.Begin(1001, "a", false));
        Assert.False(d.Begin(int.MaxValue, "a", false));
        Assert.True(d.Begin(1000, "a", false));
        Assert.True(d.Begin(0, "a", false)); // an empty file is legal
        Assert.Equal(DpDownloadStatus.Completed, d.Finish(0, 0xFFFF).Status);
    }

    // ---------------------------------------------------------------- signon

    private static (DpSignon signon, DpDownload download, DpSignonConfig config) NewSignon(Action<DpSignonConfig>? configure = null)
    {
        var config = new DpSignonConfig { Name = "Tester", TopColor = 4, BottomColor = 12, Rate = 25000, RateBurstSize = 2048 };
        configure?.Invoke(config);
        var download = new DpDownload();
        return (new DpSignon(config, download), download, config);
    }

    private static string[] Take(DpSignon s)
    {
        string[] c = s.Commands.ToArray();
        s.Commands.Clear();
        return c;
    }

    [Fact]
    public void Signon_Without_Csprogs_Goes_Straight_To_Prespawn_Spawn_Begin()
    {
        var (s, _, _) = NewSignon();
        s.OnServerInfo();
        Assert.True(s.OnSignonNum(1));
        Assert.Equal(new[] { "name \"Tester\"", "color 4 12", "rate 25000", "rate_burstsize 2048" }, Take(s));
        s.EndOfMessage();
        Assert.Equal(new[] { "prespawn" }, Take(s)); // csqc_progcrc is still -1: nothing to download
        s.EndOfMessage();
        Assert.Empty(Take(s));
        Assert.True(s.OnSignonNum(2));
        Assert.Equal(new[] { "spawn" }, Take(s));
        Assert.True(s.OnSignonNum(3));
        Assert.Equal(new[] { "begin" }, Take(s));
        Assert.Equal(3, s.Stage);
        s.OnEntityFrame();
        Assert.Equal(4, s.Stage);
        s.OnEntityFrame();
        Assert.Equal(4, s.Stage);
    }

    [Fact]
    public void Signon_Stage_Going_Backwards_Is_Refused_Except_A_Repeated_One()
    {
        var (s, _, _) = NewSignon();
        Assert.True(s.OnSignonNum(1));
        Assert.True(s.OnSignonNum(1));
        Assert.True(s.OnSignonNum(2));
        Assert.False(s.OnSignonNum(2));
        Assert.True(s.OnSignonNum(3));
        Assert.False(s.OnSignonNum(2));
        Assert.True(s.OnSignonNum(1)); // level change
    }

    [Fact]
    public void Signon_Player_Model_And_Skin_Are_Sent_When_Set()
    {
        var (s, _, _) = NewSignon(c => { c.PlayerModel = "models/player/erebus.iqm"; c.PlayerSkin = "0"; });
        s.OnSignonNum(1);
        Assert.Equal(new[] { "playermodel models/player/erebus.iqm", "playerskin 0" }, Take(s).Skip(4));
    }

    [Fact]
    public void Signon_Csprogs_Download_Deflate_Verified_Then_Prespawn()
    {
        byte[] file = FileBytes(20000);
        byte[] wire = DpDownload.DeflateBytes(file);
        var (s, d, _) = NewSignon();

        // The order DarkPlaces produces: stufftext precedes serverinfo in the message but is executed
        // after the whole message, so the serverinfo reset of cl_serverextension_download does not win.
        s.OnServerInfo();
        s.OnSignonNum(1);
        Assert.True(s.HandleCommand("csqc_progname csprogs.dat"));
        Assert.True(s.HandleCommand($"csqc_progsize {file.Length}"));
        Assert.True(s.HandleCommand($"csqc_progcrc {Crc16.Block(file)}"));
        Assert.True(s.HandleCommand("cl_serverextension_download 2"));
        Assert.False(s.HandleCommand("curl --clear_autodownload"));         // not the engine's: left for the handler
        Assert.False(s.HandleCommand("alias qc_cmd_cl \"cl_cmd ${* ?}\""));
        s.EndOfMessage();
        Assert.Equal("download csprogs.dat deflate", Take(s).Last());

        Assert.True(s.HandleCommand($"cl_downloadbegin {wire.Length} csprogs.dat deflate"));
        Assert.Equal(new[] { "sv_startdownload" }, Take(s));
        Assert.True(d.Active && d.Deflate);
        for (int at = 0; at < wire.Length; at += 1300)
            d.OnData(at, wire.AsSpan(at, Math.Min(1300, wire.Length - at)));

        Assert.True(s.HandleCommand($"cl_downloadfinished {wire.Length} {Crc16.Block(wire)} csprogs.dat"));
        Assert.Equal(new[] { "prespawn" }, Take(s));
        Assert.Equal(file, s.CsprogsData);
        Assert.True(s.CsprogsVerified);
        Assert.Equal(DpDownloadStatus.Completed, s.LastDownload!.Status);
    }

    [Fact]
    public void Signon_Csprogs_That_Does_Not_Match_The_Announced_Crc_Is_Not_Verified()
    {
        byte[] file = FileBytes(5000);
        var (s, d, _) = NewSignon();
        s.OnServerInfo();
        s.OnSignonNum(1);
        s.HandleCommand($"csqc_progsize {file.Length}");
        s.HandleCommand($"csqc_progcrc {(Crc16.Block(file) + 1) & 0xFFFF}"); // the server lies about, or mixes up, the program
        s.HandleCommand("cl_serverextension_download 1");
        s.EndOfMessage();
        Assert.Equal("download csprogs.dat", Take(s).Last()); // extension level 1: no deflate
        s.HandleCommand($"cl_downloadbegin {file.Length} csprogs.dat");
        d.OnData(0, file);
        s.HandleCommand($"cl_downloadfinished {file.Length} {Crc16.Block(file)} csprogs.dat");
        Assert.NotNull(s.CsprogsData);
        Assert.False(s.CsprogsVerified);
        Assert.Contains("prespawn", Take(s));
    }

    [Fact]
    public void Signon_Corrupt_Download_Still_Proceeds_Without_A_Program()
    {
        var (s, d, _) = NewSignon();
        s.OnServerInfo();
        s.OnSignonNum(1);
        s.HandleCommand("csqc_progsize 100");
        s.HandleCommand("csqc_progcrc 1234");
        s.HandleCommand("cl_serverextension_download 2");
        s.EndOfMessage();
        Take(s);
        s.HandleCommand("cl_downloadbegin 100 csprogs.dat");
        Assert.Equal(new[] { "sv_startdownload" }, Take(s));
        d.OnData(0, new byte[100]);
        s.HandleCommand("cl_downloadfinished 100 1 csprogs.dat");
        Assert.Equal(DpDownloadStatus.Corrupt, s.LastDownload!.Status);
        Assert.Null(s.CsprogsData);
        Assert.False(s.CsprogsVerified);
        Assert.Equal(new[] { "prespawn" }, Take(s));
    }

    [Fact]
    public void Signon_Skips_The_Download_When_The_File_Is_Already_On_Hand_Or_Downloads_Are_Off()
    {
        (string, int, int) asked = default;
        var (s, _, _) = NewSignon(c => c.HaveFile = (n, size, crc) => { asked = (n, size, crc); return true; });
        s.OnServerInfo();
        s.OnSignonNum(1);
        s.HandleCommand("csqc_progname prog.dat");
        s.HandleCommand("csqc_progsize 100");
        s.HandleCommand("csqc_progcrc 55");
        s.HandleCommand("cl_serverextension_download 2");
        s.EndOfMessage();
        Assert.Equal(("prog.dat", 100, 55), asked);
        Assert.Equal("prespawn", Take(s).Last());

        var (s2, _, _) = NewSignon();
        s2.OnServerInfo();
        s2.OnSignonNum(1);
        s2.HandleCommand("csqc_progsize 100");
        s2.HandleCommand("csqc_progcrc 55");
        // no cl_serverextension_download: the server cannot serve files
        s2.EndOfMessage();
        Assert.Equal("prespawn", Take(s2).Last());
    }

    [Fact]
    public void Signon_Refused_Download_And_Bogus_Begin()
    {
        var (s, d, _) = NewSignon();
        s.OnServerInfo();
        s.OnSignonNum(1);
        s.HandleCommand("csqc_progsize 100");
        s.HandleCommand("csqc_progcrc 55");
        s.HandleCommand("cl_serverextension_download 2");
        s.EndOfMessage();
        Take(s);
        Assert.True(s.HandleCommand("cl_downloadbegin 100 ../../etc/passwd"));
        Assert.False(d.Active);
        Assert.Empty(Take(s)); // no sv_startdownload for bogus information
        Assert.True(s.HandleCommand("cl_downloadbegin -5 csprogs.dat"));
        Assert.False(d.Active);
        Assert.True(s.HandleCommand("cl_downloadfinished")); // malformed: ignored
        Assert.Empty(Take(s));
        Assert.True(s.HandleCommand("stopdownload"));
        Assert.Equal(new[] { "prespawn" }, Take(s));
    }

    [Fact]
    public void Signon_Cmd_Forwards_With_Variable_Expansion()
    {
        var (s, _, config) = NewSignon();
        Assert.True(s.HandleCommand("cmd clientversion $gameversion"));
        Assert.Equal(new[] { "clientversion 806" }, Take(s));
        config.Variables["gameversion"] = "807";
        Assert.True(s.HandleCommand("CMD clientversion ${gameversion}  extra \"quoted arg\""));
        Assert.Equal(new[] { "clientversion 807  extra \"quoted arg\"" }, Take(s));
        Assert.True(s.HandleCommand("cmd"));
        Assert.True(s.HandleCommand("   "));
        Assert.Empty(Take(s));
    }

    // ---------------------------------------------------------------- whole client against a scripted server

    /// <summary>The server end of the wire: a netchan, a clc decoder, and helpers to script messages.</summary>
    private sealed class FakeServer
    {
        public DpNetChannel Channel = new(0);
        public readonly List<byte[]> ToClient = new();
        public readonly List<string> Received = new();
        public readonly List<string> Oob = new();
        public bool Accept = true;
        public string RejectReason = "";
        public bool Connected;

        public static List<string> ParseClientMessage(byte[] message)
        {
            var log = new List<string>();
            var r = new DpMessageReader(message);
            while (r.Remaining > 0)
            {
                int clc = r.ReadByte();
                switch (clc)
                {
                    case 1: log.Add("nop"); break;
                    case 2: log.Add("disconnect"); break;
                    case 3:
                        int sequence = r.ReadLong();
                        r.ReadSpan(51);
                        log.Add($"move {sequence}");
                        break;
                    case 4: log.Add("cmd " + r.ReadString()); break;
                    case 50: log.Add($"ackframe {r.ReadLong()}"); break;
                    case 51: { int start = r.ReadLong(); int size = r.ReadUShort(); log.Add($"ackdl {start} {size}"); break; }
                    default: log.Add($"BAD {clc}"); return log;
                }
                Assert.False(r.BadRead);
            }
            return log;
        }

        public void Receive(byte[] datagram, double now)
        {
            if (MasterServerProtocol.TryStripOob(datagram, out ReadOnlySpan<byte> body))
            {
                string text = Encoding.ASCII.GetString(body);
                Oob.Add(text);
                if (text == "getchallenge")
                    ToClient.Add(OobPacket("challenge XyZ"));
                else if (text.StartsWith("connect\\", StringComparison.Ordinal))
                {
                    if (Accept)
                    {
                        Connected = true;
                        Channel = new DpNetChannel(now);
                        ToClient.Add(OobPacket("accept"));
                    }
                    else
                        ToClient.Add(OobPacket("reject " + RejectReason));
                }
                return;
            }
            if (Channel.Receive(datagram, now, ToClient, out byte[]? message) == DpChannelReceive.Message)
                Received.AddRange(ParseClientMessage(message!));
        }

        public static byte[] OobPacket(string text)
        {
            byte[] t = Encoding.ASCII.GetBytes(text);
            var p = new byte[4 + t.Length];
            p[0] = p[1] = p[2] = p[3] = 0xFF;
            t.CopyTo(p, 4);
            return p;
        }

        public void SendReliable(Action<DpMessageWriter> build, double now)
        {
            build(Channel.Reliable);
            Channel.Transmit(default, now, ToClient);
        }

        public void SendUnreliable(Action<DpMessageWriter> build, double now)
        {
            var w = new DpMessageWriter();
            build(w);
            Channel.Transmit(w.WrittenSpan, now, ToClient);
        }

        public string[] TakeReceived()
        {
            string[] r = Received.ToArray();
            Received.Clear();
            return r;
        }
    }

    private sealed class Wire
    {
        public readonly RecordingHandler Handler = new();
        public readonly DpClient Client;
        public readonly FakeServer Server = new();
        public double Now;

        public Wire(Action<DpClientConfig>? configure = null)
        {
            var config = new DpClientConfig();
            config.Signon.Name = "Tester";
            // These tests step the clock 10 ms at a time and expect each step to be able to send:
            // the fastest pacing CL_SendMove allows (1 ms). The pacing itself is DpClientPacingTests.
            config.NetFps = 1000;
            configure?.Invoke(config);
            Client = new DpClient(Handler, config);
        }

        /// <summary>Run both ends until neither has anything more to say (or a tick limit).</summary>
        public void Settle(int maxTicks = 200, double dt = 0.01)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                Now += dt;
                bool moved = false;
                foreach (byte[] d in Client.Update(Now))
                {
                    Server.Receive(d, Now);
                    moved = true;
                }
                Server.Channel.Transmit(default, Now, Server.ToClient);
                if (Server.ToClient.Count > 0)
                {
                    byte[][] batch = Server.ToClient.ToArray();
                    Server.ToClient.Clear();
                    foreach (byte[] d in batch)
                        Client.Receive(d, Now);
                    moved = true;
                }
                if (!moved)
                    return;
            }
        }

        /// <summary>Hand the client what the server has queued, without letting the client send first.</summary>
        public void DeliverToClient()
        {
            byte[][] batch = Server.ToClient.ToArray();
            Server.ToClient.Clear();
            foreach (byte[] d in batch)
                Client.Receive(d, Now);
        }

        public void ConnectAndSettle()
        {
            Client.Connect(Now);
            Settle();
        }
    }

    private static void WriteSignonMessage(DpMessageWriter w, int size, int crc)
    {
        w.WriteByte(8); w.WriteString("\nServer: DarkPlaces test (progs 1 crc)\n");
        w.WriteByte(9); w.WriteString("csqc_progname csprogs.dat\n");
        w.WriteByte(9); w.WriteString($"csqc_progsize {size}\n");
        w.WriteByte(9); w.WriteString($"csqc_progcrc {crc}\n");
        w.WriteByte(9); w.WriteString("cl_serverextension_download 2\n");
        w.WriteByte(9); w.WriteString("curl --clear_autodownload\ncurl --finish_autodownload\n");
        DpServerMessageParserTests.WriteServerInfo(w);
        w.WriteByte(32); w.WriteByte(3); w.WriteByte(3);
        w.WriteByte(5); w.WriteShort(1);
        w.WriteByte(25); w.WriteByte(1);
    }

    [Fact]
    public void Client_Handshake_Then_Connected()
    {
        var wire = new Wire();
        Assert.Equal(DpClientState.Disconnected, wire.Client.State);
        wire.ConnectAndSettle();
        Assert.Equal(DpClientState.Connected, wire.Client.State);
        Assert.Equal(new[] { "getchallenge", "connect\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\XyZ" }, wire.Server.Oob);
        Assert.Equal(0, wire.Client.Signon.Stage);
    }

    [Fact]
    public void Client_Reject_Surfaces_The_Servers_Reason()
    {
        var wire = new Wire();
        wire.Server.Accept = false;
        wire.Server.RejectReason = "Server is full.";
        wire.ConnectAndSettle();
        Assert.Equal(DpClientState.Rejected, wire.Client.State);
        Assert.Equal("Server is full.", wire.Client.LastError);
        // nothing more is sent
        wire.Now += 5;
        Assert.Empty(wire.Client.Update(wire.Now));
    }

    [Fact]
    public void Client_With_No_Server_Retries_Ten_Times_Then_Times_Out()
    {
        var client = new DpClient(new RecordingHandler());
        client.Connect(0);
        int sent = 0;
        double now = 0;
        for (int i = 0; i < 400 && client.State == DpClientState.Connecting; i++)
        {
            now += 0.1;
            sent += client.Update(now).Count;
        }
        Assert.Equal(DpClientState.TimedOut, client.State);
        Assert.Equal(10, sent);
        Assert.InRange(now, 9.5, 11.5);
        Assert.Equal("Connect: failed, no reply", client.LastError);
    }

    [Fact]
    public void Client_Full_Signon_With_Deflated_Csprogs_Download_Then_Moves_And_Disconnect()
    {
        byte[] csprogs = FileBytes(30000);
        byte[] wireBytes = DpDownload.DeflateBytes(csprogs);
        int crc = Crc16.Block(csprogs);

        var wire = new Wire();
        var server = wire.Server;
        var client = wire.Client;
        wire.ConnectAndSettle();

        // --- signon 1: the big reliable message (forced over several fragments by padding the model list)
        server.SendReliable(w => WriteSignonMessage(w, csprogs.Length, crc), wire.Now);
        wire.Settle();
        Assert.Equal(1, client.Signon.Stage);
        Assert.Equal(new[]
        {
            "cmd name \"Tester\"", "cmd color 0 0", "cmd rate 20000", "cmd rate_burstsize 1024",
            "cmd download csprogs.dat deflate",
        }, server.TakeReceived());
        // engine commands were consumed; the rest reached the handler one command at a time, in order
        Assert.Equal(new[] { "stufftext curl --clear_autodownload\n", "stufftext curl --finish_autodownload\n" },
            wire.Handler.Log.Where(l => l.StartsWith("stufftext", StringComparison.Ordinal)));
        Assert.Contains("serverinfo 3504 16 1 \"Test Map\" models=3 sounds=2", wire.Handler.Log);
        Assert.Contains("setview 1", wire.Handler.Log);
        Assert.Equal(2, client.Signon.ServerExtensionDownload);

        // --- download
        server.SendReliable(w => { w.WriteByte(9); w.WriteString($"\ncl_downloadbegin {wireBytes.Length} csprogs.dat deflate\n"); }, wire.Now);
        wire.Settle();
        Assert.Equal(new[] { "cmd sv_startdownload" }, server.TakeReceived());
        Assert.True(client.Download.Active);

        // blocks travel unreliably; the server advances only on the matching ack, as sv_user.c does
        int expected = 0;
        int guard = 0;
        bool dropNext = true; // lose one block on the way to exercise the server's seek-back
        while (expected < wireBytes.Length && guard++ < 1000)
        {
            int size = Math.Min(1400, wireBytes.Length - expected);
            int start = expected;
            if (dropNext && start == 2800)
            {
                dropNext = false;
                // the block at 2800 is "lost"; the server, unaware, sends the following one
                int nextStart = start + size;
                int nextSize = Math.Min(1400, wireBytes.Length - nextStart);
                server.SendUnreliable(w => { w.WriteByte(50); w.WriteLong(nextStart); w.WriteShort(nextSize); w.WriteBytes(wireBytes.AsSpan(nextStart, nextSize)); }, wire.Now);
                wire.Settle();
                // the client echoes what it got; that is not the expected block, so the server re-sends from 2800
                Assert.Equal(new[] { $"ackdl {nextStart} {nextSize}" }, server.TakeReceived());
                continue;
            }
            server.SendUnreliable(w => { w.WriteByte(50); w.WriteLong(start); w.WriteShort(size); w.WriteBytes(wireBytes.AsSpan(start, size)); }, wire.Now);
            wire.Settle();
            Assert.Equal(new[] { $"ackdl {start} {size}" }, server.TakeReceived());
            expected += size;
        }
        // the empty block at the end of the file is acknowledged as well
        server.SendUnreliable(w => { w.WriteByte(50); w.WriteLong(wireBytes.Length); w.WriteShort(0); }, wire.Now);
        wire.Settle();
        Assert.Equal(new[] { $"ackdl {wireBytes.Length} 0" }, server.TakeReceived());

        server.SendReliable(w => { w.WriteByte(9); w.WriteString($"\ncl_downloadfinished {wireBytes.Length} {Crc16.Block(wireBytes)} csprogs.dat\n"); }, wire.Now);
        wire.Settle();
        Assert.Equal(new[] { "cmd prespawn" }, server.TakeReceived());
        Assert.Equal(csprogs, client.Signon.CsprogsData);
        Assert.True(client.Signon.CsprogsVerified);
        Assert.False(client.Download.Active);

        // --- signon 2, 3
        server.SendReliable(w => { w.WriteByte(25); w.WriteByte(2); }, wire.Now);
        wire.Settle();
        Assert.Equal(new[] { "cmd spawn" }, server.TakeReceived());
        server.SendReliable(w =>
        {
            w.WriteByte(9); w.WriteString("cmd clientversion $gameversion\n");
            w.WriteByte(25); w.WriteByte(3);
        }, wire.Now);
        wire.Settle();
        Assert.Equal(new[] { "cmd begin", "cmd clientversion 806" }, server.TakeReceived());
        Assert.Equal(3, client.Signon.Stage);

        // no input is sent before signon completes
        client.QueueMove(new DpUserCmd { Predicted = true });
        wire.Settle();
        Assert.DoesNotContain(server.TakeReceived(), l => l.StartsWith("move", StringComparison.Ordinal));

        // --- first entity frame completes signon; moves and frame acks follow
        server.SendUnreliable(w =>
        {
            w.WriteByte(7); w.WriteFloat(1.0f);
            w.WriteByte(57); w.WriteLong(1000); w.WriteLong(0);
            w.WriteShort(1); w.WriteByte((int)(DpProtocol.E5FullUpdate | DpProtocol.E5Model)); w.WriteByte(3);
            w.WriteShort(0x8000);
        }, wire.Now);
        client.QueueMove(new DpUserCmd { Predicted = true, Time = 1.0f, ForwardMove = 400 });
        wire.Settle();
        Assert.Equal(4, client.Signon.Stage);
        Assert.Contains("entities frame=1000 move=0 changed=[1]", wire.Handler.Log);
        Assert.Equal(3, client.Parser.Entities.Current(1).ModelIndex);
        string[] got = server.TakeReceived();
        uint firstMoveSequence = client.Channel.OutgoingUnreliableSequence - 1;
        Assert.Equal(new[] { $"move {firstMoveSequence}", "ackframe 1000" }, got);

        // the next packet repeats the previous move ahead of the new one (cl_netrepeatinput 1), and the ack
        client.QueueMove(new DpUserCmd { Predicted = true, Time = 1.1f });
        wire.Settle();
        Assert.Equal(new[] { $"move {firstMoveSequence}", $"move {firstMoveSequence + 1}", "ackframe 1000" }, server.TakeReceived());

        // once the server reports having applied a move, it is no longer repeated
        server.SendUnreliable(w =>
        {
            w.WriteByte(57); w.WriteLong(1001); w.WriteLong((int)(firstMoveSequence + 2));
            w.WriteShort(0x8000);
        }, wire.Now);
        wire.DeliverToClient();
        client.QueueMove(new DpUserCmd { Predicted = true, Time = 1.2f });
        wire.Settle();
        got = server.TakeReceived();
        Assert.DoesNotContain($"move {firstMoveSequence + 1}", got);
        Assert.Equal($"move {firstMoveSequence + 2}", got[0]);
        Assert.Contains("ackframe 1001", got);
        Assert.Equal(firstMoveSequence + 2, client.ServerMoveSequence);

        // --- a ping from the server is answered while connected
        client.Receive(FakeServer.OobPacket("ping"), wire.Now);
        byte[][] reply = client.Update(wire.Now).ToArray();
        Assert.Contains(reply, d => d.Length == 7 && d[4] == (byte)'a' && d[5] == (byte)'c' && d[6] == (byte)'k');

        // --- disconnect: three unreliable clc_disconnect datagrams
        client.Disconnect(wire.Now);
        Assert.Equal(DpClientState.Disconnected, client.State);
        byte[][] last = client.Update(wire.Now).ToArray();
        Assert.Equal(3, last.Length);
        foreach (byte[] d in last)
            server.Receive(d, wire.Now);
        Assert.Equal(new[] { "disconnect", "disconnect", "disconnect" }, server.TakeReceived());
        Assert.Empty(client.Update(wire.Now + 10));
        Assert.Equal(0, client.MessagesAborted);
    }

    [Fact]
    public void Client_Large_Signon_Message_Arrives_Through_Fragments_With_A_Lost_Datagram()
    {
        var wire = new Wire();
        wire.ConnectAndSettle();
        string[] models = new[] { "maps/big.bsp" }.Concat(Enumerable.Range(0, 400).Select(i => $"models/some/long/path/model_{i:D4}.md3")).ToArray();
        wire.Server.SendReliable(w =>
        {
            DpServerMessageParserTests.WriteServerInfo(w, models: models);
            w.WriteByte(25); w.WriteByte(1);
        }, wire.Now);
        Assert.True(wire.Server.Channel.ReliableInFlight);
        // lose the first fragment outright; the server's one-second resend has to recover it
        wire.Server.ToClient.Clear();
        wire.Settle(maxTicks: 2000, dt: 0.05);
        // (Settle stops when idle, so step time forward until the resend fires)
        for (int i = 0; i < 100 && wire.Client.Signon.Stage == 0; i++)
        {
            wire.Now += 0.3;
            wire.Settle(maxTicks: 50, dt: 0.05);
        }
        Assert.Equal(1, wire.Client.Signon.Stage);
        Assert.Equal(401, wire.Handler.ServerInfo!.Models.Count - 1);
        Assert.Equal("models/some/long/path/model_0399.md3", wire.Handler.ServerInfo.Models[^1]);
        Assert.True(wire.Server.Channel.PacketsResent >= 1);
        Assert.Contains("cmd prespawn", wire.Server.Received);
    }

    [Fact]
    public void Client_Parse_Error_Fails_The_Connection_With_Id_And_Offset()
    {
        var wire = new Wire();
        wire.ConnectAndSettle();
        wire.Server.SendUnreliable(w => { w.WriteByte(1); w.WriteByte(99); }, wire.Now);
        wire.Settle();
        Assert.Equal(DpClientState.Failed, wire.Client.State);
        Assert.Contains("svc 99", wire.Client.LastError);
        Assert.Contains("offset 1", wire.Client.LastError);
        // a failed client ignores further traffic and sends nothing
        wire.Client.Receive(FakeServer.OobPacket("ping"), wire.Now);
        Assert.Empty(wire.Client.Update(wire.Now + 1));
    }

    [Fact]
    public void Client_Signon_Going_Backwards_And_Corrupt_Download_Block_Fail_The_Connection()
    {
        var a = new Wire();
        a.ConnectAndSettle();
        a.Server.SendReliable(w => { w.WriteByte(25); w.WriteByte(2); w.WriteByte(25); w.WriteByte(2); }, a.Now);
        a.Settle();
        Assert.Equal(DpClientState.Failed, a.Client.State);
        Assert.Equal("Received signon 2 when at 2", a.Client.LastError);

        var b = new Wire();
        b.ConnectAndSettle();
        b.Server.SendReliable(w => { w.WriteByte(9); w.WriteString("cl_downloadbegin 100 csprogs.dat\n"); }, b.Now);
        b.Settle();
        b.Server.SendUnreliable(w => { w.WriteByte(50); w.WriteLong(90); w.WriteShort(20); w.WriteBytes(new byte[20]); }, b.Now);
        b.Settle();
        Assert.Equal(DpClientState.Failed, b.Client.State);
        Assert.Equal("corrupt download message", b.Client.LastError);
    }

    [Fact]
    public void Client_Server_Disconnect_And_Message_Cut_At_Csqc_Are_Not_Failures()
    {
        var wire = new Wire();
        wire.ConnectAndSettle();
        wire.Server.SendUnreliable(w =>
        {
            w.WriteByte(7); w.WriteFloat(5);
            w.WriteByte(58); w.WriteShort(3); w.WriteBytes(new byte[] { 1, 2, 3 }); w.WriteShort(0);
        }, wire.Now);
        wire.Settle();
        Assert.Equal(DpClientState.Connected, wire.Client.State);
        Assert.Equal(1, wire.Client.MessagesAborted);
        Assert.Equal(DpParseStatus.Aborted, wire.Client.LastParse.Status);
        Assert.Contains("time 5", wire.Handler.Log);

        wire.Server.SendUnreliable(w => w.WriteByte(2), wire.Now);
        wire.Settle();
        Assert.Equal(DpClientState.Disconnected, wire.Client.State);
        Assert.Contains("disconnect", wire.Handler.Log);
    }

    [Fact]
    public void Client_Sends_Keepalive_Nops_While_Signing_On_And_Times_Out_On_Silence()
    {
        var wire = new Wire(c => { c.Timeout = 20; c.KeepAliveInterval = 5; });
        wire.ConnectAndSettle();
        int nops = 0;
        double start = wire.Now;
        while (wire.Client.State == DpClientState.Connected && wire.Now < start + 60)
        {
            wire.Now += 0.5;
            foreach (byte[] d in wire.Client.Update(wire.Now))
            {
                wire.Server.Receive(d, wire.Now);
            }
            nops += wire.Server.TakeReceived().Count(l => l == "nop");
        }
        Assert.Equal(DpClientState.TimedOut, wire.Client.State);
        Assert.InRange(wire.Now - start, 19.5, 21.5);
        Assert.InRange(nops, 3, 4); // one every 5 seconds until the 20 second timeout
    }

    [Fact]
    public void Client_Ignores_Garbage_Datagrams_In_Every_State()
    {
        var rng = new Random(99);
        var client = new DpClient(new RecordingHandler());
        void Throw(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var d = new byte[rng.Next(0, 64)];
                rng.NextBytes(d);
                client.Receive(d, i);
            }
        }
        Throw(500);                       // disconnected
        Assert.Equal(DpClientState.Disconnected, client.State);
        client.Connect(0);
        Throw(500);                       // connecting
        Assert.Equal(DpClientState.Connecting, client.State);
        client.Update(0.5);
        client.Receive(FakeServer.OobPacket("challenge c"), 1);
        client.Receive(FakeServer.OobPacket("accept"), 1);
        Assert.Equal(DpClientState.Connected, client.State);
        Throw(2000);                      // connected: random bytes essentially never form a valid header
        Assert.Equal(DpClientState.Connected, client.State);
        client.SendStringCommand("status");
        Assert.NotEmpty(client.Update(2));
    }
}
