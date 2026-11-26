using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cus.Platform.Linux.Interop;

// Tipi Xlib su LP64 e ILP32: Window, Atom e Time sono "unsigned long" (nuint), i puntatori nint.
// Le struct seguono l'ordine dei campi di Xlib.h: con LayoutKind.Sequential l'allineamento è lo stesso del C.

[StructLayout(LayoutKind.Sequential)]
internal struct XAnyEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XSelectionRequestEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint owner;
    public nuint requestor;
    public nuint selection;
    public nuint target;
    public nuint property;
    public nuint time;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XSelectionEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint requestor;
    public nuint selection;
    public nuint target;
    public nuint property;
    public nuint time;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XSelectionClearEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
    public nuint selection;
    public nuint time;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XPropertyEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
    public nuint atom;
    public nuint time;
    public int state;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XFixesSelectionNotifyEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
    public int subtype;
    public nuint owner;
    public nuint selection;
    public nuint timestamp;
    public nuint selection_timestamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XClientMessageEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
    public nuint message_type;
    public int format;
    public nint l0;
    public nint l1;
    public nint l2;
    public nint l3;
    public nint l4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XWMHints
{
    public nint flags;
    public int input;
    public int initial_state;
    public nuint icon_pixmap;
    public nuint icon_window;
    public int icon_x;
    public int icon_y;
    public nuint icon_mask;
    public nuint window_group;
}

/// <summary>XEvent è un'unione di 24 long: 192 byte su 64 bit. Qui con spazio sufficiente anche per 32 bit.</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] public int type;
    [FieldOffset(0)] public XAnyEvent any;
    [FieldOffset(0)] public XSelectionRequestEvent selectionRequest;
    [FieldOffset(0)] public XSelectionEvent selection;
    [FieldOffset(0)] public XSelectionClearEvent selectionClear;
    [FieldOffset(0)] public XPropertyEvent property;
    [FieldOffset(0)] public XFixesSelectionNotifyEvent fixesSelection;
    [FieldOffset(0)] public XClientMessageEvent clientMessage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int fd;
    public short events;
    public short revents;
}

internal static unsafe partial class Xlib
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXfixes = "libXfixes.so.3";
    private const string LibC = "libc";

    public const int PropertyNotify = 28;
    public const int SelectionClear = 29;
    public const int SelectionRequest = 30;
    public const int SelectionNotify = 31;
    public const int ClientMessage = 33;

    public const int PropertyNewValue = 0;
    public const int PropertyDelete = 1;

    public const int PropModeReplace = 0;
    public const int PropModeAppend = 2;

    public const nint NoEventMask = 0;
    public const nint PropertyChangeMask = 1 << 22;
    public const nint SubstructureRedirectMask = 1 << 20;
    public const nint SubstructureNotifyMask = 1 << 19;

    public const nint InputHint = 1;

    public const nuint None = 0;
    public const nuint CurrentTime = 0;
    public const nuint XA_ATOM = 4;
    public const nuint XA_CARDINAL = 6;
    public const nuint XA_INTEGER = 19;
    public const nuint XA_STRING = 31;

    public const ulong XFixesSetSelectionOwnerNotifyMask = 1;
    public const int XFixesSelectionNotify = 0;

    public const short POLLIN = 1;

    /// <summary>
    /// Rende Xlib thread-safe. Da libX11 1.8 è implicita; prima va chiamata prima di ogni altra funzione
    /// Xlib del processo. CUS apre il proprio display prima che parta Avalonia, quindi la chiama per primo.
    /// </summary>
    [LibraryImport(LibX11)]
    public static partial int XInitThreads();

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XOpenDisplay(string? name);

    [LibraryImport(LibX11)]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(LibX11)]
    public static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11)]
    public static partial nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);

    [LibraryImport(LibX11)]
    public static partial int XDestroyWindow(nint display, nuint window);

    [LibraryImport(LibX11)]
    public static partial int XSelectInput(nint display, nuint window, nint eventMask);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(LibX11)]
    public static partial nint XGetAtomName(nint display, nuint atom);

    [LibraryImport(LibX11)]
    public static partial int XSetSelectionOwner(nint display, nuint selection, nuint owner, nuint time);

    [LibraryImport(LibX11)]
    public static partial nuint XGetSelectionOwner(nint display, nuint selection);

    [LibraryImport(LibX11)]
    public static partial int XConvertSelection(nint display, nuint selection, nuint target, nuint property, nuint requestor, nuint time);

    [LibraryImport(LibX11)]
    public static partial int XGetWindowProperty(
        nint display, nuint window, nuint property, nint longOffset, nint longLength, int delete, nuint reqType,
        out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out nint data);

    [LibraryImport(LibX11)]
    public static partial int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, byte* data, int elementCount);

    [LibraryImport(LibX11)]
    public static partial int XDeleteProperty(nint display, nuint window, nuint property);

    [LibraryImport(LibX11)]
    public static partial int XSendEvent(nint display, nuint window, int propagate, nint eventMask, XEvent* evt);

    [LibraryImport(LibX11)]
    public static partial int XFlush(nint display);

    [LibraryImport(LibX11)]
    public static partial int XPending(nint display);

    [LibraryImport(LibX11)]
    public static partial int XNextEvent(nint display, XEvent* evt);

    [LibraryImport(LibX11)]
    public static partial int XConnectionNumber(nint display);

    [LibraryImport(LibX11)]
    public static partial int XFree(nint data);

    [LibraryImport(LibX11)]
    public static partial nint XMaxRequestSize(nint display);

    [LibraryImport(LibX11)]
    public static partial nint XExtendedMaxRequestSize(nint display);

    [LibraryImport(LibX11)]
    public static partial nint XSetErrorHandler(nint handler);

    [LibraryImport(LibX11)]
    public static partial nint XAllocWMHints();

    [LibraryImport(LibX11)]
    public static partial int XSetWMHints(nint display, nuint window, nint hints);

    [LibraryImport(LibXfixes)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XFixesQueryExtension(nint display, out int eventBase, out int errorBase);

    [LibraryImport(LibXfixes)]
    public static partial void XFixesSelectSelectionInput(nint display, nuint window, nuint selection, ulong eventMask);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial int pipe(int* fds);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport(LibC, SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport(LibC)]
    public static partial int close(int fd);

    /// <summary>
    /// Il gestore predefinito di Xlib chiude il processo a ogni errore (per esempio BadWindow quando chi
    /// chiedeva la clipboard ha già chiuso la finestra). Questo registra e continua. Avalonia, quando
    /// parte, installa il proprio gestore, anch'esso non fatale.
    /// </summary>
    public static void InstallNonFatalErrorHandler()
    {
        delegate* unmanaged[Cdecl]<nint, nint, int> handler = &OnError;
        XSetErrorHandler((nint)handler);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, nint errorEvent)
    {
        // XErrorEvent: type, display, resourceid, serial, error_code (unsigned char)...
        var code = *(byte*)(errorEvent + IntPtr.Size * 4);
        System.Diagnostics.Trace.WriteLine($"CUS: errore X11 ignorato (codice {code}).");
        return 0;
    }

    /// <summary>Dimensione in byte di un elemento di una proprietà: nel formato 32 Xlib usa i "long" del C.</summary>
    public static int ElementSize(int format) => format switch
    {
        8 => 1,
        16 => 2,
        32 => IntPtr.Size,
        _ => 1,
    };
}
