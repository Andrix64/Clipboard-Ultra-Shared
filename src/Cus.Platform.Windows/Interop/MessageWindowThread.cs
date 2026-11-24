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

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

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

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }

        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!Native.PostMessageW(Handle, WM_INVOKE, 0, 0))
            tcs.TrySetException(new ObjectDisposedException(nameof(MessageWindowThread)));
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

    private void Run()
    {
        Native.OleInitialize(0);
        try
        {
            Handle = Native.CreateWindowExW(0, WindowClassName, "CUS", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, Native.GetModuleHandleW(null), 0);
            if (Handle == 0)
            {
                _ready.SetException(new InvalidOperationException($"CreateWindowEx fallita ({Marshal.GetLastPInvokeError()})."));
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
            Native.OleUninitialize();
        }
    }

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_INVOKE:
                while (_queue.TryDequeue(out var action))
                    action();
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
                throw new InvalidOperationException($"RegisterClassEx fallita ({Marshal.GetLastPInvokeError()}).");
        }

        return true;
    }
}
