using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Cus.Core.Abstractions;
using Cus.Platform.Windows.Interop;

namespace Cus.Platform.Windows;

/// <summary>
/// Segreti protetti con DPAPI nello scope dell'utente Windows corrente: solo lo stesso utente,
/// sullo stesso PC, può decifrarli. Un file per segreto in <c>&lt;cartella dati&gt;/secrets</c>.
/// Ogni segreto usa un'entropia propria dell'app e del nome: un altro programma dello stesso utente
/// che chiama CryptUnprotectData sul file senza conoscerla non lo decifra.
/// </summary>
public sealed unsafe class DpapiSecretStore(string dataDirectory) : ISecretStore
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const string EntropyPrefix = "CUS/secret-store/v1/";

    private readonly string _directory = Path.Combine(dataDirectory, "secrets");

    public byte[]? Load(string name)
    {
        var path = PathFor(name);
        DeleteOrphanTemp(path);
        if (!File.Exists(path))
            return null;

        var stored = File.ReadAllBytes(path);
        var entropy = EntropyFor(name);
        try
        {
            return Unprotect(stored, entropy);
        }
        catch (Win32Exception withEntropy)
        {
            // Installazioni precedenti salvavano senza entropia: si legge il vecchio formato e si
            // risalva subito in quello nuovo, così il database esistente continua ad aprirsi.
            byte[] secret;
            try
            {
                secret = Unprotect(stored);
            }
            catch (Win32Exception)
            {
                throw withEntropy;
            }

            try
            {
                Save(name, secret);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                // Il segreto resta leggibile nel vecchio formato: si riproverà al prossimo avvio.
                System.Diagnostics.Trace.WriteLine($"CUS: migrazione del segreto '{name}' non riuscita: {ex.Message}");
            }

            return secret;
        }
    }

    public void Save(string name, byte[] secret)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(name);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, Protect(secret, EntropyFor(name)));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            DeleteOrphanTemp(path);
        }
    }

    /// <summary>Entropia aggiuntiva DPAPI: legata all'app, alla versione del formato e al nome del segreto.</summary>
    internal static byte[] EntropyFor(string name) => Encoding.UTF8.GetBytes(EntropyPrefix + name);

    internal static byte[] Protect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy = default) => Transform(data, entropy, protect: true);

    internal static byte[] Unprotect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy = default) => Transform(data, entropy, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropyBytes = entropy)
        {
            var inBlob = new Native.DATA_BLOB { cbData = data.Length, pbData = (nint)input };
            var entropyBlob = new Native.DATA_BLOB { cbData = entropy.Length, pbData = (nint)entropyBytes };
            var entropyPointer = entropy.IsEmpty ? null : &entropyBlob;
            var outBlob = default(Native.DATA_BLOB);
            var ok = protect
                ? Native.CryptProtectData(&inBlob, "CUS", entropyPointer, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, &outBlob)
                : Native.CryptUnprotectData(&inBlob, 0, entropyPointer, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, &outBlob);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), protect ? Loc.T("secrets.cryptprotectdataFailed") : Loc.T("secrets.cryptunprotectdataFailed"));

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.pbData, outBlob.cbData).ToArray();
            }
            finally
            {
                // Dopo CryptUnprotectData il buffer nativo contiene il segreto in chiaro.
                new Span<byte>((void*)outBlob.pbData, outBlob.cbData).Clear();
                Native.LocalFree(outBlob.pbData);
            }
        }
    }

    private static void DeleteOrphanTemp(string path)
    {
        // Un .tmp rimasto da una scrittura interrotta contiene un segreto cifrato inutile: si elimina.
        try
        {
            File.Delete(path + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: impossibile eliminare {path}.tmp: {ex.Message}");
        }
    }

    private string PathFor(string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException(Loc.T("secrets.invalidSecretName", name), nameof(name));
        return Path.Combine(_directory, name + ".bin");
    }
}
