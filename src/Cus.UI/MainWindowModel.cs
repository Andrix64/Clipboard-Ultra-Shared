using Cus.Core.Node;

namespace Cus.UI;

/// <summary>
/// Ciò che la finestra principale riceve dal progetto eseguibile. La UI non conosce le
/// implementazioni di piattaforma: vede il nodo e le interfacce di Cus.Core.
/// </summary>
public sealed record MainWindowModel
{
    public static MainWindowModel Empty { get; } = new() { Title = "CUS" };

    public required string Title { get; init; }

    /// <summary>Righe informative (profilo, cartella dati, porta), nella lingua corrente.</summary>
    public Func<IReadOnlyList<string>> InfoLines { get; init; } = () => [];

    /// <summary>Null se l'avvio è fallito: la finestra mostra solo <see cref="StartupError"/>.</summary>
    public CusNode? Node { get; init; }

    public string? StartupError { get; init; }

    public AvaloniaNotifier? Notifier { get; init; }

    /// <summary>Presente solo con clipboard simulata.</summary>
    public ISimulationControls? Simulation { get; init; }

    /// <summary>C'è l'icona nell'area di notifica: la finestra si può chiudere lasciando CUS attivo.</summary>
    public bool HasTrayIcon { get; init; }

    /// <summary>Chiamato alla chiusura della finestra principale, per fermare nodo e piattaforma.</summary>
    public Func<Task>? Shutdown { get; init; }
}

/// <summary>Comandi della clipboard simulata, per provare CUS con più istanze sullo stesso PC.</summary>
public interface ISimulationControls
{
    event Action? ContentChanged;

    string DescribeCurrent();

    void CopyText(string text);

    /// <summary>Come una password copiata da un password manager (contenuto marcato sensibile).</summary>
    void CopySensitiveText(string text);

    void CopyPaths(IReadOnlyList<string> paths);

    /// <summary>Come incollare in Esplora risorse: scarica i file ricevuti nella cartella.</summary>
    Task<IReadOnlyList<string>> PasteFilesAsync(string folder);
}
