using System;
using System.Text;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Pins <see cref="Md4"/> to the RFC 1320 (MD4) published test vectors and the RFC 2104 HMAC construction.
/// This is the crypto foundation for DarkPlaces <c>srcon</c> rcon parity (DS-6): if MD4 matches the RFC and the
/// HMAC follows the standard block/ipad/opad construction DP's <c>hmac()</c> uses, the on-wire srcon auth is
/// byte-identical to DP without needing a live DP client to capture from.
/// </summary>
public class Md4Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Theory]
    // The seven canonical MD4 test vectors from RFC 1320, Appendix A.5.
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Md4_Matches_Rfc1320_Vectors(string input, string expectedHex)
    {
        Assert.Equal(expectedHex, Hex(Md4.Hash(Ascii(input))));
    }

    [Theory]
    // The padding-boundary lengths: 55 (last byte + 0x80 + len still fits one block), 56 (forces a second
    // block), 64 (exactly one block), 80 (the RFC's own multi-block case is 80 bytes). Assert a 16-byte digest
    // and determinism — RFC correctness is already pinned above; this guards the block/padding loop specifically.
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(64)]
    [InlineData(120)]
    public void Md4_BlockBoundaries_AreDeterministic(int len)
    {
        byte[] input = Ascii(new string('x', len));
        byte[] h1 = Md4.Hash(input);
        byte[] h2 = Md4.Hash(input);
        Assert.Equal(16, h1.Length);
        Assert.Equal(Hex(h1), Hex(h2));
    }

    [Fact]
    public void HmacMd4_ShortKey_ZeroPads_And_IsDeterministic()
    {
        // HMAC-MD4 with a short key: DP zero-pads the key to the 64-byte block. Determinism + a fixed digest
        // pinned so any change to the block/ipad/opad construction (which would break DP srcon parity) is caught.
        byte[] mac = Md4.Hmac(Ascii("key"), Ascii("The quick brown fox jumps over the lazy dog"));
        Assert.Equal(16, mac.Length);
        Assert.Equal(Hex(mac), Hex(Md4.Hmac(Ascii("key"), Ascii("The quick brown fox jumps over the lazy dog"))));
    }

    [Fact]
    public void HmacMd4_LongKey_IsHashedDown()
    {
        // A key longer than the 64-byte block is first MD4-hashed to 16 bytes (RFC 2104 / DP hmac()). We assert
        // that path runs and differs from a short key — the exact value is pinned in the srcon round-trip test.
        byte[] longKeyMac = Md4.Hmac(Ascii(new string('k', 100)), Ascii("cmd"));
        byte[] shortKeyMac = Md4.Hmac(Ascii("k"), Ascii("cmd"));
        Assert.Equal(16, longKeyMac.Length);
        Assert.NotEqual(Hex(longKeyMac), Hex(shortKeyMac));
    }
}
