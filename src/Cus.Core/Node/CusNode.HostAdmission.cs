using System.Collections.Concurrent;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Net;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    /// <summary>Richieste di ingresso in attesa della decisione dell'host: oltre, le nuove si rifiutano.</summary>
    private const int MaxPendingJoins = 8;

    /// <summary>Ogni quanto si controlla se chi aspetta il consenso se n'è andato.</summary>
    private static readonly TimeSpan JoinWatchInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, PendingJoin> _pendingJoins = new();

    /// <summary>Si offre come host in rete: si annuncia e accetta richieste di ingresso.</summary>
    private volatile bool _visible;

    /// <summary>Si sta offrendo come host (visibile in rete).</summary>
    public bool IsVisible => _visible;

    /// <summary>Richieste di ingresso in attesa della decisione di questo PC (host).</summary>
    public IReadOnlyList<JoinRequestView> JoinRequests =>
        _pendingJoins.Values.Select(p => p.View).OrderBy(v => v.ReceivedUtc).ToList();

    public event EventHandler? JoinRequestsChanged;

    // ================================================================ host: visibilità

    /// <summary>
    /// Si offre come host (visibile in rete, come AirDrop) o smette. Smettere non chiude la sessione:
    /// chi è già dentro resta, ma nessun altro può più chiedere di entrare. Gli amici con patto "in
    /// qualsiasi momento" possono collegarsi comunque.
    /// </summary>
    public Task SetVisibleAsync(bool visible) => SetVisibleAsync(visible, fromJoin: false);

    /// <param name="fromJoin">Da <see cref="JoinAsync"/>, che nasconde e poi ripristina la visibilità.</param>
    private async Task SetVisibleAsync(bool visible, bool fromJoin)
    {
        List<PendingJoin> rejected = [];
        await _gate.WaitAsync();
        try
        {
            if (visible == _visible)
                return;
            if (visible && _memberSession is not null)
                throw new CusOperationException(Loc.T("session.youAreInASessionAs"));

            // Una richiesta di ingresso in corso, se accettata, renderebbe questo PC membro: la
            // visibilità tornerebbe falsa senza che l'utente lo sappia.
            if (visible && !fromJoin && Volatile.Read(ref _joining) != 0)
                throw new CusOperationException(Loc.T("session.aJoinRequestIsInProgress"));

            _visible = visible;
            if (visible)
            {
                _hostSession ??= new HostSession(NewId());
                _hostSession.Offered = true;
            }
            else
            {
                // Non più annunciato, ma la sessione resta aperta: chi inserisce a mano l'indirizzo può
                // ancora chiedere di entrare. Si chiude solo con "chiudi sessione" (o entrando altrove).
                if (fromJoin)
                {
                    rejected.AddRange(_pendingJoins.Values);
                    if (_hostSession is { Members.IsEmpty: true })
                        _hostSession = null;
                }
            }

            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var pending in rejected)
            pending.Decide(ConsentDecision.Reject, WireTexts.HostStoppedOffering.Text);

        if (visible)
        {
            Log(Loc.T("session.visibleOnTheNetworkOthersCan"));
            if (_discovery is { } discovery)
                await TryAnnounceAsync(discovery.AnnounceNowAsync);
        }
        else
        {
            Log(Loc.T("session.noLongerVisibleOnTheNetwork"));
            if (_discovery is { } discovery)
                await TryAnnounceAsync(() => discovery.AnnounceHiddenAsync(
                    new AnnouncementPayload(Identity.DeviceId, Identity.DisplayName, Identity.UserName, Port)));
        }
    }

    private static async Task TryAnnounceAsync(Func<Task> announce)
    {
        try
        {
            await announce();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Rete assente: l'annuncio periodico (o la scadenza) rimette le cose a posto.
        }
    }

    /// <summary>Risponde a una richiesta di ingresso dall'elenco (in alternativa alla finestra di consenso).</summary>
    /// <returns>False se la richiesta non è più in attesa.</returns>
    public bool AnswerJoinRequest(string requestId, ConsentDecision decision) =>
        Enum.IsDefined(decision) && _pendingJoins.TryGetValue(requestId, out var pending) && pending.Decide(decision, null);

    // ================================================================ host: ingresso

    private async Task HandleJoinRequestAsync(PeerConnection connection)
    {
        var remote = connection.Remote;
        var level = Friends.AgreementWith(remote.DeviceId);
        string? reason = null;
        var ask = false;
        var viaAgreement = false;

        await _gate.WaitAsync();
        try
        {
            if (_memberSession is not null)
            {
                reason = WireTexts.AlreadyInSession.Format(Identity.DisplayName);
            }
            else if (_hostSession?.Members.ContainsKey(remote.DeviceId) == true)
            {
                reason = WireTexts.YouAreAlreadyIn.Text;
            }
            else if (level == AgreementLevel.Anytime || (_visible && level == AgreementLevel.AutoJoin))
            {
                // Il patto vale come consenso dato in anticipo: entra subito, con accesso bidirezionale
                // (l'host può ridurlo dall'elenco dei connessi).
                _hostSession ??= new HostSession(NewId());
                try
                {
                    await AdmitAsync(connection, ShareDirection.Bidirectional);
                }
                catch
                {
                    // Risposta non inviata: una sessione creata apposta e rimasta vuota non resta in giro.
                    if (_hostSession is { Offered: false, Members.IsEmpty: true })
                        _hostSession = null;
                    throw;
                }

                viaAgreement = true;
            }
            else if (_hostSession is not { Offered: true })
            {
                reason = WireTexts.NotOfferingHost.Format(Identity.DisplayName);
            }
            else if (IsInJoinCooldown(connection))
            {
                reason = WireTexts.AskedTooRecently.Text;
            }
            else
            {
                ask = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (viaAgreement)
        {
            await AfterAdmittedAsync(remote.DisplayName, ShareDirection.Bidirectional);
            _notifier.Info(Loc.T("session.joinedYourSessionPact", remote.DisplayName, DescribeAgreement(level)));
            return;
        }

        if (ask)
        {
            var (decision, why) = await AskJoinAsync(connection);
            reason = why;
            // L'attesa vale per i rifiuti dell'utente, le richieste scadute e quelle annullate da chi
            // chiedeva; non quando è l'host a smettere di offrirsi o la coda è piena.
            if (decision == ConsentDecision.Reject && (why is null || WireTexts.RequestDeclined.Matches(why) || WireTexts.NoAnswer.Matches(why) || WireTexts.RequestCancelled.Matches(why)))
                StartJoinCooldown(connection);
            if (decision != ConsentDecision.Reject)
            {
                var granted = decision == ConsentDecision.AcceptReadOnly ? ShareDirection.ReadOnly : ShareDirection.Bidirectional;
                await _gate.WaitAsync();
                try
                {
                    reason = _memberSession is not null ? WireTexts.AlreadyInSession.Format(Identity.DisplayName)
                        : _hostSession is null ? WireTexts.HostClosedSession.Text
                        : _hostSession.Members.ContainsKey(remote.DeviceId) ? WireTexts.YouAreAlreadyIn.Text
                        : connection.IsClosed ? WireTexts.RequestCancelled.Text
                        : null;
                    if (reason is null)
                        await AdmitAsync(connection, granted);
                }
                finally
                {
                    _gate.Release();
                }

                if (reason is null)
                {
                    await AfterAdmittedAsync(remote.DisplayName, granted);
                    return;
                }
            }
        }

        reason ??= WireTexts.RequestDeclined.Text;
        Log(Loc.T("session.requestFromDeclined", remote.DisplayName, reason));
        try
        {
            await connection.SendAsync(new JoinReplyMessage(false, null, ShareDirection.Bidirectional, reason));
        }
        catch (Exception ex) when (ex is IOException or MessageTooLargeException)
        {
            // Chi chiedeva se n'è già andato.
        }

        connection.Close(WireTexts.RequestDeclined.Text);
    }

    /// <summary>Chiavi "id:" e "ip:" con la scadenza dell'attesa. Ripulite quando si aggiungono voci.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _joinCooldowns = new();

    private const int MaxJoinCooldowns = 1024;

    /// <summary>
    /// Chi è stato rifiutato (o non ha avuto risposta) aspetta prima di chiedere di nuovo: le identità
    /// sono gratuite, quindi conta anche l'indirizzo. Il loopback è escluso (istanze sullo stesso PC).
    /// </summary>
    private IEnumerable<string> JoinCooldownKeys(PeerConnection connection)
    {
        yield return "id:" + connection.Remote.DeviceId;
        if (connection.RemoteEndPoint is System.Net.IPEndPoint ip && !System.Net.IPAddress.IsLoopback(ip.Address))
            yield return "ip:" + (ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address);
    }

    private bool IsInJoinCooldown(PeerConnection connection)
    {
        var now = DateTimeOffset.UtcNow;
        return JoinCooldownKeys(connection).Any(k => _joinCooldowns.TryGetValue(k, out var until) && until > now);
    }

    private void StartJoinCooldown(PeerConnection connection)
    {
        if (_options.JoinRetryCooldown <= TimeSpan.Zero)
            return;
        var now = DateTimeOffset.UtcNow;
        if (_joinCooldowns.Count >= MaxJoinCooldowns)
        {
            foreach (var (key, until) in _joinCooldowns)
            {
                if (until <= now)
                    _joinCooldowns.TryRemove(key, out _);
            }
        }

        if (_joinCooldowns.Count >= MaxJoinCooldowns)
            return;
        foreach (var key in JoinCooldownKeys(connection))
            _joinCooldowns[key] = now + _options.JoinRetryCooldown;
    }

    /// <summary>
    /// Attende la decisione dell'host: dall'elenco delle richieste o dalla finestra di consenso, che
    /// compare per una richiesta alla volta (le altre restano nell'elenco).
    /// </summary>
    private async Task<(ConsentDecision Decision, string? Reason)> AskJoinAsync(PeerConnection connection)
    {
        var remote = connection.Remote;
        if (_pendingJoins.Count >= MaxPendingJoins)
            return (ConsentDecision.Reject, WireTexts.TooManyRequests.Text);

        var imitated = ImitatedFriend(remote);
        var pending = new PendingJoin(new JoinRequestView(NewId(), remote.DeviceId, remote.DisplayName, remote.UserName, remote.Fingerprint, DateTimeOffset.UtcNow, imitated));
        _pendingJoins[pending.View.RequestId] = pending;
        connection.Closed += (_, _) => pending.Decide(ConsentDecision.Reject, WireTexts.RequestCancelled.Text);
        JoinRequestsChanged?.Invoke(this, EventArgs.Empty);
        Log(Loc.T("session.asksToJoin", remote.DisplayName));

        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            // Una sola finestra di consenso alla volta: chiunque in rete può chiedere di entrare, e una
            // raffica di finestre in primo piano sarebbe un disturbo (o un invito al clic sbagliato).
            if (Interlocked.CompareExchange(ref _consentPending, 1, 0) == 0)
            {
                RunInBackground(async () =>
                {
                    try
                    {
                        var decision = await _consent.AskAsync(new ConsentRequest(remote.DisplayName, remote.UserName, remote.Fingerprint, imitated), prompt.Token);
                        pending.Decide(decision, decision == ConsentDecision.Reject ? WireTexts.RequestDeclined.Text : null);
                    }
                    catch (OperationCanceledException)
                    {
                        // Decisa dall'elenco, scaduta o chiusura.
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"CUS: finestra di consenso non mostrata: {ex}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _consentPending, 0);
                    }
                });
            }
            else
            {
                _notifier.Info(Loc.T("session.asksToJoinAnswerFromThe", remote.DisplayName));
            }

            RunInBackground(() => WatchForCancelledJoinAsync(connection, pending, prompt.Token));
            try
            {
                return await pending.Result.WaitAsync(_options.ConsentTimeout, _lifetime.Token);
            }
            catch (TimeoutException)
            {
                return (ConsentDecision.Reject, WireTexts.NoAnswer.Text);
            }
        }
        finally
        {
            prompt.Cancel();
            _pendingJoins.TryRemove(pending.View.RequestId, out _);
            JoinRequestsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Durante l'attesa del consenso nessuno legge dalla connessione: senza questo controllo chi chiede e
    /// poi rinuncia (o chiude CUS) resterebbe nell'elenco e nella finestra di consenso fino alla scadenza.
    /// </summary>
    private static async Task WatchForCancelledJoinAsync(PeerConnection connection, PendingJoin pending, CancellationToken cancellationToken)
    {
        try
        {
            while (!pending.Result.IsCompleted)
            {
                await Task.Delay(JoinWatchInterval, cancellationToken);
                if (connection.HasInputOrHangup())
                {
                    // La chiusura decide la richiesta (vedi il gestore Closed in AskJoinAsync).
                    connection.Close(WireTexts.RequestCancelled.Text);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Decisa, scaduta o chiusura del nodo.
        }
    }

    /// <summary>Risposta positiva e registrazione del membro. Sotto _gate, con _hostSession presente.</summary>
    private async Task AdmitAsync(PeerConnection connection, ShareDirection granted)
    {
        var session = _hostSession!;
        await connection.SendAsync(new JoinReplyMessage(true, session.Id, granted, null));
        var member = new Member(connection, granted);
        session.Members[connection.Remote.DeviceId] = member;
        connection.MessageHandler = HostHandleMessageAsync;
        connection.Closed += (c, reason) =>
        {
            member.Outbox.Complete();
            RunInBackground(() => OnMemberClosedAsync(c, reason));
        };
        connection.Start();
        RunInBackground(() => member.Outbox.PumpAsync(connection, Log));
        RaiseSessionChanged();
    }

    private async Task AfterAdmittedAsync(string name, ShareDirection granted)
    {
        await BroadcastStateAsync();
        Log(granted == ShareDirection.ReadOnly
            ? Loc.T("session.joinedReadOnlyReceivesTheSession", name)
            : Loc.T("session.joinedClipboardShared", name));
    }
}
