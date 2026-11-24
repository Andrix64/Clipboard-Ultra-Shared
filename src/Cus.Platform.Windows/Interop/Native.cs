using System.Runtime.InteropServices;

namespace Cus.Platform.Windows.Interop;

internal static unsafe partial class Native
{
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_APP = 0x8000;

    public const uint CF_TEXT = 1;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;

    public const uint GMEM_MOVEABLE = 0x0002;
    public static readonly nint HWND_MESSAGE = -3;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public int cbData;
        public nint pbData;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wndClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalFree(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint handle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint DragQueryFileW(nint hDrop, uint index, char* buffer, uint size);

    [LibraryImport("ole32.dll")]
    public static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    public static partial void OleUninitialize();

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptProtectData(
        DATA_BLOB* dataIn, string? description, DATA_BLOB* entropy, nint reserved, nint prompt, int flags, DATA_BLOB* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptUnprotectData(
        DATA_BLOB* dataIn, nint description, DATA_BLOB* entropy, nint reserved, nint prompt, int flags, DATA_BLOB* dataOut);

    /// <summary>Copia byte in un HGLOBAL spostabile, da passare a SetClipboardData o STGMEDIUM.</summary>
    public static nint ToHGlobal(ReadOnlySpan<byte> data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (handle == 0)
            throw new OutOfMemoryException("GlobalAlloc fallita.");
        var pointer = GlobalLock(handle);
        data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        GlobalUnlock(handle);
        return handle;
    }

    /// <summary>Legge il contenuto di un HGLOBAL.</summary>
    public static byte[]? FromHGlobal(nint handle)
    {
        if (handle == 0)
            return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0)
            return null;
        try
        {
            var size = checked((int)GlobalSize(handle));
            return new ReadOnlySpan<byte>((void*)pointer, size).ToArray();
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}

/// <summary>Le funzioni OLE che passano interfacce COM usano il marshalling classico.</summary>
internal static class OleNative
{
    [DllImport("ole32.dll")]
    public static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject? dataObject);

    [DllImport("ole32.dll")]
    public static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject);
}
