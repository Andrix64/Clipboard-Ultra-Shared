using Cus.Core.Abstractions;

namespace Cus.IntegrationTests;

public sealed class FileTransferTests : IAsyncLifetime
{
    private readonly TestRoot _root = new();
    private readonly List<TestNode> _nodes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task Files_AreAnnouncedThenDownloadedOnlyOnPaste()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.InviteAsync(luca);
        var files = CreateFiles("orig", ("relazione.pdf", 3_500_000), ("cartella/note.txt", 10));

        andrea.Clipboard.Copy(ClipboardContent.FromFiles(files));

        await Eventually.TrueAsync(() => luca.Clipboard.Current?.Kind == ClipboardContentKind.Files, "luca riceve i metadati");
        var received = luca.Clipboard.Current!.Files!;
        Assert.Equal(["relazione.pdf", "cartella/note.txt"], received.Select(f => f.Name));
        Assert.All(received, f => Assert.Null(f.LocalPath));

        var target = Path.Combine(_root.Path, "incolla-luca");
        var pasted = await luca.Clipboard.PasteFilesAsync(target);

        Assert.Equal(2, pasted.Count);
        Assert.Equal(File.ReadAllBytes(files[0].LocalPath!), File.ReadAllBytes(Path.Combine(target, "relazione.pdf")));
        Assert.Equal(File.ReadAllBytes(files[1].LocalPath!), File.ReadAllBytes(Path.Combine(target, "cartella", "note.txt")));
    }

    [Fact]
    public async Task Files_FromMember_ReachOtherMember_ThroughHost()
    {
        var (host, b, c) = (await Start("host"), await Start("b"), await Start("c"));
        await host.InviteAsync(b);
        await host.InviteAsync(c);
        var files = CreateFiles("b-files", ("foto.jpg", 1_200_000));

        b.Clipboard.Copy(ClipboardContent.FromFiles(files));
        await Eventually.TrueAsync(() => c.Clipboard.Current?.Kind == ClipboardContentKind.Files
            && host.Clipboard.Current?.Kind == ClipboardContentKind.Files, "c e host ricevono i metadati");

        var pastedByC = await c.Clipboard.PasteFilesAsync(Path.Combine(_root.Path, "incolla-c"));
        var pastedByHost = await host.Clipboard.PasteFilesAsync(Path.Combine(_root.Path, "incolla-host"));

        Assert.Equal(File.ReadAllBytes(files[0].LocalPath!), File.ReadAllBytes(pastedByC[0]));
        Assert.Equal(File.ReadAllBytes(files[0].LocalPath!), File.ReadAllBytes(pastedByHost[0]));
    }

    [Fact]
    public async Task Paste_AfterOriginCopiedSomethingElse_GivesClearError()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        luca.Consent.Decision = ConsentDecision.AcceptReadOnly; // così la copia di Andrea non sovrascrive quella di Luca
        await andrea.InviteAsync(luca);
        luca.Consent.Decision = ConsentDecision.Accept;

        var files = CreateFiles("luca-files", ("a.bin", 100));
        luca.Clipboard.Copy(ClipboardContent.FromFiles(files));
        await Eventually.TrueAsync(() => andrea.Clipboard.Current?.Kind == ClipboardContentKind.Files, "andrea riceve i file");

        // Luca copia altro, ma in sola lettura riceve solo lui: il file non è più offerto.
        luca.Copy("altro");
        await Eventually.TrueAsync(() => andrea.Text == "altro", "la nuova copia arriva");
        var history = await andrea.Node.GetHistoryAsync();
        await andrea.Node.UseHistoryEntryAsync(history.First(h => h.Content.Kind == ClipboardContentKind.Files));

        var error = await Assert.ThrowsAsync<RemoteFileUnavailableException>(
            () => andrea.Clipboard.PasteFilesAsync(Path.Combine(_root.Path, "x")));
        Assert.Contains("già copiato altro", error.Message);
        Assert.Contains(andrea.Notifier.Errors, e => e.Contains("Impossibile incollare", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paste_AfterOriginDisconnected_GivesClearError()
    {
        var (andrea, luca) = (await Start("andrea"), await Start("luca"));
        await andrea.InviteAsync(luca);
        andrea.Clipboard.Copy(ClipboardContent.FromFiles(CreateFiles("a", ("x.bin", 10))));
        await Eventually.TrueAsync(() => luca.Clipboard.Current?.Kind == ClipboardContentKind.Files, "luca riceve i file");

        await andrea.StopAsync();
        await Eventually.TrueAsync(() => luca.Node.Session is null, "sessione terminata");

        var error = await Assert.ThrowsAsync<RemoteFileUnavailableException>(
            () => luca.Clipboard.PasteFilesAsync(Path.Combine(_root.Path, "y")));
        Assert.Contains("non è raggiungibile", error.Message);
    }

    private List<ClipboardFileInfo> CreateFiles(string folder, params (string Name, int Size)[] specs)
    {
        var root = Path.Combine(_root.Path, folder);
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

    private async Task<TestNode> Start(string name)
    {
        var node = await TestNode.StartAsync(_root.Path, name);
        _nodes.Add(node);
        return node;
    }
}
