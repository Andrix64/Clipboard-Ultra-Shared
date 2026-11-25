using System.Collections.Concurrent;
using System.Net;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;
using Cus.Platform.Simulated;

namespace Cus.IntegrationTests;

/// <summary>Consenso programmabile: la decisione si imposta dal test.</summary>
public sealed class ScriptedConsent : IConsentPrompt
{
    /// <summary>Risposta alle richieste di ingresso (questo nodo è host).</summary>
    public ConsentDecision Decision { get; set; } = ConsentDecision.Accept;

    /// <summary>Risposta alle proposte di patto (questo nodo è membro).</summary>
    public bool AgreementDecision { get; set; } = true;

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public ConcurrentQueue<ConsentRequest> Requests { get; } = new();

    public ConcurrentQueue<AgreementRequest> AgreementRequests { get; } = new();

    public async Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        return Decision;
    }

    public async Task<bool> AskAgreementAsync(AgreementRequest request, CancellationToken cancellationToken)
    {
        AgreementRequests.Enqueue(request);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        return AgreementDecision;
    }
}

/// <summary>
/// Scrive nella clipboard simulata ricordando la sorgente dei file: i test leggono pezzi di file
/// come farebbe Esplora risorse durante un incolla lento.
/// </summary>
public sealed class RecordingWriter(SimulatedClipboard clipboard) : IClipboardWriter
{
    public IRemoteFileSource? FileSource { get; private set; }

    public Task WriteAsync(ClipboardContent content, IRemoteFileSource? files = null, CancellationToken cancellationToken = default)
    {
        FileSource = files;
        return clipboard.WriteAsync(content, files, cancellationToken);
    }
}

public sealed class RecordingNotifier : IUserNotifier
{
    public ConcurrentQueue<string> Infos { get; } = new();

    public ConcurrentQueue<string> Errors { get; } = new();

    public void Info(string message) => Infos.Enqueue(message);

    public void Error(string message) => Errors.Enqueue(message);
}

/// <summary>Un "dispositivo" completo: profilo con DB cifrato, clipboard simulata, nodo in loopback.</summary>
public sealed class TestNode : IAsyncDisposable
{
    private readonly string _root;
    private readonly InMemorySecretStore _secrets;
    private CusDatabase _database;

    private TestNode(string root, string name, InMemorySecretStore secrets, CusNodeOptions options)
    {
        _root = root;
        _secrets = secrets;
        Name = name;
        Options = options;
        _database = CusDatabase.Open(new ProfileAppPaths(name, root), secrets);
        Node = Create();
    }

    public string Name { get; }

    public CusNodeOptions Options { get; }

    public SimulatedClipboard Clipboard { get; private set; } = null!;

    public RecordingOverlayIndicator Overlay { get; private set; } = null!;

    public ScriptedConsent Consent { get; private set; } = null!;

    public RecordingNotifier Notifier { get; private set; } = null!;

    public CusNode Node { get; private set; }

    public ConcurrentQueue<ClipboardChangedEventArgs> ClipboardEvents { get; } = new();

    public string? Text => Clipboard.Current?.Text;

    public static async Task<TestNode> StartAsync(string root, string name, CusNodeOptions? options = null)
    {
        var node = new TestNode(root, name, new InMemorySecretStore(), options ?? DefaultOptions());
        await node.Node.StartAsync();
        return node;
    }

    public static CusNodeOptions DefaultOptions() => new()
    {
        Port = 0,
        BindAddress = IPAddress.Loopback,
        EnableDiscovery = false,
        ConsentTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Chiede di entrare nella sessione di <paramref name="host"/>.</summary>
    public Task<JoinResult> JoinAsync(TestNode host) => Node.JoinAsync("127.0.0.1", host.Node.Port);

    /// <summary>Si offre come host.</summary>
    public Task ShowAsync() => Node.SetVisibleAsync(true);

    /// <summary>Sorgente dei file dell'ultima scrittura ricevuta (per letture parziali).</summary>
    public IRemoteFileSource? FileSource => Writer.FileSource;

    public RecordingWriter Writer { get; private set; } = null!;

    public void Copy(string text) => Clipboard.Copy(ClipboardContent.FromText(text));

    /// <summary>Copia marcata come sensibile da un password manager.</summary>
    public void CopySensitive(string text) => Clipboard.Copy(ClipboardContent.FromText(text, isMarkedSensitive: true));

    /// <summary>Ferma il nodo (come un PC spento) mantenendo il profilo.</summary>
    public async Task StopAsync()
    {
        await Node.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>Riavvia il nodo sullo stesso profilo (stessa identità, stesso DB).</summary>
    public async Task RestartAsync()
    {
        _database = CusDatabase.Open(new ProfileAppPaths(Name, _root), _secrets);
        Node = Create();
        await Node.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Node.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        _database.Dispose();
    }

    private CusNode Create()
    {
        Clipboard = new SimulatedClipboard();
        Clipboard.Changed += (_, e) => ClipboardEvents.Enqueue(e);
        Writer = new RecordingWriter(Clipboard);
        Overlay = new RecordingOverlayIndicator();
        Consent = new ScriptedConsent();
        Notifier = new RecordingNotifier();
        var identity = CusNode.LoadOrCreateIdentity(_database, Name, "utente-" + Name);
        return new CusNode(Options, _database, identity, Clipboard, Writer, Overlay, Consent, Notifier);
    }
}

public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"Condizione non verificata entro {timeoutMs} ms: {because}");
    }
}

/// <summary>Cartella temporanea condivisa dai profili di un test.</summary>
public sealed class TestRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cus-it", Guid.NewGuid().ToString("N"));

    public TestRoot() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>File veri su disco da copiare nella clipboard simulata.</summary>
public static class TestFiles
{
    public static List<ClipboardFileInfo> Create(string rootPath, string folder, params (string Name, int Size)[] specs)
    {
        var root = Path.Combine(rootPath, folder);
        var result = new List<ClipboardFileInfo>();
        foreach (var (name, size) in specs)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new byte[size];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(path, data);
            result.Add(new ClipboardFileInfo(name, size) { LocalPath = path });
        }

        return result;
    }
}
