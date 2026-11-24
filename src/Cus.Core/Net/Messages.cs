using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cus.Core.Abstractions;
using Cus.Core.Storage;
using Cus.Core.Trust;

namespace Cus.Core.Net;

/// <summary>Messaggi del protocollo applicativo, serializzati in JSON dentro il canale cifrato.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(TrustSyncMessage), "trust-sync")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(SessionInviteMessage), "session-invite")]
[JsonDerivedType(typeof(SessionInviteReplyMessage), "session-invite-reply")]
[JsonDerivedType(typeof(SessionStateMessage), "session-state")]
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
[JsonDerivedType(typeof(PairingStartMessage), "pairing-start")]
[JsonDerivedType(typeof(PairingReplyMessage), "pairing-reply")]
[JsonDerivedType(typeof(PairingConfirmMessage), "pairing-confirm")]
[JsonDerivedType(typeof(PairingResultMessage), "pairing-result")]
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
                ?? throw new ProtocolException("Messaggio vuoto.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ProtocolException($"Messaggio non valido: {ex.Message}");
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
/// Secondo messaggio, sempre presente: la lista di fiducia se l'altra parte è un dispositivo
/// dello stesso utente, altrimenti vuota.
/// </summary>
public sealed record TrustSyncMessage(IReadOnlyList<TrustEntry> Entries) : CusMessage;

public sealed record PingMessage : CusMessage;

public sealed record ByeMessage(string Reason) : CusMessage;

public sealed record SessionInviteMessage(string SessionId) : CusMessage;

public sealed record SessionInviteReplyMessage(ConsentDecision Decision, string? Reason) : CusMessage;

public sealed record ParticipantDto(string DeviceId, string DisplayName, ShareDirection Direction, bool IsHost);

public sealed record SessionStateMessage(string SessionId, IReadOnlyList<ParticipantDto> Participants) : CusMessage;

/// <summary>Membro → host: copia fatta sul PC del membro.</summary>
public sealed record ClipboardPushMessage(string ItemId, ClipboardContent Content) : CusMessage;

/// <summary>Host → membri: copia distribuita, con numero di sequenza assegnato dall'host.</summary>
public sealed record ClipboardItemMessage(
    string ItemId, long Sequence, string OriginDeviceId, string OriginName, ClipboardContent Content) : CusMessage;

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
public sealed record FileReadRequestMessage(long RequestId, string ItemId, int FileIndex, long Offset, int Count)
    : CusMessage, IRequestMessage;

public sealed record FileReadReplyMessage(long RequestId, byte[]? Data, string? Error) : CusMessage, IReplyMessage;

/// <summary>Chi si abbina (iniziatore CPace) → chi mostra il codice.</summary>
public sealed record PairingStartMessage(byte[] SessionId, byte[] Share) : CusMessage;

/// <summary>Risposta CPace con la conferma di chi mostra il codice, o un errore (codice scaduto, tentativi finiti).</summary>
public sealed record PairingReplyMessage(byte[]? Share, byte[]? Confirmation, string? Error) : CusMessage;

public sealed record PairingConfirmMessage(byte[] Confirmation) : CusMessage;

public sealed record PairingResultMessage(bool Success, string? Error, TrustEntry? Entry) : CusMessage;
