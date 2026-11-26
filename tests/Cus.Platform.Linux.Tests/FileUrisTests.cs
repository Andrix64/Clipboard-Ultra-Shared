using Cus.Core.Abstractions;

namespace Cus.Platform.Linux.Tests;

public class FileUrisTests
{
    [Fact]
    public void ToUri_PercentEncodesSpacesAndNonAscii()
    {
        Assert.Equal("file:///home/anna/Relazione%20finale%20%C3%A8.pdf", FileUris.ToUri("/home/anna/Relazione finale è.pdf"));
        Assert.Equal("file:///tmp/a%23b%25c%3Fd", FileUris.ToUri("/tmp/a#b%c?d"));
    }

    [Fact]
    public void ToUri_RejectsRelativePaths() => Assert.Throws<ArgumentException>(() => FileUris.ToUri("home/anna"));

    [Theory]
    [InlineData("/home/anna/Relazione finale è.pdf")]
    [InlineData("/tmp/a#b%c?d")]
    [InlineData("/tmp/新しい フォルダ")]
    public void TryGetLocalPath_RoundTripsToUri(string path)
    {
        Assert.True(FileUris.TryGetLocalPath(FileUris.ToUri(path), out var decoded));
        Assert.Equal(path, decoded);
    }

    [Theory]
    [InlineData("file:///home/a.txt", "/home/a.txt")]
    [InlineData("file://localhost/home/a.txt", "/home/a.txt")]
    [InlineData("FILE:///home/a%20b", "/home/a b")]
    [InlineData("file:/home/a.txt", "/home/a.txt")]
    [InlineData("file:///home/foto 😀.png", "/home/foto 😀.png")]
    [InlineData("file:///home/100%", "/home/100%")]
    public void TryGetLocalPath_AcceptsLocalFileUris(string uri, string expected)
    {
        Assert.True(FileUris.TryGetLocalPath(uri, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("https://example.com/a.txt")]
    [InlineData("file://server/share/a.txt")]
    [InlineData("smb://server/share/a.txt")]
    [InlineData("file://")]
    [InlineData("file:///home/a%00b")]
    public void TryGetLocalPath_RejectsRemoteOrInvalidUris(string uri) => Assert.False(FileUris.TryGetLocalPath(uri, out _));

    [Fact]
    public void ParseUriList_SkipsCommentsBlankLinesAndRemoteUris()
    {
        var list = "# copiati da Dolphin\r\nfile:///home/a.txt\r\n\r\nhttps://example.com/x\r\nfile:///home/cartella\r\n";

        Assert.Equal(["/home/a.txt", "/home/cartella"], FileUris.ParseUriList(list));
    }

    [Theory]
    [InlineData("copy\nfile:///home/a.txt\nfile:///home/b%20c.txt")]
    [InlineData("cut\nfile:///home/a.txt\nfile:///home/b%20c.txt\n")]
    public void ParseGnomeCopiedFiles_SkipsTheOperationLine(string text) =>
        Assert.Equal(["/home/a.txt", "/home/b c.txt"], FileUris.ParseGnomeCopiedFiles(text));

    [Fact]
    public void Format_ProducesWhatTheParsersRead()
    {
        string[] paths = ["/tmp/cus/x/a b.txt", "/tmp/cus/x/cartella"];

        Assert.Equal("copy\nfile:///tmp/cus/x/a%20b.txt\nfile:///tmp/cus/x/cartella", FileUris.FormatGnomeCopiedFiles(paths));
        Assert.Equal(paths, FileUris.ParseGnomeCopiedFiles(FileUris.FormatGnomeCopiedFiles(paths)));
        Assert.Equal(paths, FileUris.ParseUriList(FileUris.FormatUriList(paths)));
        Assert.EndsWith("\r\n", FileUris.FormatUriList(paths), StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelNames_KeepsTheEntriesTheUserCopied_InOrder()
    {
        ClipboardFileInfo[] files =
        [
            new("relazione.pdf", 10),
            new("progetto/src/a.cs", 5),
            new("progetto/README.md", 3),
            new("vuota", 0) { IsDirectory = true },
        ];

        Assert.Equal(["relazione.pdf", "progetto", "vuota"], FileUris.TopLevelNames(files));
    }
}
