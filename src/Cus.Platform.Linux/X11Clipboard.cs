using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
public sealed partial class X11Clipboard : IClipboardMonitor, IClipboardWriter
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
            // Da qui nessuno serve più la coda: le chiamate successive devono fallire, non restare appese.
            _stopping = true;
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
        // Il thread può essersi fermato tra il controllo iniziale e l'accodamento: allora la coda
        // non la svuota più nessuno e il lavoro si fa fallire qui.
        if (_stopping)
        {
            while (_work.TryDequeue(out var orphan))
                orphan();
        }

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

    /// <summary>Un evento che fa fallire il suo gestore non deve fermare il thread della clipboard.</summary>
    private void Handle(in XEvent e)
    {
        try
        {
            HandleEvent(in e);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: evento X11 {e.type} non gestito: {ex}");
        }
    }

    private void HandleEvent(in XEvent e)
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
}
