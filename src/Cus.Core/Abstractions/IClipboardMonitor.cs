namespace Cus.Core.Abstractions;

/// <summary>Notifica ogni cambiamento della clipboard di sistema (o di quella simulata).</summary>
public interface IClipboardMonitor : IDisposable
{
    event EventHandler<ClipboardChangedEventArgs>? Changed;

    void Start();
}

public sealed class ClipboardChangedEventArgs(ClipboardContent content, bool isOwnWrite) : EventArgs
{
    public ClipboardContent Content { get; } = content;

    /// <summary>
    /// True se il cambiamento è dovuto a una scrittura di CUS (contenuto ricevuto da un peer):
    /// il nucleo non lo rimanda alla sessione, così non c'è rimbalzo.
    /// </summary>
    public bool IsOwnWrite { get; } = isOwnWrite;
}
