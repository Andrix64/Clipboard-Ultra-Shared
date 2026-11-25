using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Cus.Core.Abstractions;
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

    /// <summary>Saluto iniziale: nomi e porta, ben sotto questo limite anche con nomi lunghi.</summary>
    internal const int HelloMaxBytes = 4 * 1024;

    /// <summary>
    /// Tempo massimo per consegnare un messaggio al socket. Un peer che non legge più (o legge
    /// apposta troppo piano) fa scadere l'invio: la connessione si chiude invece di bloccare chi invia.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Messaggi ricevuti in attesa dell'handler: oltre, la lettura si ferma finché l'handler non recupera.</summary>
    private const int HandlerQueueCapacity = 256;

    private readonly SecureChannel _channel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CusMessage>> _pending = new();
    private long _nextRequestId;
    private long _lastReceivedTicks = Environment.TickCount64;
    private readonly Lock _closeGate = new();
    private readonly Channel<CusMessage> _handlerQueue = Channel.CreateBounded<CusMessage>(
        new BoundedChannelOptions(HandlerQueueCapacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private Action<PeerConnection, string>? _closedHandlers;
    private string? _closeReason;
    private int _closed;
    private Task? _receiveLoop;
    private Task? _handlerLoop;
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

    /// <summary>
    /// Chiamato una sola volta alla chiusura, con il motivo. Chi si iscrive a connessione già chiusa
    /// viene chiamato subito: nessuna chiusura va persa.
    /// </summary>
    public event Action<PeerConnection, string>? Closed
    {
        add
        {
            string? reason;
            lock (_closeGate)
            {
                reason = _closeReason;
                if (reason is null)
                    _closedHandlers += value;
            }

            if (reason is not null && value is not null)
                InvokeClosed(value, reason);
        }

        remove
        {
            lock (_closeGate)
                _closedHandlers -= value;
        }
    }

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
            return await HandshakeAsync(client, identity, listenPort, isInitiator: true, revealTo: null, timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <param name="revealTo">
    /// Decide, dall'ID verificato di chi si collega, se presentarsi (vedi <see cref="SecureChannel"/>).
    /// Null: sempre.
    /// </param>
    /// <exception cref="HandshakeRefusedException">Questo PC non si è presentato a chi si collegava.</exception>
    public static async Task<PeerConnection> AcceptAsync(
        TcpClient client, DeviceIdentity identity, int listenPort, CancellationToken cancellationToken, Func<string, bool>? revealTo = null)
    {
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        return await HandshakeAsync(client, identity, listenPort, isInitiator: false, revealTo, timeout.Token);
    }

    /// <summary>Avvia la ricezione continua e i ping. Prima di questo si usa <see cref="ReceiveAsync"/>.</summary>
    public void Start()
    {
        IsStarted = true;
        _handlerLoop = Task.Run(HandlerLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _pingLoop = Task.Run(PingLoopAsync);
    }

    /// <summary>Riceve un singolo messaggio, prima di <see cref="Start"/> (fasi iniziali del protocollo).</summary>
    /// <param name="maxBytes">Dimensione massima accettata: più bassa per chi non è ancora fidato.</param>
    public async Task<CusMessage> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default, int maxBytes = SecureChannel.MaxMessageBytes)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cts.CancelAfter(timeout);
        var message = CusMessage.Deserialize(await _channel.ReceiveAsync(cts.Token, maxBytes));
        Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
        return message;
    }

    public async Task<T> ReceiveAsync<T>(TimeSpan timeout, CancellationToken cancellationToken = default, int maxBytes = SecureChannel.MaxMessageBytes) where T : CusMessage
    {
        var message = await ReceiveAsync(timeout, cancellationToken, maxBytes);
        return message as T ?? throw new ProtocolException($"Atteso {typeof(T).Name}, ricevuto {message.GetType().Name}.");
    }

    /// <exception cref="MessageTooLargeException">Il messaggio serializzato non entra in un frame: non inviato, connessione intatta.</exception>
    /// <exception cref="IOException">Connessione chiusa o invio fallito (anche per <see cref="SendTimeout"/>).</exception>
    public async Task SendAsync(CusMessage message, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
            throw new IOException($"Connessione con {Remote.DisplayName} chiusa.");
        var payload = message.Serialize();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cts.CancelAfter(SendTimeout);
        try
        {
            await _channel.SendAsync(payload, cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Un invio interrotto, anche per annullamento di chi chiama, può lasciare un frame a metà
            // e il flusso cifrato avanzato: la connessione non è più utilizzabile e si chiude sempre.
            Close($"invio fallito: {ex.Message}");
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
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
        reason ??= "chiusura";
        _lifetime.Cancel();
        _handlerQueue.Writer.TryComplete();
        foreach (var pending in _pending.Values)
            pending.TrySetCanceled();
        _ = _channel.DisposeAsync().AsTask();

        Action<PeerConnection, string>? handlers;
        lock (_closeGate)
        {
            _closeReason = reason;
            handlers = _closedHandlers;
            _closedHandlers = null;
        }

        if (handlers is not null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Action<PeerConnection, string>>())
                InvokeClosed(handler, reason);
        }
    }

    private void InvokeClosed(Action<PeerConnection, string> handler, string reason)
    {
        try
        {
            handler(this, reason);
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
        foreach (var task in new[] { _receiveLoop, _handlerLoop, _pingLoop })
        {
            if (task is not null)
                await task.ConfigureAwait(false);
        }
    }

    private static async Task<PeerConnection> HandshakeAsync(
        TcpClient client, DeviceIdentity identity, int listenPort, bool isInitiator, Func<string, bool>? revealTo, CancellationToken ct)
    {
        var endPoint = client.Client.RemoteEndPoint;
        var channel = await SecureChannel.EstablishAsync(client.GetStream(), identity, isInitiator, revealTo, ct);
        try
        {
            await channel.SendAsync(new HelloMessage(identity.DisplayName, identity.UserName, listenPort).Serialize(), ct);
            // Chiunque in rete arriva fin qui: il saluto (due nomi e una porta) non può pesare 8 MB.
            var hello = CusMessage.Deserialize(await channel.ReceiveAsync(ct, HelloMaxBytes)) as HelloMessage
                ?? throw new ProtocolException("Atteso il saluto iniziale.");
            var remote = new DeviceInfo(
                channel.RemoteDeviceId,
                DisplayText.Sanitize(hello.DisplayName, 64),
                DisplayText.Sanitize(hello.UserName, 64, ""),
                channel.RemoteIdentityKey);
            return new PeerConnection(channel, remote, endPoint, isInitiator) { RemoteListenPort = hello.ListenPort };
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

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
                        reason = DisplayText.Sanitize(bye.Reason, 200, "chiusura dall'altra parte");
                        return;
                    case IReplyMessage reply when _pending.TryRemove(reply.RequestId, out var tcs):
                        tcs.TrySetResult(message);
                        continue;
                }

                // Gli handler girano in un ciclo a parte: un handler lento (per esempio una scrittura
                // nella clipboard in attesa) non deve fermare risposte e ping, altrimenti una richiesta
                // fatta mentre l'handler aspetta riceverebbe la risposta solo dopo il timeout.
                await _handlerQueue.Writer.WriteAsync(message, _lifetime.Token);
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

    private async Task HandlerLoopAsync()
    {
        try
        {
            await foreach (var message in _handlerQueue.Reader.ReadAllAsync())
            {
                if (_lifetime.IsCancellationRequested)
                    return;
                if (MessageHandler is { } handler)
                    await handler(this, message);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close("connessione interrotta");
        }
        catch (ProtocolException ex)
        {
            Close($"errore di protocollo: {ex.Message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: errore nella gestione di un messaggio da {Remote.DisplayName}: {ex}");
            Close($"errore interno: {ex.Message}");
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
