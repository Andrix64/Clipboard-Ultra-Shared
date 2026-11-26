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
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FileReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(15);

    /// <summary>Connessioni in ingresso non ancora concluse (handshake, primo messaggio, consenso).</summary>
    private const int MaxPendingIncoming = 16;

    /// <summary>Annunci di scoperta ricordati: oltre, i nuovi si ignorano finché i vecchi non scadono.</summary>
    private const int MaxDiscovered = 256;

    /// <summary>Peer scoperti accettati da uno stesso indirizzo (più istanze per PC sono legittime).</summary>
    private const int MaxDiscoveredPerAddress = 8;

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

    /// <summary>
    /// Copie ricevute da scrivere nella clipboard di questo PC. Si decidono sotto _gate, nell'ordine dei
    /// numeri di sequenza, e si scrivono qui fuori: una clipboard occupata (un altro programma, o un
    /// incolla di file in corso) non deve fermare la sessione.
    /// </summary>
    private readonly Channel<ClipboardWrite> _clipboardWrites = Channel.CreateUnbounded<ClipboardWrite>(
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
        _overlay.IncomingTransfersRequested += (_, _) => IncomingTransfersRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>L'utente ha chiesto dall'indicatore di vedere i file che sta ricevendo.</summary>
    public event EventHandler? IncomingTransfersRequested;

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

    /// <summary>CUS parte all'accesso al sistema operativo. Attivo finché l'utente non lo spegne.</summary>
    public bool LaunchAtLogin
    {
        get => _database.GetSetting("app/launch-at-login") != "0";
        set => _database.SetSetting("app/launch-at-login", value ? "1" : "0");
    }

    /// <summary>
    /// Codice della lingua dell'interfaccia scelta dall'utente; null se non l'ha mai scelta (vale quella
    /// dell'installer) o se non è più disponibile.
    /// </summary>
    public string? SavedLanguage
    {
        get => Loc.Parse(_database.GetSetting(Loc.SettingKey));
        set
        {
            if (Loc.Parse(value) is { } code)
                _database.SetSetting(Loc.SettingKey, code);
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
        RunInBackground(ProcessClipboardWritesAsync);
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
        var incoming = IncomingTransfers;
        _overlay.Show(new SessionIndicatorState(
            session.Participants,
            CurrentClipboard,
            transfers.Count,
            transfers.Count(t => t.State == TransferState.Active),
            CanToggleDirection: session.Role == SessionRole.Member && session.MyGranted == ShareDirection.Bidirectional,
            session.MyDirection,
            incoming.Count,
            incoming.Count(t => t.State == TransferState.Active)));
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
