using System.IO.Enumeration;

namespace Cus.Core.Abstractions;

/// <summary>Costruisce l'elenco dei file copiati a partire da percorsi locali (file e cartelle).</summary>
public static class LocalFileList
{
    /// <summary>
    /// File singoli e cartelle ricorsive. I nomi sono relativi alla cartella che contiene ciò che
    /// è stato copiato, con '/' come separatore: "relazione.pdf", "progetto/src/a.cs".
    /// </summary>
    /// <remarks>
    /// Come Esplora risorse, include anche i file nascosti e di sistema. Si ferma poco oltre
    /// <see cref="ClipboardFileNames.MaxFiles"/>: un elenco più lungo non verrebbe comunque condiviso.
    /// Dentro una cartella copiata i collegamenti simbolici e le giunzioni si saltano: porterebbero
    /// fuori dalla cartella (es. "docs" → ~/.ssh) file che l'utente non ha scelto di condividere.
    /// </remarks>
    public static List<ClipboardFileInfo> FromPaths(IEnumerable<string> paths)
    {
        var files = new List<ClipboardFileInfo>();
        foreach (var path in paths)
        {
            if (files.Count > ClipboardFileNames.MaxFiles)
                break;

            if (File.Exists(path))
            {
                files.Add(new ClipboardFileInfo(Path.GetFileName(path), new FileInfo(path).Length) { LocalPath = path });
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                MaxRecursionDepth = ClipboardFileNames.MaxDepth,
            };
            var entries = new FileSystemEnumerable<string>(path, (ref FileSystemEntry e) => e.ToFullPath(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !IsLink(ref e),
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsLink(ref e),
            };
            foreach (var entry in entries.Prepend(path))
            {
                if (files.Count > ClipboardFileNames.MaxFiles)
                    break;
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    // Le cartelle con contenuto compaiono già nei nomi dei file; quelle vuote
                    // andrebbero perse, quindi si elencano esplicitamente.
                    if (!Directory.EnumerateFileSystemEntries(entry, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Any())
                        files.Add(new ClipboardFileInfo(relative, 0) { IsDirectory = true });
                    continue;
                }

                files.Add(new ClipboardFileInfo(relative, new FileInfo(entry).Length) { LocalPath = entry });
            }
        }

        return files;
    }

    /// <summary>
    /// Collegamento simbolico o giunzione. Non basta l'attributo ReparsePoint: lo hanno anche i file
    /// "su richiesta" di OneDrive, che sono file veri e vanno condivisi.
    /// </summary>
    private static bool IsLink(ref FileSystemEntry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            return false;
        var fullPath = entry.ToFullPath();
        FileSystemInfo info = entry.IsDirectory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        try
        {
            return info.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
