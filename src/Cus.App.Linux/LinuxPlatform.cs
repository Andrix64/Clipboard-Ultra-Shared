using Avalonia.Controls;
using Cus.App.Shared;
using Cus.Core.Abstractions;
using Cus.Platform.Linux;

namespace Cus.App.Linux;

/// <summary>Le parti Linux dell'avvio: lock del profilo, Secret Service, clipboard X11 e finestre X11.</summary>
internal sealed class LinuxPlatform : PlatformHost
{
    public override IInstanceLock? TryAcquireInstance(string profile, string dataDirectory, Action onActivate) =>
        SingleInstance.TryAcquire(dataDirectory, profile, onActivate) is { } instance ? new ProfileLock(instance) : null;

    public override ISecretStore CreateSecretStore(string dataDirectory) => new SecretServiceStore(dataDirectory);

    public override (IClipboardMonitor Monitor, IClipboardWriter Writer) CreateSystemClipboard(string dataDirectory)
    {
        try
        {
            // I file ricevuti si scaricano qui all'incolla; la cartella si svuota all'avvio e alla chiusura.
            var clipboard = new X11Clipboard(Path.Combine(dataDirectory, "received-files"));
            return (clipboard, clipboard);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new ClipboardUnavailableException(ex.Message, ex);
        }
    }

    /// <summary>Su X11 l'handle di piattaforma è l'id della finestra.</summary>
    public override void MakeNonActivating(Window window)
    {
        if (window.TryGetPlatformHandle() is { HandleDescriptor: "XID", Handle: var handle })
            X11WindowStyle.MakeNonActivating(handle);
    }

    /// <summary>
    /// Il lock del profilo. Si chiude una volta sola: dopo un avvio fallito il socket di attivazione è già
    /// stato cancellato e potrebbe appartenere a un'istanza nuova.
    /// </summary>
    private sealed class ProfileLock(SingleInstance instance) : IInstanceLock
    {
        private int _disposed;

        public void ReleaseAfterFailedStart() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                instance.Dispose();
        }
    }
}
