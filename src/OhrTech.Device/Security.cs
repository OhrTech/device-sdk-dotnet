using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Javax.Net.Ssl;
using Bound = Net.Ohrtech.Device;
using BoundKeys = Net.Ohrtech.Device.Keys;

namespace OhrTech.Device;

/// <summary>The suite of a device attestation: <see cref="Ec"/> (P-256), or <see cref="EcMlDsa65"/>, post-quantum (both keys attested, everything signed with both).</summary>
public enum OhrAttestationSuite
{
    Ec,
    EcMlDsa65,
}

/// <summary>
/// The attested key(s): <paramref name="Ec"/> and, for <see cref="OhrAttestationSuite.EcMlDsa65"/>, <paramref name="MlDsa65"/>, each a
/// certificate chain (DER) from the key's certificate to the OhrTech root. Send <paramref name="Json"/> to your server with the challenge.
/// </summary>
public sealed record OhrAttestationResult(OhrAttestationSuite Suite, IReadOnlyList<byte[]> Ec, IReadOnlyList<byte[]>? MlDsa65, JsonObject Json);

public interface IOhrAttestation
{
    Task<OhrAttestationResult> AttestAsync(byte[] challenge, OhrAttestationSuite suite = OhrAttestationSuite.Ec);
    Task<string> GetTokenAsync(string audience, TimeSpan? ttl = null, string? nonce = null);
    OhrAttestationSuite? Suite { get; }
}

/// <summary>
/// Device attestation: proof, for the app's server, that a request comes from this app, unmodified, on a specific,
/// genuine and secure OhrTech device. Attest once (the server checks the chain and keeps the key), then a token with
/// each request.
/// </summary>
public static class OhrAttestation
{
    public static IOhrAttestation Current { get; } = new Impl();

    /// <summary>A new attested key (or keys) bound to <paramref name="challenge"/> (your server's nonce, 1 to 128 bytes).</summary>
    public static Task<OhrAttestationResult> AttestAsync(byte[] challenge, OhrAttestationSuite suite = OhrAttestationSuite.Ec) => Native.Run(() =>
    {
        var a = Bound.OhrAttestation.Attest(challenge, suite == OhrAttestationSuite.Ec ? Bound.OhrAttestation.Suite.Ec! : Bound.OhrAttestation.Suite.EcMldsa65!);
        return new OhrAttestationResult(suite, [.. a.Ec.Select(c => c.GetEncoded()!)], a.MlDsa65 is { } m ? [.. m.Select(c => c.GetEncoded()!)] : null,
            Native.Json(a.ToJson()));
    });

    /// <summary>A short-lived proof for one request, signed by the attested key(s): <c>header.payload.ecSignature[.mlDsa65Signature]</c>.</summary>
    public static Task<string> GetTokenAsync(string audience, TimeSpan? ttl = null, string? nonce = null) =>
        Native.Run(() => Bound.OhrAttestation.Token(audience, (long)(ttl ?? TimeSpan.FromMinutes(5)).TotalSeconds, nonce));

    /// <summary>The suite of the current attested key, or null before attesting.</summary>
    public static OhrAttestationSuite? Suite => Native.Call(() => Bound.OhrAttestation.GetSuite()?.Name() switch
    {
        "EC" => OhrAttestationSuite.Ec,
        "EC_MLDSA65" => OhrAttestationSuite.EcMlDsa65,
        _ => (OhrAttestationSuite?)null,
    });

    private sealed class Impl : IOhrAttestation
    {
        public Task<OhrAttestationResult> AttestAsync(byte[] challenge, OhrAttestationSuite suite = OhrAttestationSuite.Ec) => OhrAttestation.AttestAsync(challenge, suite);
        public Task<string> GetTokenAsync(string audience, TimeSpan? ttl = null, string? nonce = null) => OhrAttestation.GetTokenAsync(audience, ttl, nonce);
        public OhrAttestationSuite? Suite => OhrAttestation.Suite;
    }
}

/// <summary>
/// A client certificate: <paramref name="Alias"/> as the app names it; <paramref name="Imported"/> from the fleet's PKCS#12. A
/// device-made one has two <paramref name="Paths"/> (DER, leaf first): through the organisation's client root and through
/// the OhrTech Device Identity Root.
/// </summary>
public sealed record OhrCertificate(string Alias, IReadOnlyList<IReadOnlyList<byte[]>> Paths, bool Imported)
{
    public IReadOnlyList<byte[]> Chain => Paths[0];
    public byte[] Certificate => Paths[0][0];

    internal static OhrCertificate Of(Bound.OhrCertificates.Certificate c) =>
        new(c.Alias, [.. c.Paths.Select(p => (IReadOnlyList<byte[]>)[.. p.Select(x => x.GetEncoded()!)])], c.Imported);
}

/// <summary>
/// The app's client certificates and trusted CAs (Agent 1061+). The keys live in the device's Android Keystore, which
/// .NET's own TLS can't use: make requests with <see cref="CreateHttpHandler"/>, which goes through Android's.
/// </summary>
public static class OhrCertificates
{
    /// <summary>Whether the fleet gives this app an issuing CA, so <see cref="RequestAsync"/> can work.</summary>
    public static Task<bool> CanRequestAsync() => Native.Run(Bound.OhrCertificates.CanRequest);

    /// <summary>A new key and certificate under <paramref name="alias"/> (1 to 64 of A-Z a-z 0-9 . _ -), made in the TEE and attested; the hub certifies it.</summary>
    public static Task<OhrCertificate> RequestAsync(string alias, TimeSpan? timeout = null) => Native.Run(() =>
    {
        var c = OhrCertificate.Of(Bound.OhrCertificates.Request(alias, (long)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds));
        factories.Clear();
        return c;
    });

    /// <summary>A new key and certificate for <paramref name="alias"/> when less than a third of its lifetime is left; null when not due.</summary>
    public static Task<OhrCertificate?> RenewIfDueAsync(string alias) => Native.Run(() =>
    {
        var c = Bound.OhrCertificates.RenewIfDue(alias) is { } r ? OhrCertificate.Of(r) : null;
        if (c is not null) factories.Clear();
        return c;
    });

    /// <summary>The app's client certificates: device-made and imported.</summary>
    public static Task<IReadOnlyList<OhrCertificate>> ListAsync() => Native.Run<IReadOnlyList<OhrCertificate>>(() => [.. Bound.OhrCertificates.List().Select(OhrCertificate.Of)]);

    /// <summary>
    /// An HttpClient handler presenting <paramref name="alias"/>'s certificate (or, null, any of the app's) to servers that ask for
    /// one, and trusting the system's CAs and the private CAs the fleet gives the app: <c>new HttpClient(OhrCertificates.CreateHttpHandler("orders"))</c>.
    /// </summary>
    public static HttpMessageHandler CreateHttpHandler(string? alias = null) => new CertificateHandler(alias);

    /// <summary>The fleet changed the app's certificates (issuing CA, trusted CAs, imported bundles).</summary>
    public static event EventHandler<EventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    private static readonly WatchedEvent<EventArgs> changed = new(nameof(Changed), Bound.OhrCertificates.Listen, _ => EventArgs.Empty);

    private sealed record Factory(SSLSocketFactory SocketFactory, DateTime MadeAt);

    private static readonly ConcurrentDictionary<string, Factory> factories = new();

    // The fleet's changes too (trusted CAs, bundles) clear the cache, from its first use; with an Agent before 1061, five minutes.
    private static int watching;
    private static Java.IO.ICloseable? watch;

    private static void Watch()
    {
        if (Interlocked.Exchange(ref watching, 1) == 1) return;
        try
        {
            watch = Native.Call(() => Bound.OhrCertificates.Listen(Native.Direct, new Native.Listener(_ => factories.Clear())));
        }
        catch (OhrException)
        {
        }
    }

    internal static SSLSocketFactory SocketFactory(string? alias)
    {
        Watch();
        var key = alias ?? "";
        if (factories.TryGetValue(key, out var f) && DateTime.UtcNow - f.MadeAt < TimeSpan.FromMinutes(5)) return f.SocketFactory;
        var made = Native.Call(() => Bound.OhrCertificates.SslContext(alias).SocketFactory!);
        factories[key] = new Factory(made, DateTime.UtcNow);
        return made;
    }

    private sealed class CertificateHandler(string? alias) : Xamarin.Android.Net.AndroidMessageHandler
    {
        protected override SSLSocketFactory? ConfigureCustomSSLSocketFactory(HttpsURLConnection connection) => SocketFactory(alias);
    }
}

/// <summary>A KEM algorithm of <see cref="OhrKeys"/>.</summary>
public enum OhrKeyAlgorithm
{
    MlKem768,
    MlKem1024,
    XWing,
    /// <summary>HPKE DHKEM(X25519) only: <see cref="OhrKeys.HpkeOpenAsync"/>; there is no raw encapsulate or decapsulate.</summary>
    X25519,
}

public static class OhrKeyAlgorithmNames
{
    extension(OhrKeyAlgorithm algorithm)
    {
        /// <summary>The algorithm's standard name, as the Management API takes it: "ML-KEM-768", "ML-KEM-1024", "X-Wing" or "X25519".</summary>
        public string StandardName => algorithm switch
        {
            OhrKeyAlgorithm.MlKem768 => "ML-KEM-768",
            OhrKeyAlgorithm.MlKem1024 => "ML-KEM-1024",
            OhrKeyAlgorithm.XWing => "X-Wing",
            _ => "X25519",
        };
    }

    /// <summary>The algorithm with the standard name <paramref name="standardName"/>, or null.</summary>
    public static OhrKeyAlgorithm? FromStandardName(string standardName) => standardName switch
    {
        "ML-KEM-768" => OhrKeyAlgorithm.MlKem768,
        "ML-KEM-1024" => OhrKeyAlgorithm.MlKem1024,
        "X-Wing" => OhrKeyAlgorithm.XWing,
        "X25519" => OhrKeyAlgorithm.X25519,
        _ => null,
    };
}

public sealed record OhrKeyInfo(string Alias, OhrKeyAlgorithm Algorithm, DateTimeOffset CreatedAt);

/// <summary>An encapsulation: <paramref name="Ciphertext"/> to send, <paramref name="SharedSecret"/> (32 bytes) to keep.</summary>
public sealed record OhrEncapsulated(byte[] Ciphertext, byte[] SharedSecret);

/// <summary>
/// Proof that <paramref name="PublicKey"/> was made in this device's TEE for this app: send all of it, with the challenge, to
/// <c>POST /v1/devices/{deviceId}/key-attestations/verify</c>. <paramref name="Owner"/>: how the TEE names this app
/// ("app:&lt;the Agent's UUID for the app&gt;:&lt;package&gt;"), as signed.
/// </summary>
public sealed record OhrKeyAttestation(OhrKeyAlgorithm Algorithm, byte[] PublicKey, string Owner, byte[] CoseSign1, byte[] DiceChain);

/// <summary>HPKE (RFC 9180) identifiers for <see cref="OhrKeys.HpkeOpenAsync"/>.</summary>
public static class OhrHpke
{
    public const int KdfHkdfSha256 = 1;
    public const int AeadAes128Gcm = 1;
    public const int AeadAes256Gcm = 2;
    public const int AeadChaCha20Poly1305 = 3;
}

/// <summary>
/// Post-quantum and classical KEM keys whose private halves never leave the device's TEE: ML-KEM-768, ML-KEM-1024, X-Wing,
/// and X25519 for HPKE. Each app has its own keys, by alias.
/// </summary>
public static class OhrKeys
{
    private static BoundKeys.OhrKeys.Algorithm Bound(OhrKeyAlgorithm a) => BoundKeys.OhrKeys.Algorithm.Named(a.StandardName);

    private static OhrKeyAlgorithm Of(BoundKeys.OhrKeys.Algorithm a) => OhrKeyAlgorithmNames.FromStandardName(a.StandardName) ?? OhrKeyAlgorithm.X25519;

    /// <summary>Whether this device's OS has the OhrTech KEM app.</summary>
    public static Task<bool> IsAvailableAsync() => Native.Run(() => BoundKeys.OhrKeys.IsAvailable);

    /// <summary>A new key under <paramref name="alias"/>, replacing any key of that alias. Returns its public key. At most 32 keys per app.</summary>
    public static Task<byte[]> GenerateAsync(string alias, OhrKeyAlgorithm algorithm) => Native.Run(() => BoundKeys.OhrKeys.Generate(alias, Bound(algorithm)));

    public static Task<IReadOnlyList<OhrKeyInfo>> ListAsync() => Native.Run<IReadOnlyList<OhrKeyInfo>>(() =>
        [.. BoundKeys.OhrKeys.List().Select(k => new OhrKeyInfo(k.Alias, Of(k.Algorithm), DateTimeOffset.FromUnixTimeMilliseconds(k.CreatedAtMs)))]);

    /// <summary>The public key, or null if there's no such key.</summary>
    public static Task<byte[]?> GetPublicKeyAsync(string alias) => Native.Run(() => BoundKeys.OhrKeys.PublicKey(alias));
    public static Task DeleteAsync(string alias) => Native.Run(() => BoundKeys.OhrKeys.Delete(alias));

    /// <summary>The 32-byte shared secret of <paramref name="ciphertext"/> (ML-KEM and X-Wing keys).</summary>
    public static Task<byte[]> DecapsulateAsync(string alias, byte[] ciphertext) => Native.Run(() => BoundKeys.OhrKeys.Decapsulate(alias, ciphertext));

    /// <summary>Encapsulates to anyone's public key of <paramref name="algorithm"/> (ML-KEM, X-Wing); needs no key of yours.</summary>
    public static Task<OhrEncapsulated> EncapsulateAsync(OhrKeyAlgorithm algorithm, byte[] publicKey) => Native.Run(() =>
    {
        var e = BoundKeys.OhrKeys.Encapsulate(Bound(algorithm), publicKey);
        return new OhrEncapsulated(e.GetCiphertext(), e.GetSharedSecret());
    });

    /// <summary>HPKE base mode, one message, with the key's own KEM. <see cref="OhrFailedException"/> ("does not open") if it isn't for this key.</summary>
    public static Task<byte[]> HpkeOpenAsync(string alias, byte[] enc, byte[] ciphertext, byte[]? info = null, byte[]? aad = null,
        int kdf = OhrHpke.KdfHkdfSha256, int aead = OhrHpke.AeadAes128Gcm) =>
        Native.Run(() => BoundKeys.OhrKeys.HpkeOpen(alias, kdf, aead, enc, info ?? [], aad ?? [], ciphertext));

    /// <summary>Proof the key is this app's, in this device's TEE, bound to <paramref name="challenge"/> (at most 128 bytes).</summary>
    public static Task<OhrKeyAttestation> AttestAsync(string alias, byte[] challenge) => Native.Run(() =>
    {
        var a = BoundKeys.OhrKeys.Attest(alias, challenge);
        return new OhrKeyAttestation(Of(a.Algorithm), a.GetPublicKey(), a.Owner, a.GetCoseSign1(), a.GetDiceChain());
    });
}
