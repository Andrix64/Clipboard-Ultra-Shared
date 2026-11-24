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
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                if (files.Count > ClipboardFileNames.MaxFiles)
                    break;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                files.Add(new ClipboardFileInfo(relative, new FileInfo(file).Length) { LocalPath = file });
            }
        }

        return files;
    }
}
