using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Cus.Core.Abstractions;
using Cus.Core.Crypto;
using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Net;
using Cus.Core.Storage;
using Cus.Core.Trust;

namespace Cus.Core.Node;

/// <summary>
/// Nodo CUS senza UI: rete, sessioni con host, abbinamento, lista di fiducia e clipboard.
/// La UI e i test lo usano allo stesso modo; le parti native arrivano dalle interfacce.
/// </summary>
public sealed partial class CusNode : IAsyncDisposable
{
    private static readonly TimeSpan FirstMessageTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Lista di fiducia da chi non è nel gruppo: in genere vuota, comunque non più di così.</summary>
    private const int PreTrustSyncMaxBytes = 1024 * 1024;

    /// <summary>Primo messaggio (invito o avvio dell'abbinamento): piccolo.</summary>
    private const int FirstMessageMaxBytes = 64 * 1024;

    /// <summary>Connessioni in fase iniziale da uno stesso indirizzo (il loopback, usato dalle istanze locali, è escluso).</summary>
    private const int MaxPendingIncomingPerAddress = 4;
    private static readonly TimeSpan FileReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(15);

    /// <summary>Connessioni in ingresso non ancora concluse (handshake, primo messaggio, consenso).</summary>
    private const int MaxPendingIncoming = 16;

    /// <summary>Annunci di scoperta ricordati: oltre, i nuovi si ignorano finché i vecchi non scadono.</summary>
    private const int MaxDiscovered = 256;

    private readonly CusNodeOptions _options;
    private readonly CusDatabase _database;
    private readonly IClipboardMonitor _monitor;
    private readonly IClipboardWriter _writer;
    private readonly IOverlayIndicator _overlay;
    private readonly IConsentPrompt _consent;
    private readonly IUserNotifier _notifier;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<PeerConnection, byte> _connections = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly ConcurrentDictionary<string, (DiscoveryAnnouncement Announcement, DateTimeOffset Seen)> _discovered = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSync = new();
    private readonly Lock _offerGate = new();
    private readonly SemaphoreSlim _incomingSlots = new(MaxPendingIncoming, MaxPendingIncoming);
    private readonly ConcurrentDictionary<IPAddress, int> _pendingByAddress = new();
    private readonly SemaphoreSlim _fileReadSlots = new(8, 8);

    /// <summary>Le copie locali si elaborano una alla volta, nell'ordine in cui sono avvenute.</summary>
    private readonly Channel<ClipboardChangedEventArgs> _localChanges = Channel.CreateUnbounded<ClipboardChangedEventArgs>(
        new UnboundedChannelOptions { SingleReader = true });

    private TcpListener? _listener;
    private Discovery? _discovery;
    private Task? _acceptLoop;
    private HostSession? _hostSession;
    private MemberSession? _memberSession;
    private (string ItemId, IReadOnlyList<ClipboardFileInfo> Files)? _localOffer;
    private PairingCode? _pairingCode;
    private int _syncScheduled;
    private long _clipboardStamp;
    private long _lastRemoteWriteStamp;
    private int _consentPending;

    public CusNode(
        CusNodeOptions options,
        CusDatabase database,
        DeviceIdentity identity,
        IClipboardMonitor monitor,
        IClipboardWriter writer,
        IOverlayIndicator overlay,
        IConsentPrompt consent,
        IUserNotifier notifier)
    {
        _options = options;
        _database = database;
        Identity = identity;
        _monitor = monitor;
        _writer = writer;
        _overlay = overlay;
        _consent = consent;
        _notifier = notifier;
        Trust = new TrustLog(database, identity);
        Trust.Changed += (_, _) => RunInBackground(OnTrustChangedAsync);
        _monitor.Changed += (_, e) => _localChanges.Writer.TryWrite(e);
        _overlay.LeaveRequested += (_, _) => RunInBackground(() => LeaveSessionAsync());
    }

    public DeviceIdentity Identity { get; }

    public TrustLog Trust { get; }

    /// <summary>Porta TCP effettiva, nota dopo <see cref="StartAsync"/>.</summary>
    public int Port { get; private set; }

    public SessionView? Session { get; private set; }

    public PairingCode? ActivePairingCode
    {
        get
        {
            var code = _pairingCode;
            return code is not null && code.ExpiresAt > DateTimeOffset.UtcNow && code.AttemptsLeft > 0 ? code : null;
        }
    }

    public event EventHandler? SessionChanged;

    public event EventHandler? PeersChanged;

    public event EventHandler? PairingCodeChanged;

    /// <summary>Righe di attività leggibili (copie inviate e ricevute, ingressi e uscite).</summary>
    public event Action<string>? Activity;

    /// <summary>Peer manuali e scoperti in rete, senza questo dispositivo.</summary>
    public IReadOnlyList<PeerView> Peers
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var trust = Trust.State;
            // Gli annunci non sono autenticati: "IsTrusted" dice solo che l'annuncio usa l'ID di un
            // dispositivo abbinato. L'identità vera si verifica alla connessione (InviteAsync con l'ID atteso).
            var result = _discovered.Values
                .Where(d => now - d.Seen < PeerExpiry && d.Announcement.DeviceId != Identity.DeviceId && !trust.IsRevoked(d.Announcement.DeviceId))
                .Select(d => new PeerView(d.Announcement.DeviceId, d.Announcement.DisplayName, d.Announcement.UserName,
                    d.Announcement.Host, d.Announcement.Port, trust.IsTrusted(d.Announcement.DeviceId), IsManual: false))
                .ToList();
            foreach (var (id, device) in trust.Trusted)
            {
                if (result.Any(p => p.DeviceId == id) || KnownEndpoint(id) is not { } known)
                    continue;
                result.Add(new PeerView(id, device.DisplayName, "", known.Host, known.Port, IsTrusted: true, IsManual: false));
            }

            foreach (var manual in ManualPeers())
            {
                if (!result.Any(p => p.Host == manual.Host && p.Port == manual.Port))
                    result.Add(new PeerView(null, manual.ToString(), "", manual.Host, manual.Port, IsTrusted: false, IsManual: true));
            }

            return result.OrderByDescending(p => p.IsTrusted).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Crea o carica l'identità del profilo.</summary>
    public static DeviceIdentity LoadOrCreateIdentity(CusDatabase database, string displayName, string userName)
    {
        var identity = database.LoadIdentity();
        if (identity is null)
        {
            identity = DeviceIdentity.Generate(displayName, userName);
            database.SaveIdentity(identity);
        }

        return identity;
    }

    /// <summary>
    /// Nuova identità (nuova coppia di chiavi): serve per riabbinare un dispositivo revocato.
    /// Va chiamata a nodo fermo; il nodo successivo userà la nuova identità.
    /// </summary>
    public static DeviceIdentity ResetIdentity(CusDatabase database)
    {
        var old = database.LoadIdentity();
        var identity = DeviceIdentity.Generate(old?.DisplayName ?? Environment.MachineName, old?.UserName ?? Environment.UserName);
        database.SaveIdentity(identity);
        return identity;
    }

    public async Task StartAsync()
    {
        _listener = BindListener();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);

        if (_options.EnableDiscovery)
        {
            try
            {
                _discovery = new Discovery(() => new AnnouncementPayload(Identity.DeviceId, Identity.DisplayName, Identity.UserName, Port), _options.DiscoveryPort);
                _discovery.Announced += OnAnnounced;
                _discovery.Start();
            }
            catch (SocketException ex)
            {
                _discovery = null;
                _notifier.Error($"Scoperta automatica non disponibile ({ex.Message}). Usa l'IP o il nome host a mano.");
            }
        }

        RunInBackground(ProcessLocalChangesAsync);
        _monitor.Start();
        Log($"In ascolto sulla porta {Port}.");
        RunInBackground(SyncWithKnownDevicesAsync);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LeaveSessionAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: uscita dalla sessione fallita: {ex.Message}");
        }

        _lifetime.Cancel();
        _listener?.Stop();
        if (_discovery is not null)
            await _discovery.DisposeAsync();
        foreach (var connection in _connections.Keys)
            connection.Close("chiusura di CUS");
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (Exception) { /* chiusura */ }
        }

        // Chi chiama chiude il database subito dopo: le attività in sottofondo (connessioni in
        // ingresso, sincronizzazioni, uscite dei membri) devono essere finite, entro un limite.
        var pending = _background.Keys.ToArray();
        if (pending.Length > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(BackgroundShutdownTimeout));
    }

    private static readonly TimeSpan BackgroundShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lavoro in sottofondo: un'eccezione si registra invece di perdersi, e <see cref="DisposeAsync"/>
    /// aspetta che finisca prima che si chiuda il database.
    /// </summary>
    private void RunInBackground(Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex) when (_lifetime.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException)
            {
                // Chiusura in corso.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: errore in un'attività in sottofondo: {ex}");
            }
        });
        _background[task] = 0;
        task.ContinueWith(t => _background.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ================================================================ peer manuali

    public IReadOnlyList<PeerEndpoint> ManualPeers() =>
        _options.ManualPeers
            .Concat(_database.GetManualPeers().Select(a => PeerEndpoint.Parse(a)))
            .Distinct()
            .ToList();

    public void AddManualPeer(string address)
    {
        var endpoint = PeerEndpoint.Parse(address);
        _database.AddManualPeer(endpoint.ToString());
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveManualPeer(string address)
    {
        _database.RemoveManualPeer(PeerEndpoint.Parse(address).ToString());
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

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
            $"Nessuna porta libera tra {NetworkDefaults.DefaultPort} e {NetworkDefaults.PortRangeEnd}. Chiudi altre istanze di CUS o usa --port.");
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
                continue;
            }

            // Chiunque in rete può collegarsi: oltre un certo numero di connessioni ancora in fase
            // iniziale le nuove si rifiutano subito, invece di accumulare memoria e task.
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (!TryTakePendingSlot(address))
            {
                client.Dispose();
                continue;
            }

            RunInBackground(async () =>
            {
                try
                {
                    await HandleIncomingAsync(client);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"CUS: errore nella connessione in ingresso: {ex}");
                }
                finally
                {
                    ReleasePendingSlot(address);
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

    private void ReleaseAddress(IPAddress address)
    {
        if (_pendingByAddress.AddOrUpdate(address, 0, (_, n) => n - 1) <= 0)
            _pendingByAddress.TryRemove(new KeyValuePair<IPAddress, int>(address, 0));
    }

    /// <summary>Handshake, saluto e scambio della lista di fiducia: comune a ogni connessione.</summary>
    /// <param name="shareWith">Dispositivi a cui consegnare la lista anche se ora non sono fidati (auto-revoca).</param>
    private async Task<PeerConnection> SetUpConnectionAsync(PeerConnection connection, IReadOnlySet<string>? shareWith = null)
    {
        _connections[connection] = 0;
        connection.Closed += (c, _1) => _connections.TryRemove(c, out byte _);

        var remoteId = connection.Remote.DeviceId;
        var inGroup = Trust.State.IsGroupMember(remoteId);
        var share = inGroup || shareWith?.Contains(remoteId) == true;
        await connection.SendAsync(new TrustSyncMessage(share ? Trust.Entries : []));
        var sync = await connection.ReceiveAsync<TrustSyncMessage>(PeerConnection.HandshakeTimeout,
            maxBytes: inGroup ? SecureChannel.MaxMessageBytes : PreTrustSyncMaxBytes);
        if (inGroup)
        {
            Trust.Merge(sync.Entries ?? []);
            _lastSync[remoteId] = DateTimeOffset.UtcNow;
        }

        if (Trust.State.IsTrusted(remoteId))
            RememberEndpoint(connection);

        if (Trust.State.SelfRevoked)
        {
            await connection.CloseAsync("dispositivo revocato");
            throw new CusOperationException("Questo dispositivo è stato revocato: genera una nuova identità e riabbinalo.");
        }

        if (Trust.State.IsRevoked(remoteId))
        {
            await connection.CloseAsync("dispositivo revocato");
            throw new CusOperationException($"{connection.Remote.DisplayName} è un dispositivo revocato.");
        }

        return connection;
    }

    private async Task<PeerConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken, IReadOnlySet<string>? shareWith = null)
    {
        PeerConnection connection;
        try
        {
            connection = await PeerConnection.ConnectAsync(host, port, Identity, Port, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProtocolException)
        {
            throw new CusOperationException($"Impossibile collegarsi a {new PeerEndpoint(host, port)}: {Describe(ex)}", ex);
        }

        try
        {
            return await SetUpConnectionAsync(connection, shareWith);
        }
        catch (Exception ex) when (ex is not CusOperationException)
        {
            connection.Close("errore iniziale");
            throw new CusOperationException($"Collegamento con {new PeerEndpoint(host, port)} non riuscito: {Describe(ex)}", ex);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client)
    {
        PeerConnection? connection = null;
        try
        {
            // Assegnata prima della fase iniziale: se questa fallisce la connessione va chiusa (e tolta
            // da _connections), non solo il socket.
            connection = await PeerConnection.AcceptAsync(client, Identity, Port, _lifetime.Token);
            await SetUpConnectionAsync(connection);
            var first = await connection.ReceiveAsync(FirstMessageTimeout, maxBytes: FirstMessageMaxBytes);
            switch (first)
            {
                case SessionInviteMessage invite:
                    await HandleInviteAsync(connection, invite);
                    break;
                case PairingStartMessage start:
                    await HandlePairingStartAsync(connection, start);
                    break;
                default:
                    // Connessione di sola sincronizzazione (o saluto): si chiude.
                    connection.Close("fine");
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

    // ================================================================ scoperta e sincronizzazione

    private void OnAnnounced(DiscoveryAnnouncement announcement)
    {
        // Chiave = ID + indirizzo: un annuncio falso con l'ID di un dispositivo abbinato non sostituisce
        // quello vero, compare accanto (e l'invito a quell'indirizzo fallisce la verifica d'identità).
        var key = $"{announcement.DeviceId}|{announcement.Host}|{announcement.Port}";
        var now = DateTimeOffset.UtcNow;
        var isNew = !_discovered.ContainsKey(key);
        if (isNew && _discovered.Count >= MaxDiscovered)
        {
            foreach (var (oldKey, old) in _discovered)
            {
                if (now - old.Seen >= PeerExpiry)
                    _discovered.TryRemove(oldKey, out _);
            }

            if (_discovered.Count >= MaxDiscovered)
                return;
        }

        _discovered[key] = (announcement, now);
        if (isNew)
            PeersChanged?.Invoke(this, EventArgs.Empty);

        if (Trust.State.IsTrusted(announcement.DeviceId)
            && (!_lastSync.TryGetValue(announcement.DeviceId, out var last) || DateTimeOffset.UtcNow - last > SyncInterval))
        {
            _lastSync[announcement.DeviceId] = DateTimeOffset.UtcNow;
            RunInBackground(() => SyncWithAsync(announcement.Host, announcement.Port, announcement.DeviceId));
        }
    }

    /// <summary>Sincronizza la lista di fiducia con un dispositivo dell'utente e chiude.</summary>
    /// <param name="expectedDeviceId">Per gli annunci (non autenticati): l'identità che deve rispondere.</param>
    public async Task SyncWithAsync(string host, int port, string? expectedDeviceId = null)
    {
        try
        {
            var connection = await ConnectAsync(host, port, _lifetime.Token);
            if (expectedDeviceId is not null && !string.Equals(connection.Remote.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                connection.Close("identità diversa da quella annunciata");
                return;
            }

            await connection.CloseAsync("sincronizzazione completata");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: sincronizzazione con {host}:{port} non riuscita: {ex.Message}");
        }
    }

    private async Task SyncWithKnownDevicesAsync()
    {
        var targets = Peers.Where(p => p.IsTrusted).ToList();
        foreach (var peer in targets)
            await SyncWithAsync(peer.Host, peer.Port);
    }

    private async Task OnTrustChangedAsync()
    {
        var state = Trust.State;
        PeersChanged?.Invoke(this, EventArgs.Empty);

        if (state.SelfRevoked)
        {
            _notifier.Error("Questo dispositivo è stato revocato. Per riabbinarlo genera una nuova identità dalle impostazioni.");
            await LeaveSessionAsync();
        }

        // Una revoca chiude subito ogni connessione con il dispositivo revocato.
        foreach (var connection in _connections.Keys)
        {
            if (state.SelfRevoked || state.IsRevoked(connection.Remote.DeviceId))
                await connection.CloseAsync("dispositivo revocato");
        }

        // Propaga la lista ai dispositivi dell'utente collegati in questo momento (solo a connessioni
        // già avviate: in quelle in fase iniziale la lista viaggia comunque nello scambio previsto).
        foreach (var connection in _connections.Keys.Where(c => c.IsStarted && state.IsTrusted(c.Remote.DeviceId)))
        {
            try
            {
                await connection.SendAsync(new TrustSyncMessage(Trust.Entries));
            }
            catch (IOException)
            {
                // Connessione caduta: riceverà la lista alla prossima.
            }
        }

        // ...e a quelli raggiungibili ma non collegati. Una sola sincronizzazione alla volta: si
        // ferma da sola quando nessuno ha più voci nuove.
        if (!state.SelfRevoked && Interlocked.Exchange(ref _syncScheduled, 1) == 0)
        {
            RunInBackground(async () =>
            {
                try
                {
                    await Task.Delay(200, _lifetime.Token);
                    Interlocked.Exchange(ref _syncScheduled, 0);
                    await SyncWithKnownDevicesAsync();
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    private PeerEndpoint? KnownEndpoint(string deviceId)
    {
        var value = _database.GetSetting($"endpoint/{deviceId}");
        if (value is null)
            return null;
        try
        {
            return PeerEndpoint.Parse(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Memorizza dove raggiungere un dispositivo dell'utente (per consegnargli la lista di fiducia).</summary>
    private void RememberEndpoint(PeerConnection connection)
    {
        if (connection.RemoteEndPoint is not IPEndPoint ip || connection.RemoteListenPort is <= 0 or > 65535)
            return;
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        var endpoint = new PeerEndpoint(address.ToString(), connection.RemoteListenPort);
        if (KnownEndpoint(connection.Remote.DeviceId) != endpoint)
            _database.SetSetting($"endpoint/{connection.Remote.DeviceId}", endpoint.ToString());
    }

    private async Task ProcessLocalChangesAsync()
    {
        try
        {
            await foreach (var change in _localChanges.Reader.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    await OnLocalClipboardChangedAsync(change);
                }
                catch (Exception ex)
                {
                    _notifier.Error($"Copia non condivisa: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Gestione dei messaggi validi in qualunque momento della connessione.</summary>
    private bool HandleCommonMessage(PeerConnection connection, CusMessage message)
    {
        if (message is TrustSyncMessage sync)
        {
            if (Trust.State.IsGroupMember(connection.Remote.DeviceId))
                Trust.Merge(sync.Entries ?? []);
            return true;
        }

        if (message is FileReadRequestMessage read)
        {
            RunInBackground(() => ServeFileReadAsync(connection, read));
            return true;
        }

        return false;
    }

    // ================================================================ utilità

    private void Log(string message) => Activity?.Invoke(message);

    private void RaiseSessionChanged()
    {
        Session = _hostSession?.ToView(Identity) ?? _memberSession?.View;
        if (Session is null)
            _overlay.Hide();
        else
            _overlay.Show(new SessionIndicatorState(Session.Participants));
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "connessione rifiutata (CUS non è in ascolto su quella porta)",
        SocketException { SocketErrorCode: SocketError.TimedOut } or OperationCanceledException => "nessuna risposta entro il tempo massimo",
        SocketException { SocketErrorCode: SocketError.HostNotFound } => "nome host sconosciuto",
        _ => ex.Message,
    };

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    internal static byte[] PairingChannelId(byte[] initiatorKey, byte[] responderKey) =>
        [.. "CUS/pairing/ci/v1"u8, .. initiatorKey, .. responderKey];

    internal static byte[] PairingConfirmation(byte[] isk, char role, byte[] channelId, byte[] sid) =>
        HMACSHA512.HashData(isk, (byte[])[.. "CUS/pairing/confirm/v1"u8, (byte)role, .. channelId, .. sid]);

    internal static CPace CreateCPace(CPaceRole role, string code, byte[] channelId, byte[] sid) =>
        CPace.Create(role, Utf8(code), channelId, sid, []);
}
