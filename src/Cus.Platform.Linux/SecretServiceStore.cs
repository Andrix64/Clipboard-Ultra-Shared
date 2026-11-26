using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cus.Core.Abstractions;
using Cus.Platform.Linux.Interop;

namespace Cus.Platform.Linux;

/// <summary>
/// Segreti nel Secret Service dell'utente (GNOME Keyring, KWallet, KeePassXC), tramite libsecret: li
/// protegge il portachiavi della sessione, sbloccato al login. Ogni segreto è identificato dalla cartella
/// dati del profilo e dal nome, così profili diversi hanno chiavi diverse. Nessuna copia su disco: se il
/// Secret Service manca, l'apertura del profilo fallisce con un messaggio chiaro invece di salvare la
/// chiave in chiaro accanto al DB.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class SecretServiceStore(string dataDirectory) : ISecretStore
{
    private const string SchemaName = "io.github.cus.Secret";

    private readonly string _profile = Path.GetFullPath(dataDirectory);

    public byte[]? Load(string name)
    {
        Validate(name);
        nint password;
        nint error;
        try
        {
            using var native = new NativeArguments(_profile, name);
            password = LibSecret.secret_password_lookupv_sync(native.Schema, native.Attributes, 0, out error);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(Loc.T("secrets.libsecretMissing"), ex);
        }

        if (error != 0)
            throw new InvalidOperationException(Loc.T("secrets.secretServiceFailed", LibSecret.TakeErrorMessage(error)));
        if (password == 0)
            return null;

        try
        {
            return Convert.FromBase64String(Marshal.PtrToStringUTF8(password) ?? "");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(Loc.T("secrets.secretServiceFailed", "invalid stored value"));
        }
        finally
        {
            LibSecret.secret_password_free(password);
        }
    }

    public void Save(string name, byte[] secret)
    {
        Validate(name);
        bool ok;
        nint error;
        try
        {
            using var native = new NativeArguments(_profile, name);
            ok = LibSecret.secret_password_storev_sync(
                native.Schema, native.Attributes, null, $"CUS ({name}) — {_profile}", Convert.ToBase64String(secret), 0, out error);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(Loc.T("secrets.libsecretMissing"), ex);
        }

        if (!ok)
            throw new InvalidOperationException(Loc.T("secrets.secretServiceFailed", LibSecret.TakeErrorMessage(error)));
    }

    private static void Validate(string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException(Loc.T("secrets.invalidSecretName", name), nameof(name));
    }

    /// <summary>
    /// Schema e attributi in memoria nativa, liberati insieme.
    /// SecretSchema: { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32];
    /// gint reserved; gpointer reserved1..7; } con SecretSchemaAttribute = { const gchar *name; enum type; }.
    /// Gli attributi sono una GHashTable stringa → stringa; le stringhe restano di proprietà nostra.
    /// </summary>
    private sealed class NativeArguments : IDisposable
    {
        private static readonly string[] AttributeNames = ["app", "profile", "name"];
        private readonly List<nint> _strings = [];

        public NativeArguments(string profile, string name)
        {
            var pointerSize = IntPtr.Size;
            var attributeSize = pointerSize * 2; // nome + enum, con il padding dell'allineamento
            var size = pointerSize * 2 + attributeSize * 32 + pointerSize * 8;
            Schema = (nint)NativeMemory.AllocZeroed((nuint)size);
            *(nint*)Schema = Utf8(SchemaName);
            // flags = SECRET_SCHEMA_NONE (0), già azzerato.
            for (var i = 0; i < AttributeNames.Length; i++)
            {
                var attribute = Schema + pointerSize * 2 + attributeSize * i;
                *(nint*)attribute = Utf8(AttributeNames[i]);
                // type = SECRET_SCHEMA_ATTRIBUTE_STRING (0), già azzerato.
            }

            var (hash, equal) = LibSecret.StringHashFunctions();
            Attributes = LibSecret.g_hash_table_new(hash, equal);
            LibSecret.g_hash_table_insert(Attributes, Utf8("app"), Utf8("cus"));
            LibSecret.g_hash_table_insert(Attributes, Utf8("profile"), Utf8(profile));
            LibSecret.g_hash_table_insert(Attributes, Utf8("name"), Utf8(name));
        }

        public nint Schema { get; }

        public nint Attributes { get; }

        public void Dispose()
        {
            if (Attributes != 0)
                LibSecret.g_hash_table_unref(Attributes);
            NativeMemory.Free((void*)Schema);
            foreach (var s in _strings)
                Marshal.FreeCoTaskMem(s);
        }

        private nint Utf8(string value)
        {
            var p = Marshal.StringToCoTaskMemUTF8(value);
            _strings.Add(p);
            return p;
        }
    }
}
