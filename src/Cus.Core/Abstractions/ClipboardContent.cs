using System.Text.Json.Serialization;

namespace Cus.Core.Abstractions;

/// <summary>Tipo di contenuto della clipboard gestito da CUS.</summary>
public enum ClipboardContentKind
{
    Text,
    Image,
    Files,
}

/// <summary>
/// Metadati di un file copiato. Il contenuto si trasferisce in modo lazy, solo all'incolla.
/// <see cref="Name"/> è un percorso relativo con '/' come separatore (per esempio "cartella/a.txt").
/// </summary>
public sealed record ClipboardFileInfo(string Name, long SizeBytes)
{
    /// <summary>Percorso locale sul PC d'origine. Non viaggia mai in rete.</summary>
    [JsonIgnore]
    public string? LocalPath { get; init; }

    /// <summary>
    /// True per una cartella vuota (dimensione 0, nessun contenuto da trasferire). Le cartelle con
    /// file dentro non servono: compaiono già come prefisso dei nomi dei file.
    /// </summary>
    public bool IsDirectory { get; init; }
}

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
    [JsonIgnore]
    public bool IsMarkedSensitive { get; init; }

    public static ClipboardContent FromText(string text, bool isMarkedSensitive = false) => new()
    {
        Kind = ClipboardContentKind.Text,
        Text = text ?? throw new ArgumentNullException(nameof(text)),
        IsMarkedSensitive = isMarkedSensitive,
    };

    public static ClipboardContent FromImage(byte[] png) => new()
    {
        Kind = ClipboardContentKind.Image,
        ImagePng = png ?? throw new ArgumentNullException(nameof(png)),
    };

    public static ClipboardContent FromFiles(IReadOnlyList<ClipboardFileInfo> files) => new()
    {
        Kind = ClipboardContentKind.Files,
        Files = files ?? throw new ArgumentNullException(nameof(files)),
    };

    /// <summary>Descrizione breve per UI e cronologia.</summary>
    public string Describe() => Kind switch
    {
        ClipboardContentKind.Text when IsMarkedSensitive => "(testo sensibile, non condiviso)",
        ClipboardContentKind.Text => Text ?? "",
        ClipboardContentKind.Image => $"(immagine, {ImagePng?.Length ?? 0:N0} byte)",
        ClipboardContentKind.Files when Files is { Count: 1 } => $"(file: {Files[0].Name}, {Files[0].SizeBytes:N0} byte)",
        ClipboardContentKind.Files => $"({Files?.Count ?? 0} file, {Files?.Sum(f => f.SizeBytes) ?? 0:N0} byte)",
        _ => "(contenuto sconosciuto)",
    };
}
