using Cus.Core.Abstractions;
using Cus.Platform.Linux.Interop;
using static Cus.Platform.Linux.Interop.Xlib;

namespace Cus.Platform.Linux;

public sealed partial class X11Clipboard : IClipboardMonitor, IClipboardWriter
{
    // ---- Tipi di supporto ----------------------------------------------------------------------

    private sealed class Atoms(nint display)
    {
        public nuint Clipboard { get; } = XInternAtom(display, "CLIPBOARD", 0);
        public nuint Targets { get; } = XInternAtom(display, "TARGETS", 0);
        public nuint Timestamp { get; } = XInternAtom(display, "TIMESTAMP", 0);
        public nuint Incr { get; } = XInternAtom(display, "INCR", 0);
        public nuint Utf8String { get; } = XInternAtom(display, "UTF8_STRING", 0);
        public nuint TextPlainUtf8 { get; } = XInternAtom(display, "text/plain;charset=utf-8", 0);
        public nuint TextPlain { get; } = XInternAtom(display, "text/plain", 0);
        public nuint Png { get; } = XInternAtom(display, "image/png", 0);
        public nuint UriList { get; } = XInternAtom(display, FileUris.UriListTarget, 0);
        public nuint GnomeCopiedFiles { get; } = XInternAtom(display, FileUris.GnomeCopiedFilesTarget, 0);
        public nuint PasswordHint { get; } = XInternAtom(display, PasswordHintTarget, 0);
        public nuint ReadProperty { get; } = XInternAtom(display, "CUS_SELECTION", 0);
        public nuint TimeProperty { get; } = XInternAtom(display, "CUS_TIMESTAMP", 0);
    }

    /// <summary>Ciò che CUS offre come proprietario della clipboard.</summary>
    private sealed record Offer(Dictionary<nuint, byte[]> Data, FileOffer? Files, nuint Time);

    private sealed class FileOffer(IReadOnlyList<ClipboardFileInfo> files, IRemoteFileSource source)
    {
        public IReadOnlyList<ClipboardFileInfo> Files { get; } = files;
        public IRemoteFileSource Source { get; } = source;
        public List<XSelectionRequestEvent> Pending { get; } = [];
        public Task<DownloadedBatch?>? Download { get; set; }
        public IReadOnlyList<string>? Ready { get; set; }
        public string? BatchDirectory { get; set; }
    }

    private sealed record DownloadedBatch(string Directory, IReadOnlyList<string> TopLevelPaths);

    private sealed class IncrTransfer(nuint requestor, nuint property, nuint type, byte[] data)
    {
        public nuint Requestor { get; } = requestor;
        public nuint Property { get; } = property;
        public nuint Type { get; } = type;
        public byte[] Data { get; } = data;
        public int Offset { get; set; }
        public DateTime LastActivity { get; set; }
    }

    /// <summary>Dati letti con il thread X; <see cref="Own"/> se il proprietario è CUS.</summary>
    private sealed record RawClipboard(
        bool Sensitive = false,
        List<string>? Paths = null,
        string? Text = null,
        byte[]? Png = null,
        ClipboardContent? Own = null);
}
