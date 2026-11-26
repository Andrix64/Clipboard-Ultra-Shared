using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Cus.Core.Abstractions;

namespace Cus.Platform.Linux.Tests;

/// <summary>
/// Clipboard X11 reale, con xclip nel ruolo dell'altra applicazione. Tutti in questa classe: la clipboard
/// è una sola e i test di una stessa classe xunit girano in serie.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class X11ClipboardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cus-x11-tests-" + Guid.NewGuid().ToString("N"));
    private readonly X11Clipboard _clipboard;

    public X11ClipboardTests()
    {
        _clipboard = new X11Clipboard(Path.Combine(_root, "cache"));
        _clipboard.Start();
    }

    public void Dispose()
    {
        _clipboard.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [X11Fact]
    public async Task Monitor_ReportsTextCopiedByAnotherApp()
    {
        var text = "copiato altrove è " + Guid.NewGuid();

        var change = await NextChange(e => e.Content.Text == text, () => XclipIn("UTF8_STRING", Encoding.UTF8.GetBytes(text)));

        Assert.False(change.IsOwnWrite);
        Assert.Equal(ClipboardContentKind.Text, change.Content.Kind);
        Assert.False(change.Content.IsMarkedSensitive);
    }

    [X11Fact]
    public async Task Write_Text_IsPastableElsewhere_AndReportedAsOwnWrite()
    {
        var text = "ricevuto da un peer ✓ " + Guid.NewGuid();

        var change = await NextChange(e => e.Content.Text == text,
            () => _clipboard.WriteAsync(ClipboardContent.FromText(text)).GetAwaiter().GetResult());

        Assert.True(change.IsOwnWrite);
        Assert.Equal(text, Encoding.UTF8.GetString(XclipOut("UTF8_STRING")));
    }

    /// <summary>Più grande di un pezzo (256 KB): passa per il protocollo INCR in scrittura.</summary>
    [X11Fact]
    public async Task Write_LargeImage_IsTransferredIncrementally()
    {
        var png = RandomNumberGenerator.GetBytes(1_500_000);

        await _clipboard.WriteAsync(ClipboardContent.FromImage(png));

        Assert.Equal(png, XclipOut("image/png"));
    }

    /// <summary>xclip manda i dati grandi con INCR: qui si prova la lettura incrementale.</summary>
    [X11Fact]
    public async Task Monitor_ReadsLargeImage_CopiedByAnotherApp()
    {
        var png = RandomNumberGenerator.GetBytes(1_500_000);

        var change = await NextChange(e => e.Content.Kind == ClipboardContentKind.Image, () => XclipIn("image/png", png));

        Assert.False(change.IsOwnWrite);
        Assert.Equal(png, change.Content.ImagePng);
    }

    [X11Fact]
    public async Task Monitor_ReadsFiles_FromUriList()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "relazione finale.txt");
        await File.WriteAllTextAsync(path, "contenuto");

        var change = await NextChange(e => e.Content.Kind == ClipboardContentKind.Files,
            () => XclipIn(FileUris.UriListTarget, Encoding.UTF8.GetBytes(FileUris.ToUri(path) + "\r\n")));

        var file = Assert.Single(change.Content.Files!);
        Assert.Equal("relazione finale.txt", file.Name);
        Assert.Equal(path, file.LocalPath);
        Assert.Equal(9, file.SizeBytes);
    }

    [X11Fact]
    public async Task Write_Files_AreDownloadedOnPaste_IntoThePrivateCache()
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var source = new MemorySource(data);
        var files = new[] { new ClipboardFileInfo("cartella/a b.bin", data.Length), new ClipboardFileInfo("cartella/vuota", 0) { IsDirectory = true } };

        await _clipboard.WriteAsync(ClipboardContent.FromFiles(files), source);
        Assert.Equal(0, source.Reads); // Lazy: niente download finché nessuno incolla.

        var pasted = FileUris.ParseGnomeCopiedFiles(Encoding.UTF8.GetString(XclipOut(FileUris.GnomeCopiedFilesTarget)));

        var folder = Assert.Single(pasted);
        Assert.Equal("cartella", Path.GetFileName(folder));
        Assert.StartsWith(Path.Combine(_root, "cache") + "/", folder, StringComparison.Ordinal);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, "a b.bin")));
        Assert.True(Directory.Exists(Path.Combine(folder, "vuota")));

        // Il secondo incolla usa i file già scaricati.
        var reads = source.Reads;
        Assert.Equal(pasted, FileUris.ParseUriList(Encoding.UTF8.GetString(XclipOut(FileUris.UriListTarget))));
        Assert.Equal(reads, source.Reads);
    }

    [X11Fact]
    public async Task Write_Files_WithDangerousNames_IsRejected_WithoutTouchingTheClipboard()
    {
        var text = "resta com'era " + Guid.NewGuid();
        await _clipboard.WriteAsync(ClipboardContent.FromText(text));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _clipboard.WriteAsync(ClipboardContent.FromFiles([new ClipboardFileInfo("../fuori.txt", 1)]), new MemorySource([1])));

        Assert.Equal(text, Encoding.UTF8.GetString(XclipOut("UTF8_STRING")));
    }

    private async Task<ClipboardChangedEventArgs> NextChange(Func<ClipboardChangedEventArgs, bool> match, Action act)
    {
        var result = new TaskCompletionSource<ClipboardChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, ClipboardChangedEventArgs e)
        {
            if (match(e))
                result.TrySetResult(e);
        }

        _clipboard.Changed += Handler;
        try
        {
            act();
            return await result.Task.WaitAsync(Timeout);
        }
        finally
        {
            _clipboard.Changed -= Handler;
        }
    }

    /// <summary>xclip diventa proprietario della clipboard e resta in background a servirla.</summary>
    private static void XclipIn(string target, byte[] data)
    {
        var start = new ProcessStartInfo("xclip") { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-selection", "clipboard", "-t", target, "-i" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.BaseStream.Write(data);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(Timeout), "xclip -i non è terminato.");
    }

    /// <summary>Come un'applicazione che incolla il formato indicato.</summary>
    private static byte[] XclipOut(string target)
    {
        var start = new ProcessStartInfo("xclip") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-selection", "clipboard", "-t", target, "-o" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        Assert.True(process.WaitForExit(Timeout), "xclip -o non è terminato.");
        copy.Wait(Timeout);
        Assert.Equal(0, process.ExitCode);
        return output.ToArray();
    }

    /// <summary>Il PC d'origine dei file, in memoria: ogni file ha gli stessi byte.</summary>
    private sealed class MemorySource(byte[] data) : IRemoteFileSource
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            var length = (int)Math.Min(count, data.Length - offset);
            return Task.FromResult(data.AsSpan((int)offset, Math.Max(length, 0)).ToArray());
        }
    }
}
