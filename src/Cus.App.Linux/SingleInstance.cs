using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Cus.App.Linux;

/// <summary>
/// Un profilo per volta, come il mutex di Cus.App.Windows: un file di lock (flock esclusivo, che .NET
/// applica con FileShare.None) nella cartella dati, più un socket Unix su cui il secondo avvio chiede alla
/// prima istanza di mostrarsi. Il lock si libera da solo quando il processo termina, anche se va in crash.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly Socket? _listener;
    private readonly string? _socketPath;

    private SingleInstance(FileStream lockFile, Socket? listener, string? socketPath)
    {
        _lock = lockFile;
        _listener = listener;
        _socketPath = socketPath;
    }

    /// <summary>
    /// Prende il profilo. Se è già aperto chiede alla prima istanza di mostrarsi e restituisce null.
    /// </summary>
    /// <param name="onActivate">Chiamato (su un thread in background) a ogni secondo avvio dello stesso profilo.</param>
    public static SingleInstance? TryAcquire(string dataDirectory, string profile, Action onActivate)
    {
        Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var socketPath = SocketPath(dataDirectory, profile);

        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            SignalRunningInstance(socketPath);
            return null;
        }

        Socket? listener = null;
        try
        {
            // Un socket rimasto da un'istanza in crash: il lock è nostro, quindi nessuno lo usa più.
            File.Delete(socketPath);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            var accepting = listener;
            new Thread(() => AcceptLoop(accepting, onActivate)) { IsBackground = true, Name = "CUS-activate" }.Start();
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Senza socket CUS funziona lo stesso: il secondo avvio esce senza portare avanti la finestra.
            System.Diagnostics.Trace.WriteLine($"CUS: socket di attivazione non disponibile: {ex.Message}");
            listener?.Dispose();
            listener = null;
        }

        return new SingleInstance(lockFile, listener, listener is null ? null : socketPath);
    }

    public void Dispose()
    {
        _listener?.Dispose();
        if (_socketPath is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        _lock.Dispose();
    }

    /// <summary>
    /// In $XDG_RUNTIME_DIR (privata dell'utente, svuotata al logout) se c'è, altrimenti nella cartella dati.
    /// Un percorso di socket Unix non supera 107 byte.
    /// </summary>
    private static string SocketPath(string dataDirectory, string profile)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var path = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)
            ? Path.Combine(runtime, $"cus-{profile}.sock")
            : Path.Combine(dataDirectory, "activate.sock");
        return path;
    }

    private static void SignalRunningInstance(string socketPath)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            client.Send([1]);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
        {
            // La prima istanza si sta chiudendo o non ha il socket: niente da fare, nessun avviso.
        }
    }

    private static void AcceptLoop(Socket listener, Action onActivate)
    {
        while (true)
        {
            try
            {
                using var client = listener.Accept();
                onActivate();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    }
}
