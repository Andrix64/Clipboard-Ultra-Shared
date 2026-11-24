namespace Cus.Core.Abstractions;

/// <summary>Notifica ogni cambiamento della clipboard di sistema (o di quella simulata).</summary>
public interface IClipboardMonitor : IDisposable
{
    event EventHandler<ClipboardChangedEventArgs>? Changed;

    void Start();
}

public sealed class ClipboardChangedEventArgs(ClipboardContent content) : EventArgs
{
    public ClipboardContent Content { get; } = content;
}
