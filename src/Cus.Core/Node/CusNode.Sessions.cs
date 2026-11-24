using System.Collections.Concurrent;
using Cus.Core.Abstractions;
using Cus.Core.Identity;
using Cus.Core.Net;
using Cus.Core.Storage;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    /// <summary>PNG oltre questa dimensione non entrano in un messaggio (limite del canale).</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    public const int MaxTextChars = 2 * 1024 * 1024;

    private const int MaxFileReadBytes = 1024 * 1024;

    // ================================================================ host

    /// <summary>
    /// Invia una richiesta di accesso: questo dispositivo diventa (o resta) host della sessione.
    /// Attende la risposta dell'altro utente.
    /// </summary>
    public async Task<InviteResult> InviteAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (_memberSession is not null)
            throw new CusOperationException("Sei membro di una sessione: esci prima di invitare altri.");

        var connection = await ConnectAsync(host, port, cancellationToken);
        var name = connection.Remote.DisplayName;
        string sessionId;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_memberSession is not null)
            {
                connection.Close("già membro di una sessione");
                throw new CusOperationException("Sei membro di una sessione: esci prima di invitare altri.");
            }

            if (_hostSession?.Members.ContainsKey(connection.Remote.DeviceId) == true)
            {
                connection.Close("già nella sessione");
                throw new CusOperationException($"{name} è già nella sessione.");
            }

            _hostSession ??= new HostSession(NewId());
            _hostSession.PendingInvites++;
            sessionId = _hostSession.Id;
        }
        finally
        {
            _gate.Release();
        }

        var added = false;
        try
        {
            Log($"Richiesta di accesso inviata a {name}.");
            await connection.SendAsync(new SessionInviteMessage(sessionId), cancellationToken);
            var reply = await connection.ReceiveAsync<SessionInviteReplyMessage>(_options.ConsentTimeout + TimeSpan.FromSeconds(15), cancellationToken);

            if (reply.Decision == ConsentDecision.Reject)
            {
                connection.Close("richiesta rifiutata");
                Log($"{name} ha rifiutato la richiesta.");
                return new InviteResult(InviteOutcome.Rejected, name, reply.Reason);
            }

            var direction = reply.Decision == ConsentDecision.AcceptReadOnly ? ShareDirection.ReadOnly : ShareDirection.Bidirectional;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_hostSession is null || _hostSession.Id != sessionId)
                {
                    await connection.CloseAsync("sessione terminata");
                    throw new CusOperationException("La sessione è terminata mentre si attendeva la risposta.");
                }

                _hostSession.Members[connection.Remote.DeviceId] = new Member(connection, direction);
                connection.MessageHandler = HostHandleMessageAsync;
                connection.Closed += (c, reason) => _ = Task.Run(() => OnMemberClosedAsync(c, reason));
                connection.Start();
                added = true;
                RaiseSessionChanged();
            }
            finally
            {
                _gate.Release();
            }

            await BroadcastStateAsync();
            Log(direction == ShareDirection.ReadOnly
                ? $"{name} ha accettato in sola lettura: invia le sue copie ma non riceve le altre."
                : $"{name} ha accettato: clipboard condivisa.");
            return new InviteResult(direction == ShareDirection.ReadOnly ? InviteOutcome.AcceptedReadOnly : InviteOutcome.Accepted, name, null);
        }
        catch (Exception ex) when (ex is not CusOperationException)
        {
            connection.Close("errore");
            throw new CusOperationException($"Richiesta a {name} non riuscita: {Describe(ex)}", ex);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_hostSession is { } session && session.Id == sessionId)
                {
                    session.PendingInvites--;
                    if (!added && session.Members.IsEmpty && session.PendingInvites == 0)
                    {
                        _hostSession = null;
                        RaiseSessionChanged();
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task HostHandleMessageAsync(PeerConnection connection, CusMessage message)
    {
        if (HandleCommonMessage(connection, message))
            return;

        switch (message)
        {
            case ClipboardPushMessage push:
                if (!IsAcceptableContent(push.Content, out var why))
                {
                    Log($"Copia di {connection.Remote.DisplayName} ignorata: {why}");
                    return;
                }

                Log($"Ricevuto da {connection.Remote.DisplayName}: {Short(push.Content)}");
                await DistributeAsync(connection.Remote.DeviceId, connection.Remote.DisplayName, push.ItemId, push.Content, connection);
                break;
            case LeaveSessionMessage:
                connection.Close("uscito dalla sessione");
                break;
            case HistoryRequestMessage request:
                await connection.SendAsync(new HistoryReplyMessage(request.RequestId, _database.GetHistory()));
                break;
        }
    }

    /// <summary>
    /// L'host assegna il numero di sequenza e distribuisce: vince sempre l'ultima copia arrivata qui.
    /// </summary>
    /// <param name="localStamp">Per le copie dell'host: quando è avvenuta la copia (vedi _clipboardStamp).</param>
    private async Task DistributeAsync(string originId, string originName, string itemId, ClipboardContent content, PeerConnection? originConnection, long localStamp = 0)
    {
        await _gate.WaitAsync();
        try
        {
            if (_hostSession is not { } session)
                return;

            // Una copia dell'host superata da una copia remota già scritta nella sua clipboard è
            // vecchia: distribuirla renderebbe la clipboard dell'host diversa da quella dei membri.
            if (originConnection is null && localStamp < Interlocked.Read(ref _lastRemoteWriteStamp))
                return;

            var sequence = ++session.Sequence;
            session.ItemOrigins[itemId] = originId;
            _database.AddHistory(new HistoryEntry(itemId, session.Id, sequence, originId, originName, content, DateTimeOffset.UtcNow));

            if (originConnection is not null)
            {
                Interlocked.Exchange(ref _lastRemoteWriteStamp, Interlocked.Increment(ref _clipboardStamp));
                var source = content.Kind == ClipboardContentKind.Files ? new RemoteFileSource(this, originConnection, itemId) : null;
                await _writer.WriteAsync(content, source);
                try
                {
                    await originConnection.SendAsync(new ClipboardAckMessage(itemId, sequence));
                }
                catch (IOException)
                {
                    // Origine caduta.
                }
            }

            var item = new ClipboardItemMessage(itemId, sequence, originId, originName, content);
            foreach (var member in session.Members.Values)
            {
                if (member.Direction != ShareDirection.Bidirectional || member.Connection == originConnection)
                    continue;
                try
                {
                    await member.Connection.SendAsync(item);
                }
                catch (IOException)
                {
                    // Membro caduto: lo rimuove OnMemberClosedAsync.
                }
            }
        }
        catch (Exception ex)
        {
            _notifier.Error($"Distribuzione della copia non riuscita: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnMemberClosedAsync(PeerConnection connection, string reason)
    {
        var changed = false;
        await _gate.WaitAsync();
        try
        {
            if (_hostSession is { } session
                && session.Members.TryGetValue(connection.Remote.DeviceId, out var member)
                && member.Connection == connection)
            {
                session.Members.TryRemove(connection.Remote.DeviceId, out _);
                changed = true;
                Log($"{connection.Remote.DisplayName} è uscito dalla sessione ({reason}).");
                if (session.Members.IsEmpty && session.PendingInvites == 0)
                {
                    _hostSession = null;
                    Log("Nessun membro rimasto: sessione terminata.");
                }

                RaiseSessionChanged();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
            await BroadcastStateAsync();
    }

    private async Task BroadcastStateAsync()
    {
        HostSession? session;
        SessionStateMessage? state;
        await _gate.WaitAsync();
        try
        {
            session = _hostSession;
            state = session is null ? null : new SessionStateMessage(session.Id, session.Participants(Identity));
        }
        finally
        {
            _gate.Release();
        }

        if (session is null || state is null)
            return;
        foreach (var member in session.Members.Values)
        {
            try
            {
                await member.Connection.SendAsync(state);
            }
            catch (IOException)
            {
                // Membro caduto.
            }
        }
    }

    // ================================================================ membro

    private async Task HandleInviteAsync(PeerConnection connection, SessionInviteMessage invite)
    {
        var remote = connection.Remote;
        ConsentDecision decision;
        string? reason = null;

        if (_hostSession is not null || _memberSession is not null)
        {
            decision = ConsentDecision.Reject;
            reason = "è già in un'altra sessione";
        }
        else if (Trust.State.IsTrusted(remote.DeviceId))
        {
            decision = ConsentDecision.Accept;
            _notifier.Info($"{remote.DisplayName} (tuo dispositivo abbinato) condivide la clipboard con te.");
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(_options.ConsentTimeout);
            try
            {
                decision = await _consent.AskAsync(new ConsentRequest(remote.DisplayName, remote.UserName, remote.Fingerprint), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                decision = ConsentDecision.Reject;
                reason = "nessuna risposta";
            }

            if (decision == ConsentDecision.Reject)
                reason ??= "richiesta rifiutata";
        }

        await _gate.WaitAsync();
        try
        {
            if (decision != ConsentDecision.Reject && (_hostSession is not null || _memberSession is not null))
            {
                decision = ConsentDecision.Reject;
                reason = "è già in un'altra sessione";
            }

            await connection.SendAsync(new SessionInviteReplyMessage(decision, reason));
            if (decision == ConsentDecision.Reject)
            {
                connection.Close("rifiutata");
                return;
            }

            var direction = decision == ConsentDecision.AcceptReadOnly ? ShareDirection.ReadOnly : ShareDirection.Bidirectional;
            _memberSession = new MemberSession(invite.SessionId, connection, direction, new SessionView(invite.SessionId, SessionRole.Member,
            [
                new SessionParticipant(remote.DisplayName, ShareDirection.Bidirectional, IsHost: true),
                new SessionParticipant(Identity.DisplayName, direction, IsHost: false),
            ]));
            connection.MessageHandler = MemberHandleMessageAsync;
            connection.Closed += (c, why) => _ = Task.Run(() => OnHostClosedAsync(c, why));
            connection.Start();
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        Log($"Sessione con {remote.DisplayName} avviata.");
    }

    private async Task MemberHandleMessageAsync(PeerConnection connection, CusMessage message)
    {
        if (HandleCommonMessage(connection, message))
            return;

        switch (message)
        {
            case SessionStateMessage state:
                await _gate.WaitAsync();
                try
                {
                    if (_memberSession is { } session && session.Host == connection)
                    {
                        session.View = new SessionView(state.SessionId, SessionRole.Member,
                            state.Participants.Select(p => new SessionParticipant(
                                p.DeviceId == Identity.DeviceId ? $"{p.DisplayName} (tu)" : p.DisplayName, p.Direction, p.IsHost)).ToList());
                        RaiseSessionChanged();
                    }
                }
                finally
                {
                    _gate.Release();
                }

                break;
            case ClipboardItemMessage item:
                await ApplyItemAsync(connection, item);
                break;
            case ClipboardAckMessage ack:
                await _gate.WaitAsync();
                try
                {
                    if (_memberSession is { } session && session.Host == connection)
                    {
                        session.PendingPushes.Remove(ack.ItemId);
                        session.LastSequence = Math.Max(session.LastSequence, ack.Sequence);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                break;
            case SessionEndedMessage ended:
                _notifier.Info($"Sessione terminata: {ended.Reason}.");
                if (_memberSession is { } current && current.Host == connection)
                    current.EndedByHost = true;
                connection.Close(ended.Reason);
                break;
        }
    }

    private async Task ApplyItemAsync(PeerConnection connection, ClipboardItemMessage item)
    {
        if (!IsAcceptableContent(item.Content, out var why))
        {
            Log($"Copia ignorata: {why}");
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_memberSession is not { } session || session.Host != connection || item.Sequence <= session.LastSequence)
                return;
            session.LastSequence = item.Sequence;

            // Una propria copia è ancora in viaggio verso l'host: verrà ordinata dopo questa e la
            // supererà. Applicare questa ora lascerebbe questo PC diverso dagli altri.
            if (session.PendingPushes.Count > 0)
                return;
            var source = item.Content.Kind == ClipboardContentKind.Files ? new RemoteFileSource(this, connection, item.ItemId) : null;
            await _writer.WriteAsync(item.Content, source);
        }
        catch (Exception ex)
        {
            _notifier.Error($"Impossibile scrivere nella clipboard: {ex.Message}");
            return;
        }
        finally
        {
            _gate.Release();
        }

        Log($"Ricevuto da {item.OriginName}: {Short(item.Content)}");
    }

    private async Task OnHostClosedAsync(PeerConnection connection, string reason)
    {
        MemberSession? ended = null;
        await _gate.WaitAsync();
        try
        {
            if (_memberSession is { } session && session.Host == connection)
            {
                ended = session;
                _memberSession = null;
                RaiseSessionChanged();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (ended is not null && !ended.Leaving && !ended.EndedByHost)
            _notifier.Info($"L'host {connection.Remote.DisplayName} si è disconnesso: sessione terminata ({reason}).");
        if (ended is not null)
            Log("Sessione terminata.");
    }

    // ================================================================ comuni

    /// <summary>Esce dalla sessione (membro) o la chiude per tutti (host).</summary>
    public async Task LeaveSessionAsync()
    {
        HostSession? host;
        MemberSession? member;
        await _gate.WaitAsync();
        try
        {
            host = _hostSession;
            member = _memberSession;
            _hostSession = null;
            _memberSession = null;
            if (member is not null)
                member.Leaving = true;
            if (host is not null || member is not null)
                RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        if (host is not null)
        {
            foreach (var m in host.Members.Values)
            {
                try
                {
                    await m.Connection.SendAsync(new SessionEndedMessage("l'host ha chiuso la sessione"));
                }
                catch (IOException)
                {
                    // Già caduto.
                }

                m.Connection.Close("sessione chiusa dall'host");
            }
        }

        if (member is not null)
        {
            try
            {
                await member.Host.SendAsync(new LeaveSessionMessage());
            }
            catch (IOException)
            {
                // Host già caduto.
            }

            member.Host.Close("uscito dalla sessione");
        }

        if (host is not null || member is not null)
            Log("Sessione terminata.");
    }

    private async Task OnLocalClipboardChangedAsync(ClipboardChangedEventArgs e)
    {
        if (e.IsOwnWrite)
            return;

        var content = e.Content;
        var itemId = NewId();
        var stamp = Interlocked.Increment(ref _clipboardStamp);
        lock (_offerGate)
            _localOffer = content.Kind == ClipboardContentKind.Files && content.Files is { } files ? (itemId, files) : null;

        if (content.IsMarkedSensitive)
        {
            if (Session is not null)
                Log("Contenuto marcato come sensibile da un password manager: non condiviso.");
            return;
        }

        if (!IsAcceptableContent(content, out var why))
        {
            if (Session is not null)
                _notifier.Error($"Copia non condivisa: {why}");
            return;
        }

        if (_hostSession is not null)
        {
            Log($"Condiviso: {Short(content)}");
            await DistributeAsync(Identity.DeviceId, Identity.DisplayName, itemId, content, null, stamp);
        }
        else if (_memberSession is { } member)
        {
            try
            {
                await _gate.WaitAsync();
                try
                {
                    member.PendingPushes.Add(itemId);
                }
                finally
                {
                    _gate.Release();
                }

                await member.Host.SendAsync(new ClipboardPushMessage(itemId, content));
                Log($"Inviato all'host: {Short(content)}");
            }
            catch (IOException)
            {
                // L'host è caduto: la sessione si chiude in OnHostClosedAsync.
            }
        }
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_memberSession is { } member)
        {
            var reply = await member.Host.RequestAsync<HistoryReplyMessage>(
                id => new HistoryRequestMessage(id), TimeSpan.FromSeconds(15), cancellationToken);
            return reply.Entries;
        }

        return _database.GetHistory();
    }

    /// <summary>Rimette una voce della cronologia nella propria clipboard (senza ridistribuirla).</summary>
    public async Task UseHistoryEntryAsync(HistoryEntry entry)
    {
        IRemoteFileSource? source = null;
        if (entry.Content.Kind == ClipboardContentKind.Files)
        {
            if (entry.OriginDeviceId == Identity.DeviceId)
            {
                lock (_offerGate)
                {
                    if (_localOffer?.ItemId != entry.ItemId)
                        throw new CusOperationException("I file non sono più disponibili: su questo PC è stato copiato altro.");
                }

                source = new LocalFileSource(this, entry.ItemId);
            }
            else if (_memberSession is { } member)
            {
                source = new RemoteFileSource(this, member.Host, entry.ItemId);
            }
            else if (_hostSession is { } host && host.Members.TryGetValue(entry.OriginDeviceId, out var origin))
            {
                source = new RemoteFileSource(this, origin.Connection, entry.ItemId);
            }
            else
            {
                throw new CusOperationException("Il PC d'origine non è più nella sessione: i file non sono recuperabili.");
            }
        }

        await _writer.WriteAsync(entry.Content, source);
    }

    // ================================================================ file lazy

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
            reply = new FileReadReplyMessage(request.RequestId, null, $"Lettura del file non riuscita sul PC d'origine: {ex.Message}");
        }

        try
        {
            await connection.SendAsync(reply);
        }
        catch (IOException)
        {
            // Chi ha chiesto si è disconnesso.
        }
    }

    private async Task<byte[]> ReadFileForPeerAsync(PeerConnection connection, FileReadRequestMessage request)
    {
        var host = _hostSession;
        var member = _memberSession;
        var inSession = host?.Members.Values.Any(m => m.Connection == connection) == true || member?.Host == connection;
        if (!inSession)
            throw new RemoteFileUnavailableException("Richiesta non autorizzata: il dispositivo non è nella sessione.");

        if (TryReadLocalOffer(request.ItemId, request.FileIndex, request.Offset, request.Count, out var data))
            return data;

        if (host is not null)
        {
            var originId = host.ItemOrigins.TryGetValue(request.ItemId, out var id)
                ? id
                : _database.GetHistory().FirstOrDefault(h => h.ItemId == request.ItemId)?.OriginDeviceId;
            if (originId is not null && originId != Identity.DeviceId)
            {
                if (!host.Members.TryGetValue(originId, out var origin) || origin.Connection == connection)
                    throw new RemoteFileUnavailableException("Il PC d'origine non è più nella sessione.");
                var relayed = await new RemoteFileSource(this, origin.Connection, request.ItemId, notify: false)
                    .ReadAsync(request.FileIndex, request.Offset, request.Count);
                return relayed;
            }
        }

        throw new RemoteFileUnavailableException("Il PC d'origine ha già copiato altro: copia di nuovo i file.");
    }

    internal bool TryReadLocalOffer(string itemId, int fileIndex, long offset, int count, out byte[] data)
    {
        data = [];
        IReadOnlyList<ClipboardFileInfo> files;
        lock (_offerGate)
        {
            if (_localOffer is not { } offer || offer.ItemId != itemId)
                return false;
            files = offer.Files;
        }

        if (fileIndex < 0 || fileIndex >= files.Count || files[fileIndex].LocalPath is not { } path)
            throw new RemoteFileUnavailableException("File richiesto non valido.");
        if (offset < 0 || count <= 0)
            throw new RemoteFileUnavailableException("Intervallo richiesto non valido.");

        count = Math.Min(count, MaxFileReadBytes);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset >= stream.Length)
                return true;
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
            stream.ReadExactly(buffer);
            data = buffer;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RemoteFileUnavailableException($"Il file '{files[fileIndex].Name}' non è più leggibile sul PC d'origine: {ex.Message}");
        }
    }

    private static bool IsAcceptableContent(ClipboardContent content, out string reason)
    {
        reason = content.Kind switch
        {
            ClipboardContentKind.Image when content.ImagePng is not { Length: > 0 } => "immagine vuota",
            ClipboardContentKind.Image when content.ImagePng.Length > MaxImageBytes => $"immagine troppo grande (oltre {MaxImageBytes / 1024 / 1024} MB)",
            ClipboardContentKind.Text when content.Text is null => "testo vuoto",
            ClipboardContentKind.Text when content.Text.Length > MaxTextChars => "testo troppo lungo",
            ClipboardContentKind.Files when content.Files is not { Count: > 0 } => "nessun file",
            _ => "",
        };
        return reason.Length == 0;
    }

    private static string Short(ClipboardContent content)
    {
        var text = content.Describe().ReplaceLineEndings(" ");
        return text.Length <= 60 ? text : text[..57] + "...";
    }

    private sealed class HostSession(string id)
    {
        public string Id { get; } = id;

        public ConcurrentDictionary<string, Member> Members { get; } = new();

        public ConcurrentDictionary<string, string> ItemOrigins { get; } = new();

        public long Sequence { get; set; }

        public int PendingInvites { get; set; }

        public IReadOnlyList<ParticipantDto> Participants(DeviceIdentity self) =>
        [
            new ParticipantDto(self.DeviceId, self.DisplayName, ShareDirection.Bidirectional, IsHost: true),
            .. Members.Values.Select(m => new ParticipantDto(m.Connection.Remote.DeviceId, m.Connection.Remote.DisplayName, m.Direction, IsHost: false)),
        ];

        public SessionView ToView(DeviceIdentity self) => new(Id, SessionRole.Host,
            Participants(self).Select(p => new SessionParticipant(p.IsHost ? $"{p.DisplayName} (tu)" : p.DisplayName, p.Direction, p.IsHost)).ToList());
    }

    private sealed record Member(PeerConnection Connection, ShareDirection Direction);

    private sealed class MemberSession(string id, PeerConnection host, ShareDirection direction, SessionView view)
    {
        public string Id { get; } = id;

        public PeerConnection Host { get; } = host;

        public ShareDirection Direction { get; } = direction;

        public SessionView View { get; set; } = view;

        public long LastSequence { get; set; }

        public bool Leaving { get; set; }

        public bool EndedByHost { get; set; }

        /// <summary>Copie inviate all'host di cui non è ancora arrivata la conferma.</summary>
        public HashSet<string> PendingPushes { get; } = [];
    }

    /// <summary>File da leggere da un altro PC: tramite l'host per i membri, direttamente dall'origine per l'host.</summary>
    private sealed class RemoteFileSource(CusNode node, PeerConnection connection, string itemId, bool notify = true) : IRemoteFileSource
    {
        public async Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default)
        {
            try
            {
                if (connection.IsClosed)
                    throw new RemoteFileUnavailableException("Il PC d'origine non è raggiungibile: la sessione è terminata.");

                FileReadReplyMessage reply;
                try
                {
                    reply = await connection.RequestAsync<FileReadReplyMessage>(
                        id => new FileReadRequestMessage(id, itemId, fileIndex, offset, Math.Min(count, MaxFileReadBytes)),
                        FileReadTimeout, cancellationToken);
                }
                catch (IOException)
                {
                    throw new RemoteFileUnavailableException("Il PC d'origine non è raggiungibile: la sessione è terminata.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RemoteFileUnavailableException("Il PC d'origine non risponde.");
                }

                if (reply.Error is not null)
                    throw new RemoteFileUnavailableException(reply.Error);
                return reply.Data ?? [];
            }
            catch (RemoteFileUnavailableException ex) when (notify)
            {
                node._notifier.Error($"Impossibile incollare: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>File copiati su questo stesso PC (cronologia dell'host).</summary>
    private sealed class LocalFileSource(CusNode node, string itemId) : IRemoteFileSource
    {
        public Task<byte[]> ReadAsync(int fileIndex, long offset, int count, CancellationToken cancellationToken = default) =>
            node.TryReadLocalOffer(itemId, fileIndex, offset, count, out var data)
                ? Task.FromResult(data)
                : throw new RemoteFileUnavailableException("Su questo PC è stato copiato altro: i file non sono più disponibili.");
    }
}
