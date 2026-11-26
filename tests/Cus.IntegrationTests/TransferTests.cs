using Cus.Core.Abstractions;
using Cus.Core.Node;

namespace Cus.IntegrationTests;

/// <summary>
/// Trasferimenti dal lato di chi ha copiato i file: continuano anche se nel frattempo si copia
/// altro, si vedono (chi, quanto) e si possono annullare.
/// </summary>
public sealed class TransferTests : IAsyncLifetime
{
    private const int Chunk = 1024 * 1024;

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
    public async Task TransferInProgress_ContinuesAfterTheOriginCopiesSomethingElse()
    {
        var (host, b, _) = await StartGroupAsync();
        var files = TestFiles.Create(_root.Path, "b-files", ("video.mp4", 3 * Chunk));
        var original = File.ReadAllBytes(files[0].LocalPath!);
        b.Clipboard.Copy(ClipboardContent.FromFiles(files));
        await Eventually.TrueAsync(() => host.FileSource is not null, "l'host riceve i file");
        var source = host.FileSource!;

        var first = await source.ReadAsync(0, 0, Chunk);
        var transfer = Assert.Single(b.Node.Transfers);
        Assert.Equal("host", transfer.RequesterName);
        Assert.Equal("video.mp4", transfer.ItemName);
        Assert.Equal(TransferState.Active, transfer.State);
        Assert.Equal(Chunk, transfer.BytesDone);
        Assert.Equal(3 * Chunk, transfer.TotalBytes);
        await Eventually.TrueAsync(() => b.Overlay.State?.ActiveTransfers == 1, "l'indicatore di b conta il trasferimento");

        // b copia altro: il trasferimento già avviato non si interrompe.
        b.Copy("altro");
        await Eventually.TrueAsync(() => host.Text == "altro", "la nuova copia arriva");
        var second = await source.ReadAsync(0, Chunk, Chunk);
        var third = await source.ReadAsync(0, 2 * Chunk, Chunk);

        Assert.Equal(original, (byte[])[.. first, .. second, .. third]);
        transfer = Assert.Single(b.Node.Transfers);
        Assert.Equal(TransferState.Completed, transfer.State);
        Assert.Equal(1.0, transfer.Progress);
        await Eventually.TrueAsync(() => b.Overlay.State?.ActiveTransfers == 0, "nessun trasferimento attivo");
    }

    [Fact]
    public async Task ReReadingTheSameChunk_DoesNotCountTwice_NorCompleteTheTransferEarly()
    {
        var (host, b, _) = await StartGroupAsync();
        var files = TestFiles.Create(_root.Path, "b-files", ("archivio.zip", 2 * Chunk));
        b.Clipboard.Copy(ClipboardContent.FromFiles(files));
        await Eventually.TrueAsync(() => host.FileSource is not null, "l'host riceve i file");
        var source = host.FileSource!;

        await source.ReadAsync(0, 0, Chunk);
        await source.ReadAsync(0, Chunk / 2, Chunk / 2);
        await source.ReadAsync(0, 0, Chunk);

        var transfer = Assert.Single(b.Node.Transfers);
        Assert.Equal(Chunk, transfer.BytesDone);
        Assert.Equal(TransferState.Active, transfer.State);

        await source.ReadAsync(0, Chunk, Chunk);
        transfer = Assert.Single(b.Node.Transfers);
        Assert.Equal(2 * Chunk, transfer.BytesDone);
        Assert.Equal(TransferState.Completed, transfer.State);
    }

    [Fact]
    public async Task NewPaste_OfASupersededItem_FailsClearly_ForSomeoneWhoWasNotDownloading()
    {
        var (host, b, c) = await StartGroupAsync();
        b.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "b-files", ("a.bin", 2 * Chunk))));
        await Eventually.TrueAsync(() => host.FileSource is not null && c.FileSource is not null, "tutti ricevono i file");
        var hostSource = host.FileSource!;
        var cSource = c.FileSource!;
        await hostSource.ReadAsync(0, 0, Chunk);

        b.Copy("altro");
        await Eventually.TrueAsync(() => c.Text == "altro", "la nuova copia arriva");

        var error = await Assert.ThrowsAsync<RemoteFileUnavailableException>(() => cSource.ReadAsync(0, 0, Chunk));
        Assert.Contains("già copiato altro", error.Message);
        Assert.Equal(Chunk, (await hostSource.ReadAsync(0, Chunk, Chunk)).Length);
    }

    [Fact]
    public async Task NewPaste_OfASupersededItem_FailsClearly_EvenForWhoJustFinishedDownloadingIt()
    {
        var (host, b, _) = await StartGroupAsync();
        b.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "b-files", ("a.bin", Chunk))));
        await Eventually.TrueAsync(() => host.FileSource is not null, "l'host riceve i file");
        var source = host.FileSource!;
        await source.ReadAsync(0, 0, Chunk);
        Assert.Equal(TransferState.Completed, Assert.Single(b.Node.Transfers).State);

        b.Copy("altro");
        await Eventually.TrueAsync(() => host.Text == "altro", "la nuova copia arriva");

        // Un incolla nuovo, da capo, di file che b ha già sostituito: deve fallire come prima.
        var error = await Assert.ThrowsAsync<RemoteFileUnavailableException>(() => source.ReadAsync(0, 0, Chunk));
        Assert.Contains("già copiato altro", error.Message);
    }

    [Fact]
    public async Task CancelledTransfer_FailsAtTheNextRead()
    {
        var (host, b, _) = await StartGroupAsync();
        b.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "b-files", ("a.bin", 3 * Chunk))));
        await Eventually.TrueAsync(() => host.FileSource is not null, "l'host riceve i file");
        var source = host.FileSource!;
        await source.ReadAsync(0, 0, Chunk);
        var transfer = Assert.Single(b.Node.Transfers);

        b.Node.CancelTransfer(transfer.ItemId, transfer.RequesterId);

        Assert.Equal(TransferState.Cancelled, Assert.Single(b.Node.Transfers).State);
        var error = await Assert.ThrowsAsync<RemoteFileUnavailableException>(() => source.ReadAsync(0, Chunk, Chunk));
        Assert.Contains("annullato", error.Message);
        Assert.Contains(host.Notifier.Errors, e => e.Contains("annullato", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelayedRead_ShowsTheRealRequester_OnTheOrigin()
    {
        var (_, b, c) = await StartGroupAsync();
        b.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "b-files", ("foto.jpg", 2 * Chunk))));
        await Eventually.TrueAsync(() => c.FileSource is not null, "c riceve i file");

        await c.FileSource!.ReadAsync(0, 0, Chunk);

        var transfer = Assert.Single(b.Node.Transfers);
        Assert.Equal("c", transfer.RequesterName);
        Assert.Equal(c.Node.Identity.DeviceId, transfer.RequesterId);
        Assert.Equal(0.5, transfer.Progress);
    }

    [Fact]
    public async Task IncomingFiles_AreTrackedOnWhoPastes_NotOnTheRelayingHost()
    {
        var (host, b, c) = await StartGroupAsync();
        b.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "b-files", ("foto.jpg", 2 * Chunk))));
        await Eventually.TrueAsync(() => c.FileSource is not null, "c riceve i file");
        var source = c.FileSource!;

        await source.ReadAsync(0, 0, Chunk);

        var incoming = Assert.Single(c.Node.IncomingTransfers);
        Assert.Equal("b", incoming.OriginName);
        Assert.Equal("foto.jpg", incoming.ItemName);
        Assert.Equal(TransferState.Active, incoming.State);
        Assert.Equal(0.5, incoming.Progress);
        await Eventually.TrueAsync(() => c.Overlay.State?.ActiveIncomingTransfers == 1, "l'indicatore di c conta il file in arrivo");
        Assert.Empty(host.Node.IncomingTransfers);
        Assert.Empty(b.Node.IncomingTransfers);

        await source.ReadAsync(0, Chunk, Chunk);

        Assert.Equal(TransferState.Completed, Assert.Single(c.Node.IncomingTransfers).State);
        await Eventually.TrueAsync(() => c.Overlay.State is { IncomingTransfers: 1, ActiveIncomingTransfers: 0 }, "concluso, ancora in elenco");
    }

    [Fact]
    public async Task CancelledIncomingFile_FailsAtTheNextRead_OnWhoPastes()
    {
        var (host, b, _) = await StartGroupAsync();
        b.Clipboard.Copy(ClipboardContent.FromFiles(TestFiles.Create(_root.Path, "b-files", ("a.bin", 3 * Chunk))));
        await Eventually.TrueAsync(() => host.FileSource is not null, "l'host riceve i file");
        var source = host.FileSource!;
        await source.ReadAsync(0, 0, Chunk);
        var incoming = Assert.Single(host.Node.IncomingTransfers);

        host.Node.CancelIncomingTransfer(incoming.ItemId);

        Assert.Equal(TransferState.Cancelled, Assert.Single(host.Node.IncomingTransfers).State);
        var error = await Assert.ThrowsAsync<RemoteFileUnavailableException>(() => source.ReadAsync(0, Chunk, Chunk));
        Assert.Contains("annullata", error.Message);
        Assert.Contains(host.Notifier.Errors, e => e.Contains("annullata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverlayButton_RaisesTheIncomingTransfersRequest()
    {
        var (_, b, _) = await StartGroupAsync();
        var raised = false;
        b.Node.IncomingTransfersRequested += (_, _) => raised = true;

        b.Overlay.ClickIncomingTransfers();

        Assert.True(raised);
    }

    [Fact]
    public async Task OverlayButton_RaisesTheTransfersRequest()
    {
        var (_, b, _) = await StartGroupAsync();
        var raised = false;
        b.Node.TransfersRequested += (_, _) => raised = true;

        b.Overlay.ClickTransfers();

        Assert.True(raised);
    }

    private async Task<(TestNode Host, TestNode B, TestNode C)> StartGroupAsync()
    {
        var host = await Start("host");
        await host.ShowAsync();
        var b = await Start("b");
        await b.JoinAsync(host);
        var c = await Start("c");
        await c.JoinAsync(host);
        await Eventually.TrueAsync(() => b.Node.Session?.Participants.Count == 3 && c.Node.Session?.Participants.Count == 3, "tutti vedono tutti");
        return (host, b, c);
    }

    private async Task<TestNode> Start(string name)
    {
        var node = await TestNode.StartAsync(_root.Path, name);
        _nodes.Add(node);
        return node;
    }
}
