using System.Collections.Concurrent;
using System.Threading.Channels;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Hosting;
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

    /// <summary>Richieste di ingresso in attesa della decisione dell'host: oltre, le nuove si rifiutano.</summary>
    private const int MaxPendingJoins = 8;

    /// <summary>Partecipanti di una sessione che un membro accetta dallo stato dell'host.</summary>
    private const int MaxParticipants = 64;

    private readonly ConcurrentDictionary<string, PendingJoin> _pendingJoins = new();

    /// <summary>Si offre come host in rete: si annuncia e accetta richieste di ingresso.</summary>
    private volatile bool _visible;

    private int _joining;

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
                throw new CusOperationException("Sei in una condivisione come membro: esci prima di offrirti come host.");

            // Una richiesta di ingresso in corso, se accettata, renderebbe questo PC membro: la
            // visibilità tornerebbe falsa senza che l'utente lo sappia.
            if (visible && !fromJoin && Volatile.Read(ref _joining) != 0)
                throw new CusOperationException("C'è una richiesta di ingresso in corso: attendi la risposta prima di offrirti come host.");

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
            pending.Decide(ConsentDecision.Reject, "l'host ha smesso di offrirsi");

        if (visible)
        {
            Log("Visibile in rete: gli altri possono chiederti di entrare.");
            if (_discovery is { } discovery)
                await TryAnnounceAsync(discovery.AnnounceNowAsync);
        }
        else
        {
            Log("Non più visibile in rete.");
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
                reason = $"{Identity.DisplayName} è già in una condivisione";
            }
            else if (_hostSession?.Members.ContainsKey(remote.DeviceId) == true)
            {
                reason = "sei già nella sessione";
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
                reason = $"{Identity.DisplayName} non si sta offrendo come host";
            }
            else if (IsInJoinCooldown(connection))
            {
                reason = "hai chiesto da poco: riprova tra qualche secondo";
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
            _notifier.Info($"{remote.DisplayName} è entrato nella tua condivisione (patto {DescribeAgreement(level)}).");
            return;
        }

        if (ask)
        {
            var (decision, why) = await AskJoinAsync(connection);
            reason = why;
            // L'attesa vale per i rifiuti dell'utente, le richieste scadute e quelle annullate da chi
            // chiedeva; non quando è l'host a smettere di offrirsi o la coda è piena.
            if (decision == ConsentDecision.Reject && why is null or "richiesta rifiutata" or "nessuna risposta" or "richiesta annullata")
                StartJoinCooldown(connection);
            if (decision != ConsentDecision.Reject)
            {
                var granted = decision == ConsentDecision.AcceptReadOnly ? ShareDirection.ReadOnly : ShareDirection.Bidirectional;
                await _gate.WaitAsync();
                try
                {
                    reason = _memberSession is not null ? $"{Identity.DisplayName} è già in una condivisione"
                        : _hostSession is null ? "l'host ha chiuso la sessione"
                        : _hostSession.Members.ContainsKey(remote.DeviceId) ? "sei già nella sessione"
                        : connection.IsClosed ? "richiesta annullata"
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

        reason ??= "richiesta rifiutata";
        Log($"Richiesta di {remote.DisplayName} rifiutata: {reason}.");
        try
        {
            await connection.SendAsync(new JoinReplyMessage(false, null, ShareDirection.Bidirectional, reason));
        }
        catch (Exception ex) when (ex is IOException or MessageTooLargeException)
        {
            // Chi chiedeva se n'è già andato.
        }

        connection.Close("richiesta rifiutata");
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
            return (ConsentDecision.Reject, "troppe richieste in attesa, riprova tra poco");

        var imitated = ImitatedFriend(remote);
        var pending = new PendingJoin(new JoinRequestView(NewId(), remote.DeviceId, remote.DisplayName, remote.UserName, remote.Fingerprint, DateTimeOffset.UtcNow, imitated));
        _pendingJoins[pending.View.RequestId] = pending;
        connection.Closed += (_, _) => pending.Decide(ConsentDecision.Reject, "richiesta annullata");
        JoinRequestsChanged?.Invoke(this, EventArgs.Empty);
        Log($"{remote.DisplayName} chiede di entrare.");

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
                        pending.Decide(decision, decision == ConsentDecision.Reject ? "richiesta rifiutata" : null);
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
                _notifier.Info($"{remote.DisplayName} chiede di entrare: rispondi dall'elenco delle richieste.");
            }

            try
            {
                return await pending.Result.WaitAsync(_options.ConsentTimeout, _lifetime.Token);
            }
            catch (TimeoutException)
            {
                return (ConsentDecision.Reject, "nessuna risposta");
            }
        }
        finally
        {
            prompt.Cancel();
            _pendingJoins.TryRemove(pending.View.RequestId, out _);
            JoinRequestsChanged?.Invoke(this, EventArgs.Empty);
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
            ? $"{name} è entrato in sola lettura: riceve le copie della sessione ma non invia le sue."
            : $"{name} è entrato: clipboard condivisa.");
    }

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
                throw new CusOperationException("Il dispositivo non è più nella sessione.");
            member!.Granted = granted;
            name = member.Connection.Remote.DisplayName;
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        await BroadcastStateAsync();
        Log(granted == ShareDirection.ReadOnly ? $"{name} ora è in sola lettura." : $"{name} ora ha accesso bidirezionale.");
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
                throw new CusOperationException("Non stai ospitando una condivisione.");
            session.SensitiveForAll = enabled;
            RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        await BroadcastStateAsync();
        Log(enabled
            ? "Contenuti sensibili visibili a tutti i membri, fino alla fine della sessione."
            : "Contenuti sensibili solo ai membri affidabili.");
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
                Log($"Copia di {connection.Remote.DisplayName} ignorata: partecipa in sola lettura");
                if (push.ItemId is { Length: <= 64 })
                    await AcknowledgeIgnoredPushAsync(connection, push.ItemId);
                break;
            case ClipboardPushMessage push:
                var why = push.ItemId is null || push.Content is null ? "messaggio incompleto"
                    : !IsValidItemId(push.ItemId) ? "identificativo non valido"
                    : IsAcceptableContent(push.Content, out var reason) ? null : reason;
                if (why is not null)
                {
                    Log($"Copia di {connection.Remote.DisplayName} ignorata: {why}");
                    if (push.ItemId is { Length: <= 64 })
                        await AcknowledgeIgnoredPushAsync(connection, push.ItemId);
                    return;
                }

                Log(push.Sensitive
                    ? $"Ricevuto da {connection.Remote.DisplayName}: contenuto sensibile"
                    : $"Ricevuto da {connection.Remote.DisplayName}: {Short(push.Content!)}");
                var recipients = push.Sensitive
                    ? (push.SensitiveRecipients ?? []).Where(id => id is { Length: 32 }).Take(MaxParticipants).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                await DistributeAsync(connection.Remote.DeviceId, connection.Remote.DisplayName, push.ItemId!, push.Content!, connection,
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
                    ? $"{connection.Remote.DisplayName} è passato in sola lettura."
                    : $"{connection.Remote.DisplayName} è tornato bidirezionale.");
                break;
            case RenameMessage rename:
                var newName = DisplayText.Sanitize(rename.DisplayName, 64);
                await _gate.WaitAsync();
                try
                {
                    if (CurrentMember(connection) is not { } renamed || renamed.DisplayName == newName)
                        return;
                    renamed.DisplayName = newName;
                    RaiseSessionChanged();
                }
                finally
                {
                    _gate.Release();
                }

                await BroadcastStateAsync();
                Log($"{connection.Remote.DisplayName} ora si chiama {newName}.");
                break;
            case LeaveSessionMessage:
                connection.Close("uscito dalla sessione");
                break;
            case HistoryRequestMessage request:
                await connection.SendAsync(new HistoryReplyMessage(request.RequestId, SessionHistoryPreviews(connection)));
                break;
            case HistoryItemRequestMessage request:
                await SendHistoryItemAsync(connection, request);
                break;
        }
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
    private IReadOnlyList<HistoryEntry> SessionHistoryPreviews(PeerConnection connection)
    {
        var session = _hostSession;
        if (session is null || !session.Members.Values.Any(m => m.Connection == connection))
            return [];
        return _database.GetHistory(session.Id).Select(h => h.ToPreview()).ToList();
    }

    private async Task SendHistoryItemAsync(PeerConnection connection, HistoryItemRequestMessage request)
    {
        var session = _hostSession;
        HistoryEntry? entry = null;
        string? error = null;
        if (session is null || !session.Members.Values.Any(m => m.Connection == connection))
            error = "Richiesta non autorizzata: il dispositivo non è nella sessione.";
        else if ((entry = _database.GetHistoryEntry(request.ItemId)) is null || entry.SessionId != session.Id)
            error = "Voce non più presente nella cronologia della sessione.";

        try
        {
            await connection.SendAsync(new HistoryItemReplyMessage(request.RequestId, error is null ? entry : null, error));
        }
        catch (MessageTooLargeException)
        {
            await connection.SendAsync(new HistoryItemReplyMessage(request.RequestId, null, "Voce troppo grande per essere inviata."));
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
        if (member.Outbox.TryEnqueue(message, bytes ?? message.Serialize().Length))
            return;
        Log($"{member.Connection.Remote.DisplayName} non riceve le copie abbastanza in fretta: scollegato.");
        member.Connection.Close("troppo lento a ricevere le copie");
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
                Log($"Copia di {originName} ignorata: identificativo già usato");
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
                _notifier.Error("Copia non condivisa: troppo grande per essere inviata.");
                return -1;
            }

            var sequence = ++session.Sequence;
            session.RememberOrigin(itemId, originId);
            if (!sensitive)
                _database.AddHistory(new HistoryEntry(itemId, session.Id, sequence, originId, originName, content, DateTimeOffset.UtcNow));

            if (originConnection is not null)
            {
                Interlocked.Exchange(ref _lastRemoteWriteStamp, Interlocked.Increment(ref _clipboardStamp));
                var source = content.Kind == ClipboardContentKind.Files ? new RemoteFileSource(this, originConnection, itemId) : null;
                try
                {
                    await _writer.WriteAsync(content with { IsMarkedSensitive = sensitive }, source);
                    SetCurrentClipboard(ClipboardSummary.From(content, originId, originName, sensitive, isShared: true));
                    WarnAboutExecutables(content, originName);
                }
                catch (Exception ex)
                {
                    // La clipboard dell'host può essere occupata da un altro programma: la copia va
                    // comunque confermata e distribuita, altrimenti il membro resterebbe bloccato.
                    _notifier.Error($"Impossibile scrivere nella clipboard: {ex.Message}");
                }

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
            _notifier.Error($"Distribuzione della copia non riuscita: {ex.Message}");
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
                Log($"{connection.Remote.DisplayName} è uscito dalla sessione ({reason}).");

                // Senza membri la sessione finisce. Mentre ci si offre ne parte subito una nuova, vuota:
                // chi entra dopo non eredita la cronologia né il permesso temporaneo per le password
                // dati ai membri di prima.
                if (session.Members.IsEmpty)
                {
                    _hostSession = session.Offered ? new HostSession(NewId()) { Offered = true } : null;
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
            throw new CusOperationException("C'è già una richiesta di ingresso in corso.");
        var wasVisible = _visible;
        var joined = false;
        try
        {
            if (_memberSession is not null)
                throw new CusOperationException("Sei già in una condivisione: esci prima di entrare in un'altra.");
            if (_hostSession is { Members.IsEmpty: false })
                throw new CusOperationException("Stai ospitando una condivisione: chiudila prima di entrare in un'altra.");

            // Chi entra in una sessione altrui smette di offrirsi.
            if (_visible)
                await SetVisibleAsync(false, fromJoin: true);

            var connection = await ConnectAsync(host, port, cancellationToken);
            var remote = connection.Remote;
            if (expectedDeviceId is not null && !string.Equals(remote.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                connection.Close("identità diversa da quella annunciata");
                throw new CusOperationException(
                    $"Il dispositivo a {new PeerEndpoint(host, port)} non è quello scelto: la sua identità ({remote.Fingerprint}) "
                    + "non corrisponde a quella annunciata in rete. Richiesta annullata.");
            }

            if (ImitatedFriend(remote) is { } imitated)
                _notifier.Error($"Attenzione: {remote.DisplayName} (impronta {remote.Fingerprint}) non è il tuo amico {imitated}, anche se si chiama come lui.");
            Log($"Richiesta di ingresso inviata a {remote.DisplayName} (impronta {remote.Fingerprint}): in attesa della risposta...");
            JoinReplyMessage reply;
            try
            {
                await connection.SendAsync(new JoinRequestMessage(), cancellationToken);
                reply = await connection.ReceiveAsync<JoinReplyMessage>(_options.ConsentTimeout + TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (Exception ex) when (ex is not CusOperationException)
            {
                connection.Close("errore");
                throw new CusOperationException($"Richiesta a {remote.DisplayName} non riuscita: {Describe(ex)}", ex);
            }

            if (!reply.Accepted)
            {
                var reason = DisplayText.Sanitize(reply.Reason, 200, "richiesta rifiutata");
                connection.Close("rifiutata");
                Log($"{remote.DisplayName} ha rifiutato: {reason}.");
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
                    await connection.CloseAsync("già in un'altra sessione");
                    throw new CusOperationException("Nel frattempo è iniziata un'altra condivisione su questo PC.");
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
                ? $"Sei entrato nella condivisione di {remote.DisplayName} in sola lettura."
                : $"Sei entrato nella condivisione di {remote.DisplayName}.");
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
                throw new CusOperationException("Non sei membro di una condivisione.");
            if (direction == ShareDirection.Bidirectional && current.Granted == ShareDirection.ReadOnly)
                throw new CusOperationException("L'host ti ha concesso solo la sola lettura.");
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
            ? "Sola lettura: ricevi le copie della sessione, le tue restano su questo PC."
            : "Bidirezionale: le tue copie tornano a essere condivise.");
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
                _notifier.Info($"Sessione terminata: {DisplayText.Sanitize(ended.Reason, 200, "chiusa dall'host")}.");
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
            Log("Copia ignorata: messaggio incompleto");
            return;
        }

        if (!IsAcceptableContent(item.Content, out var why))
        {
            Log($"Copia ignorata: {why}");
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
            var source = item.Content.Kind == ClipboardContentKind.Files ? new RemoteFileSource(this, connection, item.ItemId!) : null;
            await _writer.WriteAsync(item.Content with { IsMarkedSensitive = item.Sensitive }, source);
            var originId = item.OriginDeviceId is { Length: 32 } id ? id : connection.Remote.DeviceId;
            SetCurrentClipboard(ClipboardSummary.From(item.Content, originId, originName, item.Sensitive, isShared: true));
            WarnAboutExecutables(item.Content, originName);
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

        Log(item.Sensitive ? $"Ricevuto da {originName}: contenuto sensibile" : $"Ricevuto da {originName}: {Short(item.Content)}");
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

    /// <summary>Esce dalla sessione (membro) o la chiude per tutti e smette di offrirsi (host).</summary>
    public async Task LeaveSessionAsync()
    {
        HostSession? host;
        MemberSession? member;
        var wasVisible = false;
        List<PendingJoin> rejected = [];
        await _gate.WaitAsync();
        try
        {
            host = _hostSession;
            member = _memberSession;
            wasVisible = _visible;
            _hostSession = null;
            _memberSession = null;
            _visible = false;
            rejected.AddRange(_pendingJoins.Values);
            if (member is not null)
                member.Leaving = true;
            if (host is not null || member is not null)
                RaiseSessionChanged();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var pending in rejected)
            pending.Decide(ConsentDecision.Reject, "l'host ha chiuso la sessione");

        if (wasVisible && _discovery is { } discovery)
            await TryAnnounceAsync(() => discovery.AnnounceHiddenAsync(
                new AnnouncementPayload(Identity.DeviceId, Identity.DisplayName, Identity.UserName, Port)));

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

        if (host is { Members.IsEmpty: false } || member is not null)
            Log("Sessione terminata.");
    }

    private async Task OnLocalClipboardChangedAsync(ClipboardChangedEventArgs e)
    {
        if (e.IsOwnWrite)
            return;

        var content = e.Content;
        var sensitive = content.IsMarkedSensitive;
        var itemId = NewId();
        var stamp = Interlocked.Increment(ref _clipboardStamp);
        RegisterLocalOffer(itemId, content);
        var shared = await ShareLocalCopyAsync(itemId, content, sensitive, stamp);
        if (shared)
            MarkOfferShared(itemId);
        SetCurrentClipboard(ClipboardSummary.From(content, Identity.DeviceId, Identity.DisplayName, sensitive, shared));
    }

    /// <returns>True se la copia è uscita da questo PC (verso almeno un altro partecipante).</returns>
    private async Task<bool> ShareLocalCopyAsync(string itemId, ClipboardContent content, bool sensitive, long stamp)
    {
        // Sola lettura: le copie fatte su questo PC non escono.
        if (_memberSession is { Direction: ShareDirection.ReadOnly })
            return false;
        if (_hostSession is { Members.IsEmpty: true })
            return false;
        if (_hostSession is null && _memberSession is null)
            return false;

        if (!IsAcceptableContent(content, out var why))
        {
            _notifier.Error($"Copia non condivisa: {why}");
            return false;
        }

        if (_hostSession is not null)
        {
            var sent = await DistributeAsync(Identity.DeviceId, Identity.DisplayName, itemId, content, null, stamp, sensitive);
            if (sensitive)
            {
                Log(sent > 0
                    ? $"Contenuto sensibile condiviso con {sent} {(sent == 1 ? "membro affidabile" : "membri")}."
                    : "Contenuto sensibile: nessun membro può vederlo, resta su questo PC.");
                return sent > 0;
            }

            if (sent >= 0)
                Log($"Condiviso: {Short(content)}");
            return sent >= 0;
        }

        if (_memberSession is not { } member)
            return false;

        IReadOnlyList<string>? recipients = null;
        if (sensitive)
        {
            // Un contenuto sensibile di un membro passa dall'host: esce solo se il membro si fida
            // dell'host, e l'host lo inoltra solo ai partecipanti di cui il membro si fida.
            var hostId = member.Host.Remote.DeviceId;
            if (!Friends.TrustsWithSensitive(hostId))
            {
                Log("Contenuto marcato come sensibile: l'host non è tra i tuoi affidabili, resta su questo PC.");
                return false;
            }

            recipients = member.Participants
                .Where(p => p.DeviceId != hostId && p.DeviceId != Identity.DeviceId && Friends.TrustsWithSensitive(p.DeviceId))
                .Select(p => p.DeviceId)
                .ToList();
        }

        // In attesa prima dell'invio: la conferma dell'host può arrivare prima che SendAsync ritorni.
        await SetPendingPushAsync(member, itemId, pending: true);
        try
        {
            await member.Host.SendAsync(new ClipboardPushMessage(itemId, content, sensitive, recipients));
            Log(sensitive ? "Inviato all'host: contenuto sensibile" : $"Inviato all'host: {Short(content)}");
            return true;
        }
        catch (MessageTooLargeException)
        {
            await SetPendingPushAsync(member, itemId, pending: false);
            _notifier.Error("Copia non condivisa: troppo grande per essere inviata.");
        }
        catch (IOException)
        {
            // L'host è caduto: la sessione si chiude in OnHostClosedAsync.
            await SetPendingPushAsync(member, itemId, pending: false);
        }

        return false;
    }

    private async Task SetPendingPushAsync(MemberSession member, string itemId, bool pending)
    {
        await _gate.WaitAsync();
        try
        {
            if (pending)
                member.PendingPushes.Add(itemId);
            else
                member.PendingPushes.Remove(itemId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cronologia: sull'host tutte le voci salvate; per un membro le anteprime della sessione in
    /// corso (la voce completa si chiede all'host in <see cref="UseHistoryEntryAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_memberSession is { } member)
        {
            var reply = await member.Host.RequestAsync<HistoryReplyMessage>(
                id => new HistoryRequestMessage(id), TimeSpan.FromSeconds(15), cancellationToken);
            return reply.Entries ?? [];
        }

        return _database.GetHistory();
    }

    /// <summary>Rimette una voce della cronologia nella propria clipboard (senza ridistribuirla).</summary>
    public async Task UseHistoryEntryAsync(HistoryEntry entry)
    {
        if (entry.IsPreview)
            entry = await LoadFullHistoryEntryAsync(entry);
        if (!IsAcceptableContent(entry.Content, out var why))
            throw new CusOperationException($"Voce della cronologia non utilizzabile: {why}.");

        IRemoteFileSource? source = null;
        if (entry.Content.Kind == ClipboardContentKind.Files)
        {
            if (entry.OriginDeviceId == Identity.DeviceId)
            {
                if (!IsCurrentOffer(entry.ItemId))
                    throw new CusOperationException("I file non sono più disponibili: su questo PC è stato copiato altro.");
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
        SetCurrentClipboard(ClipboardSummary.From(entry.Content, entry.OriginDeviceId, DisplayText.Sanitize(entry.OriginName, 64), false, isShared: false));
    }

    private async Task<HistoryEntry> LoadFullHistoryEntryAsync(HistoryEntry preview)
    {
        if (_memberSession is { } member)
        {
            HistoryItemReplyMessage reply;
            try
            {
                reply = await member.Host.RequestAsync<HistoryItemReplyMessage>(
                    id => new HistoryItemRequestMessage(id, preview.ItemId), TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                throw new CusOperationException("L'host non risponde: voce della cronologia non recuperata.", ex);
            }

            if (reply.Error is not null || reply.Entry is null)
                throw new CusOperationException(DisplayText.Sanitize(reply.Error, 200, "Voce della cronologia non disponibile."));
            if (reply.Entry.ItemId != preview.ItemId || reply.Entry.IsPreview || reply.Entry.Content is null)
                throw new CusOperationException("Risposta dell'host non valida.");
            return reply.Entry;
        }

        return _database.GetHistoryEntry(preview.ItemId)
            ?? throw new CusOperationException("Voce non più presente nella cronologia.");
    }

    /// <summary>
    /// File ricevuti che avviano codice all'apertura: lo si dice subito. Esplora risorse li marca come
    /// scaricati (Mark-of-the-Web), ma il nome e l'icona li sceglie chi li manda.
    /// </summary>
    private void WarnAboutExecutables(ClipboardContent content, string originName)
    {
        if (content.Kind != ClipboardContentKind.Files)
            return;
        var executables = ClipboardFileNames.ExecutableNames(content.Files);
        if (executables.Count == 0)
            return;
        var shown = string.Join(", ", executables.Take(3).Select(n => DisplayText.Sanitize(n, 60, "?")))
            + (executables.Count > 3 ? $" e altri {executables.Count - 3}" : "");
        _notifier.Error($"Attenzione: {originName} ha condiviso file che avviano programmi ({shown}). Aprili solo se ti fidi.");
    }

    private static bool IsAcceptableContent(ClipboardContent content, out string reason)
    {
        reason = content.Kind switch
        {
            ClipboardContentKind.Image when content.ImagePng is not { Length: > 0 } => "immagine vuota",
            ClipboardContentKind.Image when content.ImagePng.Length > MaxImageBytes => $"immagine troppo grande (oltre {MaxImageBytes / 1024 / 1024} MB)",
            ClipboardContentKind.Text when content.Text is null => "testo vuoto",
            ClipboardContentKind.Text when content.Text.Length > MaxTextChars => "testo troppo lungo",
            ClipboardContentKind.Files when !ClipboardFileNames.TryValidate(content.Files, out var fileReason) => fileReason,
            ClipboardContentKind.Text or ClipboardContentKind.Image or ClipboardContentKind.Files => "",
            _ => "tipo di contenuto sconosciuto",
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
                .Select(m => new ParticipantDto(m.Connection.Remote.DeviceId, m.DisplayName ?? m.Connection.Remote.DisplayName, m.Direction, IsHost: false, m.Granted)),
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
                        log($"Copia non inviata a {connection.Remote.DisplayName}: troppo grande.");
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
                    throw new RemoteFileUnavailableException("Il PC d'origine non è raggiungibile: la sessione è terminata.");

                FileReadReplyMessage reply;
                try
                {
                    reply = await connection.RequestAsync<FileReadReplyMessage>(
                        id => new FileReadRequestMessage(id, itemId, fileIndex, offset, Math.Min(count, MaxFileReadBytes), forDeviceId, forName),
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
                    throw new RemoteFileUnavailableException(DisplayText.Sanitize(reply.Error, 300, "errore sul PC d'origine"));

                // Mai più byte di quelli chiesti, anche se l'altro PC ne manda di più.
                var data = reply.Data ?? [];
                var requested = Math.Min(count, MaxFileReadBytes);
                return data.Length <= requested ? data : data[..requested];
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
            node.TryReadLocalOffer(itemId, fileIndex, offset, count, node.Identity.DeviceId, node.Identity.DisplayName, out var data)
                ? Task.FromResult(data)
                : throw new RemoteFileUnavailableException("Su questo PC è stato copiato altro: i file non sono più disponibili.");
    }
}
