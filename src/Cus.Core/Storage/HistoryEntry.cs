using System.Text.Json.Serialization;
using Cus.Core.Abstractions;

namespace Cus.Core.Storage;

/// <summary>Voce della cronologia della sessione, salvata nel DB dell'host.</summary>
/// <param name="Preview">
/// Non null nelle voci che l'host manda ai membri come elenco: il contenuto è vuoto (solo il tipo)
/// e la voce completa si chiede all'host quando la si usa. Così l'elenco resta piccolo anche con
/// immagini e testi lunghi.
/// </param>
public sealed record HistoryEntry(
    string ItemId,
    string SessionId,
    long Sequence,
    string OriginDeviceId,
    string OriginName,
    ClipboardContent Content,
    DateTimeOffset CreatedUtc,
    string? Preview = null)
{
    /// <summary>Lunghezza massima del testo di anteprima.</summary>
    public const int PreviewChars = 200;

    [JsonIgnore]
    public bool IsPreview => Preview is not null;

    /// <summary>Descrizione per la UI: l'anteprima se c'è, altrimenti quella del contenuto.</summary>
    public string Describe() => Preview ?? Content.Describe();

    /// <summary>Versione leggera da mandare in elenco: solo tipo e anteprima.</summary>
    public HistoryEntry ToPreview()
    {
        var description = Content.Describe();
        return this with
        {
            Content = new ClipboardContent { Kind = Content.Kind },
            Preview = description.Length <= PreviewChars ? description : description[..PreviewChars] + "...",
        };
    }
}
