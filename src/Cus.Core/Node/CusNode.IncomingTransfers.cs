using Cus.Core.Abstractions;

namespace Cus.Core.Node;

/// <summary>
/// File di altri PC che questo PC sta incollando: l'altra metà dei trasferimenti. Si contano le
/// letture fatte quando si incolla (non gli inoltri dell'host per conto di un membro), con la
/// possibilità di annullarle: le letture successive falliscono e l'incolla si interrompe.
/// </summary>
public sealed partial class CusNode
{
    /// <summary>Per elemento copiato: questo PC è sempre chi incolla. Protetto da _offerGate.</summary>
    private readonly Dictionary<string, IncomingRecord> _incoming = [];

    /// <summary>File che questo PC sta ricevendo: in corso e finiti da poco.</summary>
    public IReadOnlyList<IncomingTransferView> IncomingTransfers
    {
        get
        {
            lock (_offerGate)
            {
                PruneIncoming(DateTimeOffset.UtcNow);
                return _incoming.Values.Select(t => t.ToView()).OrderBy(t => t.State).ThenBy(t => t.OriginName).ToList();
            }
        }
    }

    /// <summary>Annulla un file in arrivo: la prossima lettura fallisce e l'incolla si interrompe.</summary>
    public void CancelIncomingTransfer(string itemId)
    {
        lock (_offerGate)
        {
            if (!_incoming.TryGetValue(itemId, out var record) || record.State != TransferState.Active)
                return;
            record.State = TransferState.Cancelled;
            record.LastActivity = DateTimeOffset.UtcNow;
            Log(Loc.T("transfers.incomingOfFromCancelled", record.ItemName, record.OriginName));
        }

        OnTransfersChanged(force: true);
    }

    /// <summary>Prima di chiedere un pezzo: crea o riprende il trasferimento, o rifiuta se annullato.</summary>
    private IncomingRecord BeginIncomingRead(string itemId, IncomingFiles files, int fileIndex, long offset)
    {
        IncomingRecord record;
        bool created;
        lock (_offerGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneIncoming(now);
            _incoming.TryGetValue(itemId, out var existing);
            if (existing?.State == TransferState.Cancelled)
                throw new RemoteFileUnavailableException(Loc.T("transfers.cancelledOnThisPc"));

            // Incollare di nuovo da capo un file già ricevuto è un trasferimento nuovo.
            created = existing is null || (existing.State == TransferState.Completed && fileIndex == 0 && offset == 0);
            record = created ? new IncomingRecord(itemId, files) : existing!;
            if (created)
                _incoming[itemId] = record;
            else if (record.State != TransferState.Completed)
                record.State = TransferState.Active;
            record.LastActivity = now;
        }

        if (created)
            OnTransfersChanged(force: true);
        return record;
    }

    /// <summary>Pezzo arrivato: avanzamento, e completamento quando c'è tutto.</summary>
    private void EndIncomingRead(IncomingRecord record, int fileIndex, long offset, int length)
    {
        bool finished;
        lock (_offerGate)
        {
            record.RecordRead(fileIndex, offset, offset + length);
            record.LastActivity = DateTimeOffset.UtcNow;
            finished = record.State == TransferState.Active && record.BytesDone >= record.TotalBytes;
            if (finished)
                record.State = TransferState.Completed;
        }

        if (finished)
            Log(Loc.T("transfers.receivedFrom", record.ItemName, record.OriginName));
        OnTransfersChanged(force: finished);
    }

    /// <summary>Lettura fallita: il trasferimento risulta fermo (riprende se l'incolla riprova).</summary>
    private void FailIncomingRead(IncomingRecord record)
    {
        lock (_offerGate)
        {
            if (record.State != TransferState.Active)
                return;
            record.State = TransferState.Stalled;
            record.LastActivity = DateTimeOffset.UtcNow;
        }

        OnTransfersChanged(force: true);
    }

    /// <summary>Sotto _offerGate: fermi quelli senza letture, fuori dall'elenco quelli finiti da un po'.</summary>
    private void PruneIncoming(DateTimeOffset now)
    {
        foreach (var (key, record) in _incoming.ToList())
        {
            if (record.State == TransferState.Active && now - record.LastActivity >= _options.TransferIdleTimeout)
            {
                record.State = TransferState.Stalled;
                record.LastActivity = now;
            }

            if (record.State != TransferState.Active && now - record.LastActivity >= FinishedTransferRetention)
                _incoming.Remove(key);
        }
    }

    /// <summary>Cosa si sta incollando: nome da mostrare, da chi, quanto pesa in tutto.</summary>
    private sealed record IncomingFiles(string ItemName, string OriginName, long TotalBytes)
    {
        public static IncomingFiles From(ClipboardContent content, string originName) => new(
            ClipboardSummary.From(content, "", "", false, false).Title,
            DisplayText.Sanitize(originName, 64),
            content.Files?.Sum(f => f.SizeBytes) ?? 0);
    }

    private sealed class IncomingRecord(string itemId, IncomingFiles files)
    {
        private readonly Dictionary<int, CoveredRanges> _read = [];

        public string ItemId { get; } = itemId;

        public string OriginName { get; } = files.OriginName;

        public string ItemName { get; } = files.ItemName;

        public long TotalBytes { get; } = files.TotalBytes;

        public long BytesDone { get; private set; }

        public TransferState State { get; set; } = TransferState.Active;

        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Come per i trasferimenti in uscita: un pezzo letto due volte non conta doppio. Sotto _offerGate.</summary>
        public void RecordRead(int fileIndex, long start, long end)
        {
            if (!_read.TryGetValue(fileIndex, out var ranges))
                _read[fileIndex] = ranges = new CoveredRanges();
            BytesDone = Math.Min(TotalBytes, BytesDone + ranges.Add(start, end));
        }

        public IncomingTransferView ToView() => new(ItemId, OriginName, ItemName, TotalBytes, BytesDone, State, LastActivity);
    }
}
