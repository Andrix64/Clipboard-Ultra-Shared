using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cus.Core.Abstractions;
using Cus.Core.Storage;
using Cus.Core.Friends;

namespace Cus.Core.Net;

/// <summary>Messaggi del protocollo applicativo, serializzati in JSON dentro il canale cifrato.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(FriendSyncMessage), "friend-sync")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(JoinRequestMessage), "join-request")]
[JsonDerivedType(typeof(JoinReplyMessage), "join-reply")]
[JsonDerivedType(typeof(SessionStateMessage), "session-state")]
[JsonDerivedType(typeof(SetDirectionMessage), "set-direction")]
[JsonDerivedType(typeof(RenameMessage), "rename")]
[JsonDerivedType(typeof(ClipboardPushMessage), "clipboard-push")]
[JsonDerivedType(typeof(ClipboardItemMessage), "clipboard-item")]
[JsonDerivedType(typeof(ClipboardAckMessage), "clipboard-ack")]
[JsonDerivedType(typeof(LeaveSessionMessage), "leave")]
[JsonDerivedType(typeof(SessionEndedMessage), "session-ended")]
[JsonDerivedType(typeof(HistoryRequestMessage), "history-request")]
[JsonDerivedType(typeof(HistoryReplyMessage), "history-reply")]
[JsonDerivedType(typeof(HistoryItemRequestMessage), "history-item")]
[JsonDerivedType(typeof(HistoryItemReplyMessage), "history-item-reply")]
[JsonDerivedType(typeof(FileReadRequestMessage), "file-read")]
[JsonDerivedType(typeof(FileReadReplyMessage), "file-read-reply")]
[JsonDerivedType(typeof(AgreementProposalMessage), "agreement-proposal")]
[JsonDerivedType(typeof(AgreementReplyMessage), "agreement-reply")]
public abstract record CusMessage
{
    // L'encoder di default scrive ogni carattere non ASCII come \uXXXX (6 byte): un testo italiano o
    // cinese crescerebbe fino a 6 volte. Il JSON non finisce mai in una pagina HTML, quindi l'escaping
    // "rilassato" è sicuro qui.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <exception cref="ProtocolException">JSON non valido o tipo sconosciuto.</exception>
    public static CusMessage Deserialize(byte[] data)
    {
        try
        {
            return JsonSerializer.Deserialize<CusMessage>(data, Options)
                ?? throw new ProtocolException(Loc.T("net.emptyMessage"));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ProtocolException(Loc.T("net.invalidMessage", ex.Message));
        }
    }
}

/// <summary>Richiesta con risposta correlata.</summary>
public interface IRequestMessage
{
    long RequestId { get; }
}

public interface IReplyMessage
{
    long RequestId { get; }
}

/// <summary>Primo messaggio dopo l'handshake: nomi leggibili (l'identità la prova il canale).</summary>
public sealed record HelloMessage(string DisplayName, string UserName, int ListenPort) : CusMessage;

/// <summary>
/// Secondo messaggio, sempre presente, e di nuovo quando un patto si scioglie: il livello del patto
/// che questo PC ha con l'altro. Entrambi tengono il più basso dei due, così un patto sciolto da
/// una parte cade anche dall'altra. Non può mai alzarlo.
/// </summary>
public sealed record FriendSyncMessage(AgreementLevel Level) : CusMessage;

public sealed record PingMessage : CusMessage;

public sealed record ByeMessage(string Reason) : CusMessage;

/// <summary>Chi vuole entrare → host. La modalità la decide l'host (o il patto, se c'è).</summary>
public sealed record JoinRequestMessage : CusMessage;

/// <summary>Host → chi vuole entrare.</summary>
public sealed record JoinReplyMessage(bool Accepted, string? SessionId, ShareDirection Granted, string? Reason) : CusMessage;

/// <param name="Direction">Modalità attiva (la più restrittiva tra concessa e scelta dal membro).</param>
/// <param name="Granted">Modalità concessa dall'host.</param>
public sealed record ParticipantDto(string DeviceId, string DisplayName, ShareDirection Direction, bool IsHost, ShareDirection Granted = ShareDirection.Bidirectional);

public sealed record SessionStateMessage(string SessionId, IReadOnlyList<ParticipantDto> Participants, bool SensitiveForAll = false) : CusMessage;

/// <summary>Membro → host: la modalità che il membro sceglie per sé (mai oltre quella concessa).</summary>
public sealed record SetDirectionMessage(ShareDirection Direction) : CusMessage;

/// <summary>Membro → host: il membro ha cambiato il proprio nome visibile; l'host lo ripropone a tutti.</summary>
public sealed record RenameMessage(string DisplayName) : CusMessage;

/// <summary>Membro → host: copia fatta sul PC del membro.</summary>
/// <param name="Sensitive">Contenuto marcato sensibile da un password manager.</param>
/// <param name="SensitiveRecipients">Per i sensibili: i partecipanti a cui il membro li concede (l'host li inoltra solo a loro).</param>
public sealed record ClipboardPushMessage(
    string ItemId, ClipboardContent Content, bool Sensitive = false, IReadOnlyList<string>? SensitiveRecipients = null) : CusMessage;

/// <summary>Host → membri: copia distribuita, con numero di sequenza assegnato dall'host.</summary>
public sealed record ClipboardItemMessage(
    string ItemId, long Sequence, string OriginDeviceId, string OriginName, ClipboardContent Content, bool Sensitive = false) : CusMessage;

/// <summary>
/// Host → membro d'origine: la sua copia ha ricevuto questo numero di sequenza. Finché non arriva,
/// il membro non applica copie altrui: sono state ordinate prima della sua e verrebbero superate.
/// </summary>
public sealed record ClipboardAckMessage(string ItemId, long Sequence) : CusMessage;

public sealed record LeaveSessionMessage : CusMessage;

public sealed record SessionEndedMessage(string Reason) : CusMessage;

public sealed record HistoryRequestMessage(long RequestId) : CusMessage, IRequestMessage;

/// <summary>Elenco della cronologia della sessione: solo anteprime (vedi <see cref="HistoryEntry.Preview"/>).</summary>
public sealed record HistoryReplyMessage(long RequestId, IReadOnlyList<HistoryEntry> Entries) : CusMessage, IReplyMessage;

/// <summary>Membro → host: voce completa della cronologia, quando il membro la usa.</summary>
public sealed record HistoryItemRequestMessage(long RequestId, string ItemId) : CusMessage, IRequestMessage;

public sealed record HistoryItemReplyMessage(long RequestId, HistoryEntry? Entry, string? Error) : CusMessage, IReplyMessage;

/// <summary>Lettura lazy di un pezzo di file: membro → host, host → PC d'origine.</summary>
/// <param name="ForDeviceId">
/// Solo quando l'host inoltra la lettura per un altro membro: chi incolla davvero. Il PC d'origine lo
/// usa per mostrare a chi vanno i suoi file; lo accetta solo dall'host.
/// </param>
public sealed record FileReadRequestMessage(long RequestId, string ItemId, int FileIndex, long Offset, int Count, string? ForDeviceId = null, string? ForName = null)
    : CusMessage, IRequestMessage;

public sealed record FileReadReplyMessage(long RequestId, byte[]? Data, string? Error) : CusMessage, IReplyMessage;

/// <summary>Host → membro: proposta di patto reciproco. La risposta arriva dopo la scelta dell'utente.</summary>
public sealed record AgreementProposalMessage(long RequestId, AgreementLevel Level) : CusMessage, IRequestMessage;

public sealed record AgreementReplyMessage(long RequestId, bool Accepted) : CusMessage, IReplyMessage;
