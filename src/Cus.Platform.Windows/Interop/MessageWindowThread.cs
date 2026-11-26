using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cus.Platform.Windows.Interop;

/// <summary>
/// Finestra di soli messaggi su un thread STA dedicato, con OLE inizializzato. Riceve
/// WM_CLIPBOARDUPDATE ed esegue le operazioni sulla clipboard che Windows vuole sul thread
/// proprietario (scritture, OleSetClipboard, rendering ritardato).
/// </summary>
internal sealed unsafe class MessageWindowThread : IDisposable
{
    private const uint WM_INVOKE = Native.WM_APP + 1;
    private const string WindowClassName = "CUS.MessageWindow";

    private static readonly ConcurrentDictionary<nint, MessageWindowThread> Windows = new();
    private static readonly Lazy<bool> ClassRegistered = new(RegisterClass);

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private volatile bool _exited;

    public MessageWindowThread()
    {
        _ = ClassRegistered.Value;
        _thread = new Thread(Run) { IsBackground = true, Name = "CUS clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    public nint Handle { get; private set; }

    /// <summary>Messaggi ricevuti dalla finestra (sul thread della finestra).</summary>
    public event Action<uint>? MessageReceived;

    /// <summary>
    /// Esegue <paramref name="action"/> sul thread della finestra. Dopo <see cref="Dispose"/> (o se
    /// il ciclo dei messaggi è terminato) il task fallisce subito con <see cref="ObjectDisposedException"/>:
    /// non resta mai in attesa per sempre.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }

        if (Volatile.Read(ref _disposed) != 0 || _exited)
            return Task.FromException<T>(new ObjectDisposedException(nameof(MessageWindowThread)));

        _queue.Enqueue(new WorkItem(
            () =>
            {
                try { tcs.TrySetResult(action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            ex => tcs.TrySetException(ex)));

        // Il ciclo può essere terminato tra il controllo e l'accodamento, oppure la finestra può
        // essere già distrutta (PostMessage fallisce): il lavoro in coda non verrebbe mai eseguito.
        if (_exited || !Native.PostMessageW(Handle, WM_INVOKE, 0, 0))
            FailPending();
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Native.PostMessageW(Handle, Native.WM_CLOSE, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void FailPending()
    {
        while (_queue.TryDequeue(out var item))
            item.Fail(new ObjectDisposedException(nameof(MessageWindowThread)));
    }

    private void Run()
    {
        Native.OleInitialize(0);
        try
        {
            Handle = Native.CreateWindowExW(0, WindowClassName, "CUS", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, Native.GetModuleHandleW(null), 0);
            if (Handle == 0)
            {
                _ready.SetException(new InvalidOperationException(Loc.T("clipboard.createwindowexFailed", Marshal.GetLastPInvokeError())));
                return;
            }

            Windows[Handle] = this;
            _ready.SetResult();

            Native.MSG msg;
            while (Native.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(&msg);
                Native.DispatchMessageW(&msg);
            }
        }
        finally
        {
            // Il lavoro accodato dopo WM_CLOSE (o mai arrivato alla finestra) fallisce invece di restare appeso.
            _exited = true;
            FailPending();
            Native.OleUninitialize();
        }
    }

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_INVOKE:
                while (_queue.TryDequeue(out var item))
                    item.Run();
                return 0;
            case Native.WM_CLOSE:
                Native.DestroyWindow(hwnd);
                return 0;
            case Native.WM_DESTROY:
                Windows.TryRemove(hwnd, out _);
                Native.PostQuitMessage(0);
                return 0;
            default:
                MessageReceived?.Invoke(msg);
                return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            return Windows.TryGetValue(hwnd, out var window)
                ? window.HandleMessage(hwnd, msg, wParam, lParam)
                : Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Un'eccezione che attraversa il confine nativo chiuderebbe il processo.
            System.Diagnostics.Trace.WriteLine($"CUS WndProc: {ex}");
            return 0;
        }
    }

    private static bool RegisterClass()
    {
        fixed (char* name = WindowClassName)
        {
            var wndClass = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = name,
            };
            if (Native.RegisterClassExW(&wndClass) == 0)
                throw new InvalidOperationException(Loc.T("clipboard.registerclassexFailed", Marshal.GetLastPInvokeError()));
        }

        return true;
    }

    private readonly record struct WorkItem(Action Run, Action<Exception> Fail);
}
