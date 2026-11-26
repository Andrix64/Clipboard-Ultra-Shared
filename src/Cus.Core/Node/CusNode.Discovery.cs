using System.Net;
using Cus.Core.Friends;
using Cus.Core.Net;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    // ================================================================ scoperta

    /// <summary>
    /// Annuncio di questo PC: solo mentre si offre come host. Gli annunci non sono cifrati e li riceve
    /// chiunque sia in rete, quindi il nome utente del sistema non c'è (resta vuoto: per le versioni che lo
    /// mostrano, vuoto vuol dire "non mostrarlo"). Lo vede solo chi si collega, nel saluto cifrato.
    /// </summary>
    private AnnouncementPayload? AnnouncementOrNull() =>
        _visible ? new AnnouncementPayload(Identity.DeviceId, Identity.DisplayName, UserName: "", Port, Math.Max(1, Session?.Participants.Count ?? 1)) : null;

    private void OnAnnounced(DiscoveryAnnouncement announcement)
    {
        // Chiave = ID + indirizzo: gli annunci sono firmati, ma uno vero rispedito da un altro indirizzo
        // non sostituisce l'originale, compare accanto (e la richiesta lì fallisce la verifica d'identità).
        var key = $"{announcement.DeviceId}|{announcement.Host}|{announcement.Port}";
        if (announcement.Hidden)
        {
            if (_discovered.TryRemove(key, out _))
                PeersChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isNew = !_discovered.TryGetValue(key, out var previous);
        if (isNew && !MakeRoomForDiscovered(announcement, now))
            return;

        _discovered[key] = (announcement, now);
        if (isNew || previous.Announcement != announcement)
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fa posto a un nuovo peer scoperto. Generare identità è gratis: chiunque in rete può annunciarne
    /// a centinaia. Per questo ogni indirizzo ha un tetto, e a elenco pieno un amico prende il posto
    /// dello sconosciuto visto meno di recente invece di restare fuori.
    /// </summary>
    private bool MakeRoomForDiscovered(DiscoveryAnnouncement announcement, DateTimeOffset now)
    {
        foreach (var (oldKey, old) in _discovered)
        {
            if (now - old.Seen >= PeerExpiry)
                _discovered.TryRemove(oldKey, out _);
        }

        var isFriend = Friends.AgreementWith(announcement.DeviceId) != AgreementLevel.None;
        if (!isFriend && _discovered.Values.Count(d => d.Announcement.Host == announcement.Host) >= MaxDiscoveredPerAddress)
            return false;
        if (_discovered.Count < MaxDiscovered)
            return true;
        if (!isFriend)
            return false;

        var stranger = _discovered
            .Where(d => Friends.AgreementWith(d.Value.Announcement.DeviceId) == AgreementLevel.None)
            .OrderBy(d => d.Value.Seen)
            .Select(d => d.Key)
            .FirstOrDefault();
        return stranger is not null && _discovered.TryRemove(stranger, out _);
    }

    /// <summary>Annuncia subito lo stato (per esempio quanti sono nella sessione).</summary>
    private void AnnounceSoon()
    {
        if (_visible && _discovery is { } discovery)
            RunInBackground(discovery.AnnounceNowAsync);
    }

    /// <summary>Memorizza dove raggiungere un amico, anche quando non si annuncia.</summary>
    private void RememberEndpoint(PeerConnection connection)
    {
        if (connection.RemoteEndPoint is not IPEndPoint ip || connection.RemoteListenPort is <= 0 or > 65535)
            return;
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        Friends.RememberEndpoint(connection.Remote.DeviceId, connection.Remote.DisplayName, address.ToString(), connection.RemoteListenPort);
    }
}
