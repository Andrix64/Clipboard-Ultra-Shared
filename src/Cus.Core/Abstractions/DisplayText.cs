using System.Globalization;
using System.Text;

namespace Cus.Core.Abstractions;

/// <summary>
/// Pulizia dei testi che arrivano dalla rete e finiscono in UI (nomi di dispositivi e utenti).
/// A capo, caratteri di controllo e di formato (per esempio gli override bidirezionali) servirebbero
/// a simulare righe in più o a capovolgere il testo in una richiesta di consenso.
/// </summary>
public static class DisplayText
{
    private const int MaxCombiningMarks = 2;

    /// <summary>
    /// Lettere che si disegnano come spazio vuoto (riempitivi Hangul, Braille vuoto): un nome fatto
    /// solo di queste sembrerebbe senza nome, o un nome noto con spazi invisibili in mezzo.
    /// </summary>
    private static readonly HashSet<char> BlankLooking = ['ᅟ', 'ᅠ', '⠀', 'ㅤ', 'ﾠ'];

    public static string Sanitize(string? value, int maxLength, string fallback = "(senza nome)")
    {
        if (string.IsNullOrEmpty(value))
            return fallback;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasSpace = true;
        var marks = 0;
        foreach (var c in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            // Gli "uso privato" sono i codici dei glifi delle icone dell'interfaccia (Font Awesome): un nome
            // di rete non deve poterli usare per far comparire un'icona in una richiesta di consenso.
            if (category is UnicodeCategory.Format or UnicodeCategory.PrivateUse)
                continue;

            // Segni combinanti impilati ("zalgo") coprirebbero le righe vicine: al massimo due per lettera.
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                if (++marks > MaxCombiningMarks)
                    continue;
            }
            else
            {
                marks = 0;
            }

            var isSpace = char.IsControl(c) || char.IsWhiteSpace(c) || BlankLooking.Contains(c)
                || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (isSpace)
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        var text = builder.ToString().Trim();
        if (text.Length > maxLength)
        {
            text = text[..maxLength];
            if (char.IsHighSurrogate(text[^1]))
                text = text[..^1];
            text = text.TrimEnd();
        }

        return text.Length == 0 ? fallback : text;
    }

    /// <summary>
    /// Lettere di altri alfabeti che si disegnano come lettere latine (e cifre come lettere): due nomi
    /// che differiscono solo per queste sembrano lo stesso nome.
    /// </summary>
    private static readonly Dictionary<char, char> Confusables = new()
    {
        // Cirillico
        ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['ё'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o', ['р'] = 'p',
        ['с'] = 'c', ['т'] = 't', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ї'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ԁ'] = 'd', ['һ'] = 'h',
        // Greco
        ['α'] = 'a', ['β'] = 'b', ['ε'] = 'e', ['ζ'] = 'z', ['η'] = 'n', ['ι'] = 'i', ['κ'] = 'k', ['μ'] = 'u', ['ν'] = 'v',
        ['ο'] = 'o', ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u', ['χ'] = 'x',
        // Cifre e lettere che si confondono tra loro
        ['0'] = 'o', ['1'] = 'l', ['i'] = 'l', ['ı'] = 'l', ['|'] = 'l', ['ɡ'] = 'g',
    };

    /// <summary>
    /// Forma "visiva" di un nome: senza differenze di maiuscole, spazi, punteggiatura, segni diacritici
    /// e lettere di altri alfabeti che sembrano latine. Due nomi con la stessa forma si confondono a occhio.
    /// </summary>
    public static string Skeleton(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var normalized = Sanitize(value, 256, "").Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark || !(char.IsLetterOrDigit(c) || c == '|'))
                continue;
            builder.Append(Confusables.TryGetValue(c, out var latin) ? latin : c);
        }

        return builder.ToString();
    }

    /// <summary>I due nomi si confondono a occhio (vedi <see cref="Skeleton"/>).</summary>
    public static bool LooksAlike(string? a, string? b)
    {
        var skeleton = Skeleton(a);
        return skeleton.Length > 0 && skeleton == Skeleton(b);
    }

    /// <summary>
    /// Accorcia un nome di file con i puntini in mezzo, tenendo sempre visibile l'estensione:
    /// "relazione_finale_molto_lunga_v3.pdf" → "relazione_fi…a_v3.pdf".
    /// </summary>
    public static string MiddleEllipsis(string name, int maxChars)
    {
        const string ellipsis = "…";
        if (maxChars < 1 || name.Length <= maxChars)
            return name;

        // Estensione: dall'ultimo punto, se non è il primo carattere (".gitignore") e se è corta.
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= Math.Max(2, maxChars / 2) ? name[dot..] : "";
        var stem = name[..(name.Length - extension.Length)];
        var room = maxChars - extension.Length - ellipsis.Length;
        if (room < 2)
            return ellipsis + name[^Math.Max(1, maxChars - 1)..];

        // Più spazio all'inizio del nome (è la parte che si riconosce), un po' alla fine.
        var tail = Math.Min(room / 3, stem.Length);
        var head = room - tail;
        return SafeSlice(stem, 0, head) + ellipsis + SafeSlice(stem, stem.Length - tail, tail) + extension;
    }

    /// <summary>Dimensione leggibile: "512 B", "12,4 KB", "120 MB" (una cifra decimale sotto 100, unità binarie, separatori della cultura corrente).</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {units[0]}"
            : $"{value.ToString(value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    /// <summary>Sottostringa che non spezza una coppia surrogata.</summary>
    private static string SafeSlice(string value, int start, int length)
    {
        if (length <= 0)
            return "";
        if (start > 0 && char.IsLowSurrogate(value[start]))
        {
            start++;
            length--;
        }

        var end = Math.Min(value.Length, start + length);
        if (end > start && end < value.Length && char.IsHighSurrogate(value[end - 1]))
            end--;
        return end > start ? value[start..end] : "";
    }
}
