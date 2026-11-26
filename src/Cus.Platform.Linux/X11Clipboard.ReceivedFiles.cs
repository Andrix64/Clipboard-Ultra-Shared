using System.Text;
using Cus.Core.Abstractions;
using static Cus.Platform.Linux.Interop.Xlib;

namespace Cus.Platform.Linux;

public sealed partial class X11Clipboard : IClipboardMonitor, IClipboardWriter
{
    private byte[] FileListBytes(nuint target, IReadOnlyList<string> paths) => Encoding.UTF8.GetBytes(
        target == _atoms.GnomeCopiedFiles ? FileUris.FormatGnomeCopiedFiles(paths) : FileUris.FormatUriList(paths));

    private Task<DownloadedBatch?> StartDownload(Offer offer, FileOffer files)
    {
        var download = Task.Run(() => DownloadAsync(files));
        download.ContinueWith(t => InvokeAsync(() =>
        {
            var batch = t.IsCompletedSuccessfully ? t.Result : null;
            if (t.Exception is { } ex)
                System.Diagnostics.Trace.WriteLine($"CUS: download dei file ricevuti fallito: {ex.InnerException?.Message ?? ex.Message}");

            var current = ReferenceEquals(_offer, offer);
            if (batch is not null && current)
            {
                files.Ready = batch.TopLevelPaths;
                files.BatchDirectory = batch.Directory;
            }
            else
            {
                // Fallito (si riproverà al prossimo incolla) oppure offerta già sostituita.
                files.Download = null;
            }

            foreach (var request in files.Pending)
            {
                if (files.Ready is { } ready)
                    SendData(request, FileListBytes(request.target, ready));
                else
                    Notify(request, None);
            }

            files.Pending.Clear();
            XFlush(_display);
            return true;
        }), TaskScheduler.Default);
        return download;
    }

    /// <summary>Scarica i file dal PC d'origine in una cartella nuova della cache (0700), come farebbe un incolla.</summary>
    private async Task<DownloadedBatch?> DownloadAsync(FileOffer offer)
    {
        var directory = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        EnsureDiskSpaceFor(offer.Files);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var root = directory + Path.DirectorySeparatorChar;
        try
        {
            for (var i = 0; i < offer.Files.Count; i++)
            {
                var file = offer.Files[i];
                var target = Path.GetFullPath(Path.Combine(root, file.Name));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new InvalidDataException(Loc.T("clipboard.invalidFileListReceived", file.Name));
                if (file.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                for (long position = 0; position < file.SizeBytes;)
                {
                    var count = (int)Math.Min(1024 * 1024, file.SizeBytes - position);
                    var chunk = await offer.Source.ReadAsync(i, position, count).ConfigureAwait(false);
                    if (chunk.Length > count)
                        chunk = chunk[..count];
                    if (chunk.Length == 0)
                        throw new RemoteFileUnavailableException(Loc.T("common.theSourcePcReturnedAnEmpty"));
                    await output.WriteAsync(chunk).ConfigureAwait(false);
                    position += chunk.Length;
                }
            }
        }
        catch
        {
            DeleteDirectory(directory);
            throw;
        }

        return new DownloadedBatch(directory, [.. FileUris.TopLevelNames(offer.Files).Select(n => Path.Combine(directory, n))]);
    }

    /// <summary>Spazio che il download lascia comunque libero sul disco della cache.</summary>
    private const long MinFreeBytesAfterDownload = 1L << 30;

    /// <summary>
    /// Il download parte anche senza un incolla dell'utente (i gestori di clipboard chiedono l'elenco a
    /// ogni cambio): un elenco che dichiara più byte di quanti ce ne stanno riempirebbe il disco. Si scrive
    /// al massimo quanto dichiarato, quindi basta controllare il totale prima di cominciare.
    /// </summary>
    private void EnsureDiskSpaceFor(IReadOnlyList<ClipboardFileInfo> files)
    {
        var total = files.Where(f => !f.IsDirectory).Sum(f => f.SizeBytes);
        var free = new DriveInfo(_cacheDirectory).AvailableFreeSpace;
        if (total > free - MinFreeBytesAfterDownload)
            throw new IOException(Loc.T("clipboard.notEnoughDiskSpace", DisplayText.FormatSize(total), DisplayText.FormatSize(Math.Max(0, free))));
    }

    // ---- Cache dei file ricevuti ---------------------------------------------------------------

    private void DeleteCache()
    {
        if (Directory.Exists(_cacheDirectory))
            DeleteDirectory(_cacheDirectory);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"CUS: impossibile eliminare {directory}: {ex.Message}");
        }
    }
}
