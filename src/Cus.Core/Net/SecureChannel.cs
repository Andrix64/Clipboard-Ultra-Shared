using System.Buffers.Binary;
using System.Security.Cryptography;
using Cus.Core.Crypto;
using Cus.Core.Identity;

namespace Cus.Core.Net;

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>
/// Il messaggio non entra in un frame: non è stato inviato e la connessione resta valida.
/// </summary>
public sealed class MessageTooLargeException(string message) : Exception(message);

/// <summary>
/// Canale cifrato e autenticato sopra uno stream TCP, costruito con libsodium:
/// <list type="number">
/// <item>ciascuna parte invia una chiave X25519 effimera e la propria chiave d'identità Ed25519;</item>
/// <item>ciascuna firma con Ed25519 l'hash della trascrizione (chiavi effimere e identità di entrambe, più il ruolo);</item>
/// <item>le chiavi di sessione vengono da crypto_kx, legate alla trascrizione con BLAKE2b;</item>
/// <item>i messaggi viaggiano in due secretstream XChaCha20-Poly1305 (uno per direzione), che impediscono
/// alterazioni, riordini e repliche.</item>
/// </list>
/// Il canale garantisce che l'altra parte possieda la chiave privata dell'identità dichiarata;
/// decidere se fidarsi di quell'identità spetta a chi lo usa.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>Dimensione massima di un messaggio in chiaro (il frame contiene anche il tag di autenticazione).</summary>
    public const int MaxMessageBytes = MaxFrameBytes - Sodium.SecretStreamABytes;

    private const int HelloBytes = 4 + 1 + 32 + 32;

    private static readonly byte[] Magic = "CUS1"u8.ToArray();
    private const byte Version = 1;

    private readonly Stream _stream;
    private readonly byte[] _sendState;
    private readonly byte[] _receiveState;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _receiveLock = new(1, 1);

    private SecureChannel(Stream stream, byte[] remoteIdentityKey, byte[] sendState, byte[] receiveState)
    {
        _stream = stream;
        RemoteIdentityKey = remoteIdentityKey;
        _sendState = sendState;
        _receiveState = receiveState;
    }

    /// <summary>Chiave Ed25519 dell'altra parte, verificata dall'handshake.</summary>
    public byte[] RemoteIdentityKey { get; }

    public string RemoteDeviceId => DeviceIdentity.ComputeDeviceId(RemoteIdentityKey);

    /// <param name="isInitiator">True per chi ha aperto la connessione.</param>
    /// <exception cref="ProtocolException">Handshake non valido o firma sbagliata.</exception>
    public static async Task<SecureChannel> EstablishAsync(
        Stream stream, DeviceIdentity identity, bool isInitiator, CancellationToken cancellationToken)
    {
        var (ephemeralPk, ephemeralSk) = Sodium.KxKeyPair();
        try
        {
            var hello = new byte[HelloBytes];
            Magic.CopyTo(hello, 0);
            hello[4] = Version;
            ephemeralPk.CopyTo(hello, 5);
            identity.SignPublicKey.CopyTo(hello, 37);
            await WriteFrameAsync(stream, hello, cancellationToken);

            // Prima dell'autenticazione si accettano solo frame della dimensione attesa: un header che
            // dichiara 8 MB non deve far allocare 8 MB a chiunque si colleghi.
            var peerHello = await ReadFrameAsync(stream, HelloBytes, cancellationToken);
            if (peerHello.Length != hello.Length || !peerHello.AsSpan(0, 4).SequenceEqual(Magic))
                throw new ProtocolException("L'altra parte non parla il protocollo CUS.");
            if (peerHello[4] != Version)
                throw new ProtocolException($"Versione di protocollo non supportata: {peerHello[4]}.");

            var peerEphemeral = peerHello.AsSpan(5, 32).ToArray();
            var peerIdentity = peerHello.AsSpan(37, 32).ToArray();
            if (peerIdentity.AsSpan().SequenceEqual(identity.SignPublicKey))
                throw new ProtocolException("Connessione verso sé stessi.");

            var (clientEph, serverEph, clientId, serverId) = isInitiator
                ? (ephemeralPk, peerEphemeral, identity.SignPublicKey, peerIdentity)
                : (peerEphemeral, ephemeralPk, peerIdentity, identity.SignPublicKey);
            var transcript = SHA512.HashData([.. "CUS/handshake/v1"u8, .. clientEph, .. serverEph, .. clientId, .. serverId]);

            var ownRole = isInitiator ? (byte)'C' : (byte)'S';
            var peerRole = isInitiator ? (byte)'S' : (byte)'C';
            await WriteFrameAsync(stream, identity.Sign(AuthPayload(ownRole, transcript)), cancellationToken);
            var peerSignature = await ReadFrameAsync(stream, Sodium.SignBytes, cancellationToken);
            if (!DeviceIdentity.Verify(peerSignature, AuthPayload(peerRole, transcript), peerIdentity))
                throw new ProtocolException("Firma dell'handshake non valida: l'altra parte non possiede la chiave che dichiara.");

            var (rx, tx) = isInitiator
                ? Sodium.KxClientSessionKeys(ephemeralPk, ephemeralSk, peerEphemeral)
                : Sodium.KxServerSessionKeys(ephemeralPk, ephemeralSk, peerEphemeral);
            var sendKey = Sodium.GenericHash(transcript, tx);
            var receiveKey = Sodium.GenericHash(transcript, rx);
            CryptographicOperations.ZeroMemory(rx);
            CryptographicOperations.ZeroMemory(tx);

            var sendState = new byte[Sodium.SecretStreamStateBytes];
            var receiveState = new byte[Sodium.SecretStreamStateBytes];
            await WriteFrameAsync(stream, Sodium.SecretStreamInitPush(sendState, sendKey), cancellationToken);
            var peerHeader = await ReadFrameAsync(stream, Sodium.SecretStreamHeaderBytes, cancellationToken);
            if (peerHeader.Length != Sodium.SecretStreamHeaderBytes)
                throw new ProtocolException("Header del canale cifrato non valido.");
            Sodium.SecretStreamInitPull(receiveState, peerHeader, receiveKey);
            CryptographicOperations.ZeroMemory(sendKey);
            CryptographicOperations.ZeroMemory(receiveKey);

            return new SecureChannel(stream, peerIdentity, sendState, receiveState);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralSk);
        }
    }

    /// <exception cref="MessageTooLargeException">Messaggio oltre <see cref="MaxMessageBytes"/>: non inviato.</exception>
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        // Il controllo va fatto prima di cifrare: secretstream avanza lo stato a ogni push, e un
        // messaggio cifrato ma non inviato renderebbe indecifrabili tutti i successivi.
        if (message.Length > MaxMessageBytes)
            throw new MessageTooLargeException($"Messaggio troppo grande ({message.Length:N0} byte, massimo {MaxMessageBytes:N0}).");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var ciphertext = Sodium.SecretStreamPush(_sendState, message.Span);
            await WriteFrameAsync(_stream, ciphertext, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <exception cref="EndOfStreamException">L'altra parte ha chiuso la connessione.</exception>
    /// <exception cref="ProtocolException">Messaggio alterato.</exception>
    /// <param name="maxMessageBytes">Limite più basso di <see cref="MaxMessageBytes"/>, per i messaggi di chi non è ancora fidato.</param>
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken, int maxMessageBytes = MaxMessageBytes)
    {
        await _receiveLock.WaitAsync(cancellationToken);
        try
        {
            var limit = Math.Min(maxMessageBytes, MaxMessageBytes) + Sodium.SecretStreamABytes;
            var ciphertext = await ReadFrameAsync(_stream, limit, cancellationToken);
            if (!Sodium.SecretStreamPull(_receiveState, ciphertext, out var message))
                throw new ProtocolException("Messaggio alterato o fuori ordine.");
            return message;
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        CryptographicOperations.ZeroMemory(_sendState);
        CryptographicOperations.ZeroMemory(_receiveState);
    }

    private static byte[] AuthPayload(byte role, byte[] transcript) => [.. "CUS/auth/v1"u8, role, .. transcript];

    private static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxFrameBytes)
            throw new ProtocolException("Messaggio troppo grande.");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsMemory(4));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maxLength, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > maxLength)
            throw new ProtocolException($"Lunghezza di messaggio non valida: {length}.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}
