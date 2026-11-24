using Cus.Core.Abstractions;

namespace Cus.Core.Storage;

/// <summary>Voce della cronologia della sessione, salvata nel DB dell'host.</summary>
public sealed record HistoryEntry(
    string ItemId,
    string SessionId,
    long Sequence,
    string OriginDeviceId,
    string OriginName,
    ClipboardContent Content,
    DateTimeOffset CreatedUtc);
