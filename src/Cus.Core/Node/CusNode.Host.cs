using Cus.Core.Abstractions;
using Cus.Core.Net;
using Cus.Core.Storage;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    // ================================================================ host: permessi

    /// <summary>Cambia la modalità concessa a un membro (il membro può comunque ridurla a sola lettura).</summary>
    public async Task SetMemberGrantAsync(string deviceId, ShareDirection granted)
    {
        if (!Enum.IsDefined(granted))
            throw new ArgumentOutOfRangeException(nameof(granted));
        string name;
        await _gate.WaitAsync();
        try
        {
            if (_hostSession?.Members.TryGetValue(deviceId, out var member) != true)
                throw new CusOperationException(Loc.T("common.theDeviceIsNoLongerIn"));
            member!.Granted = granted;
            name = member.Connection.Remote.DisplayName;
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        await BroadcastStateAsync();
        Log(granted == ShareDirection.ReadOnly ? Loc.T("session.isNowReadOnly", name) : Loc.T("session.nowHasTwoWayAccess", name));
    }

    /// <summary>
    /// Permesso temporaneo, solo per questa sessione: i contenuti sensibili copiati dall'host vanno a
    /// tutti i membri. Senza, vanno solo agli amici marcati come affidabili.
    /// </summary>
    public async Task SetSensitiveForAllAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (_hostSession is not { } session)
                throw new CusOperationException(Loc.T("session.youArenTHostingASession"));
            session.SensitiveForAll = enabled;
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        await BroadcastStateAsync();
        Log(enabled
            ? Loc.T("session.sensitiveContentVisibleToAllMembers")
            : Loc.T("session.sensitiveContentOnlyToTrustedMembers"));
    }

    // ================================================================ host: copie

    private async Task HostHandleMessageAsync(PeerConnection connection, CusMessage message)
    {
        if (HandleCommonMessage(connection, message))
            return;

        // Solo la connessione registrata come membro: non una rimasta orfana (sessione chiusa).
        if (CurrentMember(connection) is not { } member)
            return;

        switch (message)
        {
            // Sola lettura: il membro riceve le copie della sessione ma le sue non entrano, anche se
            // le manda comunque. La conferma serve lo stesso, come per ogni copia ignorata.
            case ClipboardPushMessage push when member.Direction == ShareDirection.ReadOnly:
                Log(Loc.T("session.copyFromIgnoredParticipatingReadOnly", connection.Remote.DisplayName));
                if (push.ItemId is { Length: <= 64 })
                    await AcknowledgeIgnoredPushAsync(connection, push.ItemId);
                break;
            case ClipboardPushMessage push:
                var why = push.ItemId is null || push.Content is null ? Loc.T("session.incompleteMessage")
                    : !IsValidItemId(push.ItemId) ? Loc.T("session.invalidIdentifier")
                    : IsAcceptableContent(push.Content, out var reason) ? null : reason;
                if (why is not null)
                {
                    Log(Loc.T("session.copyFromIgnored", connection.Remote.DisplayName, why));
                    if (push.ItemId is { Length: <= 64 })
                        await AcknowledgeIgnoredPushAsync(connection, push.ItemId);
                    return;
                }

                Log(push.Sensitive
                    ? Loc.T("session.receivedFromSensitiveContent", connection.Remote.DisplayName)
                    : Loc.T("session.receivedFrom", connection.Remote.DisplayName, Short(push.Content!)));
                var recipients = push.Sensitive
                    ? (push.SensitiveRecipients ?? []).Where(id => id is { Length: 32 }).Take(MaxParticipants).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                await DistributeAsync(connection.Remote.DeviceId, member.ShownName, push.ItemId!, push.Content!, connection,
                    sensitive: push.Sensitive, sensitiveRecipients: recipients);
                break;
            case SetDirectionMessage set when Enum.IsDefined(set.Direction):
                await _gate.WaitAsync();
                try
                {
                    if (CurrentMember(connection) is not { } current || current.Chosen == set.Direction)
                        return;
                    current.Chosen = set.Direction;
                    RaiseSessionChanged();
                }
                finally
                {
                    _gate.Release();
                }

                await BroadcastStateAsync();
                Log(set.Direction == ShareDirection.ReadOnly
                    ? Loc.T("session.switchedToReadOnly", connection.Remote.DisplayName)
                    : Loc.T("session.isBackToTwoWay", connection.Remote.DisplayName));
                break;
            case RenameMessage rename:
                // Un nome più lungo di quello che l'interfaccia permette non è di un client onesto.
                if (rename.DisplayName is not { Length: <= MaxRenameChars })
                    return;
                await RenameMemberAsync(connection, DisplayText.Sanitize(rename.DisplayName, 64));
                break;
            case LeaveSessionMessage:
                connection.Close(Loc.T("session.leftTheSession"));
                break;
            case HistoryRequestMessage request:
                await connection.SendAsync(new HistoryReplyMessage(request.RequestId, SessionHistoryPreviews(connection)));
                break;
            case HistoryItemRequestMessage request:
                await SendHistoryItemAsync(connection, request);
                break;
        }
    }

    /// <summary>
    /// Applica il nuovo nome di un membro. Al più un cambio ogni <see cref="RenameInterval"/>, perché ogni
    /// cambio rimanda l'elenco a tutti; un nome arrivato prima si tiene da parte e si applica allo scadere
    /// dell'intervallo (vale l'ultimo), invece di perderlo: il membro lo vede già come suo.
    /// </summary>
    private async Task RenameMemberAsync(PeerConnection connection, string newName)
    {
        string oldName;
        string? refused;
        await _gate.WaitAsync();
        try
        {
            if (CurrentMember(connection) is not { } renamed)
                return;
            // Un nome in attesa ha già il suo timer; uno nuovo lo sostituisce (o lo annulla, se torna al nome attuale).
            var timerScheduled = renamed.PendingName is not null;
            renamed.PendingName = null;
            if (renamed.DisplayName == newName)
                return;

            var now = Environment.TickCount64;
            if (renamed.LastRenameTicks is { } last && now - last < RenameInterval.TotalMilliseconds)
            {
                renamed.PendingName = newName;
                if (!timerScheduled)
                    RunInBackground(() => ApplyPendingRenameAsync(connection, renamed, TimeSpan.FromMilliseconds(RenameInterval.TotalMilliseconds - (now - last))));
                return;
            }

            renamed.LastRenameTicks = now;
            oldName = renamed.ShownName;
            // Stessi controlli dell'ingresso: il nome non può confondersi con l'host, con un altro
            // partecipante o con un amico (a occhio).
            refused = LookAlikeRefusal(renamed, newName);
            if (refused is null)
            {
                renamed.DisplayName = newName;
                RaiseSessionChanged();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (refused is not null)
        {
            Log(Loc.T("session.triedToRenameToButIt", oldName, newName, refused));
            return;
        }

        await BroadcastStateAsync();
        Log(Loc.T("session.isNowCalled", oldName, newName));
    }

    private async Task ApplyPendingRenameAsync(PeerConnection connection, Member member, TimeSpan delay)
    {
        await Task.Delay(delay, _lifetime.Token);
        if (member.PendingName is { } name)
            await RenameMemberAsync(connection, name);
    }

    private const int MaxRenameChars = 256;

    private static readonly TimeSpan RenameInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Il nome (host, altro partecipante o amico con un'altra identità) a cui <paramref name="name"/> somiglia
    /// troppo per essere accettato come nuovo nome di <paramref name="member"/>, o null. Sotto _gate.
    /// </summary>
    private string? LookAlikeRefusal(Member member, string name)
    {
        if (DisplayText.LooksAlike(name, Identity.DisplayName))
            return Identity.DisplayName;
        if (_hostSession is { } session)
        {
            foreach (var other in session.Members.Values)
            {
                if (!ReferenceEquals(other, member) && DisplayText.LooksAlike(name, other.ShownName))
                    return other.ShownName;
            }
        }

        var self = member.Connection.Remote.DeviceId;
        return Friends.All.FirstOrDefault(f => !string.Equals(f.DeviceId, self, StringComparison.OrdinalIgnoreCase)
            && DisplayText.LooksAlike(f.DisplayName, name))?.DisplayName;
    }

    /// <summary>Il membro della sessione host collegato con questa connessione, se lo è ancora.</summary>
    private Member? CurrentMember(PeerConnection connection) =>
        _hostSession is { } session
        && session.Members.TryGetValue(connection.Remote.DeviceId, out var member)
        && member.Connection == connection
            ? member
            : null;

    /// <summary>Formato di <see cref="NewId"/>: 24 cifre esadecimali maiuscole.</summary>
    private static bool IsValidItemId(string? itemId) =>
        itemId is { Length: 24 } && itemId.All(char.IsAsciiHexDigitUpper);

    /// <summary>
    /// Ai membri va solo la cronologia della sessione in corso, e solo come anteprime: voci di
    /// sessioni precedenti (magari con altre persone) non li riguardano, e le voci complete con
    /// immagini e testi lunghi non entrerebbero in un messaggio.
    /// </summary>
    /// <summary>La cronologia della sessione che un membro può vedere: solo le copie fatte da quando è entrato.</summary>
    private IReadOnlyList<HistoryEntry> SessionHistoryPreviews(PeerConnection connection)
    {
        var session = _hostSession;
        if (session is null || session.Members.Values.FirstOrDefault(m => m.Connection == connection) is not { } member)
            return [];
        return _database.GetHistory(session.Id)
            .Where(h => h.Sequence > member.JoinedAfterSequence)
            .Select(h => h.ToPreview())
            .ToList();
    }

    private async Task SendHistoryItemAsync(PeerConnection connection, HistoryItemRequestMessage request)
    {
        var session = _hostSession;
        HistoryEntry? entry = null;
        string? error = null;
        if (session is null || session.Members.Values.FirstOrDefault(m => m.Connection == connection) is not { } member)
            error = WireTexts.NotInSession.Text;
        // Una voce di prima dell'ingresso risponde come una che non c'è: il membro non deve sapere che esiste.
        else if ((entry = _database.GetHistoryEntry(request.ItemId)) is null || entry.SessionId != session.Id
                 || entry.Sequence <= member.JoinedAfterSequence)
            error = WireTexts.HistoryItemGone.Text;

        try
        {
            await connection.SendAsync(new HistoryItemReplyMessage(request.RequestId, error is null ? entry : null, error));
        }
        catch (MessageTooLargeException)
        {
            await connection.SendAsync(new HistoryItemReplyMessage(request.RequestId, null, WireTexts.HistoryItemTooLarge.Text));
        }
    }

    /// <summary>
    /// Una copia del membro non accettata riceve comunque la conferma (con l'ultimo numero di
    /// sequenza già inviato): senza, il membro la crederebbe in viaggio e ignorerebbe ogni copia successiva.
    /// </summary>
    private async Task AcknowledgeIgnoredPushAsync(PeerConnection connection, string itemId)
    {
        await _gate.WaitAsync();
        try
        {
            if (CurrentMember(connection) is { } member)
                Post(member, new ClipboardAckMessage(itemId, _hostSession?.Sequence ?? 0));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Accoda un messaggio per un membro. Sotto _gate: l'ordine delle code è quello in cui l'host decide
    /// (numeri di sequenza, conferme, stato), e nessun invio in rete tiene il lock. Un membro che non
    /// smaltisce la sua coda (non legge più, o troppo piano) si scollega invece di rallentare gli altri.
    /// </summary>
    private void Post(Member member, CusMessage message, int? bytes = null)
    {
        // Connessione già chiusa ma membro non ancora rimosso (OnMemberClosedAsync aspetta _gate):
        // la coda è completata e il messaggio non serve, senza segnalarlo come "troppo lento".
        if (member.Connection.IsClosed)
            return;
        if (member.Outbox.TryEnqueue(message, bytes ?? message.Serialize().Length))
            return;
        Log(Loc.T("session.isnTReceivingCopiesFastEnough", member.Connection.Remote.DisplayName));
        member.Connection.Close(Loc.T("session.tooSlowReceivingCopies"));
    }

    /// <summary>
    /// L'host assegna il numero di sequenza e distribuisce: vince sempre l'ultima copia arrivata qui.
    /// I contenuti sensibili vanno solo a chi li può vedere e non finiscono in cronologia.
    /// </summary>
    /// <param name="localStamp">Per le copie dell'host: quando è avvenuta la copia (vedi _clipboardStamp).</param>
    /// <param name="sensitiveRecipients">Copie sensibili di un membro: i partecipanti a cui il membro le concede.</param>
    /// <returns>A quanti membri è stata inviata (-1 se scartata).</returns>
    private async Task<int> DistributeAsync(
        string originId, string originName, string itemId, ClipboardContent content, PeerConnection? originConnection,
        long localStamp = 0, bool sensitive = false, IReadOnlySet<string>? sensitiveRecipients = null)
    {
        // Una copia di un membro va sempre confermata, qualunque cosa succeda qui: senza conferma il
        // membro la crederebbe in viaggio e ignorerebbe ogni copia successiva.
        var acknowledged = originConnection is null;
        await _gate.WaitAsync();
        try
        {
            if (_hostSession is not { } session)
                return -1;

            // Un identificativo già usato sostituirebbe, nella cronologia e nelle letture dei file,
            // la voce di un altro PC.
            if (originConnection is not null && (session.ItemOrigins.ContainsKey(itemId) || _database.GetHistoryEntry(itemId) is not null))
            {
                Log(Loc.T("session.copyFromIgnoredIdentifierAlreadyUsed", originName));
                return -1;
            }

            // Una copia dell'host superata da una copia remota già scritta nella sua clipboard è
            // vecchia: distribuirla renderebbe la clipboard dell'host diversa da quella dei membri.
            if (originConnection is null && localStamp < Interlocked.Read(ref _lastRemoteWriteStamp))
                return -1;

            var item = new ClipboardItemMessage(itemId, session.Sequence + 1, originId, originName, content, sensitive);
            var itemBytes = item.Serialize().Length;
            if (originConnection is null && itemBytes > SecureChannel.MaxMessageBytes)
            {
                _notifier.Error(Loc.T("session.copyNotSharedTooLargeTo"));
                return -1;
            }

            var sequence = ++session.Sequence;
            session.RememberOrigin(itemId, originId);
            if (!sensitive)
                _database.AddHistory(new HistoryEntry(itemId, session.Id, sequence, originId, originName, content, DateTimeOffset.UtcNow));

            if (originConnection is not null)
            {
                var stamp = Interlocked.Increment(ref _clipboardStamp);
                Interlocked.Exchange(ref _lastRemoteWriteStamp, stamp);
                var source = content.Kind == ClipboardContentKind.Files ? new RemoteFileSource(this, originConnection, itemId) : null;
                // La scrittura avviene fuori da _gate; la copia si conferma e si distribuisce comunque,
                // anche se la clipboard dell'host è occupata, altrimenti il membro resterebbe bloccato.
                QueueClipboardWrite(new ClipboardWrite(
                    stamp, content with { IsMarkedSensitive = sensitive }, source,
                    ClipboardSummary.From(content, originId, originName, sensitive, isShared: true), originName, ReceivedLog: null));
                acknowledged = true;
                if (CurrentMember(originConnection) is { } origin)
                    Post(origin, new ClipboardAckMessage(itemId, sequence));
            }

            // Sensibili: le copie dell'host vanno ai membri affidabili (o a tutti col permesso
            // temporaneo della sessione); quelle di un membro solo a chi ha scelto lui.
            var targets = session.Members.Values
                .Where(m => m.Connection != originConnection)
                .Where(m => !sensitive || (originConnection is null
                    ? session.SensitiveForAll || Friends.TrustsWithSensitive(m.Connection.Remote.DeviceId)
                    : sensitiveRecipients?.Contains(m.Connection.Remote.DeviceId) == true))
                .ToList();

            // Nelle code dei membri, nell'ordine dei numeri di sequenza: gli invii veri avvengono fuori
            // da _gate, ognuno al ritmo del proprio membro.
            foreach (var member in targets)
                Post(member, item, itemBytes);
            return targets.Count;
        }
        catch (Exception ex)
        {
            _notifier.Error(Loc.T("session.distributingTheCopyFailed", ex.Message));
            return -1;
        }
        finally
        {
            if (!acknowledged && CurrentMember(originConnection!) is { } origin)
                Post(origin, new ClipboardAckMessage(itemId, _hostSession?.Sequence ?? 0));

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
                Log(Loc.T("session.leftTheSessionWithReason", connection.Remote.DisplayName, reason));

                // Senza membri la sessione finisce. Mentre ci si offre ne parte subito una nuova, vuota:
                // chi entra dopo non eredita la cronologia né il permesso temporaneo per le password
                // dati ai membri di prima.
                if (session.Members.IsEmpty)
                {
                    _hostSession = session.Offered ? new HostSession(NewId()) { Offered = true } : null;
                    Log(Loc.T("session.noMembersLeftSessionEnded"));
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
        await _gate.WaitAsync();
        try
        {
            if (_hostSession is not { } session)
                return;
            var state = new SessionStateMessage(session.Id, session.Participants(Identity), session.SensitiveForAll);
            var bytes = state.Serialize().Length;
            foreach (var member in session.Members.Values)
                Post(member, state, bytes);
        }
        finally
        {
            _gate.Release();
        }
    }
}
