using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cus.Core.Identity;

namespace Cus.Core.Trust;

public enum TrustEntryKind
{
    /// <summary>L'attore ha abbinato il soggetto (stesso utente).</summary>
    Pair,

    /// <summary>L'attore ha revocato il soggetto. Definitivo.</summary>
    Revoke,
}

/// <summary>
/// Voce firmata della lista di abbinamenti e revoche. Si aggiunge e non si toglie mai.
/// La firma Ed25519 è dell'attore e copre <see cref="SignedPayload"/>.
/// </summary>
public sealed record TrustEntry(
    TrustEntryKind Kind,
    byte[] ActorPublicKey,
    byte[] SubjectPublicKey,
    string SubjectName,
    DateTimeOffset CreatedUtc,
    byte[] Signature)
{
    public string ActorId => DeviceIdentity.ComputeDeviceId(ActorPublicKey);

    public string SubjectId => DeviceIdentity.ComputeDeviceId(SubjectPublicKey);

    /// <summary>Identificatore stabile: hash di contenuto e firma.</summary>
    public string EntryId => Convert.ToHexString(SHA256.HashData([.. SignedPayload(Kind, ActorPublicKey, SubjectPublicKey, SubjectName, CreatedUtc), .. Signature]));

    public bool HasValidSignature() =>
        DeviceIdentity.Verify(Signature, SignedPayload(Kind, ActorPublicKey, SubjectPublicKey, SubjectName, CreatedUtc), ActorPublicKey);

    public static TrustEntry Create(DeviceIdentity actor, TrustEntryKind kind, DeviceInfo subject, DateTimeOffset now)
    {
        var created = new DateTimeOffset(now.UtcDateTime.Ticks - now.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var payload = SignedPayload(kind, actor.SignPublicKey, subject.SignPublicKey, subject.DisplayName, created);
        return new TrustEntry(kind, actor.SignPublicKey, subject.SignPublicKey, subject.DisplayName, created, actor.Sign(payload));
    }

    /// <summary>Serializzazione canonica di ciò che viene firmato.</summary>
    public static byte[] SignedPayload(
        TrustEntryKind kind, byte[] actor, byte[] subject, string subjectName, DateTimeOffset createdUtc) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "CUS-trust-v1",
            kind == TrustEntryKind.Pair ? "pair" : "revoke",
            Convert.ToBase64String(actor),
            Convert.ToBase64String(subject),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(subjectName)),
            createdUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
}
