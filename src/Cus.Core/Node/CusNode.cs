using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Net;
using Cus.Core.Storage;

namespace Cus.Core.Node;

/// <summary>
/// Nodo CUS senza UI: rete, visibilità come host, sessioni, amici e clipboard.
/// La UI e i test lo usano allo stesso modo; le parti native arrivano dalle interfacce.
/// </summary>
public sealed partial class CusNode : IAsyncDisposable
{
    private static readonly TimeSpan FirstMessageTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Livello del patto: un numero, nient'altro.</summary>
    private const int FriendSyncMaxBytes = 1024;

    /// <summary>Primo messaggio (richiesta di ingresso): piccolo.</summary>
    private const int FirstMessageMaxBytes = 64 * 1024;

    /// <summary>Connessioni in fase iniziale da uno stesso indirizzo (il loopback, usato dalle istanze locali, è escluso).</summary>
    private const int MaxPendingIncomingPerAddress = 4;
    private static readonly TimeSpan FileReadTimeout = TimeSpan.FromSeconds(30);
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
    private readonly Lock _offerGate = new();
    private readonly SemaphoreSlim _incomingSlots = new(MaxPendingIncoming, MaxPendingIncoming);
    private readonly ConcurrentDictionary<IPAddress, int> _pendingByAddress = new();
    private readonly SemaphoreSlim _fileReadSlots = new(8, 8);

    /// <summary>Letture di file in corso per connessione (vedi <see cref="MaxFileReadsPerConnection"/>).</summary>
    private readonly ConcurrentDictionary<PeerConnection, int> _fileReadsByConnection = new();

    /// <summary>
    /// Letture di file in corso da una sola connessione. L'host inoltra sulla stessa connessione
    /// quelle di tutti i membri che incollano, quindi il limite non è stretto.
    /// </summary>
    private const int MaxFileReadsPerConnection = 16;

    /// <summary>Le copie locali si elaborano una alla volta, nell'ordine in cui sono avvenute.</summary>
    private readonly Channel<ClipboardChangedEventArgs> _localChanges = Channel.CreateUnbounded<ClipboardChangedEventArgs>(
        new UnboundedChannelOptions { SingleReader = true });

    private TcpListener? _listener;
    private Discovery? _discovery;
    private Task? _acceptLoop;
    private HostSession? _hostSession;
    private MemberSession? _memberSession;
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
        Friends = new FriendStore(database);
        Friends.Changed += (_, _) => PeersChanged?.Invoke(this, EventArgs.Empty);
        _monitor.Changed += (_, e) => _localChanges.Writer.TryWrite(e);
        _overlay.LeaveRequested += (_, _) => RunInBackground(() => LeaveSessionAsync());
        _overlay.TransfersRequested += (_, _) => TransfersRequested?.Invoke(this, EventArgs.Empty);
        _overlay.DirectionToggleRequested += (_, _) => RunInBackground(ToggleMyDirectionAsync);
    }

    public DeviceIdentity Identity { get; private set; }

    /// <summary>Il nome visibile di questo PC è cambiato.</summary>
    public event EventHandler? IdentityChanged;

    /// <summary>
    /// Cambia il nome con cui questo PC si mostra: lo salva, lo annuncia in rete e, se si è in una
    /// sessione, lo comunica all'host (che lo ripropone a tutti). Le chiavi restano le stesse.
    /// </summary>
    /// <returns>Il nome effettivo dopo la pulizia, oppure null se vuoto.</returns>
    public async Task<string?> SetDisplayNameAsync(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;
        var name = DisplayText.Sanitize(requested, 64);
        if (name == Identity.DisplayName)
            return name;

        MemberSession? member;
        bool host;
        await _gate.WaitAsync();
        try
        {
            Identity = Identity.WithDisplayName(name);
            _database.SaveIdentity(Identity);
            member = _memberSession;
            host = _hostSession is not null;
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        IdentityChanged?.Invoke(this, EventArgs.Empty);
        AnnounceSoon();
        if (host)
            await BroadcastStateAsync();
        else if (member is not null)
        {
            try { await member.Host.SendAsync(new RenameMessage(name)); }
            catch (IOException) { /* host caduto: la sessione si chiude altrove */ }
        }

        return name;
    }

    /// <summary>Patti reciproci e fiducia per i contenuti sensibili.</summary>
    public FriendStore Friends { get; }

    /// <summary>Porta TCP effettiva, nota dopo <see cref="StartAsync"/>.</summary>
    public int Port { get; private set; }

    public SessionView? Session { get; private set; }

    /// <summary>Ultimo contenuto della clipboard di questo PC, come lo mostra l'indicatore.</summary>
    public ClipboardSummary? CurrentClipboard { get; private set; }

    /// <summary>Sessione, visibilità, partecipanti, modalità: tutto ciò che mostra lo stato.</summary>
    public event EventHandler? SessionChanged;

    public event EventHandler? PeersChanged;

    public event EventHandler? CurrentClipboardChanged;

    /// <summary>L'utente ha chiesto dall'indicatore di vedere i trasferimenti dei suoi file.</summary>
    public event EventHandler? TransfersRequested;

    /// <summary>Righe di attività leggibili (copie inviate e ricevute, ingressi e uscite).</summary>
    public event Action<string>? Activity;

    /// <summary>
    /// Host visibili in rete, amici con patto "in qualsiasi momento" all'ultimo indirizzo noto e
    /// indirizzi inseriti a mano. Senza questo dispositivo.
    /// </summary>
    public IReadOnlyList<PeerView> Peers
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var friends = Friends.All.ToDictionary(f => f.DeviceId);
            // Gli annunci non sono autenticati: l'identità vera si verifica alla connessione
            // (JoinAsync con l'ID atteso), quindi un annuncio con l'ID di un amico non lo diventa.
            var result = _discovered.Values
                .Where(d => now - d.Seen < PeerExpiry && d.Announcement.DeviceId != Identity.DeviceId)
                .Select(d => new PeerView(d.Announcement.DeviceId, d.Announcement.DisplayName, d.Announcement.UserName,
                    d.Announcement.Host, d.Announcement.Port, d.Announcement.Participants, IsAnnounced: true,
                    friends.TryGetValue(d.Announcement.DeviceId, out var f) ? f.Agreement : AgreementLevel.None, IsManual: false,
                    f is null ? ImitatedFriend(d.Announcement.DisplayName, friends.Values) : null))
                .ToList();
            foreach (var friend in friends.Values)
            {
                if (friend.Agreement != AgreementLevel.Anytime || result.Any(p => p.DeviceId == friend.DeviceId) || friend.LastEndpoint is not { } known)
                    continue;
                result.Add(new PeerView(friend.DeviceId, friend.DisplayName, "", known.Host, known.Port, 0, IsAnnounced: false, friend.Agreement, IsManual: false));
            }

            foreach (var manual in ManualPeers())
            {
                if (!result.Any(p => p.Host == manual.Host && p.Port == manual.Port))
                    result.Add(new PeerView(null, manual.ToString(), "", manual.Host, manual.Port, 0, IsAnnounced: false, AgreementLevel.None, IsManual: true));
            }

            return result
                .OrderByDescending(p => p.IsAnnounced)
                .ThenByDescending(p => p.Agreement)
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// L'amico di cui un altro dispositivo porta il nome (a occhio: vedi <see cref="DisplayText.LooksAlike"/>), o null.
    /// Chi è davvero un amico lo dice l'ID verificato, mai il nome.
    /// </summary>
    private static string? ImitatedFriend(string displayName, IEnumerable<Friend> friends) =>
        friends.FirstOrDefault(f => DisplayText.LooksAlike(f.DisplayName, displayName))?.DisplayName;

    /// <summary>Come sopra, per un dispositivo già collegato (ID verificato).</summary>
    private string? ImitatedFriend(DeviceInfo remote) =>
        Friends.Get(remote.DeviceId) is null ? ImitatedFriend(remote.DisplayName, Friends.All) : null;

    // ================================================================ impostazioni

    /// <summary>Alla chiusura della finestra CUS resta attivo nell'area di notifica.</summary>
    public bool KeepRunningInBackground
    {
        get => _database.GetSetting("ui/run-in-background") != "0";
        set => _database.SetSetting("ui/run-in-background", value ? "1" : "0");
    }

    /// <summary>Lingua dell'interfaccia scelta dall'utente; null se non l'ha mai scelta (vale quella dell'installer).</summary>
    public AppLanguage? SavedLanguage
    {
        get => Loc.Parse(_database.GetSetting(Loc.SettingKey));
        set
        {
            if (value is { } language)
                _database.SetSetting(Loc.SettingKey, Loc.ToCode(language));
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

    public async Task StartAsync()
    {
        _listener = BindListener();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);

        if (_options.EnableDiscovery)
        {
            try
            {
                _discovery = new Discovery(Identity, AnnouncementOrNull, _options.DiscoveryPort);
                _discovery.Announced += OnAnnounced;
                _discovery.Start();
            }
            catch (SocketException ex)
            {
                _discovery = null;
                _notifier.Error(Loc.T("node.automaticDiscoveryUnavailableEnterTheIp", ex.Message));
            }
        }

        RunInBackground(ProcessLocalChangesAsync);
        RunInBackground(WatchTransfersAsync);
        _monitor.Start();
        Log(Loc.T("node.listeningOnPort", Port));
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

        try
        {
            await SetVisibleAsync(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: congedo dalla rete fallito: {ex.Message}");
        }

        _lifetime.Cancel();
        _listener?.Stop();
        if (_discovery is not null)
            await _discovery.DisposeAsync();
        foreach (var connection in _connections.Keys)
            connection.Close(Loc.T("node.cusIsClosing"));
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
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProtocolException)
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

    private async Task HandleIncomingAsync(TcpClient client)
    {
        PeerConnection? connection = null;
        try
        {
            // Assegnata prima della fase iniziale: se questa fallisce la connessione va chiusa (e tolta
            // da _connections), non solo il socket.
            connection = await PeerConnection.AcceptAsync(client, Identity, Port, _lifetime.Token, RevealsIdentityTo);
            await SetUpConnectionAsync(connection);
            var first = await connection.ReceiveAsync(FirstMessageTimeout, maxBytes: FirstMessageMaxBytes);
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

    // ================================================================ scoperta

    /// <summary>Annuncio di questo PC: solo mentre si offre come host.</summary>
    private AnnouncementPayload? AnnouncementOrNull() =>
        _visible ? new AnnouncementPayload(Identity.DeviceId, Identity.DisplayName, Identity.UserName, Port, Math.Max(1, Session?.Participants.Count ?? 1)) : null;

    private void OnAnnounced(DiscoveryAnnouncement announcement)
    {
        // Chiave = ID + indirizzo: gli annunci sono firmati, ma uno vero rispedito da un altro indirizzo
        // non sostituisce l'originale, compare accanto (e la richiesta lì fallisce la verifica d'identità).
        var key = $"{announcement.DeviceId}|{announcement.Host}|{announcement.Port}";
        if (announcement.Hidden)
        {
            if (_discovered.TryRemove(key, out _))
                PeersChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isNew = !_discovered.TryGetValue(key, out var previous);
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
        if (isNew || previous.Announcement != announcement)
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Annuncia subito lo stato (per esempio quanti sono nella sessione).</summary>
    private void AnnounceSoon()
    {
        if (_visible && _discovery is { } discovery)
            RunInBackground(discovery.AnnounceNowAsync);
    }

    /// <summary>Memorizza dove raggiungere un amico, anche quando non si annuncia.</summary>
    private void RememberEndpoint(PeerConnection connection)
    {
        if (connection.RemoteEndPoint is not IPEndPoint ip || connection.RemoteListenPort is <= 0 or > 65535)
            return;
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        Friends.RememberEndpoint(connection.Remote.DeviceId, connection.Remote.DisplayName, address.ToString(), connection.RemoteListenPort);
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
                    _notifier.Error(Loc.T("common.copyNotShared", ex.Message));
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
        if (message is FriendSyncMessage sync)
        {
            ApplyFriendSync(connection, sync.Level);
            return true;
        }

        if (message is FileReadRequestMessage read)
        {
            // Oltre un certo numero di letture in corso dalla stessa connessione le nuove si
            // rifiutano subito: una raffica di richieste non lascia gli altri senza risposta.
            if (_fileReadsByConnection.AddOrUpdate(connection, 1, (_, n) => n + 1) > MaxFileReadsPerConnection)
            {
                ReleaseFileRead(connection);
                RunInBackground(() => SendFileReadReplyAsync(connection,
                    new FileReadReplyMessage(read.RequestId, null, WireTexts.OriginBusy.Text)));
                return true;
            }

            RunInBackground(async () =>
            {
                try
                {
                    await ServeFileReadAsync(connection, read);
                }
                finally
                {
                    ReleaseFileRead(connection);
                }
            });
            return true;
        }

        return false;
    }

    // ================================================================ utilità

    private void Log(string message) => Activity?.Invoke(message);

    /// <summary>Ricalcola la vista della sessione e aggiorna l'indicatore.</summary>
    private void RaiseSessionChanged()
    {
        var previousCount = Session?.Participants.Count ?? 0;
        Session = _hostSession?.ToView(Identity, _visible) ?? _memberSession?.ToView(Identity);
        UpdateOverlay();
        SessionChanged?.Invoke(this, EventArgs.Empty);
        if ((Session?.Participants.Count ?? 0) != previousCount)
            AnnounceSoon();
    }

    /// <summary>L'indicatore compare solo quando la clipboard è davvero condivisa con qualcuno.</summary>
    private void UpdateOverlay()
    {
        var session = Session;
        if (session is not { IsSharing: true })
        {
            _overlay.Hide();
            return;
        }

        var transfers = Transfers;
        _overlay.Show(new SessionIndicatorState(
            session.Participants,
            CurrentClipboard,
            transfers.Count,
            transfers.Count(t => t.State == TransferState.Active),
            CanToggleDirection: session.Role == SessionRole.Member && session.MyGranted == ShareDirection.Bidirectional,
            session.MyDirection));
    }

    private void SetCurrentClipboard(ClipboardSummary summary)
    {
        CurrentClipboard = summary;
        UpdateOverlay();
        CurrentClipboardChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => Loc.T("node.connectionRefusedCusIsnTListening"),
        SocketException { SocketErrorCode: SocketError.TimedOut } or OperationCanceledException => Loc.T("node.noAnswerWithinTheTimeLimit"),
        SocketException { SocketErrorCode: SocketError.HostNotFound } => Loc.T("node.unknownHostName"),
        _ => ex.Message,
    };

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
}
