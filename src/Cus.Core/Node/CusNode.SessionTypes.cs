using System.Collections.Concurrent;
using System.Threading.Channels;
using Cus.Core.Abstractions;
using Cus.Core.Identity;
using Cus.Core.Net;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    private sealed class HostSession(string id)
    {
        private const int MaxItemOrigins = 256;

        public string Id { get; } = id;

        /// <summary>L'utente si è offerto come host: la sessione resta aperta anche quando smette di annunciarsi.</summary>
        public bool Offered { get; set; }

        public ConcurrentDictionary<string, Member> Members { get; } = new();

        /// <summary>Origine delle copie recenti, per inoltrare le letture dei file. Le più vecchie si cercano nella cronologia.</summary>
        public ConcurrentDictionary<string, string> ItemOrigins { get; } = new();

        private readonly Queue<string> _itemOrder = new();

        /// <summary>Chiamato sotto _gate.</summary>
        public void RememberOrigin(string itemId, string originId)
        {
            ItemOrigins[itemId] = originId;
            _itemOrder.Enqueue(itemId);
            while (_itemOrder.Count > MaxItemOrigins && _itemOrder.TryDequeue(out var old))
                ItemOrigins.TryRemove(old, out _);
        }

        public long Sequence { get; set; }

        /// <summary>Permesso temporaneo: i contenuti sensibili dell'host vanno a tutti, fino alla fine della sessione.</summary>
        public bool SensitiveForAll { get; set; }

        public IReadOnlyList<ParticipantDto> Participants(DeviceIdentity self) =>
        [
            new ParticipantDto(self.DeviceId, self.DisplayName, ShareDirection.Bidirectional, IsHost: true),
            .. Members.Values
                .OrderBy(m => m.JoinedUtc)
                .Select(m => new ParticipantDto(m.Connection.Remote.DeviceId, m.ShownName, m.Direction, IsHost: false, m.Granted)),
        ];

        public SessionView ToView(DeviceIdentity self, bool visible) => new(
            Id,
            SessionRole.Host,
            Participants(self).Select(p => new SessionParticipant(p.DeviceId, p.DisplayName, p.Direction, p.Granted, p.IsHost, p.DeviceId == self.DeviceId)).ToList(),
            visible,
            SensitiveForAll,
            ShareDirection.Bidirectional,
            ShareDirection.Bidirectional);
    }

    /// <summary>Membro visto dall'host. Modificato sotto _gate.</summary>
    private sealed class Member(PeerConnection connection, ShareDirection granted)
    {
        public PeerConnection Connection { get; } = connection;

        /// <summary>Messaggi dell'host per questo membro, in ordine (vedi <see cref="Post"/>).</summary>
        public MemberOutbox Outbox { get; } = new();

        public DateTimeOffset JoinedUtc { get; } = DateTimeOffset.UtcNow;

        /// <summary>Nome cambiato dal membro dopo essere entrato (null: quello dell'handshake).</summary>
        public string? DisplayName { get; set; }

        /// <summary>Il nome attuale del membro.</summary>
        public string ShownName => DisplayName ?? Connection.Remote.DisplayName;

        /// <summary>Quando il membro ha cambiato nome l'ultima volta (millisecondi monotoni).</summary>
        public long? LastRenameTicks { get; set; }

        /// <summary>Nome chiesto prima che fosse passato RenameInterval: si applica allo scadere. Sotto _gate.</summary>
        public string? PendingName { get; set; }

        /// <summary>Modalità concessa dall'host.</summary>
        public ShareDirection Granted { get; set; } = granted;

        /// <summary>Modalità scelta dal membro (può solo ridurre quella concessa).</summary>
        public ShareDirection Chosen { get; set; } = ShareDirection.Bidirectional;

        public ShareDirection Direction => Granted == ShareDirection.ReadOnly || Chosen == ShareDirection.ReadOnly
            ? ShareDirection.ReadOnly
            : ShareDirection.Bidirectional;
    }

    /// <summary>
    /// Coda d'invio verso un membro: si riempie sotto _gate e la svuota un ciclo per membro, fuori dal
    /// lock. Limitata per numero e per byte: oltre, il membro non tiene il passo e va scollegato.
    /// </summary>
    private sealed class MemberOutbox
    {
        private const int MaxMessages = 64;

        /// <summary>Qualche copia grande (immagini, testi lunghi) in attesa, non centinaia di MB.</summary>
        private const long MaxBytes = 32L * 1024 * 1024;

        private readonly Channel<(CusMessage Message, int Bytes)> _queue =
            Channel.CreateUnbounded<(CusMessage, int)>(new UnboundedChannelOptions { SingleReader = true });

        private int _count;
        private long _bytes;

        /// <returns>False se la coda è piena (o chiusa): il messaggio non è stato accodato.</returns>
        public bool TryEnqueue(CusMessage message, int bytes)
        {
            var count = Interlocked.Increment(ref _count);
            var total = Interlocked.Add(ref _bytes, bytes);
            if ((count <= MaxMessages && total <= MaxBytes) && _queue.Writer.TryWrite((message, bytes)))
                return true;
            Interlocked.Decrement(ref _count);
            Interlocked.Add(ref _bytes, -bytes);
            return false;
        }

        public void Complete() => _queue.Writer.TryComplete();

        /// <summary>Invia in ordine finché la connessione vive. Un invio fallito la chiude (PeerConnection).</summary>
        public async Task PumpAsync(PeerConnection connection, Action<string> log)
        {
            try
            {
                await foreach (var (message, bytes) in _queue.Reader.ReadAllAsync(connection.Lifetime))
                {
                    try
                    {
                        await connection.SendAsync(message);
                    }
                    catch (MessageTooLargeException)
                    {
                        log(Loc.T("session.copyNotSentToTooLarge", connection.Remote.DisplayName));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _count);
                        Interlocked.Add(ref _bytes, -bytes);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Membro caduto o sessione chiusa: lo rimuove OnMemberClosedAsync.
            }
        }
    }

    private sealed class MemberSession(string id, PeerConnection host, ShareDirection granted)
    {
        public string Id { get; } = id;

        public PeerConnection Host { get; } = host;

        /// <summary>Modalità concessa dall'host (aggiornata dallo stato della sessione).</summary>
        public ShareDirection Granted { get; set; } = granted;

        /// <summary>Modalità scelta da questo PC.</summary>
        public ShareDirection Chosen { get; set; } = ShareDirection.Bidirectional;

        public ShareDirection Direction => Granted == ShareDirection.ReadOnly || Chosen == ShareDirection.ReadOnly
            ? ShareDirection.ReadOnly
            : ShareDirection.Bidirectional;

        /// <summary>Partecipanti come li descrive l'host; vuoto finché non arriva il primo stato.</summary>
        public IReadOnlyList<ParticipantDto> Participants { get; set; } = [];

        public bool SensitiveForAll { get; set; }

        public long LastSequence { get; set; }

        public bool Leaving { get; set; }

        public bool EndedByHost { get; set; }

        /// <summary>Copie inviate all'host di cui non è ancora arrivata la conferma.</summary>
        public HashSet<string> PendingPushes { get; } = [];

        public SessionView ToView(DeviceIdentity self)
        {
            IReadOnlyList<ParticipantDto> participants = Participants.Count > 0
                ? Participants
                :
                [
                    new ParticipantDto(Host.Remote.DeviceId, Host.Remote.DisplayName, ShareDirection.Bidirectional, IsHost: true),
                    new ParticipantDto(self.DeviceId, self.DisplayName, Direction, IsHost: false, Granted),
                ];
            return new SessionView(
                Id,
                SessionRole.Member,
                participants.Select(p => p.DeviceId == self.DeviceId
                    // Per sé conta la scelta locale, applicata subito anche prima che l'host la confermi.
                    ? new SessionParticipant(p.DeviceId, p.DisplayName, Direction, Granted, p.IsHost, IsSelf: true)
                    : new SessionParticipant(p.DeviceId, p.DisplayName, p.Direction, p.Granted, p.IsHost, IsSelf: false)).ToList(),
                IsVisible: false,
                SensitiveForAll,
                Granted,
                Direction);
        }
    }

    /// <summary>Richiesta di ingresso in attesa: la prima decisione (elenco, finestra, scadenza) vince.</summary>
    private sealed class PendingJoin(JoinRequestView view)
    {
        private readonly TaskCompletionSource<(ConsentDecision, string?)> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JoinRequestView View { get; } = view;

        public Task<(ConsentDecision Decision, string? Reason)> Result => _result.Task;

        public bool Decide(ConsentDecision decision, string? reason) => _result.TrySetResult((decision, reason));
    }

    /// <summary>File da leggere da un altro PC: tramite l'host per i membri, direttamente dall'origine per l'host.</summary>
    /// <param name="forDeviceId">L'host che inoltra per un membro: chi incolla davvero, da mostrare al PC d'origine.</param>
    private sealed class RemoteFileSource(
        CusNode node, PeerConnection connection, string itemId, bool notify = true, string? forDeviceId = null, string? forName = null) : IRemoteFileSource
    {
        public async Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default)
        {
            try
            {
                if (connection.IsClosed)
                    throw new RemoteFileUnavailableException(WireTexts.OriginUnreachable.Text);

                FileReadReplyMessage reply;
                try
                {
                    reply = await connection.RequestAsync<FileReadReplyMessage>(
                        id => new FileReadRequestMessage(id, itemId, fileIndex, offset, Math.Min(count, MaxFileReadBytes), forDeviceId, forName),
                        FileReadTimeout, cancellationToken);
                }
                catch (IOException)
                {
                    throw new RemoteFileUnavailableException(WireTexts.OriginUnreachable.Text);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RemoteFileUnavailableException(WireTexts.OriginNotResponding.Text);
                }

                if (reply.Error is not null)
                    throw new RemoteFileUnavailableException(WireTexts.Localize(DisplayText.Sanitize(reply.Error, 300, WireTexts.OriginError.Text)));

                // Mai più byte di quelli chiesti, anche se l'altro PC ne manda di più.
                var data = reply.Data ?? [];
                var requested = Math.Min(count, MaxFileReadBytes);
                return data.Length <= requested ? data : data[..requested];
            }
            catch (RemoteFileUnavailableException ex) when (notify)
            {
                node._notifier.Error(Loc.T("session.canTPaste", ex.Message));
                throw;
            }
        }
    }

    /// <summary>File copiati su questo stesso PC (cronologia dell'host).</summary>
    private sealed class LocalFileSource(CusNode node, string itemId) : IRemoteFileSource
    {
        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default) =>
            node.TryReadLocalOffer(itemId, fileIndex, offset, count, node.Identity.DeviceId, node.Identity.DisplayName, out var data)
                ? Task.FromResult(data)
                : throw new RemoteFileUnavailableException(Loc.T("session.somethingElseWasCopiedOnThis"));
    }
}
