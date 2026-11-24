using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Cus.Core.Abstractions;
using Cus.Core.Imaging;
using Cus.Platform.Windows.Interop;

namespace Cus.Platform.Windows.Tests;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cus-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveThenLoad_RoundTrips_AndFileIsNotPlaintext()
    {
        var store = new DpapiSecretStore(_dir);
        var secret = Encoding.ASCII.GetBytes("chiave-segreta-di-prova-0123456789");

        store.Save("db-key", secret);

        Assert.Equal(secret, store.Load("db-key"));
        Assert.Null(store.Load("missing"));
        var onDisk = File.ReadAllBytes(Path.Combine(_dir, "secrets", "db-key.bin"));
        Assert.DoesNotContain("chiave-segreta", Encoding.ASCII.GetString(onDisk));
    }

    [Fact]
    public void InvalidName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new DpapiSecretStore(_dir).Save("../x", [1]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}

public class DibConverterTests
{
    [Fact]
    public void RgbaToDibAndBack_RoundTrips()
    {
        var pixels = new byte[4 * 3 * 4];
        Random.Shared.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 200;
        var image = new RgbaImage(4, 3, pixels);

        var back = DibConverter.ToRgba(DibConverter.FromRgba(image));

        Assert.Equal(pixels, back.Pixels);
    }

    [Fact]
    public void Dib24Bit_TopDown_IsOpaque()
    {
        // 1x2, 24 bit, top-down (altezza negativa), righe allineate a 4 byte.
        var dib = new byte[40 + 8];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), 1);
        BitConverter.TryWriteBytes(dib.AsSpan(8), -2);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (ushort)24);
        dib[40] = 3; dib[41] = 2; dib[42] = 1;       // riga 0: B G R
        dib[44] = 30; dib[45] = 20; dib[46] = 10;    // riga 1

        var image = DibConverter.ToRgba(dib);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 10, 20, 30, 255 }, image.Pixels);
    }
}

public class VirtualFileDataObjectTests
{
    [Fact]
    public async Task Descriptor_ListsFoldersBeforeFiles_WithSizes()
    {
        using var window = new MessageWindowThread();
        var formats = await window.InvokeAsync(() => new ClipboardFormats());
        var data = new VirtualFileDataObject(
            [new ClipboardFileInfo("cartella/sotto/a.txt", 5_000_000_000), new ClipboardFileInfo("b.bin", 3)],
            new FakeSource(), formats);

        Assert.Equal(
            [("cartella", -1), ("cartella\\sotto", -1), ("cartella\\sotto\\a.txt", 0), ("b.bin", 1)],
            data.Entries.Select(e => (e.Name, e.FileIndex)));

        var descriptor = data.BuildDescriptor();
        Assert.Equal(4, BitConverter.ToInt32(descriptor));
        var third = 4 + 2 * 592;
        Assert.Equal(1u, BitConverter.ToUInt32(descriptor, third + 64)); // 5 GB: parte alta
        Assert.Equal((uint)(5_000_000_000 & 0xFFFFFFFF), BitConverter.ToUInt32(descriptor, third + 68));
        Assert.StartsWith("cartella\\sotto\\a.txt", Encoding.Unicode.GetString(descriptor, third + 72, 520));
    }

    [Fact]
    public void RemoteFileStream_ReadsAcrossChunks_AndReportsErrors()
    {
        var source = new FakeSource();
        var stream = new RemoteFileStream(source, 0, 3_000_000, "a.bin");
        var buffer = new byte[2_500_000];
        var read = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            stream.Read(buffer, buffer.Length, read);
            Assert.Equal(buffer.Length, System.Runtime.InteropServices.Marshal.ReadInt32(read));
            Assert.Equal((byte)(1_234_567 % 251), buffer[1_234_567]);
            Assert.Equal(3, source.Calls); // blocchi da 1 MB

            stream.Stat(out STATSTG stat, 0);
            Assert.Equal(3_000_000, stat.cbSize);

            source.Fail = true;
            stream.Read(new byte[100], 100, read); // ancora nel buffer
            stream.Seek(0, 0, 0); // fuori dal buffer: serve una nuova lettura remota
            var error = Assert.Throws<System.Runtime.InteropServices.COMException>(() => stream.Read(new byte[1_000_000], 1_000_000, read));
            Assert.Contains("ha già copiato altro", error.Message);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(read);
        }
    }

    private sealed class FakeSource : IRemoteFileSource
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail)
                throw new RemoteFileUnavailableException("Il PC d'origine ha già copiato altro.");
            return Task.FromResult(Enumerable.Range(0, count).Select(i => (byte)((offset + i) % 251)).ToArray());
        }
    }
}

/// <summary>Toccano la clipboard reale: il contenuto dell'utente viene sovrascritto.</summary>
public sealed class WindowsClipboardTests : IDisposable
{
    private readonly WindowsClipboard _clipboard = new();
    private readonly List<ClipboardChangedEventArgs> _events = [];
    private readonly SemaphoreSlim _signal = new(0);

    public WindowsClipboardTests()
    {
        _clipboard.Changed += (_, e) =>
        {
            lock (_events) _events.Add(e);
            _signal.Release();
        };
        _clipboard.Start();
    }

    [Fact]
    public async Task ExternalTextCopy_IsReported_NotAsOwnWrite()
    {
        ExternalCopy(text: "copiato da un'altra app");

        var e = await NextEvent(e => e.Content.Text == "copiato da un'altra app");
        Assert.False(e.IsOwnWrite);
        Assert.False(e.Content.IsMarkedSensitive);
    }

    [Fact]
    public async Task PasswordManagerCopy_IsMarkedSensitive()
    {
        ExternalCopy(text: "password123", excludeFromMonitor: true);

        var e = await NextEvent(e => e.Content.Text == "password123");
        Assert.True(e.Content.IsMarkedSensitive);
    }

    [Fact]
    public async Task HistoryOptOutCopy_IsMarkedSensitive()
    {
        ExternalCopy(text: "token-segreto", canIncludeInHistory: 0);

        var e = await NextEvent(e => e.Content.Text == "token-segreto");
        Assert.True(e.Content.IsMarkedSensitive);
    }

    [Fact]
    public async Task OwnWrite_IsReportedAsOwnWrite_AndReadableByOtherApps()
    {
        await _clipboard.WriteAsync(ClipboardContent.FromText("dal peer"));

        var e = await NextEvent(e => e.Content.Text == "dal peer");
        Assert.True(e.IsOwnWrite);
        Assert.Equal("dal peer", ReadExternalText());
    }

    [Fact]
    public async Task ExternalDib_IsReadAsPng()
    {
        var image = new RgbaImage(2, 1, [255, 0, 0, 255, 0, 0, 255, 255]);
        ExternalCopy(dib: DibConverter.FromRgba(image));

        var e = await NextEvent(e => e.Content.Kind == ClipboardContentKind.Image);
        Assert.Equal(image.Pixels, PngCodec.Decode(e.Content.ImagePng!).Pixels);
    }

    [Fact]
    public async Task OwnFileWrite_OffersVirtualFiles()
    {
        await _clipboard.WriteAsync(
            ClipboardContent.FromFiles([new ClipboardFileInfo("a.txt", 3)]),
            new ConstantSource());

        var e = await NextEvent(e => e.Content.Kind == ClipboardContentKind.Files);
        Assert.True(e.IsOwnWrite);
        Assert.True(await OnClipboardThread(() => Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormatW("FileGroupDescriptorW"))));
    }

    public void Dispose() => _clipboard.Dispose();

    private async Task<ClipboardChangedEventArgs> NextEvent(Func<ClipboardChangedEventArgs, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (_events)
            {
                var found = _events.FirstOrDefault(match);
                if (found is not null)
                    return found;
            }

            await _signal.WaitAsync(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException("Nessun evento della clipboard atteso.");
    }

    /// <summary>Scrive come farebbe un'altra applicazione, da un thread suo.</summary>
    private static void ExternalCopy(string? text = null, bool excludeFromMonitor = false, int? canIncludeInHistory = null, byte[]? dib = null)
    {
        var thread = new Thread(() =>
        {
            for (var i = 0; !Native.OpenClipboard(0); i++)
            {
                if (i > 50)
                    throw new InvalidOperationException("Clipboard occupata.");
                Thread.Sleep(20);
            }

            try
            {
                Native.EmptyClipboard();
                if (text is not null)
                    Native.SetClipboardData(Native.CF_UNICODETEXT, Native.ToHGlobal(Encoding.Unicode.GetBytes(text + "\0")));
                if (dib is not null)
                    Native.SetClipboardData(Native.CF_DIB, Native.ToHGlobal(dib));
                if (excludeFromMonitor)
                    Native.SetClipboardData(Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), Native.ToHGlobal([0]));
                if (canIncludeInHistory is { } value)
                    Native.SetClipboardData(Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory"), Native.ToHGlobal(BitConverter.GetBytes(value)));
            }
            finally
            {
                Native.CloseClipboard();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static string? ReadExternalText()
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            if (!Native.OpenClipboard(0))
                return;
            try
            {
                var data = Native.FromHGlobal(Native.GetClipboardData(Native.CF_UNICODETEXT));
                result = data is null ? null : Encoding.Unicode.GetString(data).TrimEnd('\0');
            }
            finally
            {
                Native.CloseClipboard();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static Task<T> OnClipboardThread<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private sealed class ConstantSource : IRemoteFileSource
    {
        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult(new byte[count]);
    }
}
