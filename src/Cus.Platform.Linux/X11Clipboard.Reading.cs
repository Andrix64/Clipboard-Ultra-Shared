using System.Text;
using Cus.Core.Abstractions;
using Cus.Platform.Linux.Interop;
using static Cus.Platform.Linux.Interop.Xlib;

namespace Cus.Platform.Linux;

public sealed partial class X11Clipboard : IClipboardMonitor, IClipboardWriter
{
    // ---- Monitor -----------------------------------------------------------------------------

    private void OnOwnerChanged(in XFixesSelectionNotifyEvent e)
    {
        if (!_started || e.selection != _atoms.Clipboard)
            return;

        if (e.owner == _window)
        {
            if (_lastWritten is { } own)
                _events.Writer.TryWrite(new RawClipboard(Own: own));
            return;
        }

        // Proprietario sparito (applicazione chiusa): la clipboard è vuota, niente da condividere.
        if (e.owner == None)
            return;

        _changePending = true;
        _changeTime = e.selection_timestamp;
        // Dentro un'attesa annidata non si legge: lo fa il ciclo principale appena libero.
    }

    private void ReadAndPublish(nuint time)
    {
        try
        {
            if (XGetSelectionOwner(_display, _atoms.Clipboard) is var owner && (owner == None || owner == _window))
                return;
            if (ReadForeign(time) is { } raw)
                _events.Writer.TryWrite(raw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: lettura della clipboard X11 fallita: {ex.Message}");
        }
    }

    /// <summary>Legge i formati utili del proprietario attuale. Le conversioni (disco, immagini) avvengono dopo, fuori da questo thread.</summary>
    private RawClipboard? ReadForeign(nuint time)
    {
        var targets = ConvertAtoms(_atoms.Targets, time);
        bool Offers(nuint target) => targets is null || targets.Contains(target);

        var sensitive = targets is not null && targets.Contains(_atoms.PasswordHint)
            && Convert(_atoms.PasswordHint, time) is { } hint
            && Encoding.UTF8.GetString(hint).Trim('\0', ' ', '\n').Equals("secret", StringComparison.OrdinalIgnoreCase);

        List<string>? paths = null;
        if (targets is not null && targets.Contains(_atoms.GnomeCopiedFiles) && Convert(_atoms.GnomeCopiedFiles, time) is { } gnome)
            paths = FileUris.ParseGnomeCopiedFiles(Encoding.UTF8.GetString(gnome));
        if (paths is not { Count: > 0 } && targets is not null && targets.Contains(_atoms.UriList) && Convert(_atoms.UriList, time) is { } uris)
            paths = FileUris.ParseUriList(Encoding.UTF8.GetString(uris));

        // Il testo si legge anche accanto ai file: serve se i file non sono leggibili.
        string? text = null;
        if (Offers(_atoms.Utf8String) && Convert(_atoms.Utf8String, time) is { } utf8)
            text = Encoding.UTF8.GetString(utf8);
        else if (targets is not null && targets.Contains(_atoms.TextPlainUtf8) && Convert(_atoms.TextPlainUtf8, time) is { } plain)
            text = Encoding.UTF8.GetString(plain);
        else if (targets is not null && targets.Contains(XA_STRING) && Convert(XA_STRING, time) is { } latin1)
            text = Encoding.Latin1.GetString(latin1);
        if (text is not null)
        {
            var end = text.IndexOf('\0');
            if (end >= 0)
                text = text[..end];
        }

        if (paths is { Count: > 0 } || text is not null)
            return new RawClipboard(Sensitive: sensitive, Paths: paths, Text: text);

        if (targets is not null && targets.Contains(_atoms.Png) && Convert(_atoms.Png, time) is { Length: > 0 } png)
            return new RawClipboard(Sensitive: sensitive, Png: png);

        return null;
    }

    private List<nuint>? ConvertAtoms(nuint target, nuint time)
    {
        var data = Convert(target, time, out var format);
        if (data is null || format != 32)
            return null;
        var count = data.Length / IntPtr.Size;
        var atoms = new List<nuint>(count);
        for (var i = 0; i < count; i++)
            atoms.Add(IntPtr.Size == 8 ? (nuint)BitConverter.ToUInt64(data, i * 8) : BitConverter.ToUInt32(data, i * 4));
        return atoms;
    }

    private byte[]? Convert(nuint target, nuint time) => Convert(target, time, out _);

    /// <summary>Chiede al proprietario un formato e aspetta la risposta, anche incrementale (INCR).</summary>
    private byte[]? Convert(nuint target, nuint time, out int format)
    {
        format = 0;
        XDeleteProperty(_display, _window, _atoms.ReadProperty);
        XConvertSelection(_display, _atoms.Clipboard, target, _atoms.ReadProperty, _window, time);
        var window = _window;
        var clipboard = _atoms.Clipboard;
        if (!WaitForEvent((in XEvent e) => e.type == SelectionNotify && e.selection.requestor == window
                && e.selection.selection == clipboard && e.selection.target == target, ReplyTimeoutMs, out var reply))
            return null;
        if (reply.selection.property == None)
            return null;

        var property = ReadProperty(_window, _atoms.ReadProperty, delete: true);
        if (property is null)
            return null;
        format = property.Value.Format;
        if (property.Value.Type != _atoms.Incr)
            return property.Value.Data;

        // INCR: il proprietario scrive un pezzo per volta, ogni volta che cancelliamo la proprietà.
        var readProperty = _atoms.ReadProperty;
        using var buffer = new MemoryStream();
        while (true)
        {
            if (!WaitForEvent((in XEvent e) => e.type == PropertyNotify && e.property.window == window
                    && e.property.atom == readProperty && e.property.state == PropertyNewValue, ReplyTimeoutMs, out _))
                return null;
            var chunk = ReadProperty(_window, _atoms.ReadProperty, delete: true);
            if (chunk is null)
                return null;
            format = chunk.Value.Format;
            if (chunk.Value.Data.Length == 0)
                return buffer.ToArray();
            if (buffer.Length + chunk.Value.Data.Length > MaxReadBytes)
                return null;
            buffer.Write(chunk.Value.Data);
        }
    }

    private unsafe (nuint Type, int Format, byte[] Data)? ReadProperty(nuint window, nuint property, bool delete)
    {
        var status = XGetWindowProperty(_display, window, property, 0, MaxReadBytes / 4, delete ? 1 : 0, None,
            out var type, out var format, out var count, out _, out var data);
        try
        {
            if (status != 0 || type == None)
                return null;
            var size = checked((int)((long)count * ElementSize(format)));
            return (type, format, size == 0 ? [] : new ReadOnlySpan<byte>((void*)data, size).ToArray());
        }
        finally
        {
            if (data != 0)
                XFree(data);
        }
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var raw in _events.Reader.ReadAllAsync())
        {
            try
            {
                if (ToArgs(raw) is { } args)
                    Changed?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: handler della clipboard fallito: {ex}");
            }
        }
    }

    private ClipboardChangedEventArgs? ToArgs(RawClipboard raw)
    {
        if (raw.Own is { } own)
        {
            _foreignSinceWrite = false;
            return new ClipboardChangedEventArgs(own, isOwnWrite: true);
        }

        var content = Convert(raw);
        if (content is null)
            return null;

        // Un gestore di clipboard (GPaste, Klipper...) può riprendersi la selezione con lo stesso
        // contenuto appena scritto da CUS: è ancora la nostra scrittura, non una copia dell'utente.
        var echo = !_foreignSinceWrite && IsEchoOfLastWrite(content);
        _foreignSinceWrite = true;
        return echo && _lastWritten is { } last
            ? new ClipboardChangedEventArgs(last, isOwnWrite: true)
            : new ClipboardChangedEventArgs(content with { IsMarkedSensitive = raw.Sensitive }, isOwnWrite: false);
    }

    private ClipboardContent? Convert(RawClipboard raw)
    {
        if (raw.Paths is { Count: > 0 } paths)
        {
            try
            {
                var files = LocalFileList.FromPaths(paths);
                if (files.Count > 0)
                    return ClipboardContent.FromFiles(files);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: file della clipboard non leggibili: {ex.Message}");
            }
        }

        if (raw.Text is not null)
            return ClipboardContent.FromText(raw.Text);
        if (raw.Png is { Length: > 0 } png)
            return ClipboardContent.FromImage(png);
        return null;
    }

    private bool IsEchoOfLastWrite(ClipboardContent content) => (_lastWritten, content) switch
    {
        ({ Kind: ClipboardContentKind.Text } last, { Kind: ClipboardContentKind.Text }) => last.Text == content.Text,
        ({ Kind: ClipboardContentKind.Image } last, { Kind: ClipboardContentKind.Image }) =>
            last.ImagePng.AsSpan().SequenceEqual(content.ImagePng),
        ({ Kind: ClipboardContentKind.Files }, { Kind: ClipboardContentKind.Files, Files: { } files }) =>
            files.All(f => f.LocalPath is { } p && IsInsideCache(p)),
        _ => false,
    };

    private bool IsInsideCache(string path) =>
        Path.GetFullPath(path).StartsWith(_cacheDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
