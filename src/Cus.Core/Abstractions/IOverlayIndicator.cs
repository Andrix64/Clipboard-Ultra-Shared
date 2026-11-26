namespace Cus.Core.Abstractions;

public enum ShareDirection
{
    Bidirectional,

    /// <summary>Il membro riceve le copie della sessione ma non invia le proprie: la sua clipboard resta sul suo PC.</summary>
    ReadOnly,
}

/// <param name="Direction">Modalità attiva: la più restrittiva tra quella concessa dall'host e quella scelta dal membro.</param>
/// <param name="Granted">Modalità concessa dall'host.</param>
/// <param name="IsSelf">È questo PC.</param>
public sealed record SessionParticipant(
    string DeviceId,
    string DisplayName,
    ShareDirection Direction,
    ShareDirection Granted,
    bool IsHost,
    bool IsSelf);

/// <summary>
/// Ciò che c'è ora nella clipboard condivisa, come lo mostrano indicatore e finestra principale.
/// Mai il contenuto vero: solo tipo, nome e dimensione.
/// </summary>
/// <param name="Title">"testo di N caratteri", nome del file o della cartella, "N elementi", "contenuto sensibile".</param>
/// <param name="SizeBytes">Null per i contenuti sensibili: anche la lunghezza di una password è un'informazione.</param>
/// <param name="IsShared">False se la copia è rimasta su questo PC (sola lettura, contenuto sensibile non concesso).</param>
public sealed record ClipboardSummary(
    string OriginDeviceId,
    string OriginName,
    ClipboardContentKind Kind,
    string Title,
    long? SizeBytes,
    bool IsSensitive,
    bool IsShared)
{
    public static ClipboardSummary From(ClipboardContent content, string originDeviceId, string originName, bool isSensitive, bool isShared)
    {
        if (isSensitive)
            return new(originDeviceId, originName, content.Kind, Loc.T("content.sensitiveContent"), null, true, isShared);

        var (title, size) = content.Kind switch
        {
            ClipboardContentKind.Text => (Loc.T("content.textOfCharacters", (content.Text ?? "").Length), (long)System.Text.Encoding.UTF8.GetByteCount(content.Text ?? "")),
            ClipboardContentKind.Image => (Loc.T("content.image"), (long)(content.ImagePng?.Length ?? 0)),
            ClipboardContentKind.Files => (DescribeFiles(content.Files ?? []), content.Files?.Sum(f => f.SizeBytes) ?? 0),
            _ => (Loc.T("content.unknownContentTitle"), 0L),
        };
        return new(originDeviceId, originName, content.Kind, title, size, false, isShared);
    }

    /// <summary>
    /// Nome dell'elemento copiato: il file o la cartella se è uno solo (i nomi in elenco sono percorsi
    /// relativi, "cartella/a.txt"), altrimenti quanti elementi.
    /// </summary>
    private static string DescribeFiles(IReadOnlyList<ClipboardFileInfo> files)
    {
        var roots = files.Select(f => f.Name.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return roots.Count switch
        {
            0 => Loc.T("common.noFiles"),
            1 => roots[0],
            _ => Loc.T("content.items", roots.Count),
        };
    }
}

/// <param name="Current">Ultimo contenuto della clipboard condivisa, null se ancora nessuno.</param>
/// <param name="Transfers">Trasferimenti di file di questo PC (in corso o appena finiti).</param>
/// <param name="ActiveTransfers">Quanti di questi sono ancora in corso.</param>
/// <param name="CanToggleDirection">Membro con accesso bidirezionale: può passare a sola lettura e tornare indietro.</param>
public sealed record SessionIndicatorState(
    IReadOnlyList<SessionParticipant> Participants,
    ClipboardSummary? Current = null,
    int Transfers = 0,
    int ActiveTransfers = 0,
    bool CanToggleDirection = false,
    ShareDirection MyDirection = ShareDirection.Bidirectional);

/// <summary>
/// Indicatore sempre in primo piano su tutti i monitor che segnala la condivisione attiva e mostra
/// cosa c'è nella clipboard condivisa. Un doppio clic chiede di uscire dalla sessione.
/// </summary>
public interface IOverlayIndicator : IDisposable
{
    event EventHandler? LeaveRequested;

    /// <summary>Il proprietario dei file chiede di vedere i trasferimenti in corso.</summary>
    event EventHandler? TransfersRequested;

    /// <summary>Il membro chiede di passare da bidirezionale a sola lettura o viceversa.</summary>
    event EventHandler? DirectionToggleRequested;

    void Show(SessionIndicatorState state);

    void Hide();
}
