using System.Net;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Hosting;
using Cus.Core.Identity;

namespace Cus.Core.Node;

public sealed record CusNodeOptions
{
    /// <summary>Porta TCP fissa. Null: la prima libera tra 23700 e 23709.</summary>
    public int? Port { get; init; }

    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    public bool EnableDiscovery { get; init; } = true;

    public int DiscoveryPort { get; init; } = Net.Discovery.DefaultPort;

    public IReadOnlyList<PeerEndpoint> ManualPeers { get; init; } = [];

    /// <summary>Quanto attendere la risposta dell'utente a una richiesta di ingresso o a un patto.</summary>
    public TimeSpan ConsentTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Un trasferimento di file senza letture per questo tempo si considera interrotto.</summary>
    public TimeSpan TransferIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Dopo una richiesta di ingresso rifiutata, scaduta o annullata, lo stesso dispositivo (e lo
    /// stesso indirizzo, salvo il loopback) non può chiedere di nuovo per questo tempo.
    /// </summary>
    public TimeSpan JoinRetryCooldown { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Peer raggiungibile: un host visibile trovato in rete, un amico con patto "in qualsiasi momento"
/// (all'ultimo indirizzo noto) o un indirizzo inserito a mano.
/// </summary>
/// <param name="Participants">Persone nella sessione dell'host annunciato (lui compreso); 0 se non annunciato.</param>
/// <param name="IsAnnounced">Si sta offrendo come host adesso (annuncio in rete).</param>
/// <param name="LooksLikeFriend">
/// Non è un amico ma si chiama come uno (a occhio): il nome dell'amico imitato. Gli annunci non sono
/// autenticati, quindi il nome può essere scelto apposta per farsi scambiare per lui.
/// </param>
public sealed record PeerView(
    string? DeviceId,
    string DisplayName,
    string UserName,
    string Host,
    int Port,
    int Participants,
    bool IsAnnounced,
    AgreementLevel Agreement,
    bool IsManual,
    string? LooksLikeFriend = null)
{
    public string Address => new PeerEndpoint(Host, Port).ToString();

    /// <summary>Impronta dell'ID annunciato: alla connessione si verifica che il PC sia davvero quello.</summary>
    public string? Fingerprint => DeviceId is { Length: > 0 } id ? DeviceIdentity.FingerprintOf(id) : null;

    /// <summary>Si può chiedere di entrare: host visibile, amico con patto "in qualsiasi momento" o indirizzo a mano.</summary>
    public bool CanJoin => IsAnnounced || IsManual || Agreement == AgreementLevel.Anytime;
}

public enum SessionRole
{
    Host,
    Member,
}

/// <param name="IsVisible">Solo host: si sta offrendo in rete (accetta richieste di ingresso).</param>
/// <param name="SensitiveForAll">Permesso temporaneo dell'host: i suoi contenuti sensibili vanno a tutti.</param>
/// <param name="MyGranted">Membro: modalità concessa dall'host. Host: sempre bidirezionale.</param>
/// <param name="MyDirection">Modalità attiva di questo PC (il membro può ridurla a sola lettura).</param>
public sealed record SessionView(
    string SessionId,
    SessionRole Role,
    IReadOnlyList<SessionParticipant> Participants,
    bool IsVisible,
    bool SensitiveForAll,
    ShareDirection MyGranted,
    ShareDirection MyDirection)
{
    /// <summary>Qualcun altro oltre a questo PC: la clipboard è davvero esposta.</summary>
    public bool IsSharing => Participants.Count > 1;
}

/// <summary>Richiesta di ingresso in attesa della decisione dell'host.</summary>
/// <param name="LooksLikeFriend">Chi chiede non è un amico ma si chiama come uno: il nome dell'amico imitato.</param>
public sealed record JoinRequestView(
    string RequestId, string DeviceId, string DisplayName, string UserName, string Fingerprint, DateTimeOffset ReceivedUtc, string? LooksLikeFriend = null);

public enum JoinOutcome
{
    Accepted,
    AcceptedReadOnly,
    Rejected,
}

public sealed record JoinResult(JoinOutcome Outcome, string PeerName, string? Reason);

public enum TransferState
{
    Active,
    Completed,
    Cancelled,

    /// <summary>Nessuna lettura per <see cref="CusNodeOptions.TransferIdleTimeout"/>: chi incollava ha smesso.</summary>
    Stalled,
}

/// <summary>Trasferimento di file copiati su questo PC verso un altro partecipante.</summary>
public sealed record TransferView(
    string ItemId,
    string RequesterId,
    string RequesterName,
    string ItemName,
    long TotalBytes,
    long BytesDone,
    TransferState State,
    DateTimeOffset LastActivityUtc)
{
    public double Progress => TotalBytes <= 0 ? 1 : Math.Clamp((double)BytesDone / TotalBytes, 0, 1);
}

/// <summary>File di un altro PC che questo PC sta incollando (scaricando).</summary>
public sealed record IncomingTransferView(
    string ItemId,
    string OriginName,
    string ItemName,
    long TotalBytes,
    long BytesDone,
    TransferState State,
    DateTimeOffset LastActivityUtc)
{
    public double Progress => TotalBytes <= 0 ? 1 : Math.Clamp((double)BytesDone / TotalBytes, 0, 1);
}

/// <summary>Errore mostrabile all'utente così com'è.</summary>
public sealed class CusOperationException(string message, Exception? inner = null) : Exception(message, inner);
