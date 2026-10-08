using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VortexArena.Modding;
using Xunit;

namespace VortexArena.Tests.Modding;

/// <summary>Scratch directory for a test's mod cache and consent file; removed afterwards.</summary>
internal sealed class ModTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "va-modoffer-" + Guid.NewGuid().ToString("N"));
    public ModTempDir() => Directory.CreateDirectory(Path);
    public string Sub(string name) => System.IO.Path.Combine(Path, name);
    public IEnumerable<string> Files(string sub) =>
        Directory.Exists(Sub(sub)) ? Directory.EnumerateFiles(Sub(sub), "*", SearchOption.AllDirectories) : Array.Empty<string>();
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Shared builders for the offer tests.</summary>
internal static class ModOfferFixture
{
    public const uint Protocol = 0xC0FFEE01;
    public const string Server = "mods.example.org:26000";

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static ModOfferDescription Description(bool required = false, params string[] capabilities) => new()
    {
        ModId = "overkill",
        ModVersion = "1.4.0",
        BaseProtocol = Protocol,
        Consent = new ModConsent { Title = "Overkill", Description = "Instagib with a twist.", Author = "someone" },
        Required = required,
        Capabilities = capabilities,
    };

    public static ModClientOptions Options(bool allowMods = true) => new()
    {
        AllowMods = allowMods,
        ServerKey = Server,
        BaseProtocol = Protocol,
    };
}

/// <summary>The frame encoding, from the receiving side: nothing malformed parses, nothing parses to more than its cap.</summary>
public class ModWireTests
{
    [Fact]
    public void EveryFrameKind_RoundTrips()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"modId\":\"x\"}");
        Assert.True(ModWire.TryParse(ModWire.Offer(7, json), out ModFrame offer));
        Assert.Equal((ModFrameKind.Offer, (byte)7, ModWire.TransferVersion), (offer.Kind, offer.Sequence, offer.Version));
        Assert.True(offer.Body.SequenceEqual(json));

        string a = new('a', 64), b = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.True(ModWire.TryParse(ModWire.Need(3, new[] { a, b }), out ModFrame need));
        Assert.Equal(2, need.HashCount);
        Assert.Equal(a, ModWire.HashHex(need.Hash(0)));
        Assert.Equal(b, ModWire.HashHex(need.Hash(1)));

        byte[] piece = ModOfferFixture.Bytes(1000, 1);
        Assert.True(ModWire.TryParse(ModWire.Chunk(3, 5, 123456, piece), out ModFrame chunk));
        Assert.Equal(((byte)5, 123456u), (chunk.Slot, chunk.Offset));
        Assert.True(chunk.Body.SequenceEqual(piece));

        Assert.True(ModWire.TryParse(ModWire.Ready(9), out ModFrame ready));
        Assert.Equal((ModFrameKind.Ready, (byte)9), (ready.Kind, ready.Sequence));

        Assert.True(ModWire.TryParse(ModWire.Decline(2, ModDeclineReason.ConsentDenied, "no thanks"), out ModFrame decline));
        Assert.Equal((ModDeclineReason.ConsentDenied, ModWire.TransferVersion, "no thanks"), (decline.Reason, decline.Version, ModWire.SafeText(decline.Body)));

        Assert.True(ModWire.TryParse(ModWire.Abort(2, "gone"), out ModFrame abort));
        Assert.Equal("gone", ModWire.SafeText(abort.Body));

        Assert.True(ModWire.TryParse(ModWire.ToServer(2, piece), out ModFrame toServer));
        Assert.True(toServer.Body.SequenceEqual(piece));

        Assert.True(ModWire.TryParse(ModWire.ToClient(2, 77, piece), out ModFrame toClient));
        Assert.Equal(77, toClient.EventId);
        Assert.True(toClient.Body.SequenceEqual(piece));
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        byte[] need = ModWire.Need(1, new[] { new string('a', 64) });
        byte[] needWrongCount = (byte[])need.Clone();
        needWrongCount[2] = 2;
        byte[] needZero = { (byte)ModFrameKind.Need, 1, 0 };
        byte[] needTooMany = new byte[3 + 66 * 32];
        needTooMany[0] = (byte)ModFrameKind.Need; needTooMany[2] = 66;
        byte[] bigChunk = new byte[2 + 5 + ModWire.MaxChunkBytes + 1];
        bigChunk[0] = (byte)ModFrameKind.Chunk;
        byte[] bigMessage = new byte[2 + ModWire.MaxToServerBytes + 1];
        bigMessage[0] = (byte)ModFrameKind.ToServer;
        byte[] bigEvent = new byte[2 + 4 + ModWire.MaxToClientBytes + 1];
        bigEvent[0] = (byte)ModFrameKind.ToClient;
        byte[] bigDecline = new byte[2 + 2 + ModWire.MaxReasonBytes + 1];
        bigDecline[0] = (byte)ModFrameKind.Decline;
        byte[] bigOffer = new byte[ModWire.MaxFrameBytes + 1];
        bigOffer[0] = (byte)ModFrameKind.Offer;

        return new TheoryData<string, byte[]>
        {
            { "empty", Array.Empty<byte>() },
            { "one byte", new byte[] { 1 } },
            { "kind zero", new byte[] { 0, 1, 2, 3 } },
            { "unknown kind", new byte[] { 200, 1, 2, 3 } },
            { "offer without a version", new byte[] { (byte)ModFrameKind.Offer, 1 } },
            { "oversized offer", bigOffer },
            { "need whose count disagrees with its length", needWrongCount },
            { "need for nothing", needZero },
            { "need for more files than a manifest can list", needTooMany },
            { "chunk with no data", new byte[] { (byte)ModFrameKind.Chunk, 1, 0, 0, 0, 0, 0 } },
            { "oversized chunk", bigChunk },
            { "ready with a body", new byte[] { (byte)ModFrameKind.Ready, 1, 9 } },
            { "decline without a reason", new byte[] { (byte)ModFrameKind.Decline, 1, 7 } },
            { "oversized decline text", bigDecline },
            { "oversized mod message", bigMessage },
            { "event without an id", new byte[] { (byte)ModFrameKind.ToClient, 1, 0, 0 } },
            { "oversized event", bigEvent },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void TryParse_RejectsMalformedFrames(string what, byte[] frame) =>
        Assert.False(ModWire.TryParse(frame, out _), what);

    [Fact]
    public void SafeText_NeutralisesWhatTheOtherEndWrote()
    {
        Assert.Equal("ok?[2J?next", ModWire.SafeText(Encoding.UTF8.GetBytes("ok\u001b[2J\nnext")));
        Assert.Equal("??", ModWire.SafeText(new byte[] { 0xFF, 0xFE }));
        Assert.Equal(ModWire.MaxReasonBytes, ModWire.SafeText(new byte[5000].Select(_ => (byte)'x').ToArray()).Length);
        // The encoder cuts over-long text on a character boundary, never mid-sequence.
        Assert.True(ModWire.TryParse(ModWire.Decline(1, ModDeclineReason.Faulted, new string('\u00e9', 500)), out ModFrame frame));
        Assert.True(frame.Body.Length <= ModWire.MaxReasonBytes);
        Assert.DoesNotContain('?', ModWire.SafeText(frame.Body));
    }

    [Fact]
    public void Fuzz_ParserAndBothStateMachines_NeverThrow()
    {
        using ModTempDir dir = new();
        ModOffer offer = ModOffer.FromMemory(ModOfferFixture.Description(false, ModCapabilities.Net), ("client.wasm", ModOfferFixture.Bytes(3000, 5)));
        ModOfferClient client = new(ModOfferFixture.Options(), new ModCache(dir.Sub("cache")), new ModConsentStore());
        ModOfferPeer peer = new(offer, 0);
        byte[][] valid =
        {
            ModWire.Offer(1, offer.ManifestJson), ModWire.Need(1, new[] { offer.Manifest.ClientModule!.Sha256 }),
            ModWire.Chunk(1, 0, 0, new byte[64]), ModWire.Ready(1), ModWire.Decline(1, ModDeclineReason.Faulted, "x"),
            ModWire.Abort(1, "x"), ModWire.ToServer(1, new byte[10]), ModWire.ToClient(1, 3, new byte[10]),
        };

        Random random = new(424242);
        for (int i = 0; i < 20_000; i++)
        {
            byte[] frame;
            if (random.Next(3) == 0)
            {
                frame = new byte[random.Next(0, 80)];
                random.NextBytes(frame);
            }
            else
            {
                frame = (byte[])valid[random.Next(valid.Length)].Clone();
                for (int f = random.Next(4); f > 0 && frame.Length > 0; f--) frame[random.Next(frame.Length)] = (byte)random.Next(256);
                if (random.Next(5) == 0) frame = frame.AsSpan(0, random.Next(frame.Length + 1)).ToArray();
            }

            ModWire.TryParse(frame, out _);
            // A refusal is terminal for a connection, so a fresh client is started now and then.
            if (i % 500 == 499)
            {
                client.Dispose();
                client = new ModOfferClient(ModOfferFixture.Options(), new ModCache(dir.Sub("cache")), new ModConsentStore());
            }
            client.HandleFrame(frame, i * 0.01);
            if (client.State == ModClientState.AwaitingConsent && random.Next(2) == 0)
                client.ResolveConsent(random.Next(8) == 0 ? ModConsentDecision.DenyOnce : (ModConsentDecision)random.Next(3), i * 0.01);
            if (client.State == ModClientState.ReadyToLoad) client.ReportLoaded();
            if (client.State == ModClientState.Active && random.Next(4) == 0) client.TrySendToServer(frame.AsSpan(0, Math.Min(frame.Length, 64)), i * 0.01);
            client.Update(i * 0.01);
            while (client.TryDequeueOutbound(out _)) { }
            while (client.TryDequeueEvent(out _, out _)) { }

            // A violation is terminal for a peer, so a fresh one is started to keep exercising the handlers.
            if (peer.ShouldDisconnect) { peer.Dispose(); peer = new ModOfferPeer(offer, i * 0.01); }
            peer.HandleFrame(frame, i * 0.01);
            peer.Update(i * 0.01);
            for (int n = 0; n < 8 && peer.TryDequeueOutbound(i * 0.01, out _); n++) { }
        }
        client.Dispose();
        peer.Dispose();

        // Whatever the noise did, nothing may be left half-written in the cache.
        Assert.DoesNotContain(dir.Files("cache"), f => f.EndsWith(".part", StringComparison.Ordinal));
    }
}

public class ModRateLimiterTests
{
    [Fact]
    public void AllowsABurstThenTheSustainedRate()
    {
        ModRateLimiter limiter = new(perSecond: 10, burst: 5);
        int sent = 0;
        for (int i = 0; i < 100; i++) if (limiter.TryConsume(1, 0.0)) sent++;
        Assert.Equal(5, sent);

        // One second later: ten more tokens earned, but the bucket only holds five.
        sent = 0;
        for (int i = 0; i < 100; i++) if (limiter.TryConsume(1, 1.0)) sent++;
        Assert.Equal(5, sent);

        // Spread over a second, the sustained rate comes through.
        sent = 0;
        for (int i = 1; i <= 1000; i++) if (limiter.TryConsume(1, 1.0 + i * 0.001)) sent++;
        Assert.InRange(sent, 9, 11);
    }

    [Fact]
    public void LimitsBytesIndependently_AndTakesNothingWhenItRefuses()
    {
        ModRateLimiter limiter = new(perSecond: 1000, burst: 1000, bytesPerSecond: 100, burstBytes: 250);
        Assert.True(limiter.TryConsume(200, 0));
        Assert.False(limiter.TryConsume(100, 0));  // only 50 left
        Assert.True(limiter.TryConsume(50, 0));    // and the refusal above did not spend them
        Assert.False(limiter.TryConsume(1, 0));
        Assert.Equal(100, limiter.AvailableBytes(1.0));
        Assert.False(limiter.TryConsume(-1, 5.0));
    }

    [Fact]
    public void AClockThatMisbehaves_GrantsNothing()
    {
        ModRateLimiter limiter = new(perSecond: 1, burst: 1);
        Assert.True(limiter.TryConsume(0, 100));
        Assert.False(limiter.TryConsume(0, 50));                       // time went backwards: no refill
        Assert.False(limiter.TryConsume(0, double.NaN));
        Assert.False(limiter.TryConsume(0, double.PositiveInfinity));
        Assert.False(limiter.TryConsume(0, 50.5));                     // measured from the new, earlier origin
        Assert.True(limiter.TryConsume(0, 51.5));
    }
}

/// <summary>The remembered answers: what is stored, what it matches, and that anything doubtful means "ask".</summary>
public class ModConsentStoreTests
{
    private static readonly string ModA = new('a', 64), ModB = new('b', 64);
    private const string Server = "Mods.Example.org:26000", Other = "other.example.org:26000";

    [Fact]
    public void NothingRemembered_MeansAsk()
    {
        ModConsentStore store = new();
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(Server, ModA));
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(null, ModA));
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(Server, "not a hash"));
    }

    [Fact]
    public void AnAllowance_IsBoundToTheExactModAndItsScope()
    {
        ModConsentStore store = new();
        Assert.True(store.Record(Server, ModA, ModConsentDecision.AllowOnThisServer, "overkill"));
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup(Server, ModA));
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup("  mods.example.ORG:26000 ", ModA)); // the key is normalised
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(Server, ModB));    // same server, changed mod
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(Other, ModA));     // same mod, another server

        Assert.True(store.Record(Server, ModB, ModConsentDecision.AllowEverywhere));
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup(Other, ModB));
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup(null, ModB));
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(Other, ModA));
    }

    [Fact]
    public void OnceDecisions_AndDecisionsThatNeedAMissingServerKey_AreNotStored()
    {
        ModConsentStore store = new();
        Assert.False(store.Record(Server, ModA, ModConsentDecision.AllowOnce));
        Assert.False(store.Record(Server, ModA, ModConsentDecision.DenyOnce));
        Assert.False(store.Record("", ModA, ModConsentDecision.AllowOnThisServer));
        Assert.False(store.Record("bad key with spaces", ModA, ModConsentDecision.AllowOnThisServer));
        Assert.False(store.Record(new string('x', 300), ModA, ModConsentDecision.DenyThisServer));
        Assert.False(store.Record(Server, "nope", ModConsentDecision.AllowEverywhere));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void ADenial_OutranksAnAllowance()
    {
        ModConsentStore store = new();
        store.Record(Server, ModA, ModConsentDecision.AllowEverywhere);
        store.Record(Server, ModB, ModConsentDecision.DenyThisServer);
        Assert.Equal(ModConsentAnswer.Deny, store.Lookup(Server, ModA));   // allowed everywhere, but this server is refused
        Assert.Equal(ModConsentAnswer.Deny, store.Lookup(Server, ModB));
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup(Other, ModA));

        store.Record(Other, ModB, ModConsentDecision.DenyThisMod);
        Assert.Equal(ModConsentAnswer.Deny, store.Lookup("third.example.org", ModB));
    }

    [Fact]
    public void AFreshAllowance_ReplacesTheDenialsItContradicts()
    {
        ModConsentStore store = new();
        store.Record(Server, ModA, ModConsentDecision.DenyThisMod);
        store.Record(Server, ModA, ModConsentDecision.DenyThisServer);
        Assert.Equal(ModConsentAnswer.Deny, store.Lookup(Server, ModA));
        store.Record(Server, ModA, ModConsentDecision.AllowOnThisServer);
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup(Server, ModA));
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup(Server, ModB));    // and nothing wider than what was said
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Answers_SurviveARestart_AndCanBeForgotten()
    {
        using ModTempDir dir = new();
        string path = dir.Sub("consent.json");
        ModConsentStore first = new(path);
        first.Record(Server, ModA, ModConsentDecision.AllowOnThisServer, "overkill\u0007");
        first.Record(Other, ModB, ModConsentDecision.DenyThisServer);

        ModConsentStore second = new(path);
        Assert.Equal(ModConsentAnswer.Allow, second.Lookup(Server, ModA));
        Assert.Equal(ModConsentAnswer.Deny, second.Lookup(Other, ModA));
        Assert.Equal("overkill?", second.Entries[0].Label);
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.tmp"));

        Assert.Equal(1, second.Forget(Server));
        Assert.Equal(ModConsentAnswer.Ask, new ModConsentStore(path).Lookup(Server, ModA));
        Assert.Equal(1, second.Forget());
        Assert.Empty(new ModConsentStore(path).Entries);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"entries\":[{\"server\":\"\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"allow\":true}]}")]
    // An allowance with no hash would allow everything a server ever offers; one with no scope at all, everything anywhere.
    [InlineData("{\"version\":1,\"entries\":[{\"server\":\"mods.example.org:26000\",\"sha256\":\"\",\"allow\":true}]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"server\":\"\",\"sha256\":\"\",\"allow\":true},{\"server\":\"\",\"sha256\":\"\",\"allow\":false},null]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"server\":\"MODS.example.org:26000\",\"sha256\":\"AAAA\",\"allow\":true}]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"server\":null,\"sha256\":null,\"allow\":true}]}")]
    public void ADamagedOrHandEditedFile_NeverAllowsAnything(string content)
    {
        using ModTempDir dir = new();
        string path = dir.Sub("consent.json");
        File.WriteAllText(path, content);
        ModConsentStore store = new(path);
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup("mods.example.org:26000", ModA));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void AnOversizedFile_IsNotRead_AndTheStoreStaysBounded()
    {
        using ModTempDir dir = new();
        string path = dir.Sub("consent.json");
        File.WriteAllBytes(path, new byte[ModConsentStore.MaxFileBytes + 1]);
        Assert.Empty(new ModConsentStore(path).Entries);

        ModConsentStore store = new();
        for (int i = 0; i < ModConsentStore.MaxEntries + 50; i++)
            store.Record($"server{i}.example.org", ModA, ModConsentDecision.AllowOnThisServer);
        Assert.Equal(ModConsentStore.MaxEntries, store.Entries.Count);
        // The oldest answers were dropped, and a dropped answer is a question again - never a yes.
        Assert.Equal(ModConsentAnswer.Ask, store.Lookup("server0.example.org", ModA));
        Assert.Equal(ModConsentAnswer.Allow, store.Lookup($"server{ModConsentStore.MaxEntries + 49}.example.org", ModA));
    }
}

/// <summary>The piece-by-piece path into the cache that the in-band download uses.</summary>
public class ModCacheWriterTests
{
    [Fact]
    public void PiecesAddUpToTheFile_AndOnlyACommitPublishesIt()
    {
        using ModTempDir dir = new();
        ModCache cache = new(dir.Sub("cache"));
        byte[] content = ModOfferFixture.Bytes(50_000, 3);
        ModArtifact artifact = new() { Name = "pack.pk3", SizeBytes = content.Length, Sha256 = ModOfferFixture.Sha(content) };

        using (ModCacheWriter abandoned = cache.BeginStore(artifact))
        {
            abandoned.Append(content.AsSpan(0, 20_000));
            Assert.False(cache.Contains(artifact));
        }
        Assert.Empty(dir.Files("cache"));   // disposing without committing removes the partial file

        using ModCacheWriter writer = cache.BeginStore(artifact);
        for (int at = 0; at < content.Length; at += 7001) writer.Append(content.AsSpan(at, Math.Min(7001, content.Length - at)));
        Assert.True(writer.IsComplete);
        Assert.False(cache.Contains(artifact));
        string path = writer.Commit();
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Single(dir.Files("cache"));
        Assert.Throws<InvalidOperationException>(() => writer.Append(new byte[1]));
    }

    [Fact]
    public void AWrongOrShortOrLongFile_NeverReachesItsAddress()
    {
        using ModTempDir dir = new();
        ModCache cache = new(dir.Sub("cache"));
        byte[] content = ModOfferFixture.Bytes(10_000, 4);
        ModArtifact artifact = new() { Name = "pack.pk3", SizeBytes = content.Length, Sha256 = ModOfferFixture.Sha(content) };

        using (ModCacheWriter wrong = cache.BeginStore(artifact))
        {
            wrong.Append(new byte[content.Length]);
            Assert.Contains("SHA-256", Assert.Throws<ModManifestException>(() => wrong.Commit()).Message);
        }
        using (ModCacheWriter shortFile = cache.BeginStore(artifact))
        {
            shortFile.Append(content.AsSpan(0, 100));
            Assert.Contains("its manifest declares", Assert.Throws<ModManifestException>(() => shortFile.Commit()).Message);
        }
        using (ModCacheWriter longFile = cache.BeginStore(artifact))
        {
            longFile.Append(content);
            Assert.Contains("larger than", Assert.Throws<ModManifestException>(() => longFile.Append(new byte[1])).Message);
        }
        Assert.False(cache.Contains(artifact));
        Assert.Empty(dir.Files("cache"));
        Assert.Throws<ModManifestException>(() => cache.BeginStore(artifact with { Sha256 = "../../../evil" }));
        Assert.Throws<ModManifestException>(() => cache.BeginStore(artifact with { SizeBytes = 0 }));
    }

    [Fact]
    public void CleanPartials_RemovesOnlyOldLeftovers()
    {
        using ModTempDir dir = new();
        ModCache cache = new(dir.Sub("cache"));
        byte[] content = ModOfferFixture.Bytes(1000, 6);
        ModArtifact artifact = new() { Name = "pack.pk3", SizeBytes = content.Length, Sha256 = ModOfferFixture.Sha(content) };
        cache.Store(artifact, new MemoryStream(content));
        string folder = System.IO.Path.GetDirectoryName(dir.Files("cache").Single())!;
        string stale = System.IO.Path.Combine(folder, artifact.Sha256 + ".deadbeef.part"), fresh = System.IO.Path.Combine(folder, artifact.Sha256 + ".cafe.part");
        File.WriteAllBytes(stale, new byte[300]);
        File.WriteAllBytes(fresh, new byte[200]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        Assert.Equal(300, cache.CleanPartials(TimeSpan.FromHours(1)));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(cache.Contains(artifact));

        cache.Remove(artifact);
        Assert.False(cache.Contains(artifact));
    }
}

/// <summary>
/// The offer flow end to end, without a sandbox: a real server peer and a real client state machine
/// passing frames to each other, then each of them alone against a hostile other end.
/// </summary>
public class ModOfferFlowTests
{
    private static readonly byte[] Module = ModOfferFixture.Bytes(70_000, 11);
    private static readonly byte[] Pack = ModOfferFixture.Bytes(150_000, 12);

    private static ModOffer Offer(bool required = false, params string[] capabilities) =>
        ModOffer.FromMemory(ModOfferFixture.Description(required, capabilities), ("client.wasm", Module), new[] { ("overkill.pk3", Pack) });

    /// <summary>One connection: a server peer and a client, a shared clock, and the frames between them.</summary>
    private sealed class Link : IDisposable
    {
        public readonly ModOfferPeer Peer;
        public readonly ModOfferClient Client;
        public readonly ModCache Cache;
        public double Now;
        public int ChunksDelivered;
        public readonly List<ModFrameKind> ToClient = new(), ToServer = new();

        public Link(ModOffer offer, ModTempDir dir, ModConsentStore? consent = null, ModClientOptions? options = null, ModPeerOptions? peerOptions = null, double now = 0)
        {
            Now = now;
            Cache = new ModCache(dir.Sub("cache"));
            Client = new ModOfferClient(options ?? ModOfferFixture.Options(), Cache, consent ?? new ModConsentStore());
            Peer = new ModOfferPeer(offer, now, peerOptions);
        }

        /// <summary>Moves frames both ways until nothing more happens, letting time pass while a download is being paced.</summary>
        public void Pump()
        {
            for (int round = 0; round < 10_000; round++)
            {
                bool moved = false;
                while (Peer.TryDequeueOutbound(Now, out byte[] frame))
                {
                    ToClient.Add((ModFrameKind)frame[0]);
                    if (frame[0] == (byte)ModFrameKind.Chunk) ChunksDelivered++;
                    Client.HandleFrame(frame, Now);
                    moved = true;
                }
                while (Client.TryDequeueOutbound(out byte[] frame))
                {
                    ToServer.Add((ModFrameKind)frame[0]);
                    Peer.HandleFrame(frame, Now);
                    moved = true;
                }
                Client.Update(Now);
                Peer.Update(Now);
                if (moved) continue;
                if (Peer.State != ModPeerState.Sending || Client.State != ModClientState.Downloading) return;
                Now += 0.05;
            }
            throw new InvalidOperationException("the exchange did not settle");
        }

        public void Dispose() { Client.Dispose(); Peer.Dispose(); }
    }

    // ---- the path that works -----------------------------------------------------------------------

    [Fact]
    public void Offer_Consent_Download_Verify_Ready()
    {
        using ModTempDir dir = new();
        ModOffer offer = Offer();
        using Link link = new(offer, dir);

        link.Pump();
        Assert.Equal(ModClientState.AwaitingConsent, link.Client.State);
        ModConsentRequest request = link.Client.PendingConsent!;
        Assert.Equal("Overkill", request.Manifest.Consent.Title);
        Assert.Equal(offer.ManifestSha256, request.ManifestSha256);
        Assert.Equal(Module.Length + Pack.Length, request.DownloadBytes);
        Assert.Equal(2, request.DownloadFiles);
        // Until the player answers: nothing requested, nothing sent, nothing on disk.
        Assert.Empty(link.ToServer);
        Assert.Equal(0, link.Peer.BytesSent);
        Assert.Empty(dir.Files("cache"));
        Assert.True(link.Peer.MayPlay);

        link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);
        link.Pump();

        Assert.Equal(ModClientState.ReadyToLoad, link.Client.State);
        ModLoadPlan plan = link.Client.Plan!;
        Assert.Equal(Module, File.ReadAllBytes(plan.ModulePath!));
        Assert.Equal(Pack, File.ReadAllBytes(plan.Packs.Single().Path));
        Assert.Equal("overkill.pk3", plan.Packs.Single().Artifact.Name);
        Assert.Equal(Module.Length + Pack.Length, link.Peer.BytesSent);
        Assert.Equal(ModPeerState.AwaitingReady, link.Peer.State);
        Assert.Equal(2, dir.Files("cache").Count());   // two files, no leftovers

        link.Client.ReportLoaded();
        link.Pump();
        Assert.Equal(ModClientState.Active, link.Client.State);
        Assert.Equal(ModPeerState.Ready, link.Peer.State);
        Assert.Equal(new[] { ModFrameKind.Need, ModFrameKind.Ready }, link.ToServer);
        Assert.False(link.Peer.ShouldDisconnect);
    }

    [Fact]
    public void TheDownload_IsPacedByTheServersUploadLimit()
    {
        using ModTempDir dir = new();
        ModPeerOptions slow = new() { UploadBytesPerSecond = 100_000, UploadBurstBytes = 20_000 };
        using Link link = new(Offer(), dir, peerOptions: slow);
        link.Pump();
        link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);

        // With the clock held still, only the burst gets through.
        while (link.Client.TryDequeueOutbound(out byte[] need)) link.Peer.HandleFrame(need, link.Now);
        int sentAtOnce = 0;
        while (link.Peer.TryDequeueOutbound(link.Now, out byte[] frame)) sentAtOnce += frame.Length;
        Assert.InRange(sentAtOnce, 1, 20_000 + ModWire.MaxChunkBytes);

        // 220,000 bytes at 100,000 a second cannot take less than about two seconds.
        using ModTempDir dir2 = new();
        using Link timed = new(Offer(), dir2, peerOptions: slow);
        timed.Pump();
        timed.Client.ResolveConsent(ModConsentDecision.AllowOnce, timed.Now);
        timed.Pump();
        Assert.Equal(ModClientState.ReadyToLoad, timed.Client.State);
        Assert.InRange(timed.Now, 1.8, 3.0);
    }

    [Fact]
    public void WhatIsAlreadyCached_IsNotDownloadedAgain()
    {
        using ModTempDir dir = new();
        ModOffer offer = Offer();
        ModConsentStore consent = new();
        using (Link first = new(offer, dir, consent))
        {
            first.Pump();
            first.Client.ResolveConsent(ModConsentDecision.AllowOnThisServer, first.Now);
            first.Pump();
            Assert.Equal(ModClientState.ReadyToLoad, first.Client.State);
        }

        // Second connection: consent is remembered and both files are cached, so the client goes
        // straight to loading and the server is asked for nothing.
        using Link second = new(offer, dir, consent);
        second.Pump();
        Assert.Equal(ModClientState.ReadyToLoad, second.Client.State);
        Assert.Equal(0, second.Peer.BytesSent);
        second.Client.ReportLoaded();
        second.Pump();
        Assert.Equal(new[] { ModFrameKind.Ready }, second.ToServer);
        Assert.Equal(ModPeerState.Ready, second.Peer.State);

        // A mod that shares one file with the cached one downloads only the other.
        byte[] otherModule = ModOfferFixture.Bytes(40_000, 99);
        ModOffer sibling = ModOffer.FromMemory(ModOfferFixture.Description(), ("client.wasm", otherModule), new[] { ("renamed.pk3", Pack) });
        using Link third = new(sibling, dir, consent);
        third.Pump();
        Assert.Equal(ModClientState.AwaitingConsent, third.Client.State);   // a different manifest is a new question
        Assert.Equal(otherModule.Length, third.Client.PendingConsent!.DownloadBytes);
        third.Client.ResolveConsent(ModConsentDecision.AllowOnce, third.Now);
        third.Pump();
        Assert.Equal(otherModule.Length, third.Peer.BytesSent);
        Assert.Equal(ModClientState.ReadyToLoad, third.Client.State);
    }

    [Fact]
    public void AnAssetsOnlyMod_HasNoModuleInItsPlan()
    {
        using ModTempDir dir = new();
        ModOffer offer = ModOffer.FromMemory(ModOfferFixture.Description(), null, new[] { ("overkill.pk3", Pack) });
        using Link link = new(offer, dir);
        link.Pump();
        link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);
        link.Pump();
        Assert.Equal(ModClientState.ReadyToLoad, link.Client.State);
        Assert.Null(link.Client.Plan!.ModulePath);
        Assert.Single(link.Client.Plan.Packs);
    }

    // ---- consent -----------------------------------------------------------------------------------

    [Fact]
    public void WithModsOff_TheOfferIsDeclinedUnread_AndThePlayerStillJoins()
    {
        using ModTempDir dir = new();
        using Link link = new(Offer(), dir, options: ModOfferFixture.Options(allowMods: false));
        link.Pump();

        Assert.Equal(ModClientState.Declined, link.Client.State);
        Assert.Equal(ModDeclineReason.ModsDisabled, link.Client.DeclineReason);
        Assert.Null(link.Client.Manifest);          // the manifest was never parsed
        Assert.Null(link.Client.PendingConsent);    // and the player was never asked
        Assert.Equal(new[] { ModFrameKind.Decline }, link.ToServer);
        Assert.Equal(0, link.Peer.BytesSent);
        Assert.Empty(dir.Files("cache"));

        Assert.Equal(ModPeerState.Declined, link.Peer.State);
        Assert.Equal(ModDeclineReason.ModsDisabled, link.Peer.DeclineReason);
        Assert.True(link.Peer.MayPlay);
        Assert.False(link.Peer.ShouldDisconnect);
    }

    [Fact]
    public void WithModsOff_EvenARememberedAllowanceAndACachedCopy_DoNotRunTheMod()
    {
        using ModTempDir dir = new();
        ModOffer offer = Offer();
        ModConsentStore consent = new();
        using (Link first = new(offer, dir, consent))
        {
            first.Pump();
            first.Client.ResolveConsent(ModConsentDecision.AllowEverywhere, first.Now);
            first.Pump();
        }
        using Link off = new(offer, dir, consent, ModOfferFixture.Options(allowMods: false));
        off.Pump();
        Assert.Equal(ModDeclineReason.ModsDisabled, off.Client.DeclineReason);
        Assert.Null(off.Client.Plan);
    }

    [Fact]
    public void ModsOffByDefault()
    {
        Assert.False(new ModClientOptions().AllowMods);
    }

    [Fact]
    public void ARequiredMod_ThatIsRefused_EndsWithTheServerClosingTheConnection()
    {
        using ModTempDir dir = new();
        using Link off = new(Offer(required: true), dir, options: ModOfferFixture.Options(allowMods: false));
        Assert.False(off.Peer.MayPlay);   // required: not until the mod runs
        off.Pump();
        Assert.True(off.Peer.ShouldDisconnect);
        Assert.Contains("requires the mod 'overkill'", off.Peer.DisconnectReason);
        Assert.Contains("ModsDisabled", off.Peer.DisconnectReason);
        Assert.False(off.Peer.MayPlay);

        using ModTempDir dir2 = new();
        using Link denied = new(Offer(required: true), dir2);
        denied.Pump();
        Assert.True(denied.Client.PendingConsent!.Manifest.Required);   // the prompt can say what refusing costs
        denied.Client.ResolveConsent(ModConsentDecision.DenyOnce, denied.Now);
        denied.Pump();
        Assert.True(denied.Peer.ShouldDisconnect);
        Assert.Empty(dir2.Files("cache"));

        using ModTempDir dir3 = new();
        using Link accepted = new(Offer(required: true), dir3);
        accepted.Pump();
        accepted.Client.ResolveConsent(ModConsentDecision.AllowOnce, accepted.Now);
        accepted.Pump();
        Assert.False(accepted.Peer.MayPlay);
        accepted.Client.ReportLoaded();
        accepted.Pump();
        Assert.True(accepted.Peer.MayPlay);
        Assert.False(accepted.Peer.ShouldDisconnect);
    }

    [Fact]
    public void ADeclinedMod_IsNeverDownloaded()
    {
        using ModTempDir dir = new();
        using Link link = new(Offer(), dir);
        link.Pump();
        link.Client.ResolveConsent(ModConsentDecision.DenyOnce, link.Now);
        link.Pump();

        Assert.Equal(ModClientState.Declined, link.Client.State);
        Assert.Equal(ModDeclineReason.ConsentDenied, link.Client.DeclineReason);
        Assert.Equal(0, link.Peer.BytesSent);
        Assert.Equal(0, link.ChunksDelivered);
        Assert.DoesNotContain(ModFrameKind.Need, link.ToServer);
        Assert.Empty(dir.Files("cache"));
        Assert.True(link.Peer.MayPlay);

        // The server asking again on the same connection gets the same answer without a new prompt.
        link.Peer.Reoffer(link.Now);
        link.Pump();
        Assert.Equal(ModClientState.Declined, link.Client.State);
        Assert.Null(link.Client.PendingConsent);
    }

    [Fact]
    public void AllowOnce_LastsForTheConnection_AndNoLonger()
    {
        using ModTempDir dir = new();
        ModOffer offer = Offer();
        ModConsentStore consent = new();
        using (Link link = new(offer, dir, consent))
        {
            link.Pump();
            link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);
            link.Pump();
            link.Client.ReportLoaded();
            link.Pump();

            // Level change: the mod stops, the server offers again, and the same mod needs no new answer.
            ModLoadPlan before = link.Client.Plan!;
            link.Client.LevelChanged();
            Assert.Null(link.Client.Plan);
            Assert.Equal(ModClientState.Idle, link.Client.State);
            link.Peer.Reoffer(link.Now);
            link.Pump();
            Assert.Equal(ModClientState.ReadyToLoad, link.Client.State);
            Assert.NotSame(before, link.Client.Plan);
            link.Client.ReportLoaded();
            link.Pump();
            Assert.Equal(ModPeerState.Ready, link.Peer.State);

            // Disconnecting forgets it.
            link.Client.Disconnected();
            Assert.Equal(ModClientState.Idle, link.Client.State);
            Assert.Null(link.Client.Plan);
        }
        Assert.Empty(consent.Entries);
        using Link again = new(offer, dir, consent);
        again.Pump();
        Assert.Equal(ModClientState.AwaitingConsent, again.Client.State);
    }

    [Fact]
    public void RememberedAnswers_AreBoundToServerAndExactManifest()
    {
        using ModTempDir dir = new();
        ModOffer offer = Offer();
        ModConsentStore consent = new();
        using (Link link = new(offer, dir, consent))
        {
            link.Pump();
            link.Client.ResolveConsent(ModConsentDecision.AllowOnThisServer, link.Now);
            link.Pump();
        }

        // Another server offering the very same mod is still a question.
        using (Link elsewhere = new(offer, dir, consent, ModOfferFixture.Options() with { ServerKey = "other.example.org:26000" }))
        {
            elsewhere.Pump();
            Assert.Equal(ModClientState.AwaitingConsent, elsewhere.Client.State);
            Assert.Equal(0, elsewhere.Client.PendingConsent!.DownloadBytes);   // cached already; consent is still asked
        }

        // The same server changing one word of what it shows the player is a question too.
        ModOffer reworded = ModOffer.FromMemory(
            ModOfferFixture.Description() with { Consent = new ModConsent { Title = "Overkill", Description = "Totally harmless.", Author = "someone" } },
            ("client.wasm", Module), new[] { ("overkill.pk3", Pack) });
        using (Link changed = new(reworded, dir, consent))
        {
            changed.Pump();
            Assert.Equal(ModClientState.AwaitingConsent, changed.Client.State);
            changed.Client.ResolveConsent(ModConsentDecision.DenyThisServer, changed.Now);
            changed.Pump();
        }

        // "Never for this server" now outranks the earlier allowance, without asking.
        using Link refused = new(offer, dir, consent);
        refused.Pump();
        Assert.Equal(ModClientState.Declined, refused.Client.State);
        Assert.Equal(ModDeclineReason.ConsentDenied, refused.Client.DeclineReason);
        Assert.Null(refused.Client.PendingConsent);
    }

    [Fact]
    public void SwitchingModsOff_StopsTheModAtOnce_AndTellsTheServer()
    {
        using ModTempDir dir = new();
        using Link link = new(Offer(), dir);
        link.Pump();
        link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);
        link.Pump();
        link.Client.ReportLoaded();
        link.Pump();

        link.Client.AllowMods = false;
        Assert.Null(link.Client.Plan);
        Assert.Equal(ModDeclineReason.ModsDisabled, link.Client.DeclineReason);
        link.Pump();
        Assert.Equal(ModPeerState.Declined, link.Peer.State);
    }

    [Fact]
    public void Cancel_AbandonsAPromptOrADownload_AndCleansUp()
    {
        using ModTempDir dir = new();
        using Link link = new(Offer(), dir, peerOptions: new ModPeerOptions { UploadBytesPerSecond = 20_000, UploadBurstBytes = 16_384 });
        link.Pump();
        link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);
        while (link.Client.TryDequeueOutbound(out byte[] need)) link.Peer.HandleFrame(need, link.Now);
        Assert.True(link.Peer.TryDequeueOutbound(link.Now, out byte[] firstChunk));
        link.Client.HandleFrame(firstChunk, link.Now);
        Assert.Equal(ModClientState.Downloading, link.Client.State);
        Assert.True(link.Client.DownloadedBytes > 0);
        Assert.Contains(dir.Files("cache"), f => f.EndsWith(".part", StringComparison.Ordinal));

        link.Client.Cancel();
        Assert.Equal(ModDeclineReason.Cancelled, link.Client.DeclineReason);
        Assert.Empty(dir.Files("cache"));
        link.Pump();
        Assert.Equal(ModPeerState.Declined, link.Peer.State);
        long sent = link.Peer.BytesSent;
        link.Now += 60;
        link.Pump();
        Assert.Equal(sent, link.Peer.BytesSent);   // the server stopped sending
    }

    // ---- a hostile server --------------------------------------------------------------------------

    /// <summary>A client on its own, with the test playing the server by hand.</summary>
    private sealed class LoneClient : IDisposable
    {
        public readonly ModTempDir Dir = new();
        public readonly ModOfferClient Client;
        public readonly ModCache Cache;
        public readonly List<byte[]> Sent = new();

        public LoneClient(ModClientOptions? options = null, ModConsentStore? consent = null)
        {
            Cache = new ModCache(Dir.Sub("cache"));
            Client = new ModOfferClient(options ?? ModOfferFixture.Options(), Cache, consent ?? new ModConsentStore());
        }

        public void Receive(byte[] frame, double now = 0)
        {
            Client.HandleFrame(frame, now);
            while (Client.TryDequeueOutbound(out byte[] reply)) Sent.Add(reply);
        }

        /// <summary>Offers <paramref name="manifest"/> and answers the prompt with yes, leaving the client downloading.</summary>
        public void OfferAndAllow(ModManifest manifest, byte sequence = 1)
        {
            Receive(ModWire.Offer(sequence, manifest.ToJson()));
            Assert.Equal(ModClientState.AwaitingConsent, Client.State);
            Client.ResolveConsent(ModConsentDecision.AllowOnce, 0);
            while (Client.TryDequeueOutbound(out byte[] reply)) Sent.Add(reply);
            Assert.Equal(ModClientState.Downloading, Client.State);
        }

        public ModDeclineReason LastDecline()
        {
            Assert.True(ModWire.TryParse(Sent[^1], out ModFrame frame));
            Assert.Equal(ModFrameKind.Decline, frame.Kind);
            return frame.Reason;
        }

        public void Dispose() { Client.Dispose(); Dir.Dispose(); }
    }

    private static ModManifest ManifestFor(byte[] module, byte[]? pack = null, params string[] capabilities) => new()
    {
        ModId = "overkill",
        ModVersion = "1.4.0",
        BaseProtocol = ModOfferFixture.Protocol,
        ClientModule = new ModArtifact { Name = "client.wasm", SizeBytes = module.Length, Sha256 = ModOfferFixture.Sha(module) },
        AssetPacks = pack is null ? Array.Empty<ModArtifact>() : new[] { new ModArtifact { Name = "a.pk3", SizeBytes = pack.Length, Sha256 = ModOfferFixture.Sha(pack) } },
        Consent = new ModConsent { Title = "Overkill", Description = "d", Author = "a" },
        Capabilities = capabilities,
    };

    private static void AssertDeclinedAndClean(LoneClient lone, ModDeclineReason reason)
    {
        Assert.Equal(ModClientState.Declined, lone.Client.State);
        Assert.Equal(reason, lone.Client.DeclineReason);
        Assert.Equal(reason, lone.LastDecline());
        Assert.Null(lone.Client.Plan);
        Assert.Empty(lone.Dir.Files("cache"));
    }

    [Fact]
    public void SubstitutedContent_IsRejectedByItsHash_AndLeavesNothingOnDisk()
    {
        using LoneClient lone = new();
        lone.OfferAndAllow(ManifestFor(Module));
        byte[] evil = ModOfferFixture.Bytes(Module.Length, 666);
        for (int at = 0; at < evil.Length; at += ModWire.MaxChunkBytes)
            lone.Receive(ModWire.Chunk(1, 0, (uint)at, evil.AsSpan(at, Math.Min(ModWire.MaxChunkBytes, evil.Length - at))));
        AssertDeclinedAndClean(lone, ModDeclineReason.DownloadFailed);
    }

    [Fact]
    public void AFileLongerThanDeclared_IsCutOffAtItsDeclaredSize()
    {
        using LoneClient lone = new();
        byte[] small = ModOfferFixture.Bytes(1000, 21);
        lone.OfferAndAllow(ManifestFor(small));
        lone.Receive(ModWire.Chunk(1, 0, 0, new byte[1001]));
        AssertDeclinedAndClean(lone, ModDeclineReason.DownloadFailed);

        // Nothing sent afterwards is written anywhere, however much of it there is.
        for (int i = 0; i < 200; i++) lone.Receive(ModWire.Chunk(1, 0, (uint)(i * 1000), new byte[ModWire.MaxChunkBytes]));
        Assert.Empty(lone.Dir.Files("cache"));
        Assert.Equal(200, lone.Client.IgnoredFrames);
    }

    [Theory]
    [InlineData(0, 500u)]      // a gap
    [InlineData(1, 0u)]        // the second file before the first is finished
    [InlineData(9, 0u)]        // a file that was never asked for
    public void ChunksOutOfOrder_EndTheDownload(byte slot, uint offset)
    {
        using LoneClient lone = new();
        lone.OfferAndAllow(ManifestFor(Module, Pack));
        lone.Receive(ModWire.Chunk(1, slot, offset, new byte[100]));
        AssertDeclinedAndClean(lone, ModDeclineReason.DownloadFailed);
    }

    [Fact]
    public void ARepeatedChunk_EndsTheDownload()
    {
        using LoneClient lone = new();
        lone.OfferAndAllow(ManifestFor(Module));
        lone.Receive(ModWire.Chunk(1, 0, 0, Module.AsSpan(0, 1000)));
        Assert.Equal(ModClientState.Downloading, lone.Client.State);
        lone.Receive(ModWire.Chunk(1, 0, 0, Module.AsSpan(0, 1000)));
        AssertDeclinedAndClean(lone, ModDeclineReason.DownloadFailed);
    }

    [Fact]
    public void ChunksNobodyAskedFor_AreIgnored_BeforeConsentAndWithoutAnOffer()
    {
        using LoneClient lone = new();
        lone.Receive(ModWire.Chunk(1, 0, 0, new byte[1000]));              // no offer at all
        lone.Receive(ModWire.Offer(1, ManifestFor(Module).ToJson()));
        Assert.Equal(ModClientState.AwaitingConsent, lone.Client.State);
        for (int at = 0; at < Module.Length; at += 8000)                    // the real file, pushed before the player answered
            lone.Receive(ModWire.Chunk(1, 0, (uint)at, Module.AsSpan(at, Math.Min(8000, Module.Length - at))));

        Assert.Equal(ModClientState.AwaitingConsent, lone.Client.State);
        Assert.Empty(lone.Sent);
        Assert.Empty(lone.Dir.Files("cache"));
        Assert.False(lone.Cache.Contains(ManifestFor(Module).ClientModule!));
    }

    [Fact]
    public void FramesForAnEarlierOffer_AreIgnored()
    {
        using LoneClient lone = new();
        lone.OfferAndAllow(ManifestFor(Module), sequence: 5);
        lone.Receive(ModWire.Chunk(4, 0, 0, new byte[100]));     // stale: would otherwise corrupt the download
        lone.Receive(ModWire.Abort(4, "old"));
        Assert.Equal(ModClientState.Downloading, lone.Client.State);
        Assert.Equal(0, lone.Client.DownloadedBytes);

        lone.Receive(ModWire.Abort(5, "withdrawn\u0007"));
        Assert.Equal(ModClientState.Idle, lone.Client.State);
        Assert.Equal("withdrawn?", lone.Client.DeclineText);
        Assert.Empty(lone.Dir.Files("cache"));
    }

    [Fact]
    public void FramesAClientShouldNeverReceive_AreIgnored()
    {
        using LoneClient lone = new();
        lone.Receive(ModWire.Need(1, new[] { new string('a', 64) }));
        lone.Receive(ModWire.Ready(1));
        lone.Receive(ModWire.Decline(1, ModDeclineReason.Faulted));
        lone.Receive(ModWire.ToServer(1, new byte[4]));
        lone.Receive(new byte[] { 99, 1, 2 });
        Assert.Equal(ModClientState.Idle, lone.Client.State);
        Assert.Empty(lone.Sent);
        Assert.Equal(5, lone.Client.IgnoredFrames);
    }

    public static TheoryData<string, Func<ModManifest, byte[]>, ModDeclineReason> BadOffers() => new()
    {
        { "not JSON", _ => Encoding.UTF8.GetBytes("{nope"), ModDeclineReason.BadManifest },
        { "path traversal in a file name", m => (m with { AssetPacks = new[] { new ModArtifact { Name = "../../evil.pk3", SizeBytes = 10, Sha256 = new string('c', 64) } } }).ToJson(), ModDeclineReason.BadManifest },
        { "absolute path as a file name", m => (m with { ClientModule = m.ClientModule! with { Name = "C:\\Windows\\x.wasm" } }).ToJson(), ModDeclineReason.BadManifest },
        { "a module bigger than the client's ceiling", m => (m with { ClientModule = m.ClientModule! with { SizeBytes = 1L << 30 } }).ToJson(), ModDeclineReason.BadManifest },
        { "an unknown capability", m => (m with { Capabilities = new[] { "filesystem" } }).ToJson(), ModDeclineReason.BadManifest },
        { "a capability listed twice", m => (m with { Capabilities = new[] { "net", "net" } }).ToJson(), ModDeclineReason.BadManifest },
        { "a negative memory request", m => (m with { Limits = new ModLimitRequest { MaxMemoryBytes = -1 } }).ToJson(), ModDeclineReason.BadManifest },
        { "a newer guest interface", m => (m with { Abi = "vortex_2" }).ToJson(), ModDeclineReason.UnsupportedAbi },
        { "another base protocol", m => (m with { BaseProtocol = 1 }).ToJson(), ModDeclineReason.ProtocolMismatch },
        { "more than the client will download", m => (m with { AssetPacks = new[] { new ModArtifact { Name = "big.pk3", SizeBytes = 900L << 20, Sha256 = new string('c', 64) } } }).ToJson(), ModDeclineReason.TooLarge },
    };

    [Theory]
    [MemberData(nameof(BadOffers))]
    public void BadOffers_AreDeclinedWithoutAPromptOrADownload(string what, Func<ModManifest, byte[]> offer, ModDeclineReason expected)
    {
        using LoneClient lone = new();
        lone.Receive(ModWire.Offer(1, offer(ManifestFor(Module))));
        Assert.True(lone.Client.State == ModClientState.Declined, what);
        Assert.Equal(expected, lone.LastDecline());
        Assert.Null(lone.Client.PendingConsent);
        Assert.DoesNotContain(lone.Sent, f => f[0] == (byte)ModFrameKind.Need);
        Assert.Empty(lone.Dir.Files("cache"));
        // Nothing was created outside the test's own directory either (the traversal cases).
        Assert.False(File.Exists(System.IO.Path.Combine(lone.Dir.Path, "..", "evil.pk3")));
    }

    [Fact]
    public void ANewerTransferVersion_IsDeclinedByName_WithoutReadingTheRest()
    {
        using LoneClient lone = new();
        lone.Receive(ModWire.Offer(1, Encoding.UTF8.GetBytes("anything at all, in a format from the future"), transferVersion: 2));
        Assert.Equal(ModDeclineReason.UnsupportedTransferVersion, lone.LastDecline());
        Assert.True(ModWire.TryParse(lone.Sent[^1], out ModFrame decline));
        Assert.Equal(ModWire.TransferVersion, decline.Version);   // the server learns which version would work
        Assert.Null(lone.Client.Manifest);
    }

    [Fact]
    public void WithoutASandbox_OffersAreDeclinedAsUnsupported()
    {
        using LoneClient lone = new(ModOfferFixture.Options() with { SandboxAvailable = false });
        lone.Receive(ModWire.Offer(1, ManifestFor(Module).ToJson()));
        Assert.Equal(ModDeclineReason.Unsupported, lone.LastDecline());
        Assert.Null(lone.Client.Manifest);
    }

    [Fact]
    public void AFloodOfOffers_CannotFloodThePlayerWithPrompts()
    {
        using LoneClient lone = new();
        int prompts = 0;
        for (int i = 0; i < 500; i++)
        {
            // A different manifest every time, so no remembered answer applies.
            ModManifest manifest = ManifestFor(Module) with { ModVersion = "1." + i };
            lone.Receive(ModWire.Offer((byte)(i % 250 + 1), manifest.ToJson()), now: i * 0.001);
            if (lone.Client.State == ModClientState.AwaitingConsent) prompts++;
        }
        Assert.Equal(4, prompts);   // the burst allowance, then nothing for the rest of that half second
        Assert.Equal(ModDeclineReason.Busy, lone.LastDecline());
        Assert.Equal(496, lone.Sent.Count);

        // And one refusal silences the rest of the connection, however slowly the server asks.
        lone.Receive(ModWire.Offer(1, ManifestFor(Module).ToJson()), now: 1000);
        Assert.Equal(ModClientState.AwaitingConsent, lone.Client.State);
        lone.Client.ResolveConsent(ModConsentDecision.DenyOnce, 1000);
        for (int i = 0; i < 20; i++)
        {
            lone.Receive(ModWire.Offer(2, (ManifestFor(Module) with { ModVersion = "2." + i }).ToJson()), now: 2000 + i * 100);
            Assert.Equal(ModClientState.Declined, lone.Client.State);
            Assert.Null(lone.Client.PendingConsent);
        }
    }

    [Fact]
    public void AStalledOrEndlessDownload_TimesOut()
    {
        ModClientOptions options = ModOfferFixture.Options() with { StallTimeoutSeconds = 30, DownloadTimeoutSeconds = 100 };

        using (LoneClient stalled = new(options))
        {
            stalled.OfferAndAllow(ManifestFor(Module));
            stalled.Receive(ModWire.Chunk(1, 0, 0, Module.AsSpan(0, 100)), now: 5);
            stalled.Client.Update(34);
            Assert.Equal(ModClientState.Downloading, stalled.Client.State);
            stalled.Client.Update(36);
            while (stalled.Client.TryDequeueOutbound(out byte[] f)) stalled.Sent.Add(f);
            AssertDeclinedAndClean(stalled, ModDeclineReason.Timeout);
        }

        // A server that keeps the connection busy with one byte at a time still hits the overall cap.
        using LoneClient trickled = new(options);
        trickled.OfferAndAllow(ManifestFor(Module));
        int at = 0;
        for (double now = 1; trickled.Client.State == ModClientState.Downloading && now < 500; now += 10, at++)
        {
            trickled.Receive(ModWire.Chunk(1, 0, (uint)at, Module.AsSpan(at, 1)), now);
            trickled.Client.Update(now);
        }
        while (trickled.Client.TryDequeueOutbound(out byte[] f)) trickled.Sent.Add(f);
        AssertDeclinedAndClean(trickled, ModDeclineReason.Timeout);
        Assert.InRange(at, 9, 12);
    }

    [Fact]
    public void ServerMessages_ReachOnlyARunningModThatAskedForThem_AndAreCapped()
    {
        // Without the "net" capability nothing is queued.
        using (LoneClient silent = new())
        {
            silent.OfferAndAllow(ManifestFor(ModOfferFixture.Bytes(10, 1)));
            silent.Receive(ModWire.Chunk(1, 0, 0, ModOfferFixture.Bytes(10, 1)));
            silent.Client.ReportLoaded();
            silent.Receive(ModWire.ToClient(1, 5, new byte[10]));
            Assert.False(silent.Client.TryDequeueEvent(out _, out _));
            Assert.False(silent.Client.TrySendToServer(new byte[4], 0));
        }

        ModClientOptions options = ModOfferFixture.Options() with { ToClientPerSecond = 10, ToClientBurst = 20, MaxQueuedEvents = 8 };
        using LoneClient lone = new(options);
        byte[] tiny = ModOfferFixture.Bytes(10, 2);
        lone.Receive(ModWire.ToClient(1, 5, new byte[10]));                 // before any offer
        lone.OfferAndAllow(ManifestFor(tiny, null, ModCapabilities.Net));
        lone.Receive(ModWire.ToClient(1, 5, new byte[10]));                 // while downloading
        lone.Receive(ModWire.Chunk(1, 0, 0, tiny));
        Assert.Equal(ModClientState.ReadyToLoad, lone.Client.State);
        lone.Receive(ModWire.ToClient(1, 5, new byte[10]));                 // verified but not yet running
        lone.Client.ReportLoaded();
        Assert.False(lone.Client.TryDequeueEvent(out _, out _));

        lone.Receive(ModWire.ToClient(1, -1, new byte[10]));                // a host-reserved id
        lone.Receive(ModWire.ToClient(2, 5, new byte[10]));                 // another offer's sequence
        Assert.False(lone.Client.TryDequeueEvent(out _, out _));

        for (int i = 0; i < 1000; i++) lone.Receive(ModWire.ToClient(1, i, new byte[ModWire.MaxToClientBytes]));
        int queued = 0;
        while (lone.Client.TryDequeueEvent(out int id, out byte[] payload))
        {
            Assert.Equal(queued, id);   // in order, from the first
            Assert.Equal(ModWire.MaxToClientBytes, payload.Length);
            queued++;
        }
        Assert.Equal(8, queued);        // the queue cap, not the 1000 that were sent
        Assert.Equal(992, lone.Client.DroppedEvents);
    }

    [Fact]
    public void AModsMessagesToTheServer_AreSizeAndRateLimitedAtTheSource()
    {
        ModClientOptions options = ModOfferFixture.Options() with { ToServerPerSecond = 10, ToServerBurst = 5, ToServerBytesPerSecond = 1000, ToServerBurstBytes = 2000 };
        using LoneClient lone = new(options);
        byte[] tiny = ModOfferFixture.Bytes(10, 2);
        lone.OfferAndAllow(ManifestFor(tiny, null, ModCapabilities.Net));
        Assert.False(lone.Client.TrySendToServer(new byte[4], 0));          // not running yet
        lone.Receive(ModWire.Chunk(1, 0, 0, tiny));
        lone.Client.ReportLoaded();
        while (lone.Client.TryDequeueOutbound(out _)) { }

        Assert.False(lone.Client.TrySendToServer(new byte[ModWire.MaxToServerBytes + 1], 0));
        int sent = 0;
        for (int i = 0; i < 100; i++) if (lone.Client.TrySendToServer(new byte[100], 0)) sent++;
        Assert.Equal(5, sent);                                              // the message burst
        while (lone.Client.TryDequeueOutbound(out byte[] frame)) Assert.Equal((byte)ModFrameKind.ToServer, frame[0]);

        sent = 0;
        for (int i = 0; i < 100; i++) if (lone.Client.TrySendToServer(new byte[1000], 10)) sent++;
        Assert.Equal(2, sent);                                              // ten seconds refilled both budgets; the 2,000-byte one runs out first
    }

    // ---- a hostile client --------------------------------------------------------------------------

    private static ModOfferPeer NewPeer(ModOffer offer, out byte sequence, ModPeerOptions? options = null)
    {
        ModOfferPeer peer = new(offer, 0, options);
        Assert.True(peer.TryDequeueOutbound(0, out byte[] first));
        Assert.Equal((byte)ModFrameKind.Offer, first[0]);
        sequence = first[1];
        return peer;
    }

    private static ModOfferPeer ReadyPeer(ModOffer offer, out byte sequence, ModPeerOptions? options = null)
    {
        ModOfferPeer peer = NewPeer(offer, out sequence, options);
        peer.HandleFrame(ModWire.Ready(sequence), 0);
        Assert.Equal(ModPeerState.Ready, peer.State);
        return peer;
    }

    public static TheoryData<string, Func<ModOffer, byte, byte[][]>> Violations() => new()
    {
        { "garbage", (_, _) => new[] { new byte[] { 1 } } },
        { "an unknown kind", (_, s) => new[] { new byte[] { 77, s, 0 } } },
        { "an offer of its own", (o, s) => new[] { ModWire.Offer(s, o.ManifestJson) } },
        { "a chunk", (_, s) => new[] { ModWire.Chunk(s, 0, 0, new byte[10]) } },
        { "an abort", (_, s) => new[] { ModWire.Abort(s) } },
        { "a server-to-mod message", (_, s) => new[] { ModWire.ToClient(s, 1, new byte[4]) } },
        { "a file the mod does not contain", (_, s) => new[] { ModWire.Need(s, new[] { new string('e', 64) }) } },
        { "the same file twice", (o, s) => new[] { ModWire.Need(s, new[] { o.Manifest.ClientModule!.Sha256, o.Manifest.ClientModule.Sha256 }) } },
        { "a second request", (o, s) => new[] { ModWire.Need(s, new[] { o.Manifest.ClientModule!.Sha256 }), ModWire.Need(s, new[] { o.Manifest.ClientModule.Sha256 }) } },
        { "ready in the middle of a download", (o, s) => new[] { ModWire.Need(s, new[] { o.Manifest.ClientModule!.Sha256 }), ModWire.Ready(s) } },
        { "a mod message before the mod runs", (_, s) => new[] { ModWire.ToServer(s, new byte[4]) } },
        { "an oversized mod message", (_, s) => new[] { ModWire.Ready(s), new byte[] { (byte)ModFrameKind.ToServer, s }.Concat(new byte[ModWire.MaxToServerBytes + 1]).ToArray() } },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void AClientThatBreaksTheProtocol_IsDisconnected_AndSentNothingMore(string what, Func<ModOffer, byte, byte[][]> frames)
    {
        using ModOfferPeer peer = NewPeer(Offer(false, ModCapabilities.Net), out byte sequence);
        foreach (byte[] frame in frames(Offer(false, ModCapabilities.Net), sequence)) peer.HandleFrame(frame, 0);

        Assert.True(peer.ShouldDisconnect, what);
        Assert.StartsWith("mod protocol violation", peer.DisconnectReason);
        Assert.False(peer.MayPlay);
        Assert.False(peer.TryDequeueOutbound(100, out _));
        Assert.False(peer.TryDequeueMessage(out _));
        Assert.False(peer.TrySendToClient(1, new byte[1]));
    }

    [Fact]
    public void AClient_CanOnlyBeSentTheFilesItAskedFor_EachOnce()
    {
        ModOffer offer = Offer();
        using ModOfferPeer peer = NewPeer(offer, out byte sequence);
        peer.HandleFrame(ModWire.Need(sequence, new[] { offer.Manifest.AssetPacks[0].Sha256 }), 0);

        using MemoryStream received = new();
        for (double now = 0; now < 60 && peer.State == ModPeerState.Sending; now += 0.05)
        {
            while (peer.TryDequeueOutbound(now, out byte[] frame))
            {
                Assert.True(ModWire.TryParse(frame, out ModFrame chunk));
                Assert.Equal((ModFrameKind.Chunk, (byte)0, (uint)received.Length), (chunk.Kind, chunk.Slot, chunk.Offset));
                received.Write(chunk.Body);
            }
        }
        Assert.Equal(Pack, received.ToArray());
        Assert.Equal(ModPeerState.AwaitingReady, peer.State);
        Assert.Equal(Pack.Length, peer.BytesSent);
        Assert.False(peer.TryDequeueOutbound(1000, out _));
    }

    [Fact]
    public void StaleAndLateFrames_AreNotViolations()
    {
        ModOffer offer = Offer(false, ModCapabilities.Net);
        using ModOfferPeer peer = NewPeer(offer, out byte sequence, new ModPeerOptions { AnswerTimeoutSeconds = 10 });

        // The level changed while the client's answer to the previous offer was in flight.
        peer.Reoffer(1);
        Assert.True(peer.TryDequeueOutbound(1, out byte[] second));
        byte newSequence = second[1];
        Assert.NotEqual(sequence, newSequence);
        peer.HandleFrame(ModWire.Need(sequence, new[] { offer.Manifest.ClientModule!.Sha256 }), 1);
        peer.HandleFrame(ModWire.ToServer(sequence, new byte[4]), 1);
        Assert.Equal(ModPeerState.Offered, peer.State);
        Assert.False(peer.ShouldDisconnect);

        // The offer lapses while a player reads the prompt; an optional mod costs them nothing.
        peer.Update(5);
        Assert.Equal(ModPeerState.Offered, peer.State);
        peer.Update(12);
        Assert.Equal(ModPeerState.Failed, peer.State);
        Assert.False(peer.ShouldDisconnect);
        Assert.True(peer.MayPlay);
        Assert.True(peer.TryDequeueOutbound(12, out byte[] abort));
        Assert.Equal((byte)ModFrameKind.Abort, abort[0]);

        // Their eventual click arrives after that. Not an offence, and nothing is sent for it.
        peer.HandleFrame(ModWire.Need(newSequence, new[] { offer.Manifest.ClientModule.Sha256 }), 13);
        peer.HandleFrame(ModWire.Decline(newSequence, ModDeclineReason.ConsentDenied), 13);
        Assert.False(peer.ShouldDisconnect);
        Assert.False(peer.TryDequeueOutbound(100, out _));
        Assert.Equal(0, peer.BytesSent);
    }

    [Fact]
    public void ARequiredMod_ThatIsNeverStarted_TimesTheClientOut()
    {
        ModPeerOptions options = new() { AnswerTimeoutSeconds = 10, CompleteTimeoutSeconds = 50 };
        using (ModOfferPeer silent = NewPeer(Offer(required: true), out _, options))
        {
            silent.Update(9);
            Assert.False(silent.ShouldDisconnect);
            silent.Update(11);
            Assert.True(silent.ShouldDisconnect);
            Assert.Contains("did not start it in time", silent.DisconnectReason);
        }

        ModOffer offer = Offer(required: true);
        using ModOfferPeer slow = NewPeer(offer, out byte sequence, options);
        slow.HandleFrame(ModWire.Need(sequence, new[] { offer.Manifest.ClientModule!.Sha256 }), 5);
        slow.Update(40);
        Assert.False(slow.ShouldDisconnect);   // the clock restarted when it answered
        slow.Update(56);
        Assert.True(slow.ShouldDisconnect);
    }

    [Fact]
    public void ModMessages_AreRateLimitedOnArrival_AndTheQueueIsBounded()
    {
        ModOffer offer = Offer(false, ModCapabilities.Net);
        ModPeerOptions options = new() { ToServerPerSecond = 10, ToServerBurst = 20, MaxQueuedMessages = 8 };
        using ModOfferPeer peer = ReadyPeer(offer, out byte sequence, options);

        for (int i = 0; i < 1000; i++) peer.HandleFrame(ModWire.ToServer(sequence, new byte[] { (byte)i }), 0);
        Assert.False(peer.ShouldDisconnect);   // a flood is dropped, not punished here: the server decides
        int received = 0;
        while (peer.TryDequeueMessage(out byte[] payload)) Assert.Equal((byte)received++, payload[0]);
        Assert.Equal(8, received);
        Assert.Equal(992, peer.DroppedMessages);

        // With the queue drained the rate limit is what is left: 20 burst minus the 8 already taken.
        for (int i = 0; i < 1000; i++)
        {
            peer.HandleFrame(ModWire.ToServer(sequence, new byte[1]), 0);
            while (peer.TryDequeueMessage(out _)) received++;
        }
        Assert.Equal(20, received);
    }

    [Fact]
    public void TheModChannel_NeedsTheCapability_AndARunningMod()
    {
        using ModOfferPeer noNet = ReadyPeer(Offer(), out byte sequence);
        Assert.False(noNet.TrySendToClient(1, new byte[4]));
        noNet.HandleFrame(ModWire.ToServer(sequence, new byte[4]), 0);
        Assert.True(noNet.ShouldDisconnect);

        ModOffer offer = Offer(false, ModCapabilities.Net);
        using ModOfferPeer peer = NewPeer(offer, out sequence);
        Assert.False(peer.TrySendToClient(1, new byte[4]));                         // not running yet
        peer.HandleFrame(ModWire.Ready(sequence), 0);
        Assert.False(peer.TrySendToClient(-1, new byte[4]));                        // host-reserved id
        Assert.False(peer.TrySendToClient(1, new byte[ModWire.MaxToClientBytes + 1]));
        Assert.True(peer.TrySendToClient(42, new byte[] { 1, 2, 3 }));
        Assert.True(peer.TryDequeueOutbound(0, out byte[] frame));
        Assert.True(ModWire.TryParse(frame, out ModFrame parsed));
        Assert.Equal((ModFrameKind.ToClient, 42), (parsed.Kind, parsed.EventId));

        int queued = 0;
        while (peer.TrySendToClient(1, new byte[4])) queued++;
        Assert.Equal(256, queued);                                                  // bounded if the caller never drains

        // A client that later reports a fault closes the channel again.
        peer.HandleFrame(ModWire.Decline(sequence, ModDeclineReason.Faulted, "trap\u0000"), 0);
        Assert.Equal(ModPeerState.Declined, peer.State);
        Assert.Equal("trap?", peer.DeclineText);
        Assert.False(peer.TrySendToClient(1, new byte[4]));
        Assert.True(peer.MayPlay);
    }

    // ---- the server's own files --------------------------------------------------------------------

    [Fact]
    public void AnOffer_IsBuiltFromFiles_AndRejectsWhatNoClientWouldAccept()
    {
        using ModTempDir dir = new();
        string module = dir.Sub("client.wasm"), pack = dir.Sub("overkill.pk3");
        File.WriteAllBytes(module, Module);
        File.WriteAllBytes(pack, Pack);

        ModOffer offer = ModOffer.FromFiles(ModOfferFixture.Description(true, ModCapabilities.Net, ModCapabilities.Sound), module, new[] { pack });
        Assert.Equal(ModOfferFixture.Sha(Module), offer.Manifest.ClientModule!.Sha256);
        Assert.Equal(Pack.Length, offer.Manifest.AssetPacks.Single().SizeBytes);
        Assert.True(offer.Manifest.Required);
        Assert.Equal(ModManifest.HashOf(offer.ManifestJson), offer.ManifestSha256);
        // The manifest carries file names only - nothing about where the server keeps them.
        Assert.DoesNotContain(dir.Path.Replace("\\", "\\\\"), Encoding.UTF8.GetString(offer.ManifestJson));
        Assert.DoesNotContain("va-modoffer", Encoding.UTF8.GetString(offer.ManifestJson));

        string wrongExtension = dir.Sub("client.exe");
        File.WriteAllBytes(wrongExtension, Module);
        Assert.Throws<ModManifestException>(() => ModOffer.FromFiles(ModOfferFixture.Description(), wrongExtension));
        Assert.Throws<ModManifestException>(() => ModOffer.FromFiles(ModOfferFixture.Description() with { ModId = "" }, module));
        Assert.Throws<ModManifestException>(() => ModOffer.FromFiles(ModOfferFixture.Description(false, "root"), module));
        Assert.ThrowsAny<IOException>(() => ModOffer.FromFiles(ModOfferFixture.Description(), dir.Sub("missing.wasm")));
    }

    [Fact]
    public void AFileThatChangedUnderTheServer_AbortsTheOffer_InsteadOfSendingWrongBytes()
    {
        using ModTempDir dir = new();
        string module = dir.Sub("client.wasm");
        File.WriteAllBytes(module, Module);
        ModOffer offer = ModOffer.FromFiles(ModOfferFixture.Description(), module);
        File.WriteAllBytes(module, Module.AsSpan(0, 20_000).ToArray());   // truncated after start-up

        using Link link = new(offer, dir);
        link.Pump();
        link.Client.ResolveConsent(ModConsentDecision.AllowOnce, link.Now);
        link.Pump();
        Assert.Equal(ModPeerState.Failed, link.Peer.State);
        Assert.False(link.Peer.ShouldDisconnect);                          // optional mod: the player stays
        Assert.Contains(ModFrameKind.Abort, link.ToClient);
        Assert.Equal(ModClientState.Idle, link.Client.State);
        Assert.Empty(dir.Files("cache"));
    }
}

/// <summary>The per-server table of peers: the five calls a game server makes.</summary>
public class ModOfferHubTests
{
    [Fact]
    public void TheHub_OffersToEachPeer_ServesThem_AndNamesTheOnesToDrop()
    {
        using ModTempDir dir = new();
        byte[] module = ModOfferFixture.Bytes(40_000, 31);
        using ModOfferHub hub = new(ModOffer.FromMemory(ModOfferFixture.Description(required: true), ("client.wasm", module)));
        using ModOfferClient willing = new(ModOfferFixture.Options(), new ModCache(dir.Sub("a")), new ModConsentStore());
        using ModOfferClient unwilling = new(ModOfferFixture.Options(allowMods: false), new ModCache(dir.Sub("b")), new ModConsentStore());
        Dictionary<int, ModOfferClient> clients = new() { [1] = willing, [2] = unwilling };
        List<(int Peer, string Reason)> dropped = new();

        hub.PeerJoined(1, 0);
        hub.PeerJoined(2, 0);
        hub.HandleFrame(99, ModWire.Ready(1), 0);   // a peer the hub was never told about
        Assert.False(hub.MayPlay(99));
        Assert.False(hub.MayPlay(1));               // required: not before the mod runs

        for (double now = 0; now < 30; now += 0.05)
        {
            hub.Pump(now, (id, frame) => clients[id].HandleFrame(frame, now), (id, reason) => dropped.Add((id, reason)));
            foreach ((int id, ModOfferClient client) in clients)
            {
                if (client.State == ModClientState.AwaitingConsent) client.ResolveConsent(ModConsentDecision.AllowOnce, now);
                if (client.State == ModClientState.ReadyToLoad) client.ReportLoaded();
                while (client.TryDequeueOutbound(out byte[] frame)) hub.HandleFrame(id, frame, now);
            }
        }

        Assert.True(hub.MayPlay(1));
        Assert.Equal(ModClientState.Active, willing.State);
        Assert.Equal(2, Assert.Single(dropped).Peer);
        Assert.Contains("requires the mod", dropped[0].Reason);
        Assert.Null(hub.Peer(2));                   // a dropped peer is forgotten
        Assert.Equal(1, hub.PeerCount);

        // A level change offers again; the client that stopped its mod gets it back without a download.
        long sent = hub.Peer(1)!.BytesSent;
        willing.LevelChanged();
        hub.LevelChanged(31);
        Assert.False(hub.MayPlay(1));
        for (double now = 31; now < 33; now += 0.05)
        {
            hub.Pump(now, (id, frame) => clients[id].HandleFrame(frame, now), (id, reason) => dropped.Add((id, reason)));
            if (willing.State == ModClientState.ReadyToLoad) willing.ReportLoaded();
            while (willing.TryDequeueOutbound(out byte[] frame)) hub.HandleFrame(1, frame, now);
        }
        Assert.True(hub.MayPlay(1));
        Assert.Equal(sent, hub.Peer(1)!.BytesSent);

        hub.PeerLeft(1);
        Assert.Equal(0, hub.PeerCount);
        Assert.Single(dropped);
    }
}

public class ModOfferBoundaryTests
{
    [Fact]
    public void TheModLibrary_HasNoWayToOpenANetworkConnection()
    {
        // A manifest may carry URLs, and the client deliberately does not fetch them: a URL chosen by a
        // stranger's server would turn the player's machine into a tool for reaching whatever that URL
        // names, the player's own network included. Files come in-band or not at all. This pins it:
        // the day someone adds an HTTP download, this fails and the decision gets made on purpose.
        string[] referenced = typeof(ModOfferClient).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestsUrls_AreNeverRequested_FilesComeInBandOnly()
    {
        using ModTempDir dir = new();
        byte[] module = ModOfferFixture.Bytes(2000, 41);
        ModManifest manifest = new()
        {
            ModId = "overkill", ModVersion = "1", BaseProtocol = ModOfferFixture.Protocol,
            ClientModule = new ModArtifact { Name = "client.wasm", SizeBytes = module.Length, Sha256 = ModOfferFixture.Sha(module), Url = "http://192.168.1.1/admin/reboot" },
            Consent = new ModConsent { Title = "t", Description = "d", Author = "a" },
        };
        using ModOfferClient client = new(ModOfferFixture.Options(), new ModCache(dir.Sub("cache")), new ModConsentStore());
        client.HandleFrame(ModWire.Offer(1, manifest.ToJson()), 0);
        client.ResolveConsent(ModConsentDecision.AllowOnce, 0);

        // The only thing the client does about a missing file is ask the game server for it by hash.
        Assert.True(client.TryDequeueOutbound(out byte[] need));
        Assert.True(ModWire.TryParse(need, out ModFrame frame));
        Assert.Equal(ModFrameKind.Need, frame.Kind);
        Assert.Equal(manifest.ClientModule.Sha256, ModWire.HashHex(frame.Hash(0)));
        Assert.False(client.TryDequeueOutbound(out _));
        Assert.Equal(ModClientState.Downloading, client.State);
    }
}
