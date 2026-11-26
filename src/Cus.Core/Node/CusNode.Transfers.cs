using Cus.Core.Abstractions;
using Cus.Core.Net;

namespace Cus.Core.Node;

/// <summary>
/// File lazy dal lato di chi li ha copiati: le offerte (file copiati qui) e i trasferimenti verso
/// chi incolla. Copiare altro non interrompe un trasferimento già in corso: l'offerta resta
/// leggibile finché qualcuno la sta scaricando. Un nuovo incolla di file superati invece fallisce
/// con un errore chiaro, come prima.
/// </summary>
public sealed partial class CusNode
{
    /// <summary>Trasferimenti finiti (o annullati) restano in elenco per questo tempo.</summary>
    private static readonly TimeSpan FinishedTransferRetention = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TransferNotifyInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Protetti da _offerGate.</summary>
    private readonly Dictionary<string, LocalOffer> _offers = [];
    private readonly Dictionary<(string ItemId, string RequesterId), TransferRecord> _transfers = [];
    private string? _currentOfferId;
    private long _lastTransferNotifyTicks;
    private (int Total, int Active) _lastTransferCounts;

    /// <summary>Elenco o avanzamento dei trasferimenti cambiato (al massimo ogni 250 ms per l'avanzamento).</summary>
    public event EventHandler? TransfersChanged;

    /// <summary>Trasferimenti dei file copiati su questo PC: in corso e finiti da poco.</summary>
    public IReadOnlyList<TransferView> Transfers
    {
        get
        {
            lock (_offerGate)
            {
                PruneTransfers(DateTimeOffset.UtcNow);
                return _transfers.Values.Select(t => t.ToView()).OrderBy(t => t.State).ThenBy(t => t.RequesterName).ToList();
            }
        }
    }

    /// <summary>Annulla un trasferimento: chi incolla riceve un errore alla prossima lettura.</summary>
    public void CancelTransfer(string itemId, string requesterId)
    {
        lock (_offerGate)
        {
            if (!_transfers.TryGetValue((itemId, requesterId), out var record) || record.State != TransferState.Active)
                return;
            record.State = TransferState.Cancelled;
            record.LastActivity = DateTimeOffset.UtcNow;
            Log(Loc.T("transfers.transferOfToCancelled", record.ItemName, record.RequesterName));
        }

        OnTransfersChanged(force: true);
    }

    /// <summary>Nuova copia su questo PC: diventa l'offerta corrente (se sono file).</summary>
    private void RegisterLocalOffer(string itemId, ClipboardContent content)
    {
        lock (_offerGate)
        {
            if (content.Kind == ClipboardContentKind.Files && content.Files is { } files)
            {
                _offers[itemId] = new LocalOffer(files, ClipboardSummary.From(content, "", "", false, false).Title);
                _currentOfferId = itemId;
            }
            else
            {
                _currentOfferId = null;
            }

            PruneTransfers(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>L'offerta è uscita da questo PC: da qui gli altri partecipanti possono leggerla.</summary>
    private void MarkOfferShared(string itemId)
    {
        lock (_offerGate)
        {
            if (_offers.TryGetValue(itemId, out var offer))
                offer.Shared = true;
        }
    }

    private bool IsCurrentOffer(string itemId)
    {
        lock (_offerGate)
            return _currentOfferId == itemId && _offers.ContainsKey(itemId);
    }

    /// <summary>
    /// Legge un pezzo di file di un'offerta di questo PC per <paramref name="requesterId"/>. False se
    /// l'offerta non c'è più (copiato altro e nessun trasferimento in corso per chi chiede).
    /// </summary>
    internal bool TryReadLocalOffer(string itemId, int fileIndex, long offset, int count, string requesterId, string requesterName, out byte[] data)
    {
        data = [];
        var self = requesterId == Identity.DeviceId;
        LocalOffer offer;
        TransferRecord? record = null;
        lock (_offerGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneTransfers(now);
            if (!_offers.TryGetValue(itemId, out offer!))
                return false;
            var isCurrent = _currentOfferId == itemId;

            if (self)
            {
                if (!isCurrent)
                    return false;
            }
            else
            {
                // Copie fatte in sola lettura (o sensibili non concesse) non sono mai uscite da qui.
                if (!offer.Shared)
                    throw new RemoteFileUnavailableException(WireTexts.NotShared.Text);

                _transfers.TryGetValue((itemId, requesterId), out record);
                if (record?.State == TransferState.Cancelled)
                    throw new RemoteFileUnavailableException(WireTexts.TransferCancelledBy.Format(Identity.DisplayName));

                // Un trasferimento in corso continua anche se nel frattempo qui si è copiato altro;
                // uno nuovo parte solo dall'offerta corrente.
                var continuing = record is { State: TransferState.Active or TransferState.Completed }
                    && now - record.LastActivity < _options.TransferIdleTimeout;
                if (!isCurrent && !continuing)
                    return false;

                // Primo pezzo, ripresa dopo una pausa o lo stesso PC che incolla di nuovo da capo:
                // nuovo trasferimento.
                var restarting = continuing && record!.State == TransferState.Completed && fileIndex == 0 && offset == 0;

                // Ricominciare da capo è un incolla nuovo: vale solo per l'offerta corrente, anche
                // per chi ha appena finito di scaricare quella superata.
                if (restarting && !isCurrent)
                    return false;
                if (!continuing || restarting)
                {
                    record = new TransferRecord(itemId, requesterId, DisplayText.Sanitize(requesterName, 64), offer.Name, offer.TotalBytes);
                    _transfers[(itemId, requesterId)] = record;
                }

                record!.LastActivity = now;
            }
        }

        if (record is not null)
            OnTransfersChanged(force: false);

        IReadOnlyList<ClipboardFileInfo> files = offer.Files;
        if (fileIndex < 0 || fileIndex >= files.Count || files[fileIndex].LocalPath is not { } path)
            throw new RemoteFileUnavailableException(WireTexts.InvalidFileRequested.Text);
        if (offset < 0 || count <= 0)
            throw new RemoteFileUnavailableException(WireTexts.InvalidRange.Text);

        count = Math.Min(count, MaxFileReadBytes);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset < stream.Length)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
                stream.ReadExactly(buffer);
                data = buffer;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RemoteFileUnavailableException(WireTexts.FileNoLongerReadable.Format(files[fileIndex].Name, ex.Message));
        }

        if (record is not null)
        {
            bool finished;
            lock (_offerGate)
            {
                record.RecordRead(fileIndex, offset + data.Length);
                record.LastActivity = DateTimeOffset.UtcNow;
                finished = record.State == TransferState.Active && record.BytesDone >= record.TotalBytes;
                if (finished)
                    record.State = TransferState.Completed;
            }

            if (finished)
                Log(Loc.T("transfers.transferredTo", record.ItemName, record.RequesterName));
            OnTransfersChanged(force: finished);
        }

        return true;
    }

    private async Task ServeFileReadAsync(PeerConnection connection, FileReadRequestMessage request)
    {
        FileReadReplyMessage reply;
        try
        {
            var data = await ReadFileForPeerAsync(connection, request);
            reply = new FileReadReplyMessage(request.RequestId, data, null);
        }
        catch (RemoteFileUnavailableException ex)
        {
            reply = new FileReadReplyMessage(request.RequestId, null, ex.Message);
        }
        catch (Exception ex)
        {
            reply = new FileReadReplyMessage(request.RequestId, null, WireTexts.FileReadFailed.Format(ex.Message));
        }

        await SendFileReadReplyAsync(connection, reply);
    }

    private static async Task SendFileReadReplyAsync(PeerConnection connection, FileReadReplyMessage reply)
    {
        try
        {
            await connection.SendAsync(reply);
        }
        catch (IOException)
        {
            // Chi ha chiesto si è disconnesso.
        }
    }

    /// <summary>
    /// Lettura dal disco di questo PC. Ogni lettura tiene in memoria fino a 1 MB: un numero limitato
    /// alla volta. Lo slot copre solo la lettura locale, non l'attesa di un altro PC (inoltro
    /// dell'host): un PC d'origine che non risponde non blocca le letture degli altri.
    /// </summary>
    private async Task<(bool Found, byte[] Data)> ReadLocalOfferAsync(FileReadRequestMessage request, string requesterId, string requesterName)
    {
        if (!await _fileReadSlots.WaitAsync(FileReadTimeout, _lifetime.Token))
            throw new RemoteFileUnavailableException(WireTexts.OriginBusy.Text);
        try
        {
            var found = TryReadLocalOffer(request.ItemId!, request.FileIndex, request.Offset, request.Count, requesterId, requesterName, out var data);
            return (found, data);
        }
        finally
        {
            _fileReadSlots.Release();
        }
    }

    private async Task<byte[]> ReadFileForPeerAsync(PeerConnection connection, FileReadRequestMessage request)
    {
        var host = _hostSession;
        var member = _memberSession;
        var fromMember = host?.Members.Values.Any(m => m.Connection == connection) == true;
        var fromHost = member?.Host == connection;
        if (!fromMember && !fromHost)
            throw new RemoteFileUnavailableException(WireTexts.NotInSession.Text);

        // Chi incolla davvero: il dispositivo collegato, o quello per cui l'host inoltra (solo l'host
        // può inoltrare: un membro che lo dichiara viene ignorato).
        var (requesterId, requesterName) = fromHost && request.ForDeviceId is { Length: 32 } forId && forId.All(char.IsAsciiHexDigit)
            ? (forId.ToUpperInvariant(), DisplayText.Sanitize(request.ForName, 64))
            : (connection.Remote.DeviceId, connection.Remote.DisplayName);

        if (request.ItemId is null)
            throw new RemoteFileUnavailableException(WireTexts.InvalidRequest.Text);
        var (found, data) = await ReadLocalOfferAsync(request, requesterId, requesterName);
        if (found)
            return data;

        if (host is not null)
        {
            var originId = host.ItemOrigins.TryGetValue(request.ItemId, out var id)
                ? id
                : _database.GetHistoryEntry(request.ItemId)?.OriginDeviceId;
            if (originId is not null && originId != Identity.DeviceId)
            {
                if (!host.Members.TryGetValue(originId, out var origin) || origin.Connection == connection)
                    throw new RemoteFileUnavailableException(WireTexts.OriginLeft.Text);
                return await new RemoteFileSource(this, origin.Connection, request.ItemId, notify: false,
                        forDeviceId: connection.Remote.DeviceId, forName: connection.Remote.DisplayName)
                    .ReadAsync(request.FileIndex, request.Offset, request.Count);
            }
        }

        throw new RemoteFileUnavailableException(WireTexts.OriginCopiedElse.Text);
    }

    /// <summary>
    /// Sotto _offerGate. Segna come fermi i trasferimenti senza letture, toglie dall'elenco quelli
    /// finiti da un po' e scarta le offerte superate che nessuno sta più scaricando.
    /// </summary>
    private void PruneTransfers(DateTimeOffset now)
    {
        foreach (var (key, record) in _transfers.ToList())
        {
            if (record.State == TransferState.Active && now - record.LastActivity >= _options.TransferIdleTimeout)
            {
                record.State = TransferState.Stalled;
                record.LastActivity = now;
            }

            if (record.State != TransferState.Active && now - record.LastActivity >= FinishedTransferRetention)
                _transfers.Remove(key);
        }

        foreach (var itemId in _offers.Keys.ToList())
        {
            if (itemId != _currentOfferId
                && !_transfers.Values.Any(t => t.ItemId == itemId && t.State is TransferState.Active or TransferState.Completed && now - t.LastActivity < _options.TransferIdleTimeout))
                _offers.Remove(itemId);
        }
    }

    /// <summary>Notifica i cambiamenti; l'avanzamento al massimo ogni <see cref="TransferNotifyInterval"/>.</summary>
    private void OnTransfersChanged(bool force)
    {
        // I conteggi dell'indicatore si aggiornano sempre (un trasferimento nuovo non aspetta il
        // prossimo avanzamento); solo l'evento di avanzamento è limitato nel tempo.
        bool countsChanged;
        lock (_offerGate)
        {
            var counts = (_transfers.Count, _transfers.Values.Count(t => t.State == TransferState.Active));
            countsChanged = counts != _lastTransferCounts;
            _lastTransferCounts = counts;
        }

        if (countsChanged)
            UpdateOverlay();

        var now = Environment.TickCount64;
        if (!force && !countsChanged && now - Interlocked.Read(ref _lastTransferNotifyTicks) < TransferNotifyInterval.TotalMilliseconds)
            return;
        Interlocked.Exchange(ref _lastTransferNotifyTicks, now);
        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Controllo periodico: i trasferimenti fermi e quelli finiti escono dall'elenco anche senza letture.</summary>
    private async Task WatchTransfersAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                bool changed;
                lock (_offerGate)
                {
                    var before = _transfers.Values.Select(t => (t.ItemId, t.RequesterId, t.State)).ToList();
                    PruneTransfers(DateTimeOffset.UtcNow);
                    changed = !before.SequenceEqual(_transfers.Values.Select(t => (t.ItemId, t.RequesterId, t.State)));
                }

                if (changed)
                    OnTransfersChanged(force: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class LocalOffer(IReadOnlyList<ClipboardFileInfo> files, string name)
    {
        public IReadOnlyList<ClipboardFileInfo> Files { get; } = files;

        public string Name { get; } = name;

        public long TotalBytes { get; } = files.Sum(f => f.SizeBytes);

        /// <summary>Uscita da questo PC: solo allora gli altri possono leggerla.</summary>
        public bool Shared { get; set; }
    }

    private sealed class TransferRecord(string itemId, string requesterId, string requesterName, string itemName, long totalBytes)
    {
        public string ItemId { get; } = itemId;

        public string RequesterId { get; } = requesterId;

        public string RequesterName { get; } = requesterName;

        public string ItemName { get; } = itemName;

        public long TotalBytes { get; } = totalBytes;

        /// <summary>Per ogni file, fin dove è arrivato chi incolla: un pezzo letto due volte non conta doppio.</summary>
        private readonly Dictionary<int, long> _readUpTo = [];

        public long BytesDone { get; private set; }

        /// <summary>Sotto _offerGate.</summary>
        public void RecordRead(int fileIndex, long end)
        {
            _readUpTo.TryGetValue(fileIndex, out var previous);
            if (previous >= end)
                return;
            _readUpTo[fileIndex] = end;
            BytesDone = Math.Min(TotalBytes, BytesDone + (end - previous));
        }

        public TransferState State { get; set; } = TransferState.Active;

        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

        public TransferView ToView() => new(ItemId, RequesterId, RequesterName, ItemName, TotalBytes, BytesDone, State, LastActivity);
    }
}
