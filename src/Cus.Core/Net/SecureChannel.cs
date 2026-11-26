using System.Buffers.Binary;
using System.Security.Cryptography;
using Cus.Core.Crypto;
using Cus.Core.Identity;

namespace Cus.Core.Net;

public class ProtocolException(string message) : Exception(message);

/// <summary>
/// Chi risponde non si presenta a questo dispositivo (per esempio è nascosto e non lo riconosce come
/// amico). Non dice chi è: solo che lì c'è qualcuno che non accetta la connessione.
/// </summary>
public sealed class HandshakeRefusedException(string message) : ProtocolException(message);

/// <summary>
/// Il messaggio non entra in un frame: non è stato inviato e la connessione resta valida.
/// </summary>
public sealed class MessageTooLargeException(string message) : Exception(message);

/// <summary>
/// Canale cifrato e autenticato sopra uno stream TCP, costruito con libsodium (versione 2 del protocollo):
/// <list type="number">
/// <item>ciascuna parte invia solo una chiave X25519 effimera; le chiavi di sessione vengono da crypto_kx,
/// legate alle due chiavi effimere con BLAKE2b, e da qui tutto viaggia in due secretstream
/// XChaCha20-Poly1305 (uno per direzione), che impediscono alterazioni, riordini e repliche;</item>
/// <item>chi si collega manda per primo, già cifrata, la propria chiave d'identità Ed25519;</item>
/// <item>chi risponde decide da quella se presentarsi: se no, risponde solo "rifiutato" e chiude, senza
/// rivelare chi è (un PC nascosto non si fa riconoscere da chi non è suo amico);</item>
/// <item>se sì, manda la propria identità e la firma della trascrizione (chiavi effimere e identità di
/// entrambi, più il ruolo); chi si collega la verifica e risponde con la propria firma.</item>
/// </list>
/// Le identità non passano mai in chiaro. Il canale garantisce che l'altra parte possieda la chiave
/// privata dell'identità dichiarata; decidere se fidarsi di quell'identità spetta a chi lo usa.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>Dimensione massima di un messaggio in chiaro (il frame contiene anche il tag di autenticazione).</summary>
    public const int MaxMessageBytes = MaxFrameBytes - Sodium.SecretStreamABytes;

    /// <summary>Primo frame, in chiaro: magic, versione, chiave effimera. Nessuna identità.</summary>
    private const int HelloBytes = 4 + 1 + Sodium.KxPublicKeyBytes;

    /// <summary>Presentazione di chi si collega: identità e firma.</summary>
    private const int ClientAuthBytes = Sodium.SignPublicKeyBytes + Sodium.SignBytes;

    /// <summary>Risposta di chi risponde all'identità di chi si collega: esito, identità, firma.</summary>
    private const int ResponderAuthBytes = 1 + Sodium.SignPublicKeyBytes + Sodium.SignBytes;

    private const byte Refused = 0;
    private const byte Accepted = 1;

    private static readonly byte[] Magic = "CUS1"u8.ToArray();
    private const byte Version = 2;

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
    /// <exception cref="HandshakeRefusedException">Chi risponde non si è presentato a questo dispositivo.</exception>
    public static Task<SecureChannel> EstablishAsync(
        Stream stream, DeviceIdentity identity, bool isInitiator, CancellationToken cancellationToken) =>
        EstablishAsync(stream, identity, isInitiator, revealTo: null, cancellationToken);

    /// <param name="revealTo">
    /// Solo per chi risponde: riceve l'ID di chi si collega (già provato dal canale cifrato, non ancora
    /// dalla firma) e decide se presentarsi. Null: sempre. Chi si collega si presenta sempre.
    /// </param>
    public static async Task<SecureChannel> EstablishAsync(
        Stream stream, DeviceIdentity identity, bool isInitiator, Func<string, bool>? revealTo, CancellationToken cancellationToken)
    {
        var (ephemeralPk, ephemeralSk) = Sodium.KxKeyPair();
        var sendState = new byte[Sodium.SecretStreamStateBytes];
        var receiveState = new byte[Sodium.SecretStreamStateBytes];
        var established = false;
        try
        {
            var hello = new byte[HelloBytes];
            Magic.CopyTo(hello, 0);
            hello[4] = Version;
            ephemeralPk.CopyTo(hello, 5);
            await WriteFrameAsync(stream, hello, cancellationToken);

            // Prima dell'autenticazione si accettano solo frame della dimensione attesa: un header che
            // dichiara 8 MB non deve far allocare 8 MB a chiunque si colleghi.
            var peerHello = await ReadFrameAsync(stream, HelloBytes, cancellationToken);
            if (peerHello.Length != hello.Length || !peerHello.AsSpan(0, 4).SequenceEqual(Magic))
                throw new ProtocolException(Loc.T("net.theOtherSideDoesnTSpeak"));
            if (peerHello[4] != Version)
                throw new ProtocolException(Loc.T("net.unsupportedProtocolVersionThisOneIs", peerHello[4], Version));
            var peerEphemeral = peerHello.AsSpan(5, Sodium.KxPublicKeyBytes).ToArray();

            var (clientEph, serverEph) = isInitiator ? (ephemeralPk, peerEphemeral) : (peerEphemeral, ephemeralPk);
            var keyContext = SHA512.HashData([.. "CUS/handshake/v2/keys"u8, .. clientEph, .. serverEph]);
            var (rx, tx) = isInitiator
                ? Sodium.KxClientSessionKeys(ephemeralPk, ephemeralSk, peerEphemeral)
                : Sodium.KxServerSessionKeys(ephemeralPk, ephemeralSk, peerEphemeral);
            var sendKey = Sodium.GenericHash(keyContext, tx);
            var receiveKey = Sodium.GenericHash(keyContext, rx);
            CryptographicOperations.ZeroMemory(rx);
            CryptographicOperations.ZeroMemory(tx);
            await WriteFrameAsync(stream, Sodium.SecretStreamInitPush(sendState, sendKey), cancellationToken);
            var peerHeader = await ReadFrameAsync(stream, Sodium.SecretStreamHeaderBytes, cancellationToken);
            if (peerHeader.Length != Sodium.SecretStreamHeaderBytes)
                throw new ProtocolException(Loc.T("net.invalidEncryptedChannelHeader"));
            Sodium.SecretStreamInitPull(receiveState, peerHeader, receiveKey);
            CryptographicOperations.ZeroMemory(sendKey);
            CryptographicOperations.ZeroMemory(receiveKey);

            // Da qui tutto è cifrato. Chi si collega prova per primo la propria identità, firmando le due
            // chiavi effimere (cioè questa sessione) e la propria chiave: chi risponde decide se
            // presentarsi solo dopo averla verificata, così dichiarare la chiave pubblica di un amico
            // non basta a farsi rivelare un PC nascosto. Poi chi risponde firma tutto, identità di chi
            // si collega compresa: un intermediario non può legare due handshake diversi.
            var clientTranscript = (byte[] clientId) =>
                SHA512.HashData([.. "CUS/handshake/v2/client"u8, .. clientEph, .. serverEph, .. clientId]);
            var serverTranscript = (byte[] clientId, byte[] serverId) =>
                SHA512.HashData([.. "CUS/handshake/v2/server"u8, .. clientEph, .. serverEph, .. clientId, .. serverId]);

            byte[] peerIdentity;
            if (isInitiator)
            {
                await SendSealedAsync(stream, sendState,
                    [.. identity.SignPublicKey, .. identity.Sign(AuthPayload((byte)'C', clientTranscript(identity.SignPublicKey)))], cancellationToken);
                var reply = await ReceiveSealedAsync(stream, receiveState, ResponderAuthBytes, cancellationToken);
                if (reply is [Refused])
                    throw new HandshakeRefusedException(Loc.T("net.theOtherPcIsnTAccepting"));
                if (reply.Length != ResponderAuthBytes || reply[0] != Accepted)
                    throw new ProtocolException(Loc.T("net.invalidHandshakeReply"));
                peerIdentity = reply.AsSpan(1, Sodium.SignPublicKeyBytes).ToArray();
                if (peerIdentity.AsSpan().SequenceEqual(identity.SignPublicKey))
                    throw new ProtocolException(Loc.T("net.connectionToItself"));
                var signature = reply.AsSpan(1 + Sodium.SignPublicKeyBytes).ToArray();
                if (!DeviceIdentity.Verify(signature, AuthPayload((byte)'S', serverTranscript(identity.SignPublicKey, peerIdentity)), peerIdentity))
                    throw new ProtocolException(Loc.T("net.invalidHandshakeSignatureTheOtherSide"));
            }
            else
            {
                var claim = await ReceiveSealedAsync(stream, receiveState, ClientAuthBytes, cancellationToken);
                if (claim.Length != ClientAuthBytes)
                    throw new ProtocolException(Loc.T("net.invalidHandshakeIdentity"));
                peerIdentity = claim.AsSpan(0, Sodium.SignPublicKeyBytes).ToArray();
                if (peerIdentity.AsSpan().SequenceEqual(identity.SignPublicKey))
                    throw new ProtocolException(Loc.T("net.connectionToItself"));
                var signature = claim.AsSpan(Sodium.SignPublicKeyBytes).ToArray();
                if (!DeviceIdentity.Verify(signature, AuthPayload((byte)'C', clientTranscript(peerIdentity)), peerIdentity))
                    throw new ProtocolException(Loc.T("net.invalidHandshakeSignatureTheOtherSide"));

                if (revealTo is not null && !revealTo(DeviceIdentity.ComputeDeviceId(peerIdentity)))
                {
                    await SendSealedAsync(stream, sendState, [Refused], cancellationToken);
                    throw new HandshakeRefusedException(Loc.T("net.connectionNotAcceptedUnknownDeviceWhile"));
                }

                await SendSealedAsync(stream, sendState,
                    [Accepted, .. identity.SignPublicKey, .. identity.Sign(AuthPayload((byte)'S', serverTranscript(peerIdentity, identity.SignPublicKey)))],
                    cancellationToken);
            }

            established = true;
            return new SecureChannel(stream, peerIdentity, sendState, receiveState);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralSk);
            if (!established)
            {
                CryptographicOperations.ZeroMemory(sendState);
                CryptographicOperations.ZeroMemory(receiveState);
            }
        }
    }

    private static async Task SendSealedAsync(Stream stream, byte[] state, byte[] message, CancellationToken cancellationToken) =>
        await WriteFrameAsync(stream, Sodium.SecretStreamPush(state, message), cancellationToken);

    private static async Task<byte[]> ReceiveSealedAsync(Stream stream, byte[] state, int maxMessageBytes, CancellationToken cancellationToken)
    {
        var ciphertext = await ReadFrameAsync(stream, maxMessageBytes + Sodium.SecretStreamABytes, cancellationToken);
        if (!Sodium.SecretStreamPull(state, ciphertext, out var message))
            throw new ProtocolException(Loc.T("net.tamperedHandshakeMessage"));
        return message;
    }

    /// <exception cref="MessageTooLargeException">Messaggio oltre <see cref="MaxMessageBytes"/>: non inviato.</exception>
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        // Il controllo va fatto prima di cifrare: secretstream avanza lo stato a ogni push, e un
        // messaggio cifrato ma non inviato renderebbe indecifrabili tutti i successivi.
        if (message.Length > MaxMessageBytes)
            throw new MessageTooLargeException(Loc.T("net.messageTooLargeBytesMaximum", message.Length, MaxMessageBytes));

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
                throw new ProtocolException(Loc.T("net.messageTamperedWithOrOutOf"));
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
            throw new ProtocolException(Loc.T("net.messageTooLarge"));
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
            throw new ProtocolException(Loc.T("net.invalidMessageLength", length));
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}
