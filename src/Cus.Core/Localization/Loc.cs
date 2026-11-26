using System.Globalization;
using System.Text.Json;

namespace Cus.Core.Localization;

/// <summary>Una lingua disponibile: codice ("it", "en"...) e nome nella lingua stessa ("Italiano", "English").</summary>
public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// Testi dell'interfaccia. Ogni lingua è un file <c>Localization/Languages/&lt;codice&gt;.json</c>
/// incorporato nell'assembly: chiave → testo, più <c>"$name"</c> con il nome della lingua. Per aggiungere
/// una lingua basta un nuovo file con le stesse chiavi (e la voce nell'installer). Le parti variabili sono
/// segnaposto <c>{0}</c>, <c>{1:N0}</c>... come in <see cref="string.Format(string, object?[])"/>.
/// La lingua si cambia a runtime: i testi letti dopo il cambio escono nella nuova lingua.
/// </summary>
public static class Loc
{
    /// <summary>Chiave dell'impostazione del profilo con la lingua scelta dall'utente.</summary>
    public const string SettingKey = "ui/language";

    /// <summary>File scritto dall'installer accanto all'eseguibile, con la lingua scelta durante l'installazione.</summary>
    public const string InstallerFileName = "cus.ini";

    /// <summary>Lingua di riserva: le chiavi che mancano in un'altra lingua si prendono da qui.</summary>
    public const string FallbackCode = "en";

    /// <summary>Lingua iniziale finché il progetto eseguibile non ne imposta un'altra (i test si aspettano l'italiano).</summary>
    public const string InitialCode = "it";

    private const string NameKey = "$name";
    private const string ResourcePrefix = "Cus.Core.Localization.Languages.";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> s_tables = LoadTables();

    private static volatile string _current = InitialCode;

    /// <summary>Le lingue disponibili, in ordine di codice.</summary>
    public static IReadOnlyList<LanguageInfo> Available { get; } =
        [.. s_tables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new LanguageInfo(t.Key, t.Value.GetValueOrDefault(NameKey, t.Key)))];

    /// <summary>Codice della lingua corrente.</summary>
    public static string Current
    {
        get => _current;
        set
        {
            var code = Parse(value) ?? throw new ArgumentException($"Unknown language: '{value}'.", nameof(value));
            if (_current == code)
                return;
            _current = code;
            Changed?.Invoke();
        }
    }

    /// <summary>La lingua è cambiata: chi mostra testi li rilegge.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Il testo della chiave nella lingua corrente, con i segnaposto riempiti da <paramref name="args"/>.
    /// Se la chiave manca si usa la lingua di riserva, poi la chiave stessa (si nota subito a schermo).
    /// </summary>
    public static string T(string key, params object?[] args) => Get(_current, key, args);

    /// <summary>
    /// Come <see cref="T"/>, in una lingua precisa. Numeri e date seguono quella lingua, non le impostazioni
    /// del sistema: un testo italiano mostra "1.234", uno inglese "1,234".
    /// </summary>
    public static string Get(string code, string key, params object?[] args)
    {
        var template = Template(code, key) ?? Template(FallbackCode, key) ?? key;
        return args.Length == 0 ? template : string.Format(FormatCulture(code), template, args);
    }

    private static CultureInfo FormatCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            // Senza dati di globalizzazione (modalità invariante) resta la cultura del sistema.
            return CultureInfo.CurrentCulture;
        }
    }

    /// <summary>Il testo grezzo della chiave in una lingua (segnaposto non riempiti); null se manca.</summary>
    public static string? Template(string code, string key) =>
        s_tables.TryGetValue(code, out var table) && table.TryGetValue(key, out var text) ? text : null;

    /// <summary>Le chiavi di una lingua (senza <c>$name</c>); vuoto se la lingua non c'è.</summary>
    public static IEnumerable<string> Keys(string code) =>
        s_tables.TryGetValue(code, out var table) ? table.Keys.Where(k => k != NameKey) : [];

    /// <summary>
    /// Il codice di una lingua disponibile da "it", "en", "it-IT", "EN-us"... (maiuscole e spazi indifferenti).
    /// Null se il valore è vuoto o la lingua non c'è.
    /// </summary>
    public static string? Parse(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
            return null;
        if (s_tables.ContainsKey(text))
            return text;
        var dash = text.IndexOfAny(['-', '_']);
        return dash > 0 && s_tables.ContainsKey(text[..dash]) ? text[..dash] : null;
    }

    /// <summary>
    /// Lingua predefinita quando l'utente non ne ha scelta una: quella dell'installer (file
    /// <see cref="InstallerFileName"/> nella cartella <paramref name="installDirectory"/>), altrimenti
    /// quella dell'interfaccia di Windows se disponibile, altrimenti <see cref="FallbackCode"/>.
    /// </summary>
    public static string DefaultLanguage(string installDirectory, CultureInfo? uiCulture = null) =>
        ReadInstallerLanguage(installDirectory)
        ?? Parse((uiCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName)
        ?? FallbackCode;

    /// <summary>Legge <c>Language=...</c> dal file dell'installer; null se manca, non è leggibile o la lingua non c'è.</summary>
    public static string? ReadInstallerLanguage(string installDirectory)
    {
        try
        {
            var path = Path.Combine(installDirectory, InstallerFileName);
            if (!File.Exists(path))
                return null;
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator > 0 && line[..separator].Trim().Equals("Language", StringComparison.OrdinalIgnoreCase))
                    return Parse(line[(separator + 1)..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadTables()
    {
        var assembly = typeof(Loc).Assembly;
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
                continue;
            var code = name[ResourcePrefix.Length..^".json".Length].ToLowerInvariant();
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Language resource '{name}' not readable.");
            tables[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"Language file '{name}' is empty.");
        }

        if (!tables.ContainsKey(FallbackCode) || !tables.ContainsKey(InitialCode))
            throw new InvalidOperationException($"Language files '{FallbackCode}.json' and '{InitialCode}.json' are required.");
        return tables;
    }
}
