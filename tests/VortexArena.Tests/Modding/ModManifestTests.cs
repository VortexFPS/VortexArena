using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VortexArena.Modding;
using Xunit;

namespace VortexArena.Tests.Modding;

/// <summary>
/// The manifest is read from a stranger's server before the player has agreed to anything, and the
/// cache is where downloaded bytes land on disk. Both are tested from the hostile side first.
/// </summary>
public class ModManifestTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static readonly string AnySha = new('a', 64);

    private static ModManifest Valid() => new()
    {
        ModId = "overkill",
        ModVersion = "1.4.0",
        BaseProtocol = 0xDEADBEEF,
        ClientModule = new ModArtifact { Name = "client.wasm", SizeBytes = 2_000_000, Sha256 = AnySha, Url = "https://mods.example.org/overkill/client.wasm" },
        AssetPacks = new[] { new ModArtifact { Name = "overkill-assets.pk3", SizeBytes = 50_000_000, Sha256 = new string('b', 64) } },
        Limits = new ModLimitRequest { MaxMemoryBytes = 32L << 20, FrameBudgetMs = 4 },
        Consent = new ModConsent { Title = "Overkill", Description = "Instagib with a twist.", Author = "someone" },
    };

    [Fact]
    public void RoundTripsThroughJson()
    {
        ModManifest parsed = ModManifest.Parse(Valid().ToJson());
        Assert.Equal("overkill", parsed.ModId);
        Assert.Equal(0xDEADBEEF, parsed.BaseProtocol);
        Assert.Equal("client.wasm", parsed.ClientModule!.Name);
        Assert.Equal(new[] { "client.wasm", "overkill-assets.pk3" }, parsed.Artifacts.Select(a => a.Name));
        Assert.Equal(52_000_000, parsed.TotalBytes);
    }

    [Fact]
    public void ServerLimitRequests_CanLowerButNeverRaiseTheClientsCeiling()
    {
        ModLimits ceiling = ModLimits.Default;
        ModLimits lowered = Valid().EffectiveLimits(ceiling);
        Assert.Equal(32L << 20, lowered.MaxMemoryBytes);
        Assert.Equal(4, lowered.FrameBudgetMs);

        ModManifest greedy = Valid() with { Limits = new ModLimitRequest { MaxMemoryBytes = 64L << 30, FrameBudgetMs = 5000 } };
        ModLimits clamped = greedy.EffectiveLimits(ceiling);
        Assert.Equal(ceiling.MaxMemoryBytes, clamped.MaxMemoryBytes);
        Assert.Equal(ceiling.FrameBudgetMs, clamped.FrameBudgetMs);
    }

    public static TheoryData<string, Func<ModManifest, ModManifest>, string> Hostile() => new()
    {
        { "path traversal in a pack name", m => m with { AssetPacks = new[] { m.AssetPacks[0] with { Name = "maps/../../evil.pk3" } } }, "character other than" },
        { "leading parent directory", m => m with { AssetPacks = new[] { m.AssetPacks[0] with { Name = "../evil.pk3" } } }, "starts with a dot" },
        { "backslash path", m => m with { AssetPacks = new[] { m.AssetPacks[0] with { Name = "a\\b.pk3" } } }, "character other than" },
        { "hidden file", m => m with { AssetPacks = new[] { m.AssetPacks[0] with { Name = ".bashrc.pk3" } } }, "starts with a dot" },
        { "module that is not a .wasm", m => m with { ClientModule = m.ClientModule! with { Name = "client.exe" } }, "must be a .wasm" },
        { "pack that is not a .pk3", m => m with { AssetPacks = new[] { m.AssetPacks[0] with { Name = "run.bat" } } }, "must be a .pk3" },
        { "oversized module", m => m with { ClientModule = m.ClientModule! with { SizeBytes = 1L << 40 } }, "the limit is" },
        { "zero-size artifact", m => m with { ClientModule = m.ClientModule! with { SizeBytes = 0 } }, "the limit is" },
        { "upper-case hash", m => m with { ClientModule = m.ClientModule! with { Sha256 = new string('A', 64) } }, "no valid SHA-256" },
        { "short hash", m => m with { ClientModule = m.ClientModule! with { Sha256 = "abc" } }, "no valid SHA-256" },
        { "file URL", m => m with { ClientModule = m.ClientModule! with { Url = "file:///etc/passwd" } }, "not an http(s) URL" },
        { "URL with credentials", m => m with { ClientModule = m.ClientModule! with { Url = "https://user:pw@example.org/x.wasm" } }, "must not carry credentials" },
        { "another interface version", m => m with { Abi = "vortex_9" }, "this client provides 'vortex_1'" },
        { "control characters in the consent text", m => m with { Consent = m.Consent with { Title = "ok\u001b[2J" } }, "control character" },
        { "enormous description", m => m with { Consent = m.Consent with { Description = new string('x', 5000) } }, "longer than" },
        { "duplicate pack", m => m with { AssetPacks = new[] { m.AssetPacks[0], m.AssetPacks[0] with { Name = "OVERKILL-assets.pk3" } } }, "listed twice" },
        { "empty mod id", m => m with { ModId = "" }, "modId" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Parse_RejectsHostileManifests(string what, Func<ModManifest, ModManifest> corrupt, string expected)
    {
        ModManifestException e = Assert.Throws<ModManifestException>(() => ModManifest.Parse(corrupt(Valid()).ToJson()));
        Assert.True(e.Message.Contains(expected, StringComparison.Ordinal), $"{what}: message was '{e.Message}'");
    }

    [Fact]
    public void Parse_RejectsMalformedOversizedDeepAndUnknownJson()
    {
        Assert.Contains("not valid", Assert.Throws<ModManifestException>(() => ModManifest.Parse("{not json"u8)).Message);
        Assert.Contains("the limit is", Assert.Throws<ModManifestException>(() => ModManifest.Parse(new byte[ModManifest.MaxManifestBytes + 1])).Message);
        Assert.Contains("is null", Assert.Throws<ModManifestException>(() => ModManifest.Parse("null"u8)).Message);
        // An unknown property is refused rather than ignored: a field this client does not understand
        // might be one that changes what the others mean.
        Assert.Throws<ModManifestException>(() => ModManifest.Parse("{\"modId\":\"x\",\"runOnHost\":true}"u8));
        string deep = string.Concat(Enumerable.Repeat("{\"consent\":", 40)) + "1" + new string('}', 40);
        Assert.Throws<ModManifestException>(() => ModManifest.Parse(Encoding.UTF8.GetBytes(deep)));
        Assert.Throws<ModManifestException>(() => ModManifest.Parse("{\"assetPacks\":[null]}"u8));
    }

    [Fact]
    public void Parse_NeverThrowsAnythingElse_OnCorruptedInput()
    {
        byte[] valid = Valid().ToJson();
        Random random = new(77001);
        for (int i = 0; i < 3000; i++)
        {
            byte[] corrupt = (byte[])valid.Clone();
            for (int f = 0; f < 1 + random.Next(3); f++) corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
            try { ModManifest.Parse(corrupt); }
            catch (ModManifestException) { }
        }
    }

    // ---- the cache ---------------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "va-modcache-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private static ModArtifact ArtifactFor(byte[] content, string name = "pack.pk3") =>
        new() { Name = name, SizeBytes = content.Length, Sha256 = Sha(content) };

    [Fact]
    public void Cache_StoresVerifiedContent_AndFindsItByHash()
    {
        using TempDir dir = new();
        ModCache cache = new(dir.Path);
        byte[] content = Encoding.UTF8.GetBytes("pretend this is a pk3");
        ModArtifact artifact = ArtifactFor(content);

        Assert.False(cache.Contains(artifact));
        string stored = cache.Store(artifact, new MemoryStream(content));
        Assert.Equal(content, File.ReadAllBytes(stored));
        Assert.True(cache.TryGetPath(artifact, out string found));
        Assert.Equal(stored, found);

        // The same bytes under another name are the same cache entry.
        Assert.True(cache.Contains(artifact with { Name = "renamed.pk3" }));
        // The same hash with the wrong declared size is not a hit.
        Assert.False(cache.Contains(artifact with { SizeBytes = content.Length + 1 }));
    }

    [Fact]
    public void Cache_RefusesContentThatDoesNotMatch_AndLeavesNothingBehind()
    {
        using TempDir dir = new();
        ModCache cache = new(dir.Path);
        byte[] real = Encoding.UTF8.GetBytes("the real file");
        ModArtifact artifact = ArtifactFor(real);

        byte[] substituted = Encoding.UTF8.GetBytes("the evil file");
        Assert.Contains("does not match its SHA-256", Assert.Throws<ModManifestException>(() => cache.Store(artifact, new MemoryStream(substituted))).Message);
        Assert.Contains("larger than", Assert.Throws<ModManifestException>(() => cache.Store(artifact, new MemoryStream(new byte[100_000]))).Message);
        Assert.Contains("its manifest declares", Assert.Throws<ModManifestException>(() => cache.Store(artifact, new MemoryStream(real, 0, 5))).Message);

        Assert.False(cache.Contains(artifact));
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Cache_StopsReadingAnEndlessStream()
    {
        using TempDir dir = new();
        ModCache cache = new(dir.Path);
        ModArtifact artifact = new() { Name = "pack.pk3", SizeBytes = 1000, Sha256 = AnySha };
        EndlessStream endless = new();
        Assert.Throws<ModManifestException>(() => cache.Store(artifact, endless));
        Assert.True(endless.BytesServed < 1_000_000, $"read {endless.BytesServed} bytes of an endless stream");
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesServed;
        public override int Read(byte[] buffer, int offset, int count) { BytesServed += count; return count; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Cache_ReportsWhatAManifestStillNeeds_AndEvictsOldestFirst()
    {
        using TempDir dir = new();
        ModCache cache = new(dir.Path) { BudgetBytes = 2500 };
        byte[] a = new byte[1000], b = new byte[1000], c = new byte[1000];
        a[0] = 1; b[0] = 2; c[0] = 3;
        ModArtifact packA = ArtifactFor(a, "a.pk3"), packB = ArtifactFor(b, "b.pk3"), packC = ArtifactFor(c, "c.pk3");
        ModManifest manifest = Valid() with { ClientModule = null, AssetPacks = new[] { packA, packB, packC } };

        cache.Store(packA, new MemoryStream(a));
        Assert.Equal(new[] { "b.pk3", "c.pk3" }, cache.Missing(manifest).Select(m => m.Name));
        cache.Store(packB, new MemoryStream(b));
        cache.Store(packC, new MemoryStream(c));
        Assert.Empty(cache.Missing(manifest));

        // 3000 bytes against a 2500 budget: one file goes, and it is the least recently used one that
        // is not pinned. Access times are set explicitly; relying on the filesystem's own would be flaky.
        cache.TryGetPath(packA, out string pathA); cache.TryGetPath(packB, out string pathB); cache.TryGetPath(packC, out string pathC);
        File.SetLastAccessTimeUtc(pathA, DateTime.UtcNow.AddDays(-3));
        File.SetLastAccessTimeUtc(pathB, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(pathC, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(1000, cache.Evict(keep: new[] { packA.Sha256 }));
        Assert.True(File.Exists(pathA));   // oldest, but in use
        Assert.False(File.Exists(pathB));  // the oldest that could go
        Assert.True(File.Exists(pathC));
    }
}
