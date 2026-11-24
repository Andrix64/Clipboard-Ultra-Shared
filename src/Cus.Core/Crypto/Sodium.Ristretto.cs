using System.Runtime.InteropServices;

namespace Cus.Core.Crypto;

/// <summary>Binding ristretto255 aggiuntivi usati da CPace.</summary>
internal static unsafe partial class Sodium
{
    public const int Ristretto255ScalarBytes = 32;

    /// <summary>
    /// q = n * p, con p codificato. Restituisce false se p non è una codifica valida
    /// o se il risultato è l'elemento neutro (libsodium restituisce -1 in entrambi i casi).
    /// libsodium azzera solo il bit più alto dello scalare, senza ridurlo modulo l.
    /// </summary>
    public static bool Ristretto255ScalarMult(Span<byte> q, ReadOnlySpan<byte> n, ReadOnlySpan<byte> p)
    {
        if (q.Length != Ristretto255Bytes)
            throw new ArgumentException($"Servono {Ristretto255Bytes} byte.", nameof(q));
        if (n.Length != Ristretto255ScalarBytes)
            throw new ArgumentException($"Servono {Ristretto255ScalarBytes} byte.", nameof(n));
        if (p.Length != Ristretto255Bytes)
        {
            q.Clear();
            return false;
        }

        EnsureInitialized();
        int result;
        fixed (byte* qp = q)
        fixed (byte* np = n)
        fixed (byte* pp = p)
        {
            result = crypto_scalarmult_ristretto255(qp, np, pp);
        }

        if (result != 0)
            q.Clear();
        return result == 0;
    }

    [LibraryImport(Library)]
    private static partial int crypto_scalarmult_ristretto255(byte* q, byte* n, byte* p);
}
