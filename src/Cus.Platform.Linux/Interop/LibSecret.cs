using System.Runtime.InteropServices;

namespace Cus.Platform.Linux.Interop;

/// <summary>
/// libsecret (client del Secret Service: GNOME Keyring, KWallet ≥ 5.97, KeePassXC). Si usano le varianti
/// "v" con una GHashTable di attributi: le altre sono variadiche e non si chiamano bene da .NET.
/// </summary>
internal static unsafe partial class LibSecret
{
    private const string Library = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool secret_password_storev_sync(
        nint schema, nint attributes, string? collection, string label, string password, nint cancellable, out nint error);

    [LibraryImport(Library)]
    public static partial nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, out nint error);

    /// <summary>Azzera e libera una password restituita da libsecret.</summary>
    [LibraryImport(Library)]
    public static partial void secret_password_free(nint password);

    [LibraryImport(GLib)]
    public static partial nint g_hash_table_new(nint hashFunc, nint equalFunc);

    [LibraryImport(GLib)]
    public static partial void g_hash_table_insert(nint table, nint key, nint value);

    [LibraryImport(GLib)]
    public static partial void g_hash_table_unref(nint table);

    [LibraryImport(GLib)]
    public static partial void g_error_free(nint error);

    /// <summary>Puntatori a g_str_hash e g_str_equal, per una GHashTable con chiavi stringa.</summary>
    public static (nint Hash, nint Equal) StringHashFunctions()
    {
        var glib = NativeLibrary.Load(GLib);
        return (NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
    }

    /// <summary>Messaggio di un GError (struct { GQuark domain; gint code; gchar *message; }), poi lo libera.</summary>
    public static string TakeErrorMessage(nint error)
    {
        if (error == 0)
            return "";
        var message = Marshal.PtrToStringUTF8(*(nint*)(error + 8)) ?? "";
        g_error_free(error);
        return message;
    }
}
