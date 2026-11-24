using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cus.Core.Identity;

namespace Cus.Core.Net;

/// <summary>
/// Connessione applicativa con un altro dispositivo: canale cifrato, messaggi JSON,
/// richieste con risposta correlata e controllo di vita (ping).
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly SecureChannel _channel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CusMessage>> _pending = new();
    private long _nextRequestId;
    private long _lastReceivedTicks = Environment.TickCount64;
    private int _closed;
    private Task? _receiveLoop;
    private Task? _pingLoop;

    private PeerConnection(SecureChannel channel, DeviceInfo remote, EndPoint? remoteEndPoint, bool isInitiator)
    {
        _channel = channel;
        Remote = remote;
        RemoteEndPoint = remoteEndPoint;
        IsInitiator = isInitiator;
    }

    public DeviceInfo Remote { get; }

    public EndPoint? RemoteEndPoint { get; }

    public bool IsInitiator { get; }

    /// <summary>Porta su cui l'altra parte accetta connessioni (dal saluto iniziale).</summary>
    public int RemoteListenPort { get; private init; }

    /// <summary>True dopo <see cref="Start"/>: da qui i messaggi arrivano all'handler.</summary>
    public bool IsStarted { get; private set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Messaggi che non sono risposte. Gestiti uno alla volta, nell'ordine di arrivo.</summary>
    public Func<PeerConnection, CusMessage, Task>? MessageHandler { get; set; }

    /// <summary>Chiamato una sola volta alla chiusura, con il motivo.</summary>
    public event Action<PeerConnection, string>? Closed;

    public CancellationToken Lifetime => _lifetime.Token;

    public static async Task<PeerConnection> ConnectAsync(
        string host, int port, DeviceIdentity identity, int listenPort, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeout);
            await client.ConnectAsync(host, port, timeout.Token);
            return await HandshakeAsync(client, identity, listenPort, isInitiator: true, timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<PeerConnection> AcceptAsync(TcpClient client, DeviceIdentity identity, int listenPort, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        return await HandshakeAsync(client, identity, listenPort, isInitiator: false, timeout.Token);
    }

    /// <summary>Avvia la ricezione continua e i ping. Prima di questo si usa <see cref="ReceiveAsync"/>.</summary>
    public void Start()
    {
        IsStarted = true;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _pingLoop = Task.Run(PingLoopAsync);
    }

    /// <summary>Riceve un singolo messaggio, prima di <see cref="Start"/> (fasi iniziali del protocollo).</summary>
    public async Task<CusMessage> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cts.CancelAfter(timeout);
        var message = CusMessage.Deserialize(await _channel.ReceiveAsync(cts.Token));
        Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
        return message;
    }

    public async Task<T> ReceiveAsync<T>(TimeSpan timeout, CancellationToken cancellationToken = default) where T : CusMessage
    {
        var message = await ReceiveAsync(timeout, cancellationToken);
        return message as T ?? throw new ProtocolException($"Atteso {typeof(T).Name}, ricevuto {message.GetType().Name}.");
    }

    public async Task SendAsync(CusMessage message, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
            throw new IOException($"Connessione con {Remote.DisplayName} chiusa.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _channel.SendAsync(message.Serialize(), cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Close($"invio fallito: {ex.Message}");
            throw new IOException($"Connessione con {Remote.DisplayName} interrotta.", ex);
        }
    }

    /// <summary>Invia una richiesta e attende la risposta con lo stesso RequestId.</summary>
    public async Task<TReply> RequestAsync<TReply>(Func<long, CusMessage> build, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TReply : CusMessage
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<CusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(build(id), cancellationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cts.CancelAfter(timeout);
            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var reply = await tcs.Task;
                return reply as TReply ?? throw new ProtocolException($"Risposta inattesa: {reply.GetType().Name}.");
            }
        }
        catch (TaskCanceledException) when (IsClosed)
        {
            throw new IOException($"Connessione con {Remote.DisplayName} chiusa.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _lifetime.Cancel();
        foreach (var pending in _pending.Values)
            pending.TrySetCanceled();
        _ = _channel.DisposeAsync().AsTask();
        try
        {
            Closed?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: handler di chiusura fallito: {ex}");
        }
    }

    /// <summary>Invia un saluto con il motivo e chiude.</summary>
    public async Task CloseAsync(string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync(new ByeMessage(reason), cts.Token);
        }
        catch (Exception)
        {
            // La connessione può essere già caduta: si chiude comunque.
        }

        Close(reason);
    }

    public async ValueTask DisposeAsync()
    {
        Close("chiusura");
        foreach (var task in new[] { _receiveLoop, _pingLoop })
        {
            if (task is not null)
                await task.ConfigureAwait(false);
        }
    }

    private static async Task<PeerConnection> HandshakeAsync(TcpClient client, DeviceIdentity identity, int listenPort, bool isInitiator, CancellationToken ct)
    {
        var endPoint = client.Client.RemoteEndPoint;
        var channel = await SecureChannel.EstablishAsync(client.GetStream(), identity, isInitiator, ct);
        try
        {
            await channel.SendAsync(new HelloMessage(identity.DisplayName, identity.UserName, listenPort).Serialize(), ct);
            var hello = CusMessage.Deserialize(await channel.ReceiveAsync(ct)) as HelloMessage
                ?? throw new ProtocolException("Atteso il saluto iniziale.");
            var remote = new DeviceInfo(
                channel.RemoteDeviceId,
                Limit(hello.DisplayName, 64),
                Limit(hello.UserName, 64),
                channel.RemoteIdentityKey);
            return new PeerConnection(channel, remote, endPoint, isInitiator) { RemoteListenPort = hello.ListenPort };
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    private static string Limit(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "(senza nome)" : value.Length <= max ? value : value[..max];

    private async Task ReceiveLoopAsync()
    {
        var reason = "connessione chiusa dall'altra parte";
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = CusMessage.Deserialize(await _channel.ReceiveAsync(_lifetime.Token));
                Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);

                switch (message)
                {
                    case PingMessage:
                        continue;
                    case ByeMessage bye:
                        reason = bye.Reason;
                        return;
                    case IReplyMessage reply when _pending.TryRemove(reply.RequestId, out var tcs):
                        tcs.TrySetResult(message);
                        continue;
                }

                if (MessageHandler is { } handler)
                    await handler(this, message);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "chiusura";
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException or ObjectDisposedException)
        {
            reason = "connessione interrotta";
        }
        catch (ProtocolException ex)
        {
            reason = $"errore di protocollo: {ex.Message}";
        }
        catch (Exception ex)
        {
            reason = $"errore interno: {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"CUS: errore nella ricezione da {Remote.DisplayName}: {ex}");
        }
        finally
        {
            Close(reason);
        }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, _lifetime.Token);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastReceivedTicks) > IdleTimeout.TotalMilliseconds)
                {
                    Close("nessuna risposta dall'altra parte");
                    return;
                }

                await SendAsync(new PingMessage(), _lifetime.Token);
            }
        }
        catch (Exception)
        {
            // Chiusura o invio fallito: Close è già stato chiamato.
        }
    }
}
