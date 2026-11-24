using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Cus.App.Windows;

/// <summary>Ritocchi Win32 alle finestre dell'indicatore e delle notifiche.</summary>
internal static partial class NativeWindowStyle
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// La finestra non prende mai il focus (nemmeno al clic) e non compare in Alt+Tab:
    /// chi sta scrivendo altrove non viene interrotto.
    /// </summary>
    public static void MakeNonActivating(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
            return;

        if (IntPtr.Size == 8)
        {
            var style = GetWindowLongPtrW(handle, GWL_EXSTYLE);
            SetWindowLongPtrW(handle, GWL_EXSTYLE, (nint)((long)style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }
        else
        {
            var style = GetWindowLongW(handle, GWL_EXSTYLE);
            SetWindowLongW(handle, GWL_EXSTYLE, (int)(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(nint hwnd, int index, int value);
}
