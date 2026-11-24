using Cus.Core.Abstractions;

namespace Cus.UI;

/// <summary>
/// Ciò che la finestra principale riceve dal progetto eseguibile. La UI non conosce le
/// implementazioni di piattaforma: vede solo le interfacce di Cus.Core.
/// </summary>
public sealed record MainWindowModel
{
    public static MainWindowModel Empty { get; } = new() { Title = "CUS" };

    public required string Title { get; init; }

    /// <summary>Righe informative (profilo, cartella dati, porta, peer) o messaggi di errore.</summary>
    public IReadOnlyList<string> InfoLines { get; init; } = [];

    /// <summary>Monitor della clipboard da mostrare. Null se non disponibile.</summary>
    public IClipboardMonitor? Clipboard { get; init; }

    /// <summary>Presente solo con clipboard simulata: simula una copia dell'utente.</summary>
    public Action<string>? SimulateCopy { get; init; }
}
