using System.ComponentModel;
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

    [Fact]
    public void Save_UsesAppEntropy_SoPlainDpapiCannotDecrypt()
    {
        var store = new DpapiSecretStore(_dir);
        var secret = Encoding.ASCII.GetBytes("segreto-con-entropia");

        store.Save("db-key", secret);

        var onDisk = File.ReadAllBytes(Path.Combine(_dir, "secrets", "db-key.bin"));
        Assert.Throws<Win32Exception>(() => DpapiSecretStore.Unprotect(onDisk));
        Assert.Throws<Win32Exception>(() => DpapiSecretStore.Unprotect(onDisk, DpapiSecretStore.EntropyFor("altro-nome")));
        Assert.Equal(secret, DpapiSecretStore.Unprotect(onDisk, DpapiSecretStore.EntropyFor("db-key")));
        Assert.Equal(secret, store.Load("db-key"));
    }

    [Fact]
    public void LegacyBlobWithoutEntropy_IsRead_AndMigrated()
    {
        // Formato delle installazioni precedenti: DPAPI senza entropia.
        var secret = Encoding.ASCII.GetBytes("segreto-di-una-vecchia-installazione");
        var path = Path.Combine(_dir, "secrets", "db-key.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, DpapiSecretStore.Protect(secret));

        Assert.Equal(secret, new DpapiSecretStore(_dir).Load("db-key"));

        var migrated = File.ReadAllBytes(path);
        Assert.Throws<Win32Exception>(() => DpapiSecretStore.Unprotect(migrated));
        Assert.Equal(secret, DpapiSecretStore.Unprotect(migrated, DpapiSecretStore.EntropyFor("db-key")));
        Assert.Equal(secret, new DpapiSecretStore(_dir).Load("db-key"));
    }

    [Fact]
    public void CorruptBlob_Throws_AndOrphanTempIsRemoved()
    {
        var path = Path.Combine(_dir, "secrets", "db-key.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.WriteAllBytes(path + ".tmp", [5, 6, 7]); // scrittura interrotta di un avvio precedente

        Assert.Throws<Win32Exception>(() => new DpapiSecretStore(_dir).Load("db-key"));
        Assert.False(File.Exists(path + ".tmp"));
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

    public static TheoryData<string, byte[]> CraftedDibs => new()
    {
        { "intestazione da 41 byte", Dib(headerSize: 41) },
        { "intestazione BITMAPCOREHEADER (12)", Dib(headerSize: 12) },
        { "intestazione negativa", Dib(headerSize: -40) },
        { "intestazione V5 più lunga del buffer", Dib(headerSize: 124, length: 100) },
        { "larghezza enorme", Dib(width: int.MaxValue) },
        { "larghezza negativa", Dib(width: -1) },
        { "altezza int.MinValue", Dib(height: int.MinValue) },
        { "altezza zero", Dib(height: 0) },
        { "dati insufficienti", Dib(width: 1 << 20, height: 50) },
        { "colorsUsed enorme", Dib(colorsUsed: int.MaxValue) },
        { "colorsUsed negativo", Dib(colorsUsed: -1_000_000) },
        { "maschere troncate (BI_BITFIELDS)", Dib(compression: 3, length: 44) },
        { "maschere V3 troncate", Dib(headerSize: 56, compression: 3, length: 52) },
    };

    [Theory]
    [MemberData(nameof(CraftedDibs))]
    public void CraftedHeader_ThrowsInvalidData(string description, byte[] dib)
    {
        var error = Record.Exception(() => DibConverter.ToRgba(dib));
        Assert.True(error is InvalidDataException, $"{description}: {error?.GetType().Name ?? "nessuna eccezione"}");
    }

    /// <summary>DIB 32 bit BI_RGB da 1x1 con i campi dell'intestazione sovrascrivibili.</summary>
    private static byte[] Dib(int headerSize = 40, int width = 1, int height = 1, int compression = 0, int colorsUsed = 0, int? length = null)
    {
        var dib = new byte[length ?? 40 + 4];
        BitConverter.TryWriteBytes(dib.AsSpan(0), headerSize);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (ushort)32);
        BitConverter.TryWriteBytes(dib.AsSpan(16), compression);
        BitConverter.TryWriteBytes(dib.AsSpan(32), colorsUsed);
        return dib;
    }
}

public class ReceivedImageTests
{
    [Fact]
    public void HugePng_IsNotConvertedToDib()
    {
        // Solo l'IHDR: 6000x6000 = 36 Mpx, oltre il limite. Non si deve nemmeno tentare la decodifica.
        Assert.Null(WindowsClipboard.TryConvertReceivedPngToDib(PngHeader(6000, 6000)));
    }

    [Fact]
    public void MalformedPng_IsNotConvertedToDib_AndDoesNotThrow()
    {
        var png = PngHeader(10, 10).Concat(new byte[64]).ToArray();

        Assert.Null(WindowsClipboard.TryConvertReceivedPngToDib(png));
        Assert.Null(WindowsClipboard.TryConvertReceivedPngToDib([1, 2, 3]));
    }

    [Fact]
    public void SmallPng_IsConvertedToDib()
    {
        var image = new RgbaImage(2, 1, [255, 0, 0, 255, 0, 0, 255, 255]);

        var dib = WindowsClipboard.TryConvertReceivedPngToDib(PngCodec.Encode(image));

        Assert.NotNull(dib);
        Assert.Equal(image.Pixels, DibConverter.ToRgba(dib).Pixels);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var png = new byte[8 + 8 + 13 + 4];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8; // profondità
        png[25] = 6; // RGBA
        return png;
    }
}

public class MessageWindowThreadTests
{
    [Fact]
    public async Task InvokeAfterDispose_FailsFast()
    {
        var window = new MessageWindowThread();
        Assert.Equal(42, await window.InvokeAsync(() => 42));

        window.Dispose();

        var pending = window.InvokeAsync(() => 1);
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pending, completed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
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

    [Theory]
    [InlineData("../fuori.txt")]
    [InlineData("cartella/../../fuori.txt")]
    [InlineData("..\\..\\Startup\\x.bat")]
    [InlineData("cartella\\x.txt")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("C:x.txt")]
    [InlineData("//server/share/x.txt")]
    [InlineData("/assoluto.txt")]
    [InlineData("nota.txt:flusso")]
    [InlineData("nota.txt::$DATA")]
    [InlineData("CON")]
    [InlineData("cartella/nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("punto.")]
    [InlineData("spazio ")]
    [InlineData("./a.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a\u0000b.txt")]
    public async Task DangerousNames_AreRejected(string name)
    {
        using var window = new MessageWindowThread();
        var formats = await window.InvokeAsync(() => new ClipboardFormats());

        Assert.Throws<ArgumentException>(() => new VirtualFileDataObject(
            [new ClipboardFileInfo("ok.txt", 1), new ClipboardFileInfo(name, 1)], new FakeSource(), formats));
    }

    [Fact]
    public async Task TooLongName_IsRejected_NotTruncated()
    {
        using var window = new MessageWindowThread();
        var formats = await window.InvokeAsync(() => new ClipboardFormats());
        var name = string.Join('/', Enumerable.Repeat(new string('a', 100), 3)); // 302 caratteri

        Assert.Throws<ArgumentException>(() => new VirtualFileDataObject([new ClipboardFileInfo(name, 1)], new FakeSource(), formats));
    }

    [Fact]
    public void RemoteFileStream_NeverReadsBeyondDeclaredSize()
    {
        // Il PC d'origine (ostile o difettoso) restituisce più byte di quelli chiesti.
        var source = new OversizedSource(extra: 4096);
        var stream = new RemoteFileStream(source, 0, 10, "a.bin");
        var buffer = new byte[100];
        Array.Fill(buffer, (byte)0xEE);
        var read = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            stream.Read(buffer, buffer.Length, read);
            Assert.Equal(10, System.Runtime.InteropServices.Marshal.ReadInt32(read));
            Assert.All(buffer[10..], b => Assert.Equal(0xEE, b));

            stream.Read(buffer, buffer.Length, read); // a fine file
            Assert.Equal(0, System.Runtime.InteropServices.Marshal.ReadInt32(read));

            // Su più blocchi: i byte in eccesso di ogni blocco non spostano i successivi.
            var big = new RemoteFileStream(source, 0, 2_500_000, "b.bin");
            var all = new byte[3_000_000];
            big.Read(all, all.Length, read);
            Assert.Equal(2_500_000, System.Runtime.InteropServices.Marshal.ReadInt32(read));
            Assert.Equal((byte)(2_000_123 % 251), all[2_000_123]);
            Assert.Equal(0, all[2_500_000]);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(read);
        }
    }

    private sealed class OversizedSource(int extra) : IRemoteFileSource
    {
        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Range(0, count + extra).Select(i => i < count ? (byte)((offset + i) % 251) : (byte)0xAB).ToArray());
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
    public async Task OwnTextWrite_IsExcludedFromHistoryAndCloud()
    {
        await _clipboard.WriteAsync(ClipboardContent.FromText("non in Win+V"));

        var e = await NextEvent(e => e.Content.Text == "non in Win+V");
        Assert.True(e.IsOwnWrite);
        Assert.Equal<(uint?, uint?)>((0u, 0u), await OnClipboardThread(() => (ReadDword("CanIncludeInClipboardHistory"), ReadDword("CanUploadToCloudClipboard"))));
    }

    [Fact]
    public async Task OwnFileWrite_IsExcludedFromHistoryAndCloud()
    {
        await _clipboard.WriteAsync(
            ClipboardContent.FromFiles([new ClipboardFileInfo("a.txt", 3)]),
            new ConstantSource());

        await NextEvent(e => e.Content.Kind == ClipboardContentKind.Files && e.IsOwnWrite);
        Assert.Equal<(uint?, uint?)>((0u, 0u), await OnClipboardThread(() => (ReadDword("CanIncludeInClipboardHistory"), ReadDword("CanUploadToCloudClipboard"))));
    }

    [Fact]
    public async Task OwnFileWrite_WithTraversalName_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _clipboard.WriteAsync(
            ClipboardContent.FromFiles([new ClipboardFileInfo("..\\..\\Startup\\x.bat", 3)]),
            new ConstantSource()));
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

    /// <summary>Legge un formato DWORD dalla clipboard; null se manca. Da chiamare su un thread STA.</summary>
    private static uint? ReadDword(string formatName)
    {
        for (var i = 0; !Native.OpenClipboard(0); i++)
        {
            if (i > 50)
                throw new InvalidOperationException("Clipboard occupata.");
            Thread.Sleep(20);
        }

        try
        {
            var data = Native.FromHGlobal(Native.GetClipboardData(Native.RegisterClipboardFormatW(formatName)));
            return data is { Length: >= 4 } ? BitConverter.ToUInt32(data) : null;
        }
        finally
        {
            Native.CloseClipboard();
        }
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
