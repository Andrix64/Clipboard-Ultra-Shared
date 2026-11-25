using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Storage;

namespace Cus.Core.Friends;

/// <summary>
/// Patto tra due PC, proposto dall'host a chi è entrato nella sua sessione e accettato da lui. Vale
/// nei due sensi: ognuno dei due lo concede all'altro. Se uno dei due lo scioglie cade per entrambi
/// (a ogni connessione i due PC si scambiano il proprio livello e tengono il più basso).
/// </summary>
public enum AgreementLevel
{
    None = 0,

    /// <summary>Entra senza conferma quando l'altro si offre come host.</summary>
    AutoJoin = 1,

    /// <summary>Si collega in qualsiasi momento, anche se l'altro non si offre come host (salvo che sia già in una condivisione).</summary>
    Anytime = 2,
}

/// <summary>
/// Dispositivo con cui c'è un patto o a cui si mostrano i contenuti sensibili. L'identità è il
/// <see cref="DeviceId"/> (hash della chiave Ed25519), verificato dal canale cifrato a ogni connessione.
/// </summary>
/// <param name="TrustedWithSensitive">
/// Decisione solo di questo PC: le sue copie marcate sensibili (password) vanno anche a questo dispositivo.
/// </param>
/// <param name="LastHost">Ultimo indirizzo da cui si è collegato, per raggiungerlo anche quando non si annuncia.</param>
public sealed record Friend(
    string DeviceId,
    string DisplayName,
    AgreementLevel Agreement,
    bool TrustedWithSensitive,
    string? LastHost,
    int LastPort,
    DateTimeOffset UpdatedUtc)
{
    public string Fingerprint => DeviceIdentity.FingerprintOf(DeviceId);

    public PeerEndpoint? LastEndpoint => LastHost is { Length: > 0 } && LastPort is > 0 and <= 65535 ? new PeerEndpoint(LastHost, LastPort) : null;

    public bool IsEmpty => Agreement == AgreementLevel.None && !TrustedWithSensitive;
}

/// <summary>Amici salvati nel DB cifrato. Thread-safe (il DB lo è).</summary>
public sealed class FriendStore(CusDatabase database)
{
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public IReadOnlyList<Friend> All => database.GetFriends();

    public Friend? Get(string deviceId) => database.GetFriend(deviceId);

    public AgreementLevel AgreementWith(string deviceId) => Get(deviceId)?.Agreement ?? AgreementLevel.None;

    public bool TrustsWithSensitive(string deviceId) => Get(deviceId)?.TrustedWithSensitive == true;

    public void SetAgreement(string deviceId, string displayName, AgreementLevel level) =>
        Update(deviceId, displayName, f => f with { Agreement = level });

    /// <summary>Abbassa il patto al livello dell'altro PC, mai lo alza.</summary>
    /// <returns>True se il livello è cambiato.</returns>
    public bool LowerAgreement(string deviceId, AgreementLevel remote)
    {
        if (!Enum.IsDefined(remote))
            remote = AgreementLevel.None;
        var changed = false;
        Update(deviceId, null, f =>
        {
            if (remote >= f.Agreement)
                return f;
            changed = true;
            return f with { Agreement = remote };
        });
        return changed;
    }

    public void SetSensitiveTrust(string deviceId, string displayName, bool trusted) =>
        Update(deviceId, displayName, f => f with { TrustedWithSensitive = trusted });

    /// <summary>Solo per dispositivi già amici: dove raggiungerli e il nome aggiornato.</summary>
    public void RememberEndpoint(string deviceId, string displayName, string host, int port)
    {
        lock (_gate)
        {
            if (Get(deviceId) is not { } friend || (friend.LastHost == host && friend.LastPort == port && friend.DisplayName == displayName))
                return;
            database.UpsertFriend(friend with { DisplayName = displayName, LastHost = host, LastPort = port, UpdatedUtc = DateTimeOffset.UtcNow });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string deviceId)
    {
        // Sotto lo stesso lock di RememberEndpoint: un salvataggio dell'indirizzo letto prima della
        // rimozione non deve riscrivere l'amico appena tolto.
        bool removed;
        lock (_gate)
            removed = database.DeleteFriend(deviceId);
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="displayName">Null: si tiene il nome salvato (una voce nuova senza nome non si crea).</param>
    private void Update(string deviceId, string? displayName, Func<Friend, Friend> change)
    {
        bool changed;
        lock (_gate)
        {
            var current = Get(deviceId);
            if (current is null && displayName is null)
                return;
            current ??= new Friend(deviceId, displayName!, AgreementLevel.None, false, null, 0, DateTimeOffset.UtcNow);
            var updated = change(current) with { DisplayName = displayName ?? current.DisplayName, UpdatedUtc = DateTimeOffset.UtcNow };
            changed = updated with { UpdatedUtc = current.UpdatedUtc } != current;
            if (updated.IsEmpty)
                database.DeleteFriend(deviceId);
            else if (changed)
                database.UpsertFriend(updated);
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
