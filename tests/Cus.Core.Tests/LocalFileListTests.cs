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
