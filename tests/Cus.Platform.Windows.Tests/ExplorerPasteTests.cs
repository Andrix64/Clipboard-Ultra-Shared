using Cus.Core.Abstractions;

namespace Cus.Platform.Windows.Tests;

/// <summary>
/// Incolla vero con il motore di copia di Esplora risorse (verbo "Incolla" della shell) sui virtual
/// files offerti da <see cref="WindowsClipboard"/>. Verifica anche che i byte vengano letti solo
/// durante l'incolla. Tocca la clipboard reale.
/// </summary>
public sealed class ExplorerPasteTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "cus-tests", "incolla-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsClipboard _clipboard = new();

    public ExplorerPasteTests() => Directory.CreateDirectory(_target);

    [Fact]
    public async Task Explorer_PastesVirtualFiles_ReadingThemLazily()
    {
        var source = new PatternSource();
        await _clipboard.WriteAsync(
            ClipboardContent.FromFiles([new ClipboardFileInfo("relazione.bin", 3_000_000), new ClipboardFileInfo("cartella/note.txt", 10)]),
            source);

        Assert.Equal(0, source.Reads); // niente trasferimento prima dell'incolla

        await PasteWithExplorerAsync(_target);

        var big = Path.Combine(_target, "relazione.bin");
        var small = Path.Combine(_target, "cartella", "note.txt");
        await WaitForAsync(() => File.Exists(big) && new FileInfo(big).Length == 3_000_000 && File.Exists(small));
        Assert.Equal(PatternSource.Expected(0, 3_000_000), await File.ReadAllBytesAsync(big));
        Assert.Equal(PatternSource.Expected(1, 10), await File.ReadAllBytesAsync(small));
        Assert.True(source.Reads >= 3);

        // Arrivano da un altro PC: Esplora risorse marca il file come scaricato (Mark-of-the-Web, zona
        // Internet). Lo fa solo per il primo file dell'incolla: legge ZoneIdentifier una volta sola e gli
        // altri file restano senza marca (limite di Esplora risorse, accettato il 2026-11-25).
        var zone = await File.ReadAllTextAsync(big + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone);
    }

    public void Dispose()
    {
        _clipboard.Dispose();
        try
        {
            Directory.Delete(_target, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Task PasteWithExplorerAsync(string folder)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic target = shell.NameSpace(folder);
                target.Self.InvokeVerb("Paste");
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                    return;
            }
            catch (IOException)
            {
                // File ancora in scrittura.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("Esplora risorse non ha incollato i file entro 30 secondi.");
    }

    private sealed class PatternSource : IRemoteFileSource
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public static byte[] Expected(int fileIndex, int length) =>
            Enumerable.Range(0, length).Select(i => (byte)((i * 7 + fileIndex * 13) % 251)).ToArray();

        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            var data = new byte[count];
            for (var i = 0; i < count; i++)
                data[i] = (byte)(((offset + i) * 7 + fileIndex * 13) % 251);
            return Task.FromResult(data);
        }
    }
}
