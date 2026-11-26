using Cus.Core.Abstractions;
using Cus.Core.Imaging;
using Cus.Core.Net;
using Cus.Core.Storage;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    /// <summary>PNG oltre questa dimensione non entrano in un messaggio (limite del canale).</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    public const int MaxTextChars = 2 * 1024 * 1024;

    private const int MaxFileReadBytes = 1024 * 1024;

    /// <summary>Partecipanti di una sessione che un membro accetta dallo stato dell'host.</summary>
    private const int MaxParticipants = 64;

    private int _joining;

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
            pending.Decide(ConsentDecision.Reject, WireTexts.HostClosedSession.Text);

        if (wasVisible && _discovery is { } discovery)
            await TryAnnounceAsync(() => discovery.AnnounceHiddenAsync(
                new AnnouncementPayload(Identity.DeviceId, Identity.DisplayName, Identity.UserName, Port)));

        if (host is not null)
        {
            foreach (var m in host.Members.Values)
            {
                try
                {
                    await m.Connection.SendAsync(new SessionEndedMessage(WireTexts.HostClosedSession.Text));
                }
                catch (IOException)
                {
                    // Già caduto.
                }

                m.Connection.Close(Loc.T("session.sessionClosedByTheHost"));
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

            member.Host.Close(Loc.T("session.leftTheSession"));
        }

        if (host is { Members.IsEmpty: false } || member is not null)
            Log(Loc.T("session.sessionEnded"));
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
            _notifier.Error(Loc.T("common.copyNotShared", why));
            return false;
        }

        if (_hostSession is not null)
        {
            var sent = await DistributeAsync(Identity.DeviceId, Identity.DisplayName, itemId, content, null, stamp, sensitive);
            if (sensitive)
            {
                Log(sent > 0
                    ? sent == 1
                        ? Loc.T("session.sensitiveContentSharedWithTrustedMember")
                        : Loc.T("session.sensitiveContentSharedWithMembers", sent)
                    : Loc.T("session.sensitiveContentNoMemberCanSee"));
                return sent > 0;
            }

            if (sent >= 0)
                Log(Loc.T("session.shared", Short(content)));
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
                Log(Loc.T("session.contentMarkedAsSensitiveTheHost"));
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
            Log(sensitive ? Loc.T("session.sentToTheHostSensitiveContent") : Loc.T("session.sentToTheHost", Short(content)));
            return true;
        }
        catch (MessageTooLargeException)
        {
            await SetPendingPushAsync(member, itemId, pending: false);
            _notifier.Error(Loc.T("session.copyNotSharedTooLargeTo"));
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
            throw new CusOperationException(Loc.T("session.historyItemNotUsable", why));

        IRemoteFileSource? source = null;
        if (entry.Content.Kind == ClipboardContentKind.Files)
        {
            if (entry.OriginDeviceId == Identity.DeviceId)
            {
                if (!IsCurrentOffer(entry.ItemId))
                    throw new CusOperationException(Loc.T("session.theFilesAreNoLongerAvailable"));
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
                throw new CusOperationException(Loc.T("session.theSourcePcIsNoLonger"));
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
                throw new CusOperationException(Loc.T("session.theHostIsnTRespondingHistory"), ex);
            }

            if (reply.Error is not null || reply.Entry is null)
                throw new CusOperationException(WireTexts.Localize(DisplayText.Sanitize(reply.Error, 200, Loc.T("session.historyItemNotAvailable"))));
            if (reply.Entry.ItemId != preview.ItemId || reply.Entry.IsPreview || reply.Entry.Content is null)
                throw new CusOperationException(Loc.T("session.invalidReplyFromTheHost"));
            return reply.Entry;
        }

        return _database.GetHistoryEntry(preview.ItemId)
            ?? throw new CusOperationException(Loc.T("session.itemNoLongerInTheHistory"));
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
            + (executables.Count > 3 ? Loc.T("session.andMore", executables.Count - 3) : "");
        _notifier.Error(Loc.T("session.warningSharedFilesThatLaunchPrograms", originName, shown));
    }

    private static bool IsAcceptableContent(ClipboardContent content, out string reason)
    {
        reason = content.Kind switch
        {
            ClipboardContentKind.Image when content.ImagePng is not { Length: > 0 } => Loc.T("session.emptyImage"),
            ClipboardContentKind.Image when content.ImagePng.Length > MaxImageBytes => Loc.T("session.imageTooLargeOverMb", MaxImageBytes / 1024 / 1024),
            // Finisce così com'è nella clipboard di tutti: un PNG non valido o con dimensioni assurde
            // metterebbe alla prova i decoder dei programmi in cui si incolla.
            ClipboardContentKind.Image when !PngCodec.TryReadSize(content.ImagePng, out var width, out var height)
                                            || (long)width * height > PngCodec.DefaultMaxPixels => Loc.T("session.imageNotAValidPng"),
            ClipboardContentKind.Text when content.Text is null => Loc.T("session.emptyText"),
            ClipboardContentKind.Text when content.Text.Length > MaxTextChars => Loc.T("session.textTooLong"),
            ClipboardContentKind.Files when !ClipboardFileNames.TryValidate(content.Files, out var fileReason) => fileReason,
            ClipboardContentKind.Text or ClipboardContentKind.Image or ClipboardContentKind.Files => "",
            _ => Loc.T("session.unknownContentType"),
        };
        return reason.Length == 0;
    }

    /// <summary>Anteprima per il registro attività: il testo arriva da altri PC, quindi si ripulisce.</summary>
    private static string Short(ClipboardContent content)
    {
        var text = DisplayText.Sanitize(content.Describe().ReplaceLineEndings(" "), 200);
        return text.Length <= 60 ? text : text[..57] + "...";
    }
}
