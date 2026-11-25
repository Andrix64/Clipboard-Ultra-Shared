using System.Globalization;

namespace Cus.Core.Abstractions;

/// <summary>
/// Validazione dell'elenco di file che arriva da un altro PC. I nomi finiscono nei descrittori
/// dei file virtuali (su Windows FILEGROUPDESCRIPTORW) e il programma in cui si incolla li usa per
/// creare file: un nome come "..\..\Startup\x.bat" scriverebbe fuori dalla cartella di destinazione.
/// Un elenco con anche un solo nome non valido si rifiuta per intero.
/// </summary>
public static class ClipboardFileNames
{
    public const int MaxFiles = 10_000;

    public const int MaxDepth = 32;

    /// <summary>File più cartelle: ogni voce diventa un descrittore da 592 byte nella memoria globale.</summary>
    public const int MaxEntries = 20_000;

    public const int MaxSegmentLength = 255;

    /// <summary>
    /// Dimensione totale dichiarata, massimo 1 TiB. La dichiara chi copia e Esplora risorse la usa per
    /// preparare la copia: un valore assurdo non deve arrivarci.
    /// </summary>
    public const long MaxTotalBytes = 1L << 40;

    /// <summary>
    /// Estensioni che avviano codice all'apertura (programmi, script, collegamenti, installer, macro,
    /// immagini disco). Si avvisa chi le riceve: il nome lo sceglie chi le manda.
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appref-ms",
        ".application", ".lnk", ".url", ".scf", ".cpl", ".dll", ".sys", ".inf", ".reg", ".jar", ".iso", ".img",
        ".vhd", ".vhdx", ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".chm", ".settingcontent-ms",
    };

    /// <summary>Lunghezza massima del percorso relativo: cFileName di FILEDESCRIPTORW è MAX_PATH (260) con il terminatore.</summary>
    public const int MaxPathLength = 259;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Restituisce false, con il motivo, se l'elenco non è accettabile.</summary>
    public static bool TryValidate(IReadOnlyList<ClipboardFileInfo>? files, out string reason)
    {
        reason = "";
        if (files is not { Count: > 0 })
        {
            reason = "nessun file";
            return false;
        }

        if (files.Count > MaxFiles)
        {
            reason = $"troppi file (oltre {MaxFiles:N0})";
            return false;
        }

        var totalBytes = 0L;
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || !TryValidateName(file.Name, out reason))
            {
                reason = file is null ? "voce di file vuota" : reason;
                return false;
            }

            if (file.SizeBytes < 0 || file.SizeBytes > MaxTotalBytes - totalBytes)
            {
                reason = file.SizeBytes < 0
                    ? $"dimensione non valida per '{DisplayText.Sanitize(file.Name, 80, "?")}'"
                    : $"dimensione totale dichiarata troppo grande (oltre {MaxTotalBytes / (1L << 30):N0} GB)";
                return false;
            }

            totalBytes += file.SizeBytes;

            if (directories.Contains(file.Name) || !filePaths.Add(file.Name))
            {
                reason = $"nome ripetuto: '{file.Name}'";
                return false;
            }

            for (var slash = file.Name.IndexOf('/'); slash >= 0; slash = file.Name.IndexOf('/', slash + 1))
            {
                var directory = file.Name[..slash];
                if (filePaths.Contains(directory))
                {
                    reason = $"'{directory}' è sia un file sia una cartella";
                    return false;
                }

                directories.Add(directory);
            }

            if (filePaths.Count + directories.Count > MaxEntries)
            {
                reason = $"troppe voci tra file e cartelle (oltre {MaxEntries:N0})";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// I file dell'elenco che avviano codice all'apertura (vedi <see cref="ExecutableExtensions"/>),
    /// solo il nome finale. Vuoto se non ce ne sono.
    /// </summary>
    public static IReadOnlyList<string> ExecutableNames(IReadOnlyList<ClipboardFileInfo>? files) =>
        files is null
            ? []
            : files.Select(f => f.Name[(f.Name.LastIndexOf('/') + 1)..])
                .Where(n => ExecutableExtensions.Contains(Path.GetExtension(n)))
                .ToList();

    /// <summary>Un nome relativo con '/' come separatore, senza componenti pericolose.</summary>
    public static bool TryValidateName(string? name, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(name))
        {
            reason = "nome di file vuoto";
            return false;
        }

        if (name.Length > MaxPathLength)
        {
            reason = $"percorso troppo lungo (oltre {MaxPathLength} caratteri)";
            return false;
        }

        var segments = name.Split('/');
        if (segments.Length > MaxDepth)
        {
            reason = $"troppi livelli di cartelle (oltre {MaxDepth})";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                reason = $"nome di file non valido: '{DisplayText.Sanitize(name, 80, "?")}'";
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length is 0 or > MaxSegmentLength || segment is "." or "..")
            return false;
        if (segment[^1] is '.' or ' ')
            return false;

        foreach (var c in segment)
        {
            if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*' or '/')
                return false;

            // Caratteri invisibili o di direzione (U+202E e simili): "fattura<RLO>txt.exe" in Esplora
            // risorse sembrerebbe un .txt.
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
        }

        // CON, CON.txt, "CON .txt": Windows li tratta come dispositivi.
        var dot = segment.IndexOf('.');
        var baseName = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return !ReservedNames.Contains(baseName);
    }
}
