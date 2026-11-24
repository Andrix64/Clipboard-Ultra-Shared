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
    public static string Sanitize(string? value, int maxLength, string fallback = "(senza nome)")
    {
        if (string.IsNullOrEmpty(value))
            return fallback;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasSpace = true;
        foreach (var c in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format)
                continue;

            var isSpace = char.IsControl(c) || char.IsWhiteSpace(c)
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
}
