using System.Security.Cryptography;

namespace XonoticGodot.Net;

/// <summary>
/// Modern public-key player identity and the server-side challenge-response that authenticates it,
/// replacing Darkplaces' <c>d0_blind_id</c> blind-signature handshake (dropped in
/// <see href="ADR-0011">ADR-0011</see>; it only existed for DP interop, which XonoticGodot deliberately
/// forgoes).
///
/// <para><b>Scheme.</b> Each player owns a long-lived ECDSA keypair on the NIST P-256 curve
/// (<c>nistP256</c> / <c>secp256r1</c>). The <em>public</em> key (SubjectPublicKeyInfo / SPKI bytes,
/// see <see cref="PlayerIdentity.PublicKey"/>) IS the player's stable identity — the key for stats,
/// bans, and friend lists — and its SHA-256 <see cref="PlayerIdentity.Fingerprint"/> is the
/// human-visible id. There is no central password and no account server: the identity is
/// <b>anonymous-but-stable</b>. The server only ever learns the public key, never a real-world
/// identity; a player proves "I am the same person as last time" without revealing who that is.</para>
///
/// <para><b>Handshake flow.</b>
/// <list type="number">
///   <item>Client connects.</item>
///   <item>Server generates a fresh random nonce with <see cref="ServerChallenge.NewChallenge"/> and
///         sends it (it must be unpredictable and single-use, so a captured signature can't be
///         replayed).</item>
///   <item>Client replies with <c>{ PublicKey, Sign(challenge) }</c> — its SPKI public key plus an
///         ECDSA/SHA-256 signature over the exact challenge bytes
///         (see <see cref="PlayerIdentity.Sign"/>).</item>
///   <item>Server calls <see cref="ServerChallenge.Verify"/>; on <c>true</c> the connection is bound
///         to that public key (its <see cref="PlayerIdentity.Fingerprint"/> becomes the session's
///         player id).</item>
/// </list>
/// The wire framing for steps 3–4 is provided by <see cref="ServerChallenge.WriteAuthResponse"/> /
/// <see cref="ServerChallenge.ReadAuthResponse"/>, but the crypto above is the core — the framing is
/// just length-prefixed bytes and can be swapped for any transport.</para>
///
/// <para><b>Security notes.</b> This proves key possession, not a real identity, and does not by itself
/// establish a confidential channel — pair it with transport encryption (e.g. DTLS) if eavesdropping
/// or man-in-the-middle matters. The challenge MUST be server-chosen, random, and single-use to defeat
/// replay. Private keys are persisted client-side via <see cref="PlayerIdentity.ExportPrivateKey"/> /
/// <see cref="PlayerIdentity.FromPrivateKey"/> and never leave the client.</para>
/// </summary>
public sealed class PlayerIdentity : IDisposable
{
    /// <summary>The signing curve: NIST P-256 (a.k.a. secp256r1 / prime256v1).</summary>
    private static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;

    /// <summary>The signing/verification hash paired with the curve (ECDSA-with-SHA-256).</summary>
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    private readonly ECDsa _key;
    private byte[]? _publicKey;
    private string? _fingerprint;

    private PlayerIdentity(ECDsa key) => _key = key;

    /// <summary>Generate a brand-new random P-256 identity (a fresh keypair). The caller typically
    /// persists it once via <see cref="ExportPrivateKey"/> and reloads it on subsequent launches so the
    /// player keeps one stable identity.</summary>
    public static PlayerIdentity Generate() => new PlayerIdentity(ECDsa.Create(Curve));

    /// <summary>Reload an identity from a previously stored PKCS#8 private key (the inverse of
    /// <see cref="ExportPrivateKey"/>). Throws <see cref="CryptographicException"/> if the bytes are not
    /// a valid PKCS#8 P-256 key — loading your own persisted identity is not an untrusted-input path, so
    /// (unlike <see cref="ServerChallenge.Verify"/>) failure surfaces rather than being swallowed.</summary>
    /// <param name="pkcs8">The DER-encoded PKCS#8 PrivateKeyInfo bytes from <see cref="ExportPrivateKey"/>.</param>
    public static PlayerIdentity FromPrivateKey(byte[] pkcs8)
    {
        ArgumentNullException.ThrowIfNull(pkcs8);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        return new PlayerIdentity(key);
    }

    /// <summary>Export this identity's private key as DER-encoded PKCS#8 so the client can persist it to
    /// disk and reload it later with <see cref="FromPrivateKey"/>. Treat the result as a secret — anyone
    /// holding it can impersonate this identity.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    /// <summary>The player's stable public identity: the DER-encoded SubjectPublicKeyInfo (SPKI) bytes of
    /// the public key. This is what the client sends in the handshake and what the server stores/keys on.
    /// The same array instance is returned on repeated reads (cached); do not mutate it.</summary>
    public byte[] PublicKey => _publicKey ??= _key.ExportSubjectPublicKeyInfo();

    /// <summary>A short, stable, human-visible id for this identity: the lowercase hex of
    /// <c>SHA-256(<see cref="PublicKey"/>)</c>. Equal public keys yield equal fingerprints; distinct
    /// keys yield (cryptographically) distinct fingerprints, so this is safe as a display/lookup id.</summary>
    public string Fingerprint => _fingerprint ??= ComputeFingerprint(PublicKey);

    /// <summary>Sign a server challenge with the private key (ECDSA over SHA-256 of the challenge),
    /// producing the proof returned in the handshake. ECDSA is randomized, so two calls over the same
    /// challenge yield different signatures — both verify. Signatures are in the fixed-length IEEE P1363
    /// (r‖s) format, which is what <see cref="ServerChallenge.Verify"/> expects.</summary>
    /// <param name="challenge">The exact challenge bytes received from the server.</param>
    public byte[] Sign(ReadOnlySpan<byte> challenge) =>
        _key.SignData(challenge, Hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>Compute the fingerprint (lowercase hex SHA-256) for an arbitrary SPKI public-key blob —
    /// shared by <see cref="Fingerprint"/> and useful server-side to derive the id of a verified key
    /// without constructing a full <see cref="PlayerIdentity"/>.</summary>
    /// <param name="publicKeySpki">The SubjectPublicKeyInfo bytes to hash.</param>
    public static string ComputeFingerprint(byte[] publicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(publicKeySpki, digest);
        // Convert.ToHexStringLower is .NET 9+; we target net8.0, so lowercase the net5+ uppercase form.
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Release the underlying key material.</summary>
    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Server side of the <see cref="PlayerIdentity"/> handshake: minting the random challenge and verifying
/// the client's signature, plus the (optional) wire framing for the auth response. See
/// <see cref="PlayerIdentity"/> for the full flow and threat model. Stateless and allocation-light;
/// the only state a real server keeps is the per-connection outstanding challenge (so it can confirm the
/// signature is over the nonce it just issued).
/// </summary>
public static class ServerChallenge
{
    /// <summary>Length in bytes of a challenge nonce (256 bits — generous against birthday/replay
    /// concerns and cheap to send once per connect).</summary>
    public const int ChallengeSize = 32;

    /// <summary>Mint a fresh, cryptographically-random 32-byte challenge nonce for a connecting client.
    /// Must be called per connection attempt and the value must be remembered until the client answers,
    /// so a signature captured on one connection cannot be replayed on another.</summary>
    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeSize);

    /// <summary>
    /// Verify that <paramref name="signature"/> is a valid ECDSA/SHA-256 signature over
    /// <paramref name="challenge"/> for the public key in <paramref name="publicKeySpki"/>. Returns
    /// <c>true</c> only on a cryptographically valid match.
    ///
    /// <para>This is an untrusted-input boundary (the bytes come straight off the wire from an unknown
    /// peer), so it <b>never throws</b>: a malformed/garbage SPKI key, a wrong-length or corrupt
    /// signature, an empty input, or a key on the wrong curve all return <c>false</c>. The signature is
    /// expected in IEEE P1363 (r‖s) form, as produced by <see cref="PlayerIdentity.Sign"/>.</para>
    /// </summary>
    /// <param name="publicKeySpki">The client's SubjectPublicKeyInfo public-key bytes.</param>
    /// <param name="challenge">The exact challenge the server issued via <see cref="NewChallenge"/>.</param>
    /// <param name="signature">The client's signature over the challenge.</param>
    public static bool Verify(byte[] publicKeySpki, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
    {
        if (publicKeySpki is null || publicKeySpki.Length == 0)
            return false;

        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return key.VerifyData(
                challenge,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // Malformed SPKI key, wrong curve, or otherwise unimportable input — treat as a failed auth.
            return false;
        }
        catch (ArgumentException)
        {
            // Defensive: bad-length signature buffers can surface as ArgumentException on some runtimes.
            return false;
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>
    /// Serialize the client's auth response — <c>{ publicKey, sign(challenge) }</c> — into a
    /// <see cref="BitWriter"/> as two length-prefixed (ushort) byte blocks. The challenge itself is not
    /// written (the server already holds the nonce it issued); only the public key and the signature over
    /// it travel. Inverse of <see cref="ReadAuthResponse"/>.
    /// </summary>
    /// <param name="w">Destination writer.</param>
    /// <param name="id">The client's identity (its public key is sent and the challenge is signed).</param>
    /// <param name="challenge">The challenge bytes received from the server, to be signed.</param>
    public static void WriteAuthResponse(BitWriter w, PlayerIdentity id, ReadOnlySpan<byte> challenge)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(id);

        byte[] publicKey = id.PublicKey;
        byte[] signature = id.Sign(challenge);

        w.WriteUShort(publicKey.Length);
        w.WriteBytes(publicKey);
        w.WriteUShort(signature.Length);
        w.WriteBytes(signature);
    }

    /// <summary>
    /// Read an auth response written by <see cref="WriteAuthResponse"/>, returning the client's public key
    /// and signature. The caller pairs these with the challenge it issued and passes all three to
    /// <see cref="Verify"/>. On a truncated/corrupt buffer the reader's <see cref="BitReader.BadRead"/>
    /// flag is set and the returned arrays are empty (which <see cref="Verify"/> rejects), so this never
    /// throws on malformed wire data.
    /// </summary>
    /// <param name="r">Source reader positioned at the start of the auth response.</param>
    /// <returns>The decoded <c>(publicKey, signature)</c>; empty arrays if the buffer underran.</returns>
    public static (byte[] publicKey, byte[] signature) ReadAuthResponse(ref BitReader r)
    {
        int pkLen = r.ReadUShort();
        byte[] publicKey = r.ReadBytes(pkLen).ToArray();
        int sigLen = r.ReadUShort();
        byte[] signature = r.ReadBytes(sigLen).ToArray();

        if (r.BadRead)
            return (Array.Empty<byte>(), Array.Empty<byte>());

        return (publicKey, signature);
    }
}
