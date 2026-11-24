using System.Runtime.InteropServices;

namespace Cus.Core.Crypto;

/// <summary>
/// Binding minimi verso libsodium. Saranno estesi in fase 3 con quanto serve a
/// CPace-ristretto255-SHA512 e alle firme Ed25519 (vedi PLAN.md).
/// </summary>
internal static unsafe partial class Sodium
{
    private const string Library = "libsodium";

    public const int Ristretto255HashBytes = 64;
    public const int Ristretto255Bytes = 32;

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        // 0 = inizializzata ora, 1 = già inizializzata, -1 = errore.
        if (sodium_init() < 0)
            throw new InvalidOperationException("Inizializzazione di libsodium fallita.");
        return true;
    });

    public static void EnsureInitialized() => _ = Initialized.Value;

    /// <summary>Mappa 64 byte uniformi su un punto ristretto255 (usato da CPace per il generatore).</summary>
    public static byte[] Ristretto255FromHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != Ristretto255HashBytes)
            throw new ArgumentException($"Servono {Ristretto255HashBytes} byte.", nameof(hash));

        EnsureInitialized();
        var point = new byte[Ristretto255Bytes];
        fixed (byte* p = point)
        fixed (byte* r = hash)
        {
            var result = crypto_core_ristretto255_from_hash(p, r);
            if (result != 0)
                throw new InvalidOperationException($"crypto_core_ristretto255_from_hash ha restituito {result}.");
        }

        return point;
    }

    public static bool Ristretto255IsValidPoint(ReadOnlySpan<byte> point)
    {
        if (point.Length != Ristretto255Bytes)
            return false;

        EnsureInitialized();
        fixed (byte* p = point)
        {
            return crypto_core_ristretto255_is_valid_point(p) == 1;
        }
    }

    [LibraryImport(Library)]
    private static partial int sodium_init();

    [LibraryImport(Library)]
    private static partial int crypto_core_ristretto255_from_hash(byte* p, byte* r);

    [LibraryImport(Library)]
    private static partial int crypto_core_ristretto255_is_valid_point(byte* p);
}
