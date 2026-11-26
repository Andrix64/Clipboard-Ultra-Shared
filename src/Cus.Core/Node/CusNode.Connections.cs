using System.Net;
using System.Net.Sockets;
using Cus.Core.Friends;
using Cus.Core.Hosting;
using Cus.Core.Net;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    // ================================================================ connessioni

    private TcpListener BindListener()
    {
        if (_options.Port is { } fixedPort)
        {
            var listener = new TcpListener(_options.BindAddress, fixedPort);
            listener.Start();
            return listener;
        }

        for (var port = NetworkDefaults.DefaultPort; port <= NetworkDefaults.PortRangeEnd; port++)
        {
            var listener = new TcpListener(_options.BindAddress, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // Porta occupata (anche da una connessione in uscita): si prova la successiva.
            }
        }

        throw new CusOperationException(
            Loc.T("node.noFreePortBetweenAndClose", NetworkDefaults.DefaultPort, NetworkDefaults.PortRangeEnd));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_lifetime.Token);
            }
            catch (Exception) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                // Errore della singola connessione in arrivo; la pausa evita un ciclo a vuoto se persiste.
                try
                {
                    await Task.Delay(AcceptRetryDelay, _lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }
            catch (Exception ex)
            {
                Log(Loc.T("node.acceptLoopStopped", ex.Message));
                return;
            }

            // Chiunque in rete può collegarsi: oltre un certo numero di connessioni ancora in fase
            // iniziale le nuove si rifiutano subito, invece di accumulare memoria e task.
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (!TryTakePendingSlot(address))
            {
                client.Dispose();
                continue;
            }

            var released = 0;
            void ReleaseSlotOnce()
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    ReleasePendingSlot(address);
            }

            RunInBackground(async () =>
            {
                try
                {
                    await HandleIncomingAsync(client, ReleaseSlotOnce);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"CUS: errore nella connessione in ingresso: {ex}");
                }
                finally
                {
                    ReleaseSlotOnce();
                }
            });
        }
    }

    /// <summary>Uno slot globale più uno per indirizzo: un solo PC in rete non li occupa tutti.</summary>
    private bool TryTakePendingSlot(IPAddress? address)
    {
        var perAddress = address is not null && !IPAddress.IsLoopback(address);
        if (perAddress)
        {
            var count = _pendingByAddress.AddOrUpdate(address!, 1, (_, n) => n + 1);
            if (count > MaxPendingIncomingPerAddress)
            {
                ReleaseAddress(address!);
                return false;
            }
        }

        if (_incomingSlots.Wait(0))
            return true;
        if (perAddress)
            ReleaseAddress(address!);
        return false;
    }

    private void ReleasePendingSlot(IPAddress? address)
    {
        _incomingSlots.Release();
        if (address is not null && !IPAddress.IsLoopback(address))
            ReleaseAddress(address);
    }

    private void ReleaseFileRead(PeerConnection connection)
    {
        if (_fileReadsByConnection.AddOrUpdate(connection, 0, (_, n) => n - 1) <= 0)
            _fileReadsByConnection.TryRemove(new KeyValuePair<PeerConnection, int>(connection, 0));
    }

    private void ReleaseAddress(IPAddress address)
    {
        if (_pendingByAddress.AddOrUpdate(address, 0, (_, n) => n - 1) <= 0)
            _pendingByAddress.TryRemove(new KeyValuePair<IPAddress, int>(address, 0));
    }

    /// <summary>
    /// Handshake, saluto e scambio del livello del patto: comune a ogni connessione. Entrambi tengono
    /// il più basso dei due, così un patto sciolto da una parte (magari a PC spento) cade anche dall'altra.
    /// </summary>
    private async Task<PeerConnection> SetUpConnectionAsync(PeerConnection connection)
    {
        _connections[connection] = 0;
        connection.Closed += (c, _1) => _connections.TryRemove(c, out byte _);

        var remote = connection.Remote;
        await connection.SendAsync(new FriendSyncMessage(Friends.AgreementWith(remote.DeviceId)));
        var sync = await connection.ReceiveAsync<FriendSyncMessage>(PeerConnection.HandshakeTimeout, maxBytes: FriendSyncMaxBytes);
        ApplyFriendSync(connection, sync.Level);
        RememberEndpoint(connection);
        return connection;
    }

    private async Task<PeerConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        PeerConnection connection;
        try
        {
            connection = await PeerConnection.ConnectAsync(host, port, Identity, Port, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProtocolException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new CusOperationException(Loc.T("node.canTConnectTo", new PeerEndpoint(host, port), Describe(ex)), ex);
        }

        try
        {
            return await SetUpConnectionAsync(connection);
        }
        catch (Exception ex) when (ex is not CusOperationException)
        {
            connection.Close(Loc.T("node.initialError"));
            throw new CusOperationException(Loc.T("node.connectionToFailed", new PeerEndpoint(host, port), Describe(ex)), ex);
        }
    }

    /// <param name="releaseSlot">
    /// Libera lo slot di connessione in fase iniziale. Si chiama appena arriva il primo messaggio:
    /// l'attesa del consenso può durare un minuto e ha già un suo limite (MaxPendingJoins), quindi
    /// chi chiede di entrare non deve tenere occupati gli slot che servono a tutti per collegarsi.
    /// </param>
    private async Task HandleIncomingAsync(TcpClient client, Action releaseSlot)
    {
        PeerConnection? connection = null;
        try
        {
            // Assegnata prima della fase iniziale: se questa fallisce la connessione va chiusa (e tolta
            // da _connections), non solo il socket.
            connection = await PeerConnection.AcceptAsync(client, Identity, Port, _lifetime.Token, RevealsIdentityTo);
            await SetUpConnectionAsync(connection);
            var first = await connection.ReceiveAsync(FirstMessageTimeout, maxBytes: FirstMessageMaxBytes);
            releaseSlot();
            switch (first)
            {
                case JoinRequestMessage:
                    await HandleJoinRequestAsync(connection);
                    break;
                default:
                    // Connessione di sola sincronizzazione del patto (o saluto): si chiude.
                    connection.Close(Loc.T("node.end"));
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: connessione in ingresso chiusa: {ex.Message}");
            if (connection is not null)
                connection.Close(Describe(ex));
            else
                client.Dispose();
        }
    }

    /// <summary>
    /// A chi si collega questo PC dice chi è. Mentre si offre come host, a tutti (deve farsi trovare);
    /// da nascosto solo agli amici con un patto, che sono gli unici a cui serve raggiungerlo così.
    /// Gli altri non scoprono nemmeno quale PC c'è a quell'indirizzo (identità, nome, utente).
    /// </summary>
    private bool RevealsIdentityTo(string deviceId) =>
        _visible || _hostSession is { Offered: true } || Friends.AgreementWith(deviceId) != AgreementLevel.None;
}
