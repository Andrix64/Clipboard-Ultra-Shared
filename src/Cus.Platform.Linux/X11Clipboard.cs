using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Cus.Core.Abstractions;
using Cus.Platform.Linux.Interop;
using static Cus.Platform.Linux.Interop.Xlib;

namespace Cus.Platform.Linux;

/// <summary>
/// Clipboard di X11 (selezione CLIPBOARD). Funziona anche nelle sessioni Wayland di GNOME e KDE tramite
/// XWayland, che sincronizza le due clipboard: è lo stesso canale che usa la UI Avalonia.
/// </summary>
/// <remarks>
/// <para>Un thread dedicato possiede una connessione X propria e una finestra nascosta. XFixes avvisa a
/// ogni cambio di proprietario della clipboard: se il proprietario è la nostra finestra la scrittura è
/// di CUS (IsOwnWrite), altrimenti si leggono i formati offerti.</para>
/// <para>Lettura: file (<c>x-special/gnome-copied-files</c>, <c>text/uri-list</c>), poi testo
/// (<c>UTF8_STRING</c>, <c>text/plain;charset=utf-8</c>, <c>STRING</c>), poi <c>image/png</c>. Un contenuto
/// con <c>x-kde-passwordManagerHint</c> = "secret" (KeePassXC, KDE) è sensibile.</para>
/// <para>Scrittura: CUS diventa proprietario della selezione e risponde alle richieste, con il
/// protocollo INCR per i dati grandi. I file ricevuti restano lazy come su Windows: si scaricano alla
/// prima richiesta dell'elenco (cioè all'incolla) in una cartella di cache privata, e il file manager li
/// copia da lì. Se il download dura più del timeout di chi incolla (Qt aspetta 5 secondi), il download
/// continua e l'incolla successivo è immediato.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class X11Clipboard : IClipboardMonitor, IClipboardWriter
{
    private const string PasswordHintTarget = "x-kde-passwordManagerHint";

    /// <summary>Attesa massima per ogni risposta di un'altra applicazione.</summary>
    private const int ReplyTimeoutMs = 2000;

    /// <summary>Oltre questa dimensione un contenuto letto si scarta (protegge la memoria).</summary>
    private const int MaxReadBytes = 512 * 1024 * 1024;

    /// <summary>Un trasferimento INCR fermo da più di così si abbandona (chi chiedeva è sparito).</summary>
    private static readonly TimeSpan IncrStaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>Una cartella di file ricevuti sostituita si cancella dopo questo tempo (un incolla potrebbe essere in corso).</summary>
    private static readonly TimeSpan OldBatchLifetime = TimeSpan.FromMinutes(5);

    private delegate bool EventMatch(in XEvent e);

    private readonly string _cacheDirectory;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Channel<RawClipboard> _events = Channel.CreateUnbounded<RawClipboard>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly List<IncrTransfer> _incr = [];
    private readonly Thread _thread;

    private nint _display;
    private nuint _window;
    private int _wakeRead = -1;
    private int _wakeWrite = -1;
    private int _fixesEventBase;
    private int _maxChunk;
    private Atoms _atoms = null!;

    private volatile bool _stopping;
    private bool _started;
    private int _nesting;
    private bool _changePending;
    private nuint _changeTime;

    private Offer? _offer;
    private volatile ClipboardContent? _lastWritten;
    private volatile bool _foreignSinceWrite = true;

    /// <param name="cacheDirectory">Cartella privata per i file ricevuti; il contenuto si cancella all'avvio e alla chiusura.</param>
    /// <exception cref="InvalidOperationException">Nessun display X raggiungibile o XFixes assente.</exception>
    public X11Clipboard(string cacheDirectory)
    {
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        DeleteCache();

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "CUS-x11-clipboard" };
        _thread.Start();
        ready.Task.GetAwaiter().GetResult();
        _ = Task.Run(DispatchEventsAsync);
    }

    public event EventHandler<ClipboardChangedEventArgs>? Changed;

    public void Start() => InvokeAsync(() =>
    {
        if (_started)
            return true;
        XFixesSelectSelectionInput(_display, _window, _atoms.Clipboard, XFixesSetSelectionOwnerNotifyMask);
        _started = true;
        return true;
    }).GetAwaiter().GetResult();

    public Task WriteAsync(ClipboardContent content, IRemoteFileSource? files = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        // Valida i nomi ricevuti prima di toccare la clipboard: se l'elenco è pericoloso non si offre nulla.
        if (content.Kind == ClipboardContentKind.Files)
        {
            if (content.Files is null || files is null)
                throw new ArgumentException(Loc.T("clipboard.writingFilesRequiresTheListAnd"));
            if (!ClipboardFileNames.TryValidate(content.Files, out var reason))
                throw new InvalidDataException(Loc.T("clipboard.invalidFileListReceived", reason));
        }

        return InvokeAsync(() =>
        {
            WriteOnLoop(content, files);
            return true;
        });
    }

    /// <summary>Legge la clipboard attuale. Per i test.</summary>
    internal Task<ClipboardChangedEventArgs?> ReadAsync() => InvokeAsync(() =>
    {
        if (XGetSelectionOwner(_display, _atoms.Clipboard) == _window)
            return _lastWritten is { } own ? new ClipboardChangedEventArgs(own, isOwnWrite: true) : null;
        var raw = ReadForeign(CurrentTime);
        return raw is null ? null : ToArgs(raw);
    });

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        Wake();
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(5));
        _events.Writer.TryComplete();
        DeleteCache();
    }

    // ---- Ciclo del thread X -------------------------------------------------------------------

    private void Run(TaskCompletionSource ready)
    {
        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            Teardown();
            ready.SetException(ex);
            return;
        }

        ready.SetResult();
        try
        {
            while (!_stopping)
            {
                while (_work.TryDequeue(out var action))
                    action();

                ProcessQueuedEvents();

                if (_changePending)
                {
                    _changePending = false;
                    ReadAndPublish(_changeTime);
                    continue;
                }

                if (!_work.IsEmpty || XPending(_display) > 0)
                    continue;

                PollOnce(timeoutMs: 1000, includeWake: true);
                DropStaleIncrTransfers();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: ciclo della clipboard X11 terminato per errore: {ex}");
        }
        finally
        {
            // Lavori rimasti in coda: falliscono invece di restare appesi.
            while (_work.TryDequeue(out var action))
                action();
            Teardown();
        }
    }

    private unsafe void Initialize()
    {
        XInitThreads();
        InstallNonFatalErrorHandler();
        _display = XOpenDisplay(null);
        if (_display == 0)
            throw new InvalidOperationException(Loc.T("clipboard.x11CannotOpenDisplay"));
        if (!XFixesQueryExtension(_display, out _fixesEventBase, out _))
            throw new InvalidOperationException(Loc.T("clipboard.x11XfixesMissing"));

        _window = XCreateSimpleWindow(_display, XDefaultRootWindow(_display), -10, -10, 1, 1, 0, 0, 0);
        XSelectInput(_display, _window, PropertyChangeMask);
        _atoms = new Atoms(_display);

        // Richieste più grandi di così passano a INCR. Il limite del server è in unità di 4 byte.
        var units = (long)XExtendedMaxRequestSize(_display);
        if (units <= 0)
            units = XMaxRequestSize(_display);
        _maxChunk = (int)Math.Clamp(units * 4 - 256, 4096, 256 * 1024);

        var pipeFds = stackalloc int[2];
        if (pipe(pipeFds) != 0)
            throw new InvalidOperationException($"pipe() failed ({Marshal.GetLastPInvokeError()}).");
        (_wakeRead, _wakeWrite) = (pipeFds[0], pipeFds[1]);
        XFlush(_display);
    }

    private void Teardown()
    {
        if (_display != 0)
        {
            if (_window != 0)
                XDestroyWindow(_display, _window); // La selezione posseduta si libera con la finestra.
            XCloseDisplay(_display);
            _display = 0;
        }

        if (_wakeRead >= 0)
            close(_wakeRead);
        if (_wakeWrite >= 0)
            close(_wakeWrite);
        _wakeRead = _wakeWrite = -1;
    }

    private Task<T> InvokeAsync<T>(Func<T> function)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            if (_stopping || _display == 0)
            {
                completion.TrySetException(new ObjectDisposedException(nameof(X11Clipboard)));
                return;
            }

            try
            {
                completion.TrySetResult(function());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        Wake();
        return completion.Task;
    }

    private unsafe void Wake()
    {
        var fd = _wakeWrite;
        if (fd < 0)
            return;
        byte one = 1;
        write(fd, &one, 1);
    }

    private unsafe void PollOnce(int timeoutMs, bool includeWake)
    {
        var fds = stackalloc PollFd[2];
        fds[0] = new PollFd { fd = XConnectionNumber(_display), events = POLLIN };
        fds[1] = new PollFd { fd = _wakeRead, events = POLLIN };
        var count = includeWake ? 2 : 1;
        if (poll(fds, (nuint)count, timeoutMs) > 0 && includeWake && (fds[1].revents & POLLIN) != 0)
        {
            var buffer = stackalloc byte[64];
            read(_wakeRead, buffer, 64);
        }
    }

    private unsafe void ProcessQueuedEvents()
    {
        XEvent e;
        while (XPending(_display) > 0)
        {
            XNextEvent(_display, &e);
            Handle(in e);
        }
    }

    /// <summary>
    /// Aspetta un evento preciso continuando a servire gli altri (per esempio le richieste di chi incolla
    /// da CUS mentre CUS legge). I cambi di clipboard arrivati nel frattempo si leggono dopo.
    /// </summary>
    private unsafe bool WaitForEvent(EventMatch match, int timeoutMs, out XEvent found)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        _nesting++;
        try
        {
            XEvent e;
            while (true)
            {
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, &e);
                    if (match(in e))
                    {
                        found = e;
                        return true;
                    }

                    Handle(in e);
                }

                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || _stopping)
                {
                    found = default;
                    return false;
                }

                PollOnce((int)remaining, includeWake: false);
            }
        }
        finally
        {
            _nesting--;
        }
    }

    private void Handle(in XEvent e)
    {
        if (e.type == _fixesEventBase + XFixesSelectionNotify)
        {
            OnOwnerChanged(in e.fixesSelection);
            return;
        }

        switch (e.type)
        {
            case SelectionRequest:
                OnSelectionRequest(e.selectionRequest);
                break;
            case SelectionClear when e.selectionClear.selection == _atoms.Clipboard:
                // Un'altra applicazione ha copiato: la nostra offerta non vale più.
                DropOffer();
                break;
            case PropertyNotify when e.property.state == PropertyDelete:
                OnRequestorPropertyDeleted(e.property.window, e.property.atom);
                break;
        }
    }

    // ---- Monitor -----------------------------------------------------------------------------

    private void OnOwnerChanged(in XFixesSelectionNotifyEvent e)
    {
        if (!_started || e.selection != _atoms.Clipboard)
            return;

        if (e.owner == _window)
        {
            if (_lastWritten is { } own)
                _events.Writer.TryWrite(new RawClipboard(Own: own));
            return;
        }

        // Proprietario sparito (applicazione chiusa): la clipboard è vuota, niente da condividere.
        if (e.owner == None)
            return;

        _changePending = true;
        _changeTime = e.selection_timestamp;
        // Dentro un'attesa annidata non si legge: lo fa il ciclo principale appena libero.
    }

    private void ReadAndPublish(nuint time)
    {
        try
        {
            if (XGetSelectionOwner(_display, _atoms.Clipboard) is var owner && (owner == None || owner == _window))
                return;
            if (ReadForeign(time) is { } raw)
                _events.Writer.TryWrite(raw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: lettura della clipboard X11 fallita: {ex.Message}");
        }
    }

    /// <summary>Legge i formati utili del proprietario attuale. Le conversioni (disco, immagini) avvengono dopo, fuori da questo thread.</summary>
    private RawClipboard? ReadForeign(nuint time)
    {
        var targets = ConvertAtoms(_atoms.Targets, time);
        bool Offers(nuint target) => targets is null || targets.Contains(target);

        var sensitive = targets is not null && targets.Contains(_atoms.PasswordHint)
            && Convert(_atoms.PasswordHint, time) is { } hint
            && Encoding.UTF8.GetString(hint).Trim('\0', ' ', '\n').Equals("secret", StringComparison.OrdinalIgnoreCase);

        List<string>? paths = null;
        if (targets is not null && targets.Contains(_atoms.GnomeCopiedFiles) && Convert(_atoms.GnomeCopiedFiles, time) is { } gnome)
            paths = FileUris.ParseGnomeCopiedFiles(Encoding.UTF8.GetString(gnome));
        if (paths is not { Count: > 0 } && targets is not null && targets.Contains(_atoms.UriList) && Convert(_atoms.UriList, time) is { } uris)
            paths = FileUris.ParseUriList(Encoding.UTF8.GetString(uris));

        // Il testo si legge anche accanto ai file: serve se i file non sono leggibili.
        string? text = null;
        if (Offers(_atoms.Utf8String) && Convert(_atoms.Utf8String, time) is { } utf8)
            text = Encoding.UTF8.GetString(utf8);
        else if (targets is not null && targets.Contains(_atoms.TextPlainUtf8) && Convert(_atoms.TextPlainUtf8, time) is { } plain)
            text = Encoding.UTF8.GetString(plain);
        else if (targets is not null && targets.Contains(XA_STRING) && Convert(XA_STRING, time) is { } latin1)
            text = Encoding.Latin1.GetString(latin1);
        if (text is not null)
        {
            var end = text.IndexOf('\0');
            if (end >= 0)
                text = text[..end];
        }

        if (paths is { Count: > 0 } || text is not null)
            return new RawClipboard(Sensitive: sensitive, Paths: paths, Text: text);

        if (targets is not null && targets.Contains(_atoms.Png) && Convert(_atoms.Png, time) is { Length: > 0 } png)
            return new RawClipboard(Sensitive: sensitive, Png: png);

        return null;
    }

    private List<nuint>? ConvertAtoms(nuint target, nuint time)
    {
        var data = Convert(target, time, out var format);
        if (data is null || format != 32)
            return null;
        var count = data.Length / IntPtr.Size;
        var atoms = new List<nuint>(count);
        for (var i = 0; i < count; i++)
            atoms.Add(IntPtr.Size == 8 ? (nuint)BitConverter.ToUInt64(data, i * 8) : BitConverter.ToUInt32(data, i * 4));
        return atoms;
    }

    private byte[]? Convert(nuint target, nuint time) => Convert(target, time, out _);

    /// <summary>Chiede al proprietario un formato e aspetta la risposta, anche incrementale (INCR).</summary>
    private byte[]? Convert(nuint target, nuint time, out int format)
    {
        format = 0;
        XDeleteProperty(_display, _window, _atoms.ReadProperty);
        XConvertSelection(_display, _atoms.Clipboard, target, _atoms.ReadProperty, _window, time);
        var window = _window;
        var clipboard = _atoms.Clipboard;
        if (!WaitForEvent((in XEvent e) => e.type == SelectionNotify && e.selection.requestor == window
                && e.selection.selection == clipboard && e.selection.target == target, ReplyTimeoutMs, out var reply))
            return null;
        if (reply.selection.property == None)
            return null;

        var property = ReadProperty(_window, _atoms.ReadProperty, delete: true);
        if (property is null)
            return null;
        format = property.Value.Format;
        if (property.Value.Type != _atoms.Incr)
            return property.Value.Data;

        // INCR: il proprietario scrive un pezzo per volta, ogni volta che cancelliamo la proprietà.
        var readProperty = _atoms.ReadProperty;
        using var buffer = new MemoryStream();
        while (true)
        {
            if (!WaitForEvent((in XEvent e) => e.type == PropertyNotify && e.property.window == window
                    && e.property.atom == readProperty && e.property.state == PropertyNewValue, ReplyTimeoutMs, out _))
                return null;
            var chunk = ReadProperty(_window, _atoms.ReadProperty, delete: true);
            if (chunk is null)
                return null;
            format = chunk.Value.Format;
            if (chunk.Value.Data.Length == 0)
                return buffer.ToArray();
            if (buffer.Length + chunk.Value.Data.Length > MaxReadBytes)
                return null;
            buffer.Write(chunk.Value.Data);
        }
    }

    private unsafe (nuint Type, int Format, byte[] Data)? ReadProperty(nuint window, nuint property, bool delete)
    {
        var status = XGetWindowProperty(_display, window, property, 0, MaxReadBytes / 4, delete ? 1 : 0, None,
            out var type, out var format, out var count, out _, out var data);
        try
        {
            if (status != 0 || type == None)
                return null;
            var size = checked((int)((long)count * ElementSize(format)));
            return (type, format, size == 0 ? [] : new ReadOnlySpan<byte>((void*)data, size).ToArray());
        }
        finally
        {
            if (data != 0)
                XFree(data);
        }
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var raw in _events.Reader.ReadAllAsync())
        {
            try
            {
                if (ToArgs(raw) is { } args)
                    Changed?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: handler della clipboard fallito: {ex}");
            }
        }
    }

    private ClipboardChangedEventArgs? ToArgs(RawClipboard raw)
    {
        if (raw.Own is { } own)
        {
            _foreignSinceWrite = false;
            return new ClipboardChangedEventArgs(own, isOwnWrite: true);
        }

        var content = Convert(raw);
        if (content is null)
            return null;

        // Un gestore di clipboard (GPaste, Klipper...) può riprendersi la selezione con lo stesso
        // contenuto appena scritto da CUS: è ancora la nostra scrittura, non una copia dell'utente.
        var echo = !_foreignSinceWrite && IsEchoOfLastWrite(content);
        _foreignSinceWrite = true;
        return echo && _lastWritten is { } last
            ? new ClipboardChangedEventArgs(last, isOwnWrite: true)
            : new ClipboardChangedEventArgs(content with { IsMarkedSensitive = raw.Sensitive }, isOwnWrite: false);
    }

    private ClipboardContent? Convert(RawClipboard raw)
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
        return null;
    }

    private bool IsEchoOfLastWrite(ClipboardContent content) => (_lastWritten, content) switch
    {
        ({ Kind: ClipboardContentKind.Text } last, { Kind: ClipboardContentKind.Text }) => last.Text == content.Text,
        ({ Kind: ClipboardContentKind.Image } last, { Kind: ClipboardContentKind.Image }) =>
            last.ImagePng.AsSpan().SequenceEqual(content.ImagePng),
        ({ Kind: ClipboardContentKind.Files }, { Kind: ClipboardContentKind.Files, Files: { } files }) =>
            files.All(f => f.LocalPath is { } p && IsInsideCache(p)),
        _ => false,
    };

    private bool IsInsideCache(string path) =>
        Path.GetFullPath(path).StartsWith(_cacheDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // ---- Scrittura -----------------------------------------------------------------------------

    private void WriteOnLoop(ClipboardContent content, IRemoteFileSource? files)
    {
        var data = new Dictionary<nuint, byte[]>();
        FileOffer? fileOffer = null;
        switch (content.Kind)
        {
            case ClipboardContentKind.Text:
                var text = content.Text ?? "";
                var utf8 = Encoding.UTF8.GetBytes(text);
                data[_atoms.Utf8String] = utf8;
                data[_atoms.TextPlainUtf8] = utf8;
                data[_atoms.TextPlain] = utf8;
                data[XA_STRING] = Encoding.Latin1.GetBytes(text);
                break;
            case ClipboardContentKind.Image when content.ImagePng is { } png:
                data[_atoms.Png] = png;
                break;
            case ClipboardContentKind.Files:
                fileOffer = new FileOffer(content.Files!, files!);
                break;
        }

        // Un contenuto sensibile ricevuto resta sensibile: i gestori di clipboard non lo tengono in cronologia.
        if (content.IsMarkedSensitive)
            data[_atoms.PasswordHint] = "secret"u8.ToArray();

        _lastWritten = content;
        var time = ServerTime();
        var offer = new Offer(data, fileOffer, time);
        DropOffer();
        _offer = offer;
        XSetSelectionOwner(_display, _atoms.Clipboard, _window, time);
        if (XGetSelectionOwner(_display, _atoms.Clipboard) != _window)
        {
            _offer = null;
            throw new InvalidOperationException(Loc.T("clipboard.x11CannotOwnSelection"));
        }

        XFlush(_display);
    }

    /// <summary>Ora del server X, ottenuta con una modifica vuota a una proprietà: serve per possedere la selezione (ICCCM).</summary>
    private unsafe nuint ServerTime()
    {
        var property = _atoms.TimeProperty;
        var window = _window;
        XChangeProperty(_display, _window, property, XA_INTEGER, 8, PropModeAppend, null, 0);
        return WaitForEvent((in XEvent e) => e.type == PropertyNotify && e.property.window == window && e.property.atom == property,
            ReplyTimeoutMs, out var found)
            ? found.property.time
            : CurrentTime;
    }

    private void DropOffer()
    {
        var old = _offer;
        _offer = null;
        if (old?.Files is not { } files)
            return;

        foreach (var request in files.Pending)
            Notify(request, None);
        files.Pending.Clear();
        if (files.BatchDirectory is { } batch)
        {
            _ = Task.Delay(OldBatchLifetime).ContinueWith(_ => DeleteDirectory(batch), TaskScheduler.Default);
        }
        else if (files.Download is { } download)
        {
            // Download ancora in corso: la cartella si conosce solo alla fine.
            _ = download.ContinueWith(
                t => { if (t.IsCompletedSuccessfully && t.Result is { } done) DeleteDirectory(done.Directory); },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void OnSelectionRequest(XSelectionRequestEvent request)
    {
        // Client obsoleti mandano property None: si usa il target come nome della proprietà.
        if (request.property == None)
            request.property = request.target;

        var offer = _offer;
        if (request.selection != _atoms.Clipboard || offer is null || request.owner != _window)
        {
            Notify(request, None);
            return;
        }

        if (request.target == _atoms.Targets)
        {
            var targets = new List<nuint> { _atoms.Targets, _atoms.Timestamp };
            targets.AddRange(offer.Data.Keys);
            if (offer.Files is not null)
                targets.AddRange([_atoms.GnomeCopiedFiles, _atoms.UriList]);
            SetAtomProperty(request.requestor, request.property, XA_ATOM, [.. targets]);
            Notify(request, request.property);
            return;
        }

        if (request.target == _atoms.Timestamp)
        {
            SetAtomProperty(request.requestor, request.property, XA_INTEGER, [offer.Time]);
            Notify(request, request.property);
            return;
        }

        if (offer.Data.TryGetValue(request.target, out var bytes))
        {
            SendData(request, bytes);
            return;
        }

        if (offer.Files is { } files && (request.target == _atoms.UriList || request.target == _atoms.GnomeCopiedFiles))
        {
            if (files.Ready is { } ready)
            {
                SendData(request, FileListBytes(request.target, ready));
                return;
            }

            // Si risponde a download finito: chi incolla resta in attesa della SelectionNotify.
            files.Pending.Add(request);
            files.Download ??= StartDownload(offer, files);
            return;
        }

        Notify(request, None);
    }

    private byte[] FileListBytes(nuint target, IReadOnlyList<string> paths) => Encoding.UTF8.GetBytes(
        target == _atoms.GnomeCopiedFiles ? FileUris.FormatGnomeCopiedFiles(paths) : FileUris.FormatUriList(paths));

    private Task<DownloadedBatch?> StartDownload(Offer offer, FileOffer files)
    {
        var download = Task.Run(() => DownloadAsync(files));
        download.ContinueWith(t => InvokeAsync(() =>
        {
            var batch = t.IsCompletedSuccessfully ? t.Result : null;
            if (t.Exception is { } ex)
                System.Diagnostics.Trace.WriteLine($"CUS: download dei file ricevuti fallito: {ex.InnerException?.Message ?? ex.Message}");

            var current = ReferenceEquals(_offer, offer);
            if (batch is not null && current)
            {
                files.Ready = batch.TopLevelPaths;
                files.BatchDirectory = batch.Directory;
            }
            else
            {
                // Fallito (si riproverà al prossimo incolla) oppure offerta già sostituita.
                files.Download = null;
            }

            foreach (var request in files.Pending)
            {
                if (files.Ready is { } ready)
                    SendData(request, FileListBytes(request.target, ready));
                else
                    Notify(request, None);
            }

            files.Pending.Clear();
            XFlush(_display);
            return true;
        }), TaskScheduler.Default);
        return download;
    }

    /// <summary>Scarica i file dal PC d'origine in una cartella nuova della cache (0700), come farebbe un incolla.</summary>
    private async Task<DownloadedBatch?> DownloadAsync(FileOffer offer)
    {
        var directory = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var root = directory + Path.DirectorySeparatorChar;
        try
        {
            for (var i = 0; i < offer.Files.Count; i++)
            {
                var file = offer.Files[i];
                var target = Path.GetFullPath(Path.Combine(root, file.Name));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new InvalidDataException(Loc.T("clipboard.invalidFileListReceived", file.Name));
                if (file.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                for (long position = 0; position < file.SizeBytes;)
                {
                    var count = (int)Math.Min(1024 * 1024, file.SizeBytes - position);
                    var chunk = await offer.Source.ReadAsync(i, position, count).ConfigureAwait(false);
                    if (chunk.Length > count)
                        chunk = chunk[..count];
                    if (chunk.Length == 0)
                        throw new RemoteFileUnavailableException(Loc.T("common.theSourcePcReturnedAnEmpty"));
                    await output.WriteAsync(chunk).ConfigureAwait(false);
                    position += chunk.Length;
                }
            }
        }
        catch
        {
            DeleteDirectory(directory);
            throw;
        }

        return new DownloadedBatch(directory, [.. FileUris.TopLevelNames(offer.Files).Select(n => Path.Combine(directory, n))]);
    }

    private unsafe void SendData(XSelectionRequestEvent request, byte[] bytes)
    {
        if (bytes.Length <= _maxChunk)
        {
            fixed (byte* p = bytes)
                XChangeProperty(_display, request.requestor, request.property, request.target, 8, PropModeReplace, p, bytes.Length);
            Notify(request, request.property);
            return;
        }

        // INCR: si annuncia la dimensione, poi un pezzo ogni volta che chi chiede cancella la proprietà.
        XSelectInput(_display, request.requestor, PropertyChangeMask);
        SetAtomProperty(request.requestor, request.property, _atoms.Incr, [(nuint)bytes.Length]);
        _incr.Add(new IncrTransfer(request.requestor, request.property, request.target, bytes) { LastActivity = DateTime.UtcNow });
        Notify(request, request.property);
    }

    private unsafe void OnRequestorPropertyDeleted(nuint window, nuint property)
    {
        var transfer = _incr.Find(t => t.Requestor == window && t.Property == property);
        if (transfer is null)
            return;

        var count = Math.Min(_maxChunk, transfer.Data.Length - transfer.Offset);
        fixed (byte* p = transfer.Data)
            XChangeProperty(_display, window, property, transfer.Type, 8, PropModeReplace, p + transfer.Offset, count);
        transfer.Offset += count;
        transfer.LastActivity = DateTime.UtcNow;
        if (count == 0)
            EndIncr(transfer); // Pezzo vuoto: fine del trasferimento.
    }

    private void DropStaleIncrTransfers()
    {
        var now = DateTime.UtcNow;
        foreach (var transfer in _incr.Where(t => now - t.LastActivity > IncrStaleAfter).ToList())
            EndIncr(transfer);
    }

    private void EndIncr(IncrTransfer transfer)
    {
        _incr.Remove(transfer);
        if (!_incr.Exists(t => t.Requestor == transfer.Requestor))
            XSelectInput(_display, transfer.Requestor, NoEventMask);
    }

    private unsafe void SetAtomProperty(nuint window, nuint property, nuint type, nuint[] values)
    {
        // Nel formato 32 Xlib vuole un array di "long" del C, cioè nuint.
        fixed (nuint* p = values)
            XChangeProperty(_display, window, property, type, 32, PropModeReplace, (byte*)p, values.Length);
    }

    private unsafe void Notify(XSelectionRequestEvent request, nuint property)
    {
        var reply = new XEvent
        {
            selection = new XSelectionEvent
            {
                type = SelectionNotify,
                send_event = 1,
                display = _display,
                requestor = request.requestor,
                selection = request.selection,
                target = request.target,
                property = property,
                time = request.time,
            },
        };
        XSendEvent(_display, request.requestor, 0, NoEventMask, &reply);
    }

    // ---- Cache dei file ricevuti ---------------------------------------------------------------

    private void DeleteCache()
    {
        if (Directory.Exists(_cacheDirectory))
            DeleteDirectory(_cacheDirectory);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: impossibile eliminare {directory}: {ex.Message}");
        }
    }

    // ---- Tipi di supporto ----------------------------------------------------------------------

    private sealed class Atoms(nint display)
    {
        public nuint Clipboard { get; } = XInternAtom(display, "CLIPBOARD", 0);
        public nuint Targets { get; } = XInternAtom(display, "TARGETS", 0);
        public nuint Timestamp { get; } = XInternAtom(display, "TIMESTAMP", 0);
        public nuint Incr { get; } = XInternAtom(display, "INCR", 0);
        public nuint Utf8String { get; } = XInternAtom(display, "UTF8_STRING", 0);
        public nuint TextPlainUtf8 { get; } = XInternAtom(display, "text/plain;charset=utf-8", 0);
        public nuint TextPlain { get; } = XInternAtom(display, "text/plain", 0);
        public nuint Png { get; } = XInternAtom(display, "image/png", 0);
        public nuint UriList { get; } = XInternAtom(display, FileUris.UriListTarget, 0);
        public nuint GnomeCopiedFiles { get; } = XInternAtom(display, FileUris.GnomeCopiedFilesTarget, 0);
        public nuint PasswordHint { get; } = XInternAtom(display, PasswordHintTarget, 0);
        public nuint ReadProperty { get; } = XInternAtom(display, "CUS_SELECTION", 0);
        public nuint TimeProperty { get; } = XInternAtom(display, "CUS_TIMESTAMP", 0);
    }

    /// <summary>Ciò che CUS offre come proprietario della clipboard.</summary>
    private sealed record Offer(Dictionary<nuint, byte[]> Data, FileOffer? Files, nuint Time);

    private sealed class FileOffer(IReadOnlyList<ClipboardFileInfo> files, IRemoteFileSource source)
    {
        public IReadOnlyList<ClipboardFileInfo> Files { get; } = files;
        public IRemoteFileSource Source { get; } = source;
        public List<XSelectionRequestEvent> Pending { get; } = [];
        public Task<DownloadedBatch?>? Download { get; set; }
        public IReadOnlyList<string>? Ready { get; set; }
        public string? BatchDirectory { get; set; }
    }

    private sealed record DownloadedBatch(string Directory, IReadOnlyList<string> TopLevelPaths);

    private sealed class IncrTransfer(nuint requestor, nuint property, nuint type, byte[] data)
    {
        public nuint Requestor { get; } = requestor;
        public nuint Property { get; } = property;
        public nuint Type { get; } = type;
        public byte[] Data { get; } = data;
        public int Offset { get; set; }
        public DateTime LastActivity { get; set; }
    }

    /// <summary>Dati letti con il thread X; <see cref="Own"/> se il proprietario è CUS.</summary>
    private sealed record RawClipboard(
        bool Sensitive = false,
        List<string>? Paths = null,
        string? Text = null,
        byte[]? Png = null,
        ClipboardContent? Own = null);
}
