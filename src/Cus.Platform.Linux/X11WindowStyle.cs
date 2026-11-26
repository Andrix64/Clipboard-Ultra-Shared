using System.Runtime.Versioning;
using Cus.Platform.Linux.Interop;

namespace Cus.Platform.Linux;

/// <summary>Ritocchi X11 alle finestre dell'indicatore e delle notifiche.</summary>
[SupportedOSPlatform("linux")]
public static class X11WindowStyle
{
    private static readonly Lock s_gate = new();
    private static nint s_display;

    /// <summary>
    /// WM_HINTS con input = False (modello "No Input" dell'ICCCM): il window manager non dà il focus
    /// alla finestra, nemmeno al clic, e chi sta scrivendo altrove non viene interrotto. L'assenza
    /// dalla barra delle applicazioni la gestisce già Avalonia (ShowInTaskbar = false).
    /// </summary>
    /// <param name="window">L'id X11 della finestra (TryGetPlatformHandle().Handle su X11).</param>
    public static unsafe void MakeNonActivating(nint window)
    {
        if (window == 0)
            return;

        lock (s_gate)
        {
            try
            {
                if (s_display == 0)
                {
                    Xlib.XInitThreads();
                    s_display = Xlib.XOpenDisplay(null);
                }
                if (s_display == 0)
                    return;

                var hints = (XWMHints*)Xlib.XAllocWMHints();
                if (hints == null)
                    return;
                hints->flags = Xlib.InputHint;
                hints->input = 0;
                Xlib.XSetWMHints(s_display, (nuint)window, (nint)hints);
                Xlib.XFree((nint)hints);
                Xlib.XFlush(s_display);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Senza libX11 (sessione Wayland pura) la finestra resta com'è: nessun danno.
            }
        }
    }
}
