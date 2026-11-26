using System.Text;
using Cus.Core.Abstractions;

namespace Cus.Platform.Linux;

/// <summary>
/// Elenchi di file nella clipboard di Linux: <c>text/uri-list</c> (RFC 2483, righe separate da CRLF,
/// commenti con '#') e <c>x-special/gnome-copied-files</c> ("copy" o "cut", poi un URI per riga), il
/// formato di Nautilus, Nemo, Caja, Thunar e PCManFM. Dolphin usa text/uri-list.
/// </summary>
internal static class FileUris
{
    public const string UriListTarget = "text/uri-list";
    public const string GnomeCopiedFilesTarget = "x-special/gnome-copied-files";

    /// <summary>Percorsi locali di un text/uri-list. Gli URI non <c>file:</c> o di altri host si scartano.</summary>
    public static List<string> ParseUriList(string text)
    {
        var paths = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (TryGetLocalPath(line, out var path))
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>Percorsi locali di x-special/gnome-copied-files: la prima riga è l'operazione.</summary>
    public static List<string> ParseGnomeCopiedFiles(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() is not ("copy" or "cut"))
            return ParseUriList(text);
        return ParseUriList(string.Join('\n', lines.Skip(1)));
    }

    public static string FormatUriList(IEnumerable<string> paths) =>
        string.Concat(paths.Select(p => ToUri(p) + "\r\n"));

    /// <summary>Sempre "copy": chi incolla copia i file, non li sposta dalla cache di CUS.</summary>
    public static string FormatGnomeCopiedFiles(IEnumerable<string> paths) =>
        "copy" + string.Concat(paths.Select(p => "\n" + ToUri(p)));

    /// <summary>
    /// <c>file:///percorso</c> con percent-encoding UTF-8 di tutto tranne i caratteri non riservati e '/'.
    /// </summary>
    public static string ToUri(string absolutePath)
    {
        if (!absolutePath.StartsWith('/'))
            throw new ArgumentException($"Not an absolute path: '{absolutePath}'.", nameof(absolutePath));

        var builder = new StringBuilder("file://", absolutePath.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(absolutePath))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Percorso di un URI <c>file:</c> locale (host vuoto o "localhost"). Decodifica a mano, senza
    /// <see cref="Uri"/>: il risultato non deve dipendere dal sistema su cui girano i test.
    /// </summary>
    public static bool TryGetLocalPath(string uri, out string path)
    {
        path = "";
        const string scheme = "file:";
        if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = uri[scheme.Length..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var slash = rest.IndexOf('/', 2);
            if (slash < 0)
                return false;
            var host = rest[2..slash];
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return false;
            rest = rest[slash..];
        }

        if (!rest.StartsWith('/'))
            return false;

        // Query e frammento non fanno parte del percorso.
        var end = rest.IndexOfAny(['?', '#']);
        if (end >= 0)
            rest = rest[..end];

        var bytes = new List<byte>(rest.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < rest.Length;)
        {
            if (rest[i] == '%' && i + 2 < rest.Length && Uri.IsHexDigit(rest[i + 1]) && Uri.IsHexDigit(rest[i + 2]))
            {
                bytes.Add((byte)Convert.ToInt32(rest.Substring(i + 1, 2), 16));
                i += 3;
                continue;
            }

            // Per code point, non per char: un carattere fuori dal piano base (es. un'emoji) è una
            // coppia surrogata e codificato a metà diventerebbe U+FFFD.
            if (Rune.DecodeFromUtf16(rest.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                return false;
            var written = rune.EncodeToUtf8(utf8);
            for (var k = 0; k < written; k++)
                bytes.Add(utf8[k]);
            i += consumed;
        }

        var decoded = Encoding.UTF8.GetString([.. bytes]);
        if (decoded.Contains('\0'))
            return false;
        path = decoded;
        return true;
    }

    /// <summary>
    /// Le voci di primo livello di un elenco ricevuto ("progetto/src/a.cs" → "progetto"), nell'ordine
    /// in cui compaiono: sono quelle da mettere nell'elenco degli URI, come le aveva copiate l'utente.
    /// </summary>
    public static List<string> TopLevelNames(IEnumerable<ClipboardFileInfo> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var file in files)
        {
            var slash = file.Name.IndexOf('/');
            var top = slash < 0 ? file.Name : file.Name[..slash];
            if (top.Length > 0 && seen.Add(top))
                names.Add(top);
        }

        return names;
    }
}
