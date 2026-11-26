using Cus.Core.Abstractions;

namespace Cus.Core.Tests;

public class LocalFileListTests
{
    [Fact]
    public void EmptySubfolders_AreListedAsDirectories()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "progetto");
        Directory.CreateDirectory(Path.Combine(root, "vuota"));
        Directory.CreateDirectory(Path.Combine(root, "src", "annidata", "vuota2"));
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "x");

        var files = LocalFileList.FromPaths([root]);

        Assert.Equal(
            ["progetto/src/a.cs", "progetto/src/annidata/vuota2", "progetto/vuota"],
            files.Select(f => f.Name).Order(StringComparer.Ordinal).ToList());
        Assert.All(files.Where(f => f.Name.EndsWith("vuota") || f.Name.EndsWith("vuota2")), f => Assert.True(f.IsDirectory));
        Assert.False(files.Single(f => f.Name.EndsWith("a.cs")).IsDirectory);
        Assert.True(ClipboardFileNames.TryValidate(files, out var reason), reason);
    }

    [Fact]
    public void LinksInsideACopiedFolder_AreNotFollowed()
    {
        using var temp = new TempDirectory();
        var secrets = Path.Combine(temp.Path, "segreti");
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(secrets, "id_rsa"), "chiave");
        var root = Path.Combine(temp.Path, "progetto");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        var link = Path.Combine(root, "docs");
        CreateDirectoryLink(link, secrets);
        try
        {
            var files = LocalFileList.FromPaths([root]);

            Assert.Equal(["progetto/a.txt"], files.Select(f => f.Name).ToList());
        }
        finally
        {
            // La cancellazione ricorsiva della cartella temporanea non sa togliere le giunzioni.
            Directory.Delete(link);
        }
    }

    /// <summary>Su Windows una giunzione: a differenza dei collegamenti simbolici non richiede privilegi.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public void EmptyRootFolder_IsListed()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "vuota");
        Directory.CreateDirectory(root);

        var file = Assert.Single(LocalFileList.FromPaths([root]));

        Assert.Equal("vuota", file.Name);
        Assert.True(file.IsDirectory);
    }

    [Fact]
    public void DirectoryEntries_AreValidated()
    {
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("a", 5) { IsDirectory = true }], out _));
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("a", 1), new ClipboardFileInfo("a", 0) { IsDirectory = true }], out _));
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("a", 0) { IsDirectory = true }, new ClipboardFileInfo("a", 1)], out _));
        Assert.True(ClipboardFileNames.TryValidate([new ClipboardFileInfo("a/b", 0) { IsDirectory = true }, new ClipboardFileInfo("a/c.txt", 1)], out var reason), reason);
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("..", 0) { IsDirectory = true }], out _));
    }
}
