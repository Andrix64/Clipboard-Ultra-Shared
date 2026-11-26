using System.Text;
using Cus.Core.Abstractions;
using Cus.Platform.Linux.Interop;
using static Cus.Platform.Linux.Interop.Xlib;

namespace Cus.Platform.Linux;

public sealed partial class X11Clipboard : IClipboardMonitor, IClipboardWriter
{
    // ---- Scrittura -----------------------------------------------------------------------------

    private void WriteOnLoop(ClipboardContent content, IRemoteFileSource? files)
    {
        var data = new Dictionary<nuint, byte[]>();
        FileOffer? fileOffer = null;
        switch (content.Kind)
        {
            case ClipboardContentKind.Text:
                var text = content.Text ?? "";
                var utf8 = Encoding.UTF8.GetBytes(text);
                data[_atoms.Utf8String] = utf8;
                data[_atoms.TextPlainUtf8] = utf8;
                data[_atoms.TextPlain] = utf8;
                data[XA_STRING] = Encoding.Latin1.GetBytes(text);
                break;
            case ClipboardContentKind.Image when content.ImagePng is { } png:
                data[_atoms.Png] = png;
                break;
            case ClipboardContentKind.Files:
                fileOffer = new FileOffer(content.Files!, files!);
                break;
        }

        // Un contenuto sensibile ricevuto resta sensibile: i gestori di clipboard non lo tengono in cronologia.
        if (content.IsMarkedSensitive)
            data[_atoms.PasswordHint] = "secret"u8.ToArray();

        _lastWritten = content;
        var time = ServerTime();
        var offer = new Offer(data, fileOffer, time);
        DropOffer();
        _offer = offer;
        XSetSelectionOwner(_display, _atoms.Clipboard, _window, time);
        if (XGetSelectionOwner(_display, _atoms.Clipboard) != _window)
        {
            _offer = null;
            throw new InvalidOperationException(Loc.T("clipboard.x11CannotOwnSelection"));
        }

        XFlush(_display);
    }

    /// <summary>Ora del server X, ottenuta con una modifica vuota a una proprietà: serve per possedere la selezione (ICCCM).</summary>
    private unsafe nuint ServerTime()
    {
        var property = _atoms.TimeProperty;
        var window = _window;
        XChangeProperty(_display, _window, property, XA_INTEGER, 8, PropModeAppend, null, 0);
        return WaitForEvent((in XEvent e) => e.type == PropertyNotify && e.property.window == window && e.property.atom == property,
            ReplyTimeoutMs, out var found)
            ? found.property.time
            : CurrentTime;
    }

    private void DropOffer()
    {
        var old = _offer;
        _offer = null;
        if (old?.Files is not { } files)
            return;

        foreach (var request in files.Pending)
            Notify(request, None);
        files.Pending.Clear();
        if (files.BatchDirectory is { } batch)
        {
            _ = Task.Delay(OldBatchLifetime).ContinueWith(_ => DeleteDirectory(batch), TaskScheduler.Default);
        }
        else if (files.Download is { } download)
        {
            // Download ancora in corso: la cartella si conosce solo alla fine.
            _ = download.ContinueWith(
                t => { if (t.IsCompletedSuccessfully && t.Result is { } done) DeleteDirectory(done.Directory); },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void OnSelectionRequest(XSelectionRequestEvent request)
    {
        // Client obsoleti mandano property None: si usa il target come nome della proprietà.
        if (request.property == None)
            request.property = request.target;

        var offer = _offer;
        if (request.selection != _atoms.Clipboard || offer is null || request.owner != _window)
        {
            Notify(request, None);
            return;
        }

        if (request.target == _atoms.Targets)
        {
            var targets = new List<nuint> { _atoms.Targets, _atoms.Timestamp };
            targets.AddRange(offer.Data.Keys);
            if (offer.Files is not null)
                targets.AddRange([_atoms.GnomeCopiedFiles, _atoms.UriList]);
            SetAtomProperty(request.requestor, request.property, XA_ATOM, [.. targets]);
            Notify(request, request.property);
            return;
        }

        if (request.target == _atoms.Timestamp)
        {
            SetAtomProperty(request.requestor, request.property, XA_INTEGER, [offer.Time]);
            Notify(request, request.property);
            return;
        }

        if (offer.Data.TryGetValue(request.target, out var bytes))
        {
            SendData(request, bytes);
            return;
        }

        if (offer.Files is { } files && (request.target == _atoms.UriList || request.target == _atoms.GnomeCopiedFiles))
        {
            if (files.Ready is { } ready)
            {
                SendData(request, FileListBytes(request.target, ready));
                return;
            }

            // Si risponde a download finito: chi incolla resta in attesa della SelectionNotify.
            files.Pending.Add(request);
            files.Download ??= StartDownload(offer, files);
            return;
        }

        Notify(request, None);
    }

    private unsafe void SendData(XSelectionRequestEvent request, byte[] bytes)
    {
        if (bytes.Length <= _maxChunk)
        {
            fixed (byte* p = bytes)
                XChangeProperty(_display, request.requestor, request.property, request.target, 8, PropModeReplace, p, bytes.Length);
            Notify(request, request.property);
            return;
        }

        // INCR: si annuncia la dimensione, poi un pezzo ogni volta che chi chiede cancella la proprietà.
        XSelectInput(_display, request.requestor, PropertyChangeMask);
        SetAtomProperty(request.requestor, request.property, _atoms.Incr, [(nuint)bytes.Length]);
        _incr.Add(new IncrTransfer(request.requestor, request.property, request.target, bytes) { LastActivity = DateTime.UtcNow });
        Notify(request, request.property);
    }

    private unsafe void OnRequestorPropertyDeleted(nuint window, nuint property)
    {
        var transfer = _incr.Find(t => t.Requestor == window && t.Property == property);
        if (transfer is null)
            return;

        var count = Math.Min(_maxChunk, transfer.Data.Length - transfer.Offset);
        fixed (byte* p = transfer.Data)
            XChangeProperty(_display, window, property, transfer.Type, 8, PropModeReplace, p + transfer.Offset, count);
        transfer.Offset += count;
        transfer.LastActivity = DateTime.UtcNow;
        if (count == 0)
            EndIncr(transfer); // Pezzo vuoto: fine del trasferimento.
    }

    private void DropStaleIncrTransfers()
    {
        var now = DateTime.UtcNow;
        foreach (var transfer in _incr.Where(t => now - t.LastActivity > IncrStaleAfter).ToList())
            EndIncr(transfer);
    }

    private void EndIncr(IncrTransfer transfer)
    {
        _incr.Remove(transfer);
        if (!_incr.Exists(t => t.Requestor == transfer.Requestor))
            XSelectInput(_display, transfer.Requestor, NoEventMask);
    }

    private unsafe void SetAtomProperty(nuint window, nuint property, nuint type, nuint[] values)
    {
        // Nel formato 32 Xlib vuole un array di "long" del C, cioè nuint.
        fixed (nuint* p = values)
            XChangeProperty(_display, window, property, type, 32, PropModeReplace, (byte*)p, values.Length);
    }

    private unsafe void Notify(XSelectionRequestEvent request, nuint property)
    {
        var reply = new XEvent
        {
            selection = new XSelectionEvent
            {
                type = SelectionNotify,
                send_event = 1,
                display = _display,
                requestor = request.requestor,
                selection = request.selection,
                target = request.target,
                property = property,
                time = request.time,
            },
        };
        XSendEvent(_display, request.requestor, 0, NoEventMask, &reply);
    }
}
