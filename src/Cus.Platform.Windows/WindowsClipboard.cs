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

    /// <summary>
    /// CFSTR_ZONEIDENTIFIER (ShlObj_core.h): zona da cui vengono i FileContents. Esplora risorse la
    /// scrive nei file creati (Mark-of-the-Web), così SmartScreen e Office li trattano come scaricati.
    /// </summary>
    public ushort ZoneIdentifier { get; } = Register("ZoneIdentifier");

    private static ushort Register(string name)
    {
        var id = Native.RegisterClipboardFormatW(name);
        return id == 0 ? throw new Win32Exception($"RegisterClipboardFormat('{name}') fallita.") : (ushort)id;
    }
}

/// <summary>
/// Clipboard di Windows. Legge testo, immagini e file (solo metadati); scrive testo e immagini
/// subito e i file come virtual files lazy. Ogni scrittura porta il formato privato
/// "CUS.OwnWrite", così il monitor la riconosce e il nucleo non la rimanda indietro, e i formati
/// CanIncludeInClipboardHistory / CanUploadToCloudClipboard a 0: ciò che arriva dagli altri PC non
/// resta nella cronologia Win+V né va nella clipboard cloud.
/// </summary>
public sealed class WindowsClipboard : IClipboardMonitor, IClipboardWriter
{
    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    /// <summary>Oltre questi pixel un'immagine ricevuta si offre solo come PNG (il DIB occuperebbe troppa memoria).</summary>
    internal const long MaxDibPixels = 25_000_000;

    /// <summary>Attesa prima di rileggere una clipboard rimasta occupata.</summary>
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly MessageWindowThread _window = new();
    private readonly ClipboardFormats _formats;
    private readonly Channel<ClipboardChangedEventArgs> _events = Channel.CreateUnbounded<ClipboardChangedEventArgs>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private ClipboardContent? _lastWritten;
    private VirtualFileDataObject? _offeredFiles;
    private bool _started;
    private long _updates;

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
        try
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
        }
        catch (AggregateException ex) when (ex.InnerException is ObjectDisposedException)
        {
            // Già chiusa.
        }

        _events.Writer.TryComplete();
        _window.Dispose();
    }

    private void OnMessage(uint message)
    {
        if (message != Native.WM_CLIPBOARDUPDATE || !_started)
            return;
        _updates++;
        ReadAndPublish(isRetry: false);
    }

    private void ReadAndPublish(bool isRetry)
    {
        if (!_started)
            return;

        ClipboardChangedEventArgs? args;
        try
        {
            args = ReadOnWindowThread();
        }
        catch (ClipboardBusyException) when (!isRetry)
        {
            // Un'altra app tiene aperta la clipboard: invece di perdere la copia si riprova più tardi.
            System.Diagnostics.Trace.WriteLine("CUS: clipboard occupata, nuovo tentativo di lettura tra poco.");
            _ = RetryReadAsync(_updates);
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: lettura clipboard fallita{(isRetry ? " anche al nuovo tentativo" : "")}: {ex.Message}");
            return;
        }

        // Gli eventi escono dal thread della finestra (che non deve bloccarsi) ma nell'ordine giusto.
        if (args is not null)
            _events.Writer.TryWrite(args);
    }

    private async Task RetryReadAsync(long updatesAtFailure)
    {
        await Task.Delay(BusyRetryDelay).ConfigureAwait(false);
        try
        {
            await _window.InvokeAsync(() =>
            {
                // Se nel frattempo è arrivata una copia più recente, l'ha già letta il suo messaggio.
                if (_updates == updatesAtFailure)
                    ReadAndPublish(isRetry: true);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: nuovo tentativo di lettura della clipboard fallito: {ex.Message}");
        }
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
        RawClipboard raw;
        OpenWithRetry();
        try
        {
            if (Native.IsClipboardFormatAvailable(_formats.OwnWriteMarker))
                return _lastWritten is null ? null : new ClipboardChangedEventArgs(_lastWritten, isOwnWrite: true);
            raw = ReadRaw();
        }
        finally
        {
            Native.CloseClipboard();
        }

        // Conversione dell'immagine e accesso al disco a clipboard chiusa: le altre app non aspettano.
        var content = Convert(raw);
        return content is null ? null : new ClipboardChangedEventArgs(content with { IsMarkedSensitive = raw.Sensitive }, isOwnWrite: false);
    }

    /// <summary>Copia i dati grezzi con la clipboard aperta; nessuna conversione qui.</summary>
    private RawClipboard ReadRaw()
    {
        var sensitive = IsMarkedSensitive();
        var paths = Native.IsClipboardFormatAvailable(Native.CF_HDROP) ? ReadDropPaths() : null;
        // Il testo si legge anche accanto ai file: serve se i file non sono leggibili.
        var text = Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT) ? ReadText() : null;
        if (paths is { Count: > 0 } || text is not null)
            return new RawClipboard(sensitive, paths, text, null, null);

        if (Native.IsClipboardFormatAvailable(_formats.Png))
            return new RawClipboard(sensitive, null, null, Native.FromHGlobal(Native.GetClipboardData(_formats.Png)), null);
        if (Native.IsClipboardFormatAvailable(Native.CF_DIBV5) || Native.IsClipboardFormatAvailable(Native.CF_DIB))
        {
            var format = Native.IsClipboardFormatAvailable(Native.CF_DIBV5) ? Native.CF_DIBV5 : Native.CF_DIB;
            return new RawClipboard(sensitive, null, null, null, Native.FromHGlobal(Native.GetClipboardData(format)));
        }

        return new RawClipboard(sensitive, null, null, null, null);
    }

    private static ClipboardContent? Convert(RawClipboard raw)
    {
        if (raw.Paths is { Count: > 0 } paths)
        {
            try
            {
                var files = LocalFileList.FromPaths(paths);
                if (files.Count > 0)
                    return ClipboardContent.FromFiles(files);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: file della clipboard non leggibili: {ex.Message}");
            }
        }

        if (raw.Text is not null)
            return ClipboardContent.FromText(raw.Text);
        if (raw.Png is { Length: > 0 } png)
            return ClipboardContent.FromImage(png);
        if (raw.Dib is not null)
            return DibToImage(raw.Dib);
        return null;
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

    private static string? ReadText()
    {
        var data = Native.FromHGlobal(Native.GetClipboardData(Native.CF_UNICODETEXT));
        if (data is null)
            return null;
        var text = System.Text.Encoding.Unicode.GetString(data);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static ClipboardContent? DibToImage(byte[] dib)
    {
        try
        {
            return ClipboardContent.FromImage(PngCodec.Encode(DibConverter.ToRgba(dib)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: immagine non supportata: {ex.Message}");
            return null;
        }
    }

    /// <summary>Percorsi di CF_HDROP; null se il formato c'è ma non è leggibile.</summary>
    private static unsafe List<string>? ReadDropPaths()
    {
        try
        {
            var drop = Native.GetClipboardData(Native.CF_HDROP);
            if (drop == 0)
                return null;

            var count = Native.DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                var length = Native.DragQueryFileW(drop, i, null, 0);
                if (length == 0)
                    continue;
                var buffer = new char[length + 1];
                fixed (char* p = buffer)
                {
                    if (Native.DragQueryFileW(drop, i, p, length + 1) != length)
                        continue;
                }

                paths.Add(new string(buffer, 0, (int)length));
            }

            return paths;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: CF_HDROP non leggibile: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// CF_DIB per un'immagine ricevuta, o null: oltre <see cref="MaxDibPixels"/> o se la conversione
    /// fallisce per qualunque motivo resta solo il formato PNG (lo leggono browser, Office e i
    /// programmi di grafica). L'immagine arriva da un altro PC: nessun errore deve impedire la scrittura.
    /// </summary>
    internal static byte[]? TryConvertReceivedPngToDib(byte[] png)
    {
        if (!PngCodec.TryReadSize(png, out var width, out var height) || (long)width * height > MaxDibPixels)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: PNG ricevuto non convertito in DIB ({width}x{height}).");
            return null;
        }

        try
        {
            return DibConverter.FromRgba(PngCodec.Decode(png, MaxDibPixels));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: PNG non convertibile in DIB: {ex.Message}");
            return null;
        }
    }

    private void WriteOnWindowThread(ClipboardContent content, IRemoteFileSource? files)
    {
        if (content.Kind == ClipboardContentKind.Files)
        {
            if (content.Files is null || files is null)
                throw new ArgumentException("Per scrivere file servono l'elenco e la sorgente remota.");
            // Valida i nomi ricevuti: se l'elenco è pericoloso non si tocca la clipboard.
            var offered = new VirtualFileDataObject(content.Files, files, _formats);
            _lastWritten = content;
            _offeredFiles = offered;
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

        // La decodifica dell'immagine avviene prima di aprire la clipboard, per non bloccarla.
        var dib = content is { Kind: ClipboardContentKind.Image, ImagePng: { } receivedPng } ? TryConvertReceivedPngToDib(receivedPng) : null;

        _lastWritten = content;
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
                    if (dib is not null)
                        Set(Native.CF_DIB, dib);
                    break;
            }

            // Fuori dalla cronologia Win+V e dalla clipboard cloud. Il monitor riconosce comunque la
            // scrittura come nostra grazie al marcatore, che controlla prima di questi formati.
            Set(_formats.CanIncludeInHistory, BitConverter.GetBytes(0u));
            Set(_formats.CanUploadToCloud, BitConverter.GetBytes(0u));
            // Un contenuto sensibile ricevuto (password da un amico affidabile) resta sensibile anche
            // qui: gli altri programmi che osservano la clipboard lo ignorano come quello originale.
            if (content.IsMarkedSensitive)
                Set(_formats.ExcludeFromMonitor, [0]);
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
                throw new ClipboardBusyException(Marshal.GetLastPInvokeError());
            Thread.Sleep(15);
        }
    }

    /// <summary>Dati copiati dalla clipboard aperta, da convertire dopo averla chiusa.</summary>
    private sealed record RawClipboard(bool Sensitive, List<string>? Paths, string? Text, byte[]? Png, byte[]? Dib);

    private sealed class ClipboardBusyException(int error)
        : Win32Exception(error, "La clipboard è occupata da un'altra applicazione.");
}
