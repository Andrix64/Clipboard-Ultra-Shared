using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Cus.Core.Abstractions;
using Cus.Core.Imaging;
using Cus.Platform.Windows.Interop;

namespace Cus.Platform.Windows;

/// <summary>Formati clipboard registrati per nome.</summary>
internal sealed class ClipboardFormats
{
    public ushort OwnWriteMarker { get; } = Register("CUS.OwnWrite");
    public ushort Png { get; } = Register("PNG");
    public ushort ExcludeFromMonitor { get; } = Register("ExcludeClipboardContentFromMonitorProcessing");
    public ushort CanIncludeInHistory { get; } = Register("CanIncludeInClipboardHistory");
    public ushort CanUploadToCloud { get; } = Register("CanUploadToCloudClipboard");
    public ushort FileGroupDescriptor { get; } = Register("FileGroupDescriptorW");
    public ushort FileContents { get; } = Register("FileContents");
    public ushort PreferredDropEffect { get; } = Register("Preferred DropEffect");

    private static ushort Register(string name)
    {
        var id = Native.RegisterClipboardFormatW(name);
        return id == 0 ? throw new Win32Exception($"RegisterClipboardFormat('{name}') fallita.") : (ushort)id;
    }
}

/// <summary>
/// Clipboard di Windows. Legge testo, immagini e file (solo metadati); scrive testo e immagini
/// subito e i file come virtual files lazy. Ogni scrittura porta il formato privato
/// "CUS.OwnWrite", così il monitor la riconosce e il nucleo non la rimanda indietro.
/// </summary>
public sealed class WindowsClipboard : IClipboardMonitor, IClipboardWriter
{
    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    private readonly MessageWindowThread _window = new();
    private readonly ClipboardFormats _formats;
    private readonly Channel<ClipboardChangedEventArgs> _events = Channel.CreateUnbounded<ClipboardChangedEventArgs>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private ClipboardContent? _lastWritten;
    private VirtualFileDataObject? _offeredFiles;
    private bool _started;

    public WindowsClipboard()
    {
        _formats = _window.InvokeAsync(() => new ClipboardFormats()).GetAwaiter().GetResult();
        _window.MessageReceived += OnMessage;
        _ = Task.Run(DispatchEventsAsync);
    }

    public event EventHandler<ClipboardChangedEventArgs>? Changed;

    public void Start()
    {
        _window.InvokeAsync(() =>
        {
            if (_started)
                return;
            if (!Native.AddClipboardFormatListener(_window.Handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "AddClipboardFormatListener fallita.");
            _started = true;
        }).GetAwaiter().GetResult();
    }

    public Task WriteAsync(ClipboardContent content, IRemoteFileSource? files = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        return _window.InvokeAsync(() => WriteOnWindowThread(content, files));
    }

    /// <summary>Legge la clipboard attuale. Pubblico per i test e per la UI.</summary>
    public Task<ClipboardChangedEventArgs?> ReadAsync() => _window.InvokeAsync(ReadOnWindowThread);

    public void Dispose()
    {
        _window.InvokeAsync(() =>
        {
            if (_started)
                Native.RemoveClipboardFormatListener(_window.Handle);
            _started = false;
            // I virtual files rimasti nella clipboard punterebbero a un processo chiuso.
            if (_offeredFiles is not null && OleNative.OleIsCurrentClipboard(_offeredFiles) == 0)
                OleNative.OleSetClipboard(null);
            _offeredFiles = null;
        }).Wait(TimeSpan.FromSeconds(5));
        _events.Writer.TryComplete();
        _window.Dispose();
    }

    private void OnMessage(uint message)
    {
        if (message != Native.WM_CLIPBOARDUPDATE || !_started)
            return;

        ClipboardChangedEventArgs? args;
        try
        {
            args = ReadOnWindowThread();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: lettura clipboard fallita: {ex.Message}");
            return;
        }

        // Gli eventi escono dal thread della finestra (che non deve bloccarsi) ma nell'ordine giusto.
        if (args is not null)
            _events.Writer.TryWrite(args);
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var args in _events.Reader.ReadAllAsync())
        {
            try
            {
                Changed?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: handler della clipboard fallito: {ex}");
            }
        }
    }

    private ClipboardChangedEventArgs? ReadOnWindowThread()
    {
        OpenWithRetry();
        try
        {
            if (Native.IsClipboardFormatAvailable(_formats.OwnWriteMarker))
                return _lastWritten is null ? null : new ClipboardChangedEventArgs(_lastWritten, isOwnWrite: true);

            var sensitive = IsMarkedSensitive();
            ClipboardContent? content = null;

            if (Native.IsClipboardFormatAvailable(Native.CF_HDROP))
                content = ReadFiles();
            else if (Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT))
                content = ReadText();
            else if (Native.IsClipboardFormatAvailable(_formats.Png))
                content = Native.FromHGlobal(Native.GetClipboardData(_formats.Png)) is { Length: > 0 } png ? ClipboardContent.FromImage(png) : null;
            else if (Native.IsClipboardFormatAvailable(Native.CF_DIBV5) || Native.IsClipboardFormatAvailable(Native.CF_DIB))
                content = ReadDib();

            if (content is null)
                return null;
            return new ClipboardChangedEventArgs(content with { IsMarkedSensitive = sensitive }, isOwnWrite: false);
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private bool IsMarkedSensitive()
    {
        if (Native.IsClipboardFormatAvailable(_formats.ExcludeFromMonitor))
            return true;
        return IsDwordZero(_formats.CanIncludeInHistory) || IsDwordZero(_formats.CanUploadToCloud);

        static bool IsDwordZero(ushort format) =>
            Native.IsClipboardFormatAvailable(format)
            && Native.FromHGlobal(Native.GetClipboardData(format)) is { Length: >= 4 } data
            && BitConverter.ToUInt32(data) == 0;
    }

    private static ClipboardContent? ReadText()
    {
        var data = Native.FromHGlobal(Native.GetClipboardData(Native.CF_UNICODETEXT));
        if (data is null)
            return null;
        var text = System.Text.Encoding.Unicode.GetString(data);
        var end = text.IndexOf('\0');
        return ClipboardContent.FromText(end >= 0 ? text[..end] : text);
    }

    private ClipboardContent? ReadDib()
    {
        var format = Native.IsClipboardFormatAvailable(Native.CF_DIBV5) ? Native.CF_DIBV5 : Native.CF_DIB;
        var dib = Native.FromHGlobal(Native.GetClipboardData(format));
        if (dib is null)
            return null;
        try
        {
            return ClipboardContent.FromImage(PngCodec.Encode(DibConverter.ToRgba(dib)));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: immagine non supportata: {ex.Message}");
            return null;
        }
    }

    private static unsafe ClipboardContent? ReadFiles()
    {
        var drop = Native.GetClipboardData(Native.CF_HDROP);
        if (drop == 0)
            return null;

        var count = Native.DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
        var paths = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            var length = Native.DragQueryFileW(drop, i, null, 0);
            var buffer = new char[length + 1];
            fixed (char* p = buffer)
                Native.DragQueryFileW(drop, i, p, length + 1);
            paths.Add(new string(buffer, 0, (int)length));
        }

        var files = LocalFileList.FromPaths(paths);
        return files.Count == 0 ? null : ClipboardContent.FromFiles(files);
    }

    private void WriteOnWindowThread(ClipboardContent content, IRemoteFileSource? files)
    {
        _lastWritten = content;

        if (content.Kind == ClipboardContentKind.Files)
        {
            if (content.Files is null || files is null)
                throw new ArgumentException("Per scrivere file servono l'elenco e la sorgente remota.");
            _offeredFiles = new VirtualFileDataObject(content.Files, files, _formats);
            for (var attempt = 0; ; attempt++)
            {
                var hr = OleNative.OleSetClipboard(_offeredFiles);
                if (hr == 0)
                    return;
                if (hr != CLIPBRD_E_CANT_OPEN || attempt >= 10)
                    Marshal.ThrowExceptionForHR(hr);
                Thread.Sleep(20);
            }
        }

        _offeredFiles = null;
        OpenWithRetry();
        try
        {
            Native.EmptyClipboard();
            switch (content.Kind)
            {
                case ClipboardContentKind.Text:
                    Set(Native.CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes((content.Text ?? "") + "\0"));
                    break;
                case ClipboardContentKind.Image when content.ImagePng is not null:
                    Set(_formats.Png, content.ImagePng);
                    try
                    {
                        Set(Native.CF_DIB, DibConverter.FromRgba(PngCodec.Decode(content.ImagePng)));
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                    {
                        // Resta solo il formato PNG: lo leggono browser, Office e i programmi di grafica.
                        System.Diagnostics.Trace.WriteLine($"CUS: PNG non convertibile in DIB: {ex.Message}");
                    }

                    break;
            }

            Set(_formats.OwnWriteMarker, [1]);
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static void Set(uint format, ReadOnlySpan<byte> data)
    {
        var handle = Native.ToHGlobal(data);
        if (Native.SetClipboardData(format, handle) == 0)
        {
            Native.GlobalFree(handle);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"SetClipboardData({format}) fallita.");
        }
    }

    private void OpenWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (Native.OpenClipboard(_window.Handle))
                return;
            if (attempt >= 20)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "La clipboard è occupata da un'altra applicazione.");
            Thread.Sleep(15);
        }
    }
}
