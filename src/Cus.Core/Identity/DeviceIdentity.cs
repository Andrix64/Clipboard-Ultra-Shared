using System.Security.Cryptography;
using Cus.Core.Crypto;

namespace Cus.Core.Identity;

/// <summary>Informazioni pubbliche di un dispositivo: viaggiano in rete.</summary>
public sealed record DeviceInfo(string DeviceId, string DisplayName, string UserName, byte[] SignPublicKey)
{
    public string Fingerprint => DeviceIdentity.FormatFingerprint(SignPublicKey);
}

/// <summary>
/// Identità del dispositivo: coppia di chiavi Ed25519 generata al primo avvio.
/// La chiave privata resta nel DB cifrato e non lascia mai il PC.
/// </summary>
public sealed class DeviceIdentity
{
    public DeviceIdentity(string displayName, string userName, byte[] signPublicKey, byte[] signSecretKey)
    {
        if (signPublicKey.Length != Sodium.SignPublicKeyBytes || signSecretKey.Length != Sodium.SignSecretKeyBytes)
            throw new ArgumentException(Loc.T("identity.invalidEd25519Keys"));

        DisplayName = displayName;
        UserName = userName;
        SignPublicKey = signPublicKey;
        SignSecretKey = signSecretKey;
        DeviceId = ComputeDeviceId(signPublicKey);
    }

    public string DeviceId { get; }

    public string DisplayName { get; }

    public string UserName { get; }

    public byte[] SignPublicKey { get; }

    internal byte[] SignSecretKey { get; }

    public DeviceInfo Info => new(DeviceId, DisplayName, UserName, SignPublicKey);

    public static DeviceIdentity Generate(string displayName, string userName)
    {
        var (pk, sk) = Sodium.SignKeyPair();
        return new DeviceIdentity(displayName, userName, pk, sk);
    }

    /// <summary>Stessa identità (stesse chiavi) con un altro nome visibile.</summary>
    public DeviceIdentity WithDisplayName(string displayName) => new(displayName, UserName, SignPublicKey, SignSecretKey);

    public byte[] Sign(ReadOnlySpan<byte> message) => Sodium.SignDetached(message, SignSecretKey);

    public static bool Verify(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey) =>
        Sodium.VerifyDetached(signature, message, publicKey);

    /// <summary>ID stabile: primi 128 bit dello SHA-256 della chiave pubblica, in esadecimale.</summary>
    public static string ComputeDeviceId(ReadOnlySpan<byte> signPublicKey) =>
        Convert.ToHexString(SHA256.HashData(signPublicKey))[..32];

    /// <summary>Impronta leggibile a voce: 5 gruppi da 4 caratteri dello SHA-256 della chiave pubblica.</summary>
    public static string FormatFingerprint(ReadOnlySpan<byte> signPublicKey) =>
        FingerprintOf(Convert.ToHexString(SHA256.HashData(signPublicKey)));

    /// <summary>
    /// Impronta a partire dall'ID (stesso hash, quindi stessa impronta di <see cref="FormatFingerprint"/>):
    /// serve per gli amici, di cui si conserva solo l'ID.
    /// </summary>
    public static string FingerprintOf(string deviceIdOrHash) =>
        deviceIdOrHash.Length < 20
            ? deviceIdOrHash
            : string.Join('-', Enumerable.Range(0, 5).Select(i => deviceIdOrHash.Substring(i * 4, 4)));
}
