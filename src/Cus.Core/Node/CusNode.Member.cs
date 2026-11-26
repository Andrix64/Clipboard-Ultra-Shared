using Cus.Core.Abstractions;
using Cus.Core.Hosting;
using Cus.Core.Net;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    // ================================================================ membro

    /// <summary>
    /// Chiede di entrare nella sessione di un host visibile (o di un amico con patto "in qualsiasi
    /// momento"). L'host decide se far entrare e con quale modalità; attende la sua risposta.
    /// </summary>
    /// <param name="expectedDeviceId">
    /// Per i peer scelti dall'elenco: l'identità annunciata. Gli annunci non sono autenticati, quindi
    /// si verifica che il canale cifrato appartenga davvero a quel dispositivo prima di condividere.
    /// </param>
    public async Task<JoinResult> JoinAsync(string host, int port, string? expectedDeviceId = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _joining, 1, 0) != 0)
            throw new CusOperationException(Loc.T("session.aJoinRequestIsAlreadyIn"));
        var wasVisible = _visible;
        var joined = false;
        try
        {
            if (_memberSession is not null)
                throw new CusOperationException(Loc.T("session.youAreAlreadyInASession"));
            if (_hostSession is { Members.IsEmpty: false })
                throw new CusOperationException(Loc.T("session.youAreHostingASessionClose"));

            // Chi entra in una sessione altrui smette di offrirsi.
            if (_visible)
                await SetVisibleAsync(false, fromJoin: true);

            var connection = await ConnectAsync(host, port, cancellationToken);
            var remote = connection.Remote;
            if (expectedDeviceId is not null && !string.Equals(remote.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                connection.Close(Loc.T("session.identityDiffersFromTheAnnouncedOne"));
                throw new CusOperationException(
                    Loc.T("session.theDeviceAtIsnTThe", new PeerEndpoint(host, port), remote.Fingerprint));
            }

            if (ImitatedFriend(remote) is { } imitated)
                _notifier.Error(Loc.T("session.warningFingerprintIsNotYourFriend", remote.DisplayName, remote.Fingerprint, imitated));
            Log(Loc.T("session.joinRequestSentToFingerprintWaiting", remote.DisplayName, remote.Fingerprint));
            JoinReplyMessage reply;
            try
            {
                await connection.SendAsync(new JoinRequestMessage(), cancellationToken);
                reply = await connection.ReceiveAsync<JoinReplyMessage>(_options.ConsentTimeout + TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Annullata da chi chiama: non è un "nessuna risposta" e si propaga come tale.
                connection.Close(WireTexts.RequestCancelled.Text);
                throw;
            }
            catch (Exception ex) when (ex is not CusOperationException)
            {
                connection.Close(Loc.T("session.error"));
                throw new CusOperationException(Loc.T("session.requestToFailed", remote.DisplayName, Describe(ex)), ex);
            }

            if (!reply.Accepted)
            {
                var reason = WireTexts.Localize(DisplayText.Sanitize(reply.Reason, 200, WireTexts.RequestDeclined.Text));
                connection.Close(Loc.T("session.declined"));
                Log(Loc.T("session.declinedWithReason", remote.DisplayName, reason));
                return new JoinResult(JoinOutcome.Rejected, remote.DisplayName, reason);
            }

            var granted = Enum.IsDefined(reply.Granted) ? reply.Granted : ShareDirection.ReadOnly;
            var sessionId = reply.SessionId is { Length: > 0 and <= 64 } id ? id : NewId();
            await _gate.WaitAsync();
            try
            {
                // Nel frattempo questo PC può essere diventato host (un amico si è collegato).
                if (_memberSession is not null || _hostSession is { Members.IsEmpty: false })
                {
                    await connection.CloseAsync(WireTexts.InAnotherSession.Text);
                    throw new CusOperationException(Loc.T("session.meanwhileAnotherSessionStartedOnThis"));
                }

                _hostSession = null;
                _visible = false;
                _memberSession = new MemberSession(sessionId, connection, granted);
                connection.MessageHandler = MemberHandleMessageAsync;
                connection.Closed += (c, why) => RunInBackground(() => OnHostClosedAsync(c, why));
                connection.Start();
                RaiseSessionChanged();
            }
            finally
            {
                _gate.Release();
            }

            joined = true;
            Log(granted == ShareDirection.ReadOnly
                ? Loc.T("session.youJoinedSSessionReadOnly", remote.DisplayName)
                : Loc.T("session.youJoinedSSession", remote.DisplayName));
            return new JoinResult(granted == ShareDirection.ReadOnly ? JoinOutcome.AcceptedReadOnly : JoinOutcome.Accepted, remote.DisplayName, null);
        }
        finally
        {
            // Richiesta rifiutata o fallita: chi si offriva torna visibile, come prima di chiedere.
            if (!joined && wasVisible && _memberSession is null)
            {
                try
                {
                    await SetVisibleAsync(true, fromJoin: true);
                }
                catch (CusOperationException)
                {
                }
            }

            Interlocked.Exchange(ref _joining, 0);
        }
    }

    /// <summary>
    /// Membro con accesso bidirezionale: passa a sola lettura o torna bidirezionale. In sola lettura
    /// le copie fatte qui restano qui, subito, anche prima che l'host lo sappia.
    /// </summary>
    public async Task SetMyDirectionAsync(ShareDirection direction)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        MemberSession session;
        await _gate.WaitAsync();
        try
        {
            if (_memberSession is not { } current)
                throw new CusOperationException(Loc.T("session.youArenTAMemberOf"));
            if (direction == ShareDirection.Bidirectional && current.Granted == ShareDirection.ReadOnly)
                throw new CusOperationException(Loc.T("session.theHostGrantedYouReadOnly"));
            session = current;
            session.Chosen = direction;
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await session.Host.SendAsync(new SetDirectionMessage(direction));
        }
        catch (IOException)
        {
            // Host caduto: la sessione si chiude in OnHostClosedAsync.
        }

        Log(direction == ShareDirection.ReadOnly
            ? Loc.T("session.readOnlyYouReceiveTheSession")
            : Loc.T("session.twoWayYourCopiesAreShared"));
    }

    private Task ToggleMyDirectionAsync() =>
        _memberSession is { } session
            ? SetMyDirectionAsync(session.Direction == ShareDirection.ReadOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly)
            : Task.CompletedTask;

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
                        var participants = (state.Participants ?? [])
                            .Where(p => p is not null && p.DeviceId is { Length: 32 } && Enum.IsDefined(p.Direction) && Enum.IsDefined(p.Granted))
                            .Take(MaxParticipants)
                            .Select(p => p with { DisplayName = DisplayText.Sanitize(p.DisplayName, 64) })
                            .ToList();
                        if (participants.FirstOrDefault(p => p.DeviceId == Identity.DeviceId) is { } self)
                            session.Granted = self.Granted;
                        session.Participants = participants;
                        session.SensitiveForAll = state.SensitiveForAll;
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
            case AgreementProposalMessage proposal:
                // La risposta arriva dopo la scelta dell'utente: non si blocca la ricezione delle copie.
                RunInBackground(() => HandleAgreementProposalAsync(connection, proposal));
                break;
            case SessionEndedMessage ended:
                _notifier.Info(Loc.T("session.sessionEndedWithReason", WireTexts.Localize(DisplayText.Sanitize(ended.Reason, 200, Loc.T("session.closedByTheHost")))));
                if (_memberSession is { } current && current.Host == connection)
                    current.EndedByHost = true;
                connection.Close(ended.Reason);
                break;
        }
    }

    private async Task ApplyItemAsync(PeerConnection connection, ClipboardItemMessage item)
    {
        if (item.Content is null || !IsValidItemId(item.ItemId))
        {
            Log(Loc.T("session.copyIgnoredIncompleteMessage"));
            return;
        }

        if (!IsAcceptableContent(item.Content, out var why))
        {
            Log(Loc.T("session.copyIgnored", why));
            return;
        }

        var originName = DisplayText.Sanitize(item.OriginName, 64);
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
            var source = item.Content.Kind == ClipboardContentKind.Files ? new RemoteFileSource(this, connection, item.ItemId!, incoming: IncomingFiles.From(item.Content, originName)) : null;
            var originId = item.OriginDeviceId is { Length: 32 } id ? id : connection.Remote.DeviceId;
            QueueClipboardWrite(new ClipboardWrite(
                Interlocked.Increment(ref _clipboardStamp),
                item.Content with { IsMarkedSensitive = item.Sensitive },
                source,
                ClipboardSummary.From(item.Content, originId, originName, item.Sensitive, isShared: true),
                originName,
                item.Sensitive ? Loc.T("session.receivedFromSensitiveContent", originName) : Loc.T("session.receivedFrom", originName, Short(item.Content))));
        }
        finally
        {
            _gate.Release();
        }
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
            _notifier.Info(Loc.T("session.theHostDisconnectedSessionEnded", connection.Remote.DisplayName, reason));
        if (ended is not null)
            Log(Loc.T("session.sessionEnded"));
    }
}
