namespace Cus.Core.Abstractions;

/// <summary>Costruisce l'elenco dei file copiati a partire da percorsi locali (file e cartelle).</summary>
public static class LocalFileList
{
    /// <summary>
    /// File singoli e cartelle ricorsive. I nomi sono relativi alla cartella che contiene ciò che
    /// è stato copiato, con '/' come separatore: "relazione.pdf", "progetto/src/a.cs".
    /// </summary>
    public static List<ClipboardFileInfo> FromPaths(IEnumerable<string> paths)
    {
        var files = new List<ClipboardFileInfo>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                files.Add(new ClipboardFileInfo(Path.GetFileName(path), new FileInfo(path).Length) { LocalPath = path });
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                files.Add(new ClipboardFileInfo(relative, new FileInfo(file).Length) { LocalPath = file });
            }
        }

        return files;
    }
}
