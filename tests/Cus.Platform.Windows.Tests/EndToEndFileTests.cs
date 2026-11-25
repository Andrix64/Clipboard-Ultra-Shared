using System.Net;
using System.Security.Cryptography;
using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Node;
using Cus.Core.Storage;
using Cus.Platform.Simulated;

namespace Cus.Platform.Windows.Tests;

/// <summary>
/// Percorso completo su un solo PC: il nodo "andrea" (clipboard simulata) copia un file vero,
/// il nodo "luca" (clipboard di Windows) lo riceve come virtual file ed Esplora risorse lo incolla
/// scaricandolo in rete solo in quel momento. Dimensione predefinita 64 MB; con la variabile
/// d'ambiente CUS_BIG_FILE_MB si prova un file più grande (per esempio 1024).
/// </summary>
public sealed class EndToEndFileTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cus-tests", "e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FileCopiedOnOnePc_IsPastedByExplorerOnTheOther()
    {
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("CUS_BIG_FILE_MB"), out var mb) ? mb : 64;
        var original = Path.Combine(_root, "origine", "grande.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        await WriteRandomFileAsync(original, megabytes);

        var andreaClipboard = new SimulatedClipboard();
        var lucaClipboard = new WindowsClipboard();
        var andrea = await StartNodeAsync("andrea", andreaClipboard, andreaClipboard);
        var luca = await StartNodeAsync("luca", lucaClipboard, lucaClipboard);

        var received = new TaskCompletionSource();
        lucaClipboard.Changed += (_, e) =>
        {
            if (e.IsOwnWrite && e.Content.Kind == ClipboardContentKind.Files)
                received.TrySetResult();
        };

        await andrea.SetVisibleAsync(true);
        Assert.Equal(JoinOutcome.Accepted, (await luca.JoinAsync("127.0.0.1", andrea.Port)).Outcome);
        andreaClipboard.Copy(ClipboardContent.FromFiles(LocalFileList.FromPaths([original])));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var target = Path.Combine(_root, "incollato");
        Directory.CreateDirectory(target);
        await PasteWithExplorerAsync(target);

        var pasted = Path.Combine(target, "grande.bin");
        var expectedLength = new FileInfo(original).Length;
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline && !(File.Exists(pasted) && new FileInfo(pasted).Length == expectedLength && CanOpenExclusively(pasted)))
            await Task.Delay(500);

        Assert.True(File.Exists(pasted), "Esplora risorse non ha incollato il file.");
        Assert.Equal(await HashAsync(original), await HashAsync(pasted));
    }

    public async Task DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
            await item.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<CusNode> StartNodeAsync(string name, IClipboardMonitor monitor, IClipboardWriter writer)
    {
        var database = CusDatabase.Open(new ProfileAppPaths(name, _root), new InMemorySecretStore());
        var node = new CusNode(
            new CusNodeOptions { Port = 0, BindAddress = IPAddress.Loopback, EnableDiscovery = false },
            database,
            CusNode.LoadOrCreateIdentity(database, name, name),
            monitor, writer, new RecordingOverlayIndicator(), new AcceptAll(), new SilentNotifier());
        await node.StartAsync();
        _cleanup.Add(new Disposer(async () =>
        {
            await node.DisposeAsync();
            monitor.Dispose();
            database.Dispose();
        }));
        return node;
    }

    private static async Task WriteRandomFileAsync(string path, int megabytes)
    {
        await using var stream = File.Create(path);
        var buffer = new byte[1024 * 1024];
        for (var i = 0; i < megabytes; i++)
        {
            RandomNumberGenerator.Fill(buffer);
            await stream.WriteAsync(buffer);
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static Task PasteWithExplorerAsync(string folder)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                shell.NameSpace(folder).Self.InvokeVerb("Paste");
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private sealed class AcceptAll : IConsentPrompt
    {
        public Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ConsentDecision.Accept);

        public Task<bool> AskAgreementAsync(AgreementRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class SilentNotifier : IUserNotifier
    {
        public void Info(string message)
        {
        }

        public void Error(string message) => Assert.Fail($"Errore inatteso: {message}");
    }

    private sealed class Disposer(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose();
    }
}
