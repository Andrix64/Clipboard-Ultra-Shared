using Cus.Core.Abstractions;

namespace Cus.Platform.Simulated;

/// <summary>
/// Clipboard virtuale, una per istanza. Si comporta come quella di sistema: sia una copia
/// dell'utente (<see cref="Copy"/>) sia una scrittura di CUS (<see cref="WriteAsync"/>)
/// notificano il monitor, così il nucleo deve gestire il rimbalzo anche nei test.
/// </summary>
public sealed class SimulatedClipboard : IClipboardMonitor, IClipboardWriter
{
    private readonly Lock _gate = new();
    private ClipboardContent? _current;
    private bool _started;

    public event EventHandler<ClipboardChangedEventArgs>? Changed;

    public ClipboardContent? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Start()
    {
        lock (_gate) _started = true;
    }

    /// <summary>Simula una copia dell'utente (Ctrl+C).</summary>
    public void Copy(ClipboardContent content) => Set(content);

    public Task WriteAsync(ClipboardContent content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Set(content);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate) _started = false;
    }

    private void Set(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        bool notify;
        lock (_gate)
        {
            _current = content;
            notify = _started;
        }

        if (notify)
            Changed?.Invoke(this, new ClipboardChangedEventArgs(content));
    }
}
