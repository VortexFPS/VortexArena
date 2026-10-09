using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Downloads;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The client program's HTTP requests (uri_get) inside a whole session: started from a frame, answered in
/// a later frame and nowhere else, cancelled when the session ends. The transfer itself is a stand-in the
/// test ends by hand; the real one is CsqcHostTests' business.
/// </summary>
public partial class LegacyClientSessionTests
{
    /// <summary>CSQC_UpdateView starts one request (while "started" is 0); URI_Get_Callback records what it is told.</summary>
    private static byte[] UriProgram()
    {
        ProgsBuilder b = new();
        b.Int(0, "self", QcType.Entity);
        b.Float(0, "time");
        int started = b.Float(0, "started"), result = b.Float(0, "result"), cbId = b.Float(0, "cb_id"), cbStatus = b.Float(-99, "cb_status"),
            cbCount = b.Float(0, "cb_count"), frames = b.Float(0, "frames_drawn"), one = b.Float(1), id = b.Float(7);
        int url = b.Int(b.String("http://stats.example.invalid/submit?key=abc"), null, QcType.String);
        int uriGet = b.Builtin("uri_get", 513);
        b.Function("CSQC_UpdateView");
        b.Emit(QcOp.AddF, frames, one, frames);
        int skip = b.Emit(QcOp.If, started);
        b.Emit(QcOp.StoreF, one, started);
        b.Emit(QcOp.StoreF, url, ProgsFile.OfsParm0);
        b.Emit(QcOp.StoreF, id, ProgsFile.OfsParm0 + 3);
        b.EmitRaw((int)QcOp.Call0 + 2, uriGet);
        b.Emit(QcOp.StoreF, ProgsFile.OfsReturn, result);
        b.PatchJump(skip, b.NextStatement);
        b.Emit(QcOp.Done);
        b.Function("URI_Get_Callback");
        b.Emit(QcOp.StoreF, ProgsFile.OfsParm0, cbId);
        b.Emit(QcOp.StoreF, ProgsFile.OfsParm0 + 3, cbStatus);
        b.Emit(QcOp.AddF, cbCount, one, cbCount);
        b.Emit(QcOp.Done);
        b.Function("CSQC_Ent_Update");
        b.Emit(QcOp.Done);
        b.Function("CSQC_Ent_Remove");
        b.Emit(QcOp.Done);
        return b.Build();
    }

    private sealed class HeldFetcher : ILegacyUriFetcher
    {
        public readonly List<(LegacyUriRequest Request, TaskCompletionSource<LegacyUriResult> Done, CancellationToken Cancel)> Calls = new();
        public int Finished;

        public async Task<LegacyUriResult> FetchAsync(LegacyUriRequest request, CancellationToken cancel)
        {
            TaskCompletionSource<LegacyUriResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Calls) Calls.Add((request, done, cancel));
            try { return await done.Task.WaitAsync(cancel); }
            catch (OperationCanceledException) { return new LegacyUriResult(LegacyUriStatus.Aborted, Array.Empty<byte>(), "cancelled"); }
            finally { Interlocked.Increment(ref Finished); }
        }

        public int Count
        {
            get { lock (Calls) return Calls.Count; }
        }

        public void WaitUntil(Func<bool> condition)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < 10_000) Thread.Sleep(2);
            Assert.True(condition());
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void A_Programs_Http_Request_Is_Answered_In_A_Frame_And_Ends_With_The_Session()
    {
        HeldFetcher fetcher = new();
        List<string> log = new();
        LegacyUriRequests requests = new(new LegacyUriLimits(), fetcher) { ServerHost = "203.0.113.5", ServerPort = 26000, Print = log.Add };
        Rig rig = new(localProgram: true, UriProgram(), options => options.Host = new CsqcHostOptions { UriRequests = requests });
        try
        {
            LegacyClientSession session = rig.Session;
            session.Connect(0);
            rig.Settle();
            rig.Reliable(w =>
            {
                w.WriteByte(9); w.WriteString("csqc_progname csprogs.dat\n");
                w.WriteByte(9); w.WriteString($"csqc_progsize {rig.ProgramBytes.Length}\n");
                w.WriteByte(9); w.WriteString($"csqc_progcrc {Crc16.Block(rig.ProgramBytes)}\n");
                DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8);
                w.WriteByte(5); w.WriteShort(1);
                w.WriteByte(25); w.WriteByte(1);
            });
            Assert.True(session.Host is { Initialized: true });
            rig.Reliable(w => { w.WriteByte(25); w.WriteByte(2); });
            rig.Reliable(w => { w.WriteByte(25); w.WriteByte(3); });
            Assert.Equal(0, fetcher.Count);   // not in the game yet: the program has not drawn
            rig.Unreliable(w =>
            {
                w.WriteByte(7); w.WriteFloat(10.0f);
                w.WriteByte(57); w.WriteLong(rig.Frame++); w.WriteLong(0);
                w.WriteShort(0x8000);
            });
            rig.Step();
            Assert.Equal(DpProtocol.Signons, session.State.Signon);
            QcVm vm = session.Host!.Vm;
            float G(string name) => vm.GlobalFloat(vm.FindGlobal(name)!.Offset);

            // The first frame started the request; frames go by while it is in the air.
            Assert.True(G("frames_drawn") >= 1);
            Assert.Equal(1, vm.GlobalInt(vm.FindGlobal("result")!.Offset));
            fetcher.WaitUntil(() => fetcher.Count == 1);
            Assert.Equal("dp://203.0.113.5:26000/", fetcher.Calls[0].Request.Referer);
            Assert.False(fetcher.Calls[0].Request.AllowPrivateHosts);
            for (int i = 0; i < 5; i++) rig.Step();
            Assert.Equal(0, G("cb_count"));

            // It ends on its own thread; the program hears of it in the next frame, not before.
            fetcher.Calls[0].Done.SetResult(new LegacyUriResult(503, Encoding.ASCII.GetBytes("busy"), "the server answered 503"));
            fetcher.WaitUntil(() => fetcher.Finished == 1);
            Assert.Equal(0, G("cb_count"));
            float framesBefore = G("frames_drawn");
            rig.Step();
            Assert.Equal(1, G("cb_count"));
            Assert.Equal(7, G("cb_id"));
            Assert.Equal(503, G("cb_status"));
            Assert.Equal(framesBefore + 1, G("frames_drawn"));
            Assert.Equal(0, session.FramesFaulted);
            // The log names the address without its query.
            Assert.Contains(log, l => l.Contains("stats.example.invalid/submit?...") && l.Contains("503"));
            Assert.DoesNotContain(log, l => l.Contains("key=abc"));

            // A second request is in the air when the player leaves: it is cancelled, and nothing more can start.
            vm.GlobalFloat(vm.FindGlobal("started")!.Offset) = 0;
            rig.Step();
            fetcher.WaitUntil(() => fetcher.Count == 2);
            Assert.False(fetcher.Calls[1].Cancel.IsCancellationRequested);
            session.Dispose();
            Assert.True(fetcher.Calls[1].Cancel.IsCancellationRequested);
            Assert.False(requests.Available);
            Assert.Equal(0, requests.Pending);
        }
        finally { rig.Dispose(); }
    }
}
