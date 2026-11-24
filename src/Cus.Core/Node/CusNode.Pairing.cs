using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cus.Core.Crypto;
using Cus.Core.Identity;
using Cus.Core.Net;
using Cus.Core.Trust;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    private static readonly TimeSpan PairingStepTimeout = TimeSpan.FromSeconds(30);
    private readonly Lock _pairingGate = new();

    // ================================================================ chi mostra il codice

    /// <summary>Genera un codice a 6 cifre, valido <see cref="CusNodeOptions.PairingCodeLifetime"/> e per un numero limitato di tentativi.</summary>
    public PairingCode StartPairing()
    {
        if (Trust.State.SelfRevoked)
            throw new CusOperationException("Questo dispositivo è revocato: genera una nuova identità prima di abbinarlo.");

        var code = new PairingCode(
            RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow + _options.PairingCodeLifetime,
            _options.PairingAttempts);
        lock (_pairingGate)
            _pairingCode = code;
        PairingCodeChanged?.Invoke(this, EventArgs.Empty);

        _ = Task.Delay(_options.PairingCodeLifetime + TimeSpan.FromMilliseconds(50), _lifetime.Token)
            .ContinueWith(_ => PairingCodeChanged?.Invoke(this, EventArgs.Empty), TaskScheduler.Default);
        return code;
    }

    public void CancelPairing()
    {
        lock (_pairingGate)
            _pairingCode = null;
        PairingCodeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task HandlePairingStartAsync(PeerConnection connection, PairingStartMessage start)
    {
        PairingCode? code;
        string? error = null;
        lock (_pairingGate)
        {
            code = ActivePairingCode;
            if (code is null)
                error = "Nessun codice di abbinamento attivo sull'altro PC: è scaduto o non è stato generato.";
            else
                _pairingCode = code with { AttemptsLeft = code.AttemptsLeft - 1 };
        }

        if (error is null && (start.SessionId.Length != 16 || start.Share.Length != CPace.MessageBytes))
            error = "Messaggio di abbinamento non valido.";
        if (error is not null)
        {
            await connection.SendAsync(new PairingReplyMessage(null, null, error));
            await connection.CloseAsync("abbinamento non riuscito");
            return;
        }

        PairingCodeChanged?.Invoke(this, EventArgs.Empty);
        var channelId = PairingChannelId(connection.Remote.SignPublicKey, Identity.SignPublicKey);
        var cpace = CreateCPace(CPaceRole.Responder, code!.Code, channelId, start.SessionId);
        byte[] isk;
        try
        {
            isk = cpace.DeriveIsk(start.Share, []);
        }
        catch (CPaceException)
        {
            await connection.SendAsync(new PairingReplyMessage(null, null, "Messaggio di abbinamento non valido."));
            await connection.CloseAsync("abbinamento non riuscito");
            return;
        }

        try
        {
            await connection.SendAsync(new PairingReplyMessage(cpace.Message, PairingConfirmation(isk, 'B', channelId, start.SessionId), null));
            var confirm = await connection.ReceiveAsync<PairingConfirmMessage>(PairingStepTimeout);

            if (!CryptographicOperations.FixedTimeEquals(confirm.Confirmation, PairingConfirmation(isk, 'A', channelId, start.SessionId)))
            {
                int left;
                lock (_pairingGate)
                {
                    left = ActivePairingCode?.AttemptsLeft ?? 0;
                    if (left <= 0)
                        _pairingCode = null;
                }

                PairingCodeChanged?.Invoke(this, EventArgs.Empty);
                await connection.SendAsync(new PairingResultMessage(false, left > 0
                    ? $"Codice errato. Tentativi rimasti: {left}."
                    : "Codice errato. Tentativi esauriti: genera un nuovo codice.", null));
                await connection.CloseAsync("codice errato");
                return;
            }

            lock (_pairingGate)
                _pairingCode = null;
            PairingCodeChanged?.Invoke(this, EventArgs.Empty);

            var entry = Trust.Pair(connection.Remote);
            RememberEndpoint(connection);
            await connection.SendAsync(new PairingResultMessage(true, null, entry));
            var sync = await connection.ReceiveAsync<TrustSyncMessage>(PairingStepTimeout);
            Trust.Merge(sync.Entries);
            await connection.SendAsync(new TrustSyncMessage(Trust.Entries));
            _notifier.Info($"{connection.Remote.DisplayName} è ora abbinato a questo dispositivo.");
            Log($"Abbinato: {connection.Remote.DisplayName} ({connection.Remote.Fingerprint}).");
            await connection.CloseAsync("abbinamento completato");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(isk);
        }
    }

    // ================================================================ chi inserisce il codice

    /// <summary>Abbina questo dispositivo a quello che mostra il codice.</summary>
    /// <exception cref="PairingFailedException">Codice errato o scaduto, tentativi esauriti, errore di protocollo.</exception>
    public async Task<DeviceInfo> PairWithAsync(string host, int port, string code, CancellationToken cancellationToken = default)
    {
        code = code.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (!SixDigits().IsMatch(code))
            throw new PairingFailedException("Il codice deve essere di 6 cifre.");
        if (Trust.State.SelfRevoked)
            throw new PairingFailedException("Questo dispositivo è revocato: genera una nuova identità prima di abbinarlo.");

        PeerConnection connection;
        try
        {
            connection = await ConnectAsync(host, port, cancellationToken);
        }
        catch (CusOperationException ex)
        {
            throw new PairingFailedException(ex.Message);
        }

        byte[]? isk = null;
        try
        {
            var remote = connection.Remote;
            var sid = RandomNumberGenerator.GetBytes(16);
            var channelId = PairingChannelId(Identity.SignPublicKey, remote.SignPublicKey);
            var cpace = CreateCPace(CPaceRole.Initiator, code, channelId, sid);

            await connection.SendAsync(new PairingStartMessage(sid, cpace.Message), cancellationToken);
            var reply = await connection.ReceiveAsync<PairingReplyMessage>(PairingStepTimeout, cancellationToken);
            if (reply.Error is not null)
                throw new PairingFailedException(reply.Error);
            if (reply.Share is null || reply.Confirmation is null)
                throw new PairingFailedException("Risposta di abbinamento incompleta.");

            try
            {
                isk = cpace.DeriveIsk(reply.Share, []);
            }
            catch (CPaceException)
            {
                throw new PairingFailedException("Risposta di abbinamento non valida.");
            }

            await connection.SendAsync(new PairingConfirmMessage(PairingConfirmation(isk, 'A', channelId, sid)), cancellationToken);
            var result = await connection.ReceiveAsync<PairingResultMessage>(PairingStepTimeout, cancellationToken);
            if (!result.Success)
                throw new PairingFailedException(result.Error ?? "Abbinamento rifiutato.");
            if (!CryptographicOperations.FixedTimeEquals(reply.Confirmation, PairingConfirmation(isk, 'B', channelId, sid)))
                throw new PairingFailedException("La conferma dell'altro dispositivo non è valida.");
            if (result.Entry is not { Kind: TrustEntryKind.Pair } entry || entry.ActorId != remote.DeviceId || entry.SubjectId != Identity.DeviceId)
                throw new PairingFailedException("Voce di abbinamento non valida.");

            Trust.Merge([entry]);
            Trust.Pair(remote);
            RememberEndpoint(connection);
            await connection.SendAsync(new TrustSyncMessage(Trust.Entries), cancellationToken);
            var sync = await connection.ReceiveAsync<TrustSyncMessage>(PairingStepTimeout, cancellationToken);
            Trust.Merge(sync.Entries);
            await connection.CloseAsync("abbinamento completato");
            Log($"Abbinato: {remote.DisplayName} ({remote.Fingerprint}).");
            return remote;
        }
        catch (PairingFailedException)
        {
            connection.Close("abbinamento non riuscito");
            throw;
        }
        catch (Exception ex)
        {
            connection.Close("abbinamento non riuscito");
            throw new PairingFailedException($"Abbinamento non riuscito: {Describe(ex)}");
        }
        finally
        {
            if (isk is not null)
                CryptographicOperations.ZeroMemory(isk);
        }
    }

    // ================================================================ revoca

    /// <summary>
    /// Revoca un dispositivo abbinato (anche questo stesso: auto-revoca). Definitivo.
    /// La revoca si propaga subito ai dispositivi collegati e a quelli raggiungibili.
    /// </summary>
    public async Task RevokeAsync(string deviceId)
    {
        var before = Trust.State;
        DeviceInfo subject;
        if (deviceId == Identity.DeviceId)
            subject = Identity.Info;
        else if (before.Trusted.TryGetValue(deviceId, out var device))
            subject = new DeviceInfo(device.DeviceId, device.DisplayName, "", device.SignPublicKey);
        else
            throw new CusOperationException("Il dispositivo non è tra quelli abbinati.");

        var reachable = Peers.Where(p => p.IsTrusted && p.DeviceId != deviceId).ToList();
        var connected = _connections.Keys.Where(c => c.IsStarted && before.IsTrusted(c.Remote.DeviceId) && c.Remote.DeviceId != deviceId).ToList();

        Trust.Revoke(subject);
        Log($"Revocato: {subject.DisplayName}.");

        // Dopo un'auto-revoca questo PC non si fida più di nessuno: la lista va consegnata
        // esplicitamente a chi era fidato prima, altrimenti non uscirebbe mai.
        foreach (var connection in connected)
        {
            try
            {
                await connection.SendAsync(new TrustSyncMessage(Trust.Entries));
            }
            catch (IOException)
            {
                // Caduto: la riceverà alla prossima connessione.
            }
        }

        var shareWith = before.Trusted.Keys.Where(id => id != deviceId).ToHashSet();
        foreach (var peer in reachable)
        {
            try
            {
                var connection = await ConnectAsync(peer.Host, peer.Port, _lifetime.Token, shareWith);
                await connection.CloseAsync("sincronizzazione completata");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: consegna della revoca a {peer.DisplayName} non riuscita: {ex.Message}");
            }
        }
    }

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex SixDigits();
}
