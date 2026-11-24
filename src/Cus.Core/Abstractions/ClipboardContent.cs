namespace Cus.Core.Abstractions;

/// <summary>Tipo di contenuto della clipboard gestito da CUS.</summary>
public enum ClipboardContentKind
{
    Text,
    Image,
    Files,
}

/// <summary>Metadati di un file copiato. Il contenuto si trasferisce in modo lazy, solo all'incolla.</summary>
public sealed record ClipboardFileInfo(string Name, long SizeBytes);

/// <summary>
/// Istantanea indipendente dalla piattaforma di ciò che è nella clipboard.
/// </summary>
public sealed record ClipboardContent
{
    public required ClipboardContentKind Kind { get; init; }

    public string? Text { get; init; }

    /// <summary>Immagine codificata PNG.</summary>
    public byte[]? ImagePng { get; init; }

    public IReadOnlyList<ClipboardFileInfo>? Files { get; init; }

    /// <summary>
    /// True se un password manager ha marcato il contenuto come sensibile. Lo rileva
    /// l'implementazione di piattaforma; il nucleo garantisce che questi contenuti non lascino il PC.
    /// </summary>
    public bool IsMarkedSensitive { get; init; }

    public static ClipboardContent FromText(string text, bool isMarkedSensitive = false) => new()
    {
        Kind = ClipboardContentKind.Text,
        Text = text ?? throw new ArgumentNullException(nameof(text)),
        IsMarkedSensitive = isMarkedSensitive,
    };
}
