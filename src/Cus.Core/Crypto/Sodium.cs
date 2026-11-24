using System.Runtime.InteropServices;

namespace Cus.Core.Crypto;

/// <summary>
/// Binding verso libsodium. Le funzioni ristretto255 usate da CPace stanno in Sodium.Ristretto.cs.
/// </summary>
internal static unsafe partial class Sodium
{
    private const string Library = "libsodium";

    public const int Ristretto255HashBytes = 64;
    public const int Ristretto255Bytes = 32;

    public const int SignPublicKeyBytes = 32;
    public const int SignSecretKeyBytes = 64;
    public const int SignBytes = 64;

    public const int KxPublicKeyBytes = 32;
    public const int KxSecretKeyBytes = 32;
    public const int KxSessionKeyBytes = 32;

    public const int SecretStreamKeyBytes = 32;
    public const int SecretStreamHeaderBytes = 24;
    public const int SecretStreamABytes = 17;

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        // 0 = inizializzata ora, 1 = già inizializzata, -1 = errore.
        if (sodium_init() < 0)
            throw new InvalidOperationException("Inizializzazione di libsodium fallita.");
        return true;
    });

    public static void EnsureInitialized() => _ = Initialized.Value;

    // ---------------------------------------------------------------- ristretto255

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

    // ---------------------------------------------------------------- Ed25519

    public static (byte[] PublicKey, byte[] SecretKey) SignKeyPair()
    {
        EnsureInitialized();
        var pk = new byte[SignPublicKeyBytes];
        var sk = new byte[SignSecretKeyBytes];
        fixed (byte* p = pk)
        fixed (byte* s = sk)
        {
            Check(crypto_sign_keypair(p, s), "crypto_sign_keypair");
        }

        return (pk, sk);
    }

    public static byte[] SignDetached(ReadOnlySpan<byte> message, ReadOnlySpan<byte> secretKey)
    {
        if (secretKey.Length != SignSecretKeyBytes)
            throw new ArgumentException("Chiave Ed25519 privata non valida.", nameof(secretKey));

        EnsureInitialized();
        var signature = new byte[SignBytes];
        fixed (byte* sig = signature)
        fixed (byte* m = message)
        fixed (byte* sk = secretKey)
        {
            Check(crypto_sign_detached(sig, null, m, (ulong)message.Length, sk), "crypto_sign_detached");
        }

        return signature;
    }

    public static bool VerifyDetached(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message, ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != SignBytes || publicKey.Length != SignPublicKeyBytes)
            return false;

        EnsureInitialized();
        fixed (byte* sig = signature)
        fixed (byte* m = message)
        fixed (byte* pk = publicKey)
        {
            return crypto_sign_verify_detached(sig, m, (ulong)message.Length, pk) == 0;
        }
    }

    // ---------------------------------------------------------------- key exchange (X25519)

    public static (byte[] PublicKey, byte[] SecretKey) KxKeyPair()
    {
        EnsureInitialized();
        var pk = new byte[KxPublicKeyBytes];
        var sk = new byte[KxSecretKeyBytes];
        fixed (byte* p = pk)
        fixed (byte* s = sk)
        {
            Check(crypto_kx_keypair(p, s), "crypto_kx_keypair");
        }

        return (pk, sk);
    }

    /// <summary>Chiavi di sessione (rx, tx) lato client. Fallisce se la chiave del server non è valida.</summary>
    public static (byte[] Rx, byte[] Tx) KxClientSessionKeys(
        ReadOnlySpan<byte> clientPk, ReadOnlySpan<byte> clientSk, ReadOnlySpan<byte> serverPk)
    {
        EnsureInitialized();
        var rx = new byte[KxSessionKeyBytes];
        var tx = new byte[KxSessionKeyBytes];
        fixed (byte* r = rx)
        fixed (byte* t = tx)
        fixed (byte* cpk = clientPk)
        fixed (byte* csk = clientSk)
        fixed (byte* spk = serverPk)
        {
            Check(crypto_kx_client_session_keys(r, t, cpk, csk, spk), "crypto_kx_client_session_keys");
        }

        return (rx, tx);
    }

    /// <summary>Chiavi di sessione (rx, tx) lato server. Fallisce se la chiave del client non è valida.</summary>
    public static (byte[] Rx, byte[] Tx) KxServerSessionKeys(
        ReadOnlySpan<byte> serverPk, ReadOnlySpan<byte> serverSk, ReadOnlySpan<byte> clientPk)
    {
        EnsureInitialized();
        var rx = new byte[KxSessionKeyBytes];
        var tx = new byte[KxSessionKeyBytes];
        fixed (byte* r = rx)
        fixed (byte* t = tx)
        fixed (byte* spk = serverPk)
        fixed (byte* ssk = serverSk)
        fixed (byte* cpk = clientPk)
        {
            Check(crypto_kx_server_session_keys(r, t, spk, ssk, cpk), "crypto_kx_server_session_keys");
        }

        return (rx, tx);
    }

    // ---------------------------------------------------------------- BLAKE2b

    public static byte[] GenericHash(ReadOnlySpan<byte> message, ReadOnlySpan<byte> key, int outputLength = 32)
    {
        EnsureInitialized();
        var output = new byte[outputLength];
        fixed (byte* o = output)
        fixed (byte* m = message)
        fixed (byte* k = key)
        {
            Check(crypto_generichash(o, (nuint)outputLength, m, (ulong)message.Length, k, (nuint)key.Length), "crypto_generichash");
        }

        return output;
    }

    // ---------------------------------------------------------------- secretstream (XChaCha20-Poly1305)

    public static int SecretStreamStateBytes
    {
        get
        {
            EnsureInitialized();
            return (int)crypto_secretstream_xchacha20poly1305_statebytes();
        }
    }

    public static byte[] SecretStreamInitPush(Span<byte> state, ReadOnlySpan<byte> key)
    {
        EnsureInitialized();
        var header = new byte[SecretStreamHeaderBytes];
        fixed (byte* s = state)
        fixed (byte* h = header)
        fixed (byte* k = key)
        {
            Check(crypto_secretstream_xchacha20poly1305_init_push(s, h, k), "secretstream_init_push");
        }

        return header;
    }

    public static void SecretStreamInitPull(Span<byte> state, ReadOnlySpan<byte> header, ReadOnlySpan<byte> key)
    {
        EnsureInitialized();
        fixed (byte* s = state)
        fixed (byte* h = header)
        fixed (byte* k = key)
        {
            Check(crypto_secretstream_xchacha20poly1305_init_pull(s, h, k), "secretstream_init_pull");
        }
    }

    public static byte[] SecretStreamPush(Span<byte> state, ReadOnlySpan<byte> message)
    {
        var ciphertext = new byte[message.Length + SecretStreamABytes];
        fixed (byte* s = state)
        fixed (byte* c = ciphertext)
        fixed (byte* m = message)
        {
            Check(crypto_secretstream_xchacha20poly1305_push(s, c, out _, m, (ulong)message.Length, null, 0, 0), "secretstream_push");
        }

        return ciphertext;
    }

    /// <summary>Decifra un messaggio dello stream. Restituisce false se è stato alterato o è fuori ordine.</summary>
    public static bool SecretStreamPull(Span<byte> state, ReadOnlySpan<byte> ciphertext, out byte[] message)
    {
        message = [];
        if (ciphertext.Length < SecretStreamABytes)
            return false;

        var plain = new byte[ciphertext.Length - SecretStreamABytes];
        fixed (byte* s = state)
        fixed (byte* c = ciphertext)
        fixed (byte* m = plain)
        {
            if (crypto_secretstream_xchacha20poly1305_pull(s, m, out _, out _, c, (ulong)ciphertext.Length, null, 0) != 0)
                return false;
        }

        message = plain;
        return true;
    }

    private static void Check(int result, string function)
    {
        if (result != 0)
            throw new System.Security.Cryptography.CryptographicException($"{function} ha restituito {result}.");
    }

    [LibraryImport(Library)]
    private static partial int sodium_init();

    [LibraryImport(Library)]
    private static partial int crypto_core_ristretto255_from_hash(byte* p, byte* r);

    [LibraryImport(Library)]
    private static partial int crypto_core_ristretto255_is_valid_point(byte* p);

    [LibraryImport(Library)]
    private static partial int crypto_sign_keypair(byte* pk, byte* sk);

    [LibraryImport(Library)]
    private static partial int crypto_sign_detached(byte* sig, ulong* siglen, byte* m, ulong mlen, byte* sk);

    [LibraryImport(Library)]
    private static partial int crypto_sign_verify_detached(byte* sig, byte* m, ulong mlen, byte* pk);

    [LibraryImport(Library)]
    private static partial int crypto_kx_keypair(byte* pk, byte* sk);

    [LibraryImport(Library)]
    private static partial int crypto_kx_client_session_keys(byte* rx, byte* tx, byte* clientPk, byte* clientSk, byte* serverPk);

    [LibraryImport(Library)]
    private static partial int crypto_kx_server_session_keys(byte* rx, byte* tx, byte* serverPk, byte* serverSk, byte* clientPk);

    [LibraryImport(Library)]
    private static partial int crypto_generichash(byte* output, nuint outlen, byte* input, ulong inlen, byte* key, nuint keylen);

    [LibraryImport(Library)]
    private static partial nuint crypto_secretstream_xchacha20poly1305_statebytes();

    [LibraryImport(Library)]
    private static partial int crypto_secretstream_xchacha20poly1305_init_push(byte* state, byte* header, byte* key);

    [LibraryImport(Library)]
    private static partial int crypto_secretstream_xchacha20poly1305_push(
        byte* state, byte* c, out ulong clen, byte* m, ulong mlen, byte* ad, ulong adlen, byte tag);

    [LibraryImport(Library)]
    private static partial int crypto_secretstream_xchacha20poly1305_init_pull(byte* state, byte* header, byte* key);

    [LibraryImport(Library)]
    private static partial int crypto_secretstream_xchacha20poly1305_pull(
        byte* state, byte* m, out ulong mlen, out byte tag, byte* c, ulong clen, byte* ad, ulong adlen);
}
