using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the public-key player-identity challenge-response (<see cref="PlayerIdentity"/> /
/// <see cref="ServerChallenge"/>) that replaces <c>d0_blind_id</c> (ADR-0011). The contract is
/// verification-based, not signature-equality-based: ECDSA signatures are randomized, so we assert that
/// the right key/challenge/signature combinations verify and the wrong ones don't — never that two
/// signatures are byte-equal.
/// </summary>
public class SessionAuthTests
{
    // --- Happy path: a player's own signature over the issued challenge verifies. ---

    [Fact]
    public void Verify_GenuineSignature_ReturnsTrue()
    {
        using var id = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();

        byte[] signature = id.Sign(challenge);

        Assert.True(ServerChallenge.Verify(id.PublicKey, challenge, signature));
    }

    // --- A different identity signing the same challenge must not authenticate as the first. ---

    [Fact]
    public void Verify_SignatureFromDifferentIdentity_ReturnsFalse()
    {
        using var alice = PlayerIdentity.Generate();
        using var mallory = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();

        // Mallory signs the challenge but we check it against Alice's public key.
        byte[] mallorySig = mallory.Sign(challenge);

        Assert.False(ServerChallenge.Verify(alice.PublicKey, challenge, mallorySig));
    }

    // --- Replaying a signature against a different challenge fails (the core anti-replay property). ---

    [Fact]
    public void Verify_TamperedChallenge_ReturnsFalse()
    {
        using var id = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();
        byte[] signature = id.Sign(challenge);

        byte[] otherChallenge = (byte[])challenge.Clone();
        otherChallenge[0] ^= 0xFF; // flip a bit — now it's not the signed message

        Assert.False(ServerChallenge.Verify(id.PublicKey, otherChallenge, signature));
    }

    // --- A corrupted signature is rejected and, critically, does not throw. ---

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse_AndDoesNotThrow()
    {
        using var id = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();
        byte[] signature = id.Sign(challenge);

        byte[] tampered = (byte[])signature.Clone();
        tampered[^1] ^= 0xFF; // corrupt the last byte

        bool result = false;
        var ex = Record.Exception(() => result = ServerChallenge.Verify(id.PublicKey, challenge, tampered));

        Assert.Null(ex);     // never throws on bad input
        Assert.False(result);
    }

    // --- Garbage public key / signature / empty inputs all return false instead of throwing. ---

    [Fact]
    public void Verify_MalformedInputs_ReturnFalse_AndDoNotThrow()
    {
        using var id = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();
        byte[] goodSig = id.Sign(challenge);

        var ex = Record.Exception(() =>
        {
            // Garbage SPKI bytes.
            Assert.False(ServerChallenge.Verify(new byte[] { 1, 2, 3, 4 }, challenge, goodSig));
            // Empty key.
            Assert.False(ServerChallenge.Verify(Array.Empty<byte>(), challenge, goodSig));
            // Valid key, but a wrong-length / garbage signature.
            Assert.False(ServerChallenge.Verify(id.PublicKey, challenge, new byte[] { 9, 9, 9 }));
            // Valid key, empty signature.
            Assert.False(ServerChallenge.Verify(id.PublicKey, challenge, Array.Empty<byte>()));
        });

        Assert.Null(ex);
    }

    // --- Persisted identity: export the private key, reload it, and it still authenticates. ---

    [Fact]
    public void PrivateKey_ExportImportRoundTrip_StillVerifies()
    {
        byte[] pkcs8;
        byte[] originalPublicKey;
        string originalFingerprint;
        using (var original = PlayerIdentity.Generate())
        {
            pkcs8 = original.ExportPrivateKey();
            originalPublicKey = original.PublicKey;
            originalFingerprint = original.Fingerprint;
        }

        using var reloaded = PlayerIdentity.FromPrivateKey(pkcs8);

        // Same keypair => same public identity and fingerprint.
        Assert.Equal(originalPublicKey, reloaded.PublicKey);
        Assert.Equal(originalFingerprint, reloaded.Fingerprint);

        // And the reloaded key still produces signatures that verify against the (unchanged) public key.
        byte[] challenge = ServerChallenge.NewChallenge();
        byte[] signature = reloaded.Sign(challenge);
        Assert.True(ServerChallenge.Verify(reloaded.PublicKey, challenge, signature));
        Assert.True(ServerChallenge.Verify(originalPublicKey, challenge, signature));
    }

    // --- NewChallenge: correct size and unpredictable (two draws differ). ---

    [Fact]
    public void NewChallenge_Is32Bytes_AndDistinctAcrossCalls()
    {
        byte[] a = ServerChallenge.NewChallenge();
        byte[] b = ServerChallenge.NewChallenge();

        Assert.Equal(32, a.Length);
        Assert.Equal(32, b.Length);
        Assert.Equal(ServerChallenge.ChallengeSize, a.Length);
        Assert.NotEqual(a, b); // collision probability is 2^-256; effectively never equal
    }

    // --- Fingerprint: stable for one identity, distinct across identities, non-empty lowercase hex. ---

    [Fact]
    public void Fingerprint_IsStablePerIdentity_AndDiffersAcrossIdentities()
    {
        using var a = PlayerIdentity.Generate();
        using var b = PlayerIdentity.Generate();

        // Stable: repeated reads of the same identity match.
        Assert.Equal(a.Fingerprint, a.Fingerprint);

        // Distinct identities have distinct fingerprints.
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);

        // SHA-256 hex => 64 lowercase hex chars.
        Assert.Equal(64, a.Fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", a.Fingerprint);

        // The static helper agrees with the instance property.
        Assert.Equal(a.Fingerprint, PlayerIdentity.ComputeFingerprint(a.PublicKey));
    }

    // --- Wire framing: WriteAuthResponse -> ReadAuthResponse round-trips and the result verifies. ---

    [Fact]
    public void AuthResponse_SerializationRoundTrip_Verifies()
    {
        using var id = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();

        var w = new BitWriter();
        ServerChallenge.WriteAuthResponse(w, id, challenge);

        var r = new BitReader(w.WrittenSpan);
        (byte[] publicKey, byte[] signature) = ServerChallenge.ReadAuthResponse(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(id.PublicKey, publicKey);
        // Server pairs the decoded {publicKey, signature} with the challenge it issued.
        Assert.True(ServerChallenge.Verify(publicKey, challenge, signature));
    }

    // --- Wire framing is robust to truncation: a short buffer yields BadRead + empty arrays, no throw. ---

    [Fact]
    public void AuthResponse_TruncatedBuffer_SetsBadRead_AndReturnsEmpty()
    {
        using var id = PlayerIdentity.Generate();
        byte[] challenge = ServerChallenge.NewChallenge();

        var w = new BitWriter();
        ServerChallenge.WriteAuthResponse(w, id, challenge);

        // Chop the buffer in half so the signature block underruns.
        byte[] truncated = w.WrittenSpan.Slice(0, w.Length / 2).ToArray();

        var r = new BitReader(truncated);
        (byte[] publicKey, byte[] signature) = ServerChallenge.ReadAuthResponse(ref r);

        Assert.True(r.BadRead);
        Assert.Empty(publicKey);
        Assert.Empty(signature);
        // And the empty decode is safely rejected by Verify (no throw).
        Assert.False(ServerChallenge.Verify(publicKey, challenge, signature));
    }
}
