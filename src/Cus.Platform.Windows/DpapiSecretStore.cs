using System.ComponentModel;
using System.Runtime.InteropServices;
using Cus.Core.Abstractions;
using Cus.Platform.Windows.Interop;

namespace Cus.Platform.Windows;

/// <summary>
/// Segreti protetti con DPAPI nello scope dell'utente Windows corrente: solo lo stesso utente,
/// sullo stesso PC, può decifrarli. Un file per segreto in <c>&lt;cartella dati&gt;/secrets</c>.
/// </summary>
public sealed unsafe class DpapiSecretStore(string dataDirectory) : ISecretStore
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private readonly string _directory = Path.Combine(dataDirectory, "secrets");

    public byte[]? Load(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? Unprotect(File.ReadAllBytes(path)) : null;
    }

    public void Save(string name, byte[] secret)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(secret));
        File.Move(temp, path, overwrite: true);
    }

    internal static byte[] Protect(ReadOnlySpan<byte> data) => Transform(data, protect: true);

    internal static byte[] Unprotect(ReadOnlySpan<byte> data) => Transform(data, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> data, bool protect)
    {
        fixed (byte* input = data)
        {
            var inBlob = new Native.DATA_BLOB { cbData = data.Length, pbData = (nint)input };
            var outBlob = default(Native.DATA_BLOB);
            var ok = protect
                ? Native.CryptProtectData(&inBlob, "CUS", null, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, &outBlob)
                : Native.CryptUnprotectData(&inBlob, 0, null, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, &outBlob);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), protect ? "CryptProtectData fallita." : "CryptUnprotectData fallita.");

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.pbData, outBlob.cbData).ToArray();
            }
            finally
            {
                Native.LocalFree(outBlob.pbData);
            }
        }
    }

    private string PathFor(string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"Nome di segreto non valido: '{name}'.", nameof(name));
        return Path.Combine(_directory, name + ".bin");
    }
}
