using System.Globalization;

namespace Cus.Core.Localization;

/// <summary>Lingue dell'interfaccia.</summary>
public enum AppLanguage
{
    Italian,
    English,
}

/// <summary>
/// Testi dell'interfaccia in italiano e inglese. Ogni testo si scrive nel punto in cui serve con
/// <see cref="T"/>, le due lingue affiancate: niente chiavi da tenere allineate tra file diversi.
/// La lingua si sceglie a runtime: i testi letti dopo il cambio escono nella nuova lingua.
/// </summary>
public static class Loc
{
    /// <summary>Chiave dell'impostazione del profilo con la lingua scelta dall'utente.</summary>
    public const string SettingKey = "ui/language";

    /// <summary>File scritto dall'installer accanto all'eseguibile, con la lingua scelta durante l'installazione.</summary>
    public const string InstallerFileName = "cus.ini";

    private static volatile AppLanguage _current = AppLanguage.Italian;

    /// <summary>Lingua corrente. Italiano finché il progetto eseguibile non ne imposta un'altra.</summary>
    public static AppLanguage Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            Changed?.Invoke();
        }
    }

    /// <summary>La lingua è cambiata: chi mostra testi li rilegge.</summary>
    public static event Action? Changed;

    /// <summary>Il testo nella lingua corrente.</summary>
    public static string T(string italian, string english) => _current == AppLanguage.English ? english : italian;

    /// <summary>Codice breve per le impostazioni: "it" o "en".</summary>
    public static string ToCode(AppLanguage language) => language == AppLanguage.English ? "en" : "it";

    /// <summary>
    /// Riconosce "it", "en", "italian", "english", "it-IT", "en-US"... (maiuscole indifferenti).
    /// Null se il valore è vuoto o sconosciuto.
    /// </summary>
    public static AppLanguage? Parse(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text is "italian" or "italiano" || text == "it" || text.StartsWith("it-", StringComparison.Ordinal))
            return AppLanguage.Italian;
        if (text is "english" or "inglese" || text == "en" || text.StartsWith("en-", StringComparison.Ordinal))
            return AppLanguage.English;
        return null;
    }

    /// <summary>
    /// Lingua predefinita quando l'utente non ne ha scelta una: quella dell'installer (file
    /// <see cref="InstallerFileName"/> nella cartella <paramref name="installDirectory"/>), altrimenti
    /// quella di Windows, altrimenti l'inglese.
    /// </summary>
    public static AppLanguage DefaultLanguage(string installDirectory, CultureInfo? uiCulture = null) =>
        ReadInstallerLanguage(installDirectory)
        ?? ((uiCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "it" ? AppLanguage.Italian : AppLanguage.English);

    /// <summary>Legge <c>Language=...</c> dal file dell'installer; null se manca o non è leggibile.</summary>
    public static AppLanguage? ReadInstallerLanguage(string installDirectory)
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
}
