using System.Text.RegularExpressions;

namespace Cus.Core.Net;

/// <summary>
/// Testo che viaggia in rete (motivi di rifiuto, di chiusura, errori nelle risposte) e che l'altro PC
/// mostra. Si invia nella lingua di chi lo manda; chi lo riceve lo riconosce in una qualunque delle lingue
/// disponibili e lo mostra nella propria (<see cref="WireTexts.Localize"/>). Il protocollo non cambia: resta una stringa.
/// </summary>
/// <param name="key">Chiave nei file di lingua; il testo usa i segnaposto <c>{0}</c>, <c>{1}</c>... per le parti variabili.</param>
public sealed class WireText(string key)
{
    /// <summary>Un modello per lingua, in cui i segnaposto diventano gruppi.</summary>
    private readonly Regex[] _patterns =
    [
        .. Loc.Available
            .Select(l => Loc.Template(l.Code, key))
            .OfType<string>()
            .Distinct()
            .Select(Pattern),
    ];

    public string Key { get; } = key;

    /// <summary>Il testo nella lingua corrente (senza parti variabili).</summary>
    public string Text => Loc.T(Key);

    /// <summary>Il testo nella lingua corrente con le parti variabili.</summary>
    public string Format(params object?[] args) => Loc.T(Key, args);

    /// <summary>Vero se il testo è questo, in una qualunque lingua.</summary>
    public bool Matches(string? text) => text is not null && _patterns.Any(p => p.IsMatch(text));

    internal bool TryLocalize(string text, out string localized)
    {
        foreach (var pattern in _patterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                // Per nome, non per posizione: in un'altra lingua i segnaposto possono essere in ordine diverso.
                var count = match.Groups.Keys.Count(k => k.StartsWith('p'));
                localized = Format([.. Enumerable.Range(0, count).Select(i => (object?)match.Groups[$"p{i}"].Value)]);
                return true;
            }
        }

        localized = text;
        return false;
    }

    private static Regex Pattern(string template)
    {
        var pattern = Regex.Replace(Regex.Escape(template), @"\\\{(\d+)}", m => $"(?<p{m.Groups[1].Value}>.*)");
        return new Regex($"^{pattern}$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}

/// <summary>I testi che un PC manda all'altro perché li mostri.</summary>
public static class WireTexts
{
    // Risposte alla richiesta di ingresso.
    public static readonly WireText AlreadyInSession = new("wire.alreadyInSession");
    public static readonly WireText YouAreAlreadyIn = new("wire.youAreAlreadyIn");
    public static readonly WireText NotOfferingHost = new("wire.notOfferingHost");
    public static readonly WireText AskedTooRecently = new("wire.askedTooRecently");
    public static readonly WireText HostClosedSession = new("wire.hostClosedSession");
    public static readonly WireText HostStoppedOffering = new("wire.hostStoppedOffering");
    public static readonly WireText RequestCancelled = new("wire.requestCancelled");
    public static readonly WireText RequestDeclined = new("wire.requestDeclined");
    public static readonly WireText NoAnswer = new("wire.noAnswer");
    public static readonly WireText TooManyRequests = new("wire.tooManyRequests");

    // Saluti di chiusura.
    public static readonly WireText PactDissolved = new("wire.pactDissolved");
    public static readonly WireText InAnotherSession = new("wire.inAnotherSession");

    // Errori nelle risposte a letture di file e voci della cronologia.
    public static readonly WireText NotInSession = new("wire.notInSession");
    public static readonly WireText NotShared = new("wire.notShared");
    public static readonly WireText TransferCancelledBy = new("wire.transferCancelledBy");
    public static readonly WireText InvalidFileRequested = new("wire.invalidFileRequested");
    public static readonly WireText InvalidRange = new("wire.invalidRange");
    public static readonly WireText FileNoLongerReadable = new("wire.fileNoLongerReadable");
    public static readonly WireText FileReadFailed = new("wire.fileReadFailed");
    public static readonly WireText OriginBusy = new("wire.originBusy");
    public static readonly WireText InvalidRequest = new("wire.invalidRequest");
    public static readonly WireText OriginLeft = new("wire.originLeft");
    public static readonly WireText OriginCopiedElse = new("wire.originCopiedElse");
    public static readonly WireText OriginUnreachable = new("wire.originUnreachable");
    public static readonly WireText OriginNotResponding = new("wire.originNotResponding");
    public static readonly WireText OriginError = new("wire.originError");
    public static readonly WireText HistoryItemGone = new("wire.historyItemGone");
    public static readonly WireText HistoryItemTooLarge = new("wire.historyItemTooLarge");

    // Dichiarato dopo i campi che elenca: gli inizializzatori statici girano in ordine.
    private static readonly WireText[] All =
    [
        AlreadyInSession, YouAreAlreadyIn, NotOfferingHost, AskedTooRecently, HostClosedSession, HostStoppedOffering,
        RequestCancelled, RequestDeclined, NoAnswer, TooManyRequests, PactDissolved, InAnotherSession,
        NotInSession, NotShared, TransferCancelledBy, InvalidFileRequested, InvalidRange, FileNoLongerReadable,
        FileReadFailed, OriginBusy, InvalidRequest, OriginLeft, OriginCopiedElse, OriginUnreachable,
        OriginNotResponding, OriginError, HistoryItemGone, HistoryItemTooLarge,
    ];

    /// <summary>
    /// Un testo ricevuto dall'altro PC, nella lingua di questo se è uno di quelli noti (in italiano o in
    /// inglese); altrimenti invariato (per esempio da una versione diversa di CUS).
    /// </summary>
    public static string Localize(string text)
    {
        foreach (var known in All)
        {
            if (known.TryLocalize(text, out var localized))
                return localized;
        }

        return text;
    }
}
