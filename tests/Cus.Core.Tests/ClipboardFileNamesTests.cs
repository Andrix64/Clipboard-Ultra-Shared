using Cus.Core.Abstractions;

namespace Cus.Core.Tests;

public class ClipboardFileNamesTests
{
    [Theory]
    [InlineData("relazione.pdf")]
    [InlineData("progetto/src/a.cs")]
    [InlineData(".gitignore")]
    [InlineData("cartella con spazi/file (1).txt")]
    [InlineData("console.log")]
    [InlineData("COM10")]
    public void ValidNames_AreAccepted(string name) =>
        Assert.True(ClipboardFileNames.TryValidate([new ClipboardFileInfo(name, 1)], out var reason), reason);

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/../../b.txt")]
    [InlineData("./a.txt")]
    [InlineData("..\\..\\AppData\\Startup\\x.bat")]
    [InlineData("a\\b.txt")]
    [InlineData("C:x.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("/assoluto.txt")]
    [InlineData("a//b.txt")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("cartella/NUL")]
    [InlineData("LPT1 .log")]
    [InlineData("COM¹")]
    [InlineData("finisce.")]
    [InlineData("finisce ")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("a<b>")]
    [InlineData("tab\tnel nome")]
    [InlineData("nul\0nel nome")]
    public void DangerousNames_AreRejected(string name) =>
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo(name, 1)], out _));

    [Fact]
    public void LengthAndDepthLimits_AreEnforced()
    {
        Assert.False(ClipboardFileNames.TryValidateName(new string('a', 256), out _));
        Assert.False(ClipboardFileNames.TryValidateName(string.Join('/', Enumerable.Repeat("abcdefgh", 30)), out _));
        Assert.False(ClipboardFileNames.TryValidateName(string.Join('/', Enumerable.Repeat("a", 33)), out _));
        Assert.True(ClipboardFileNames.TryValidateName(string.Join('/', Enumerable.Repeat("a", 32)), out var reason), reason);
    }

    [Fact]
    public void ListLevelRules_AreEnforced()
    {
        Assert.False(ClipboardFileNames.TryValidate(null, out _));
        Assert.False(ClipboardFileNames.TryValidate([], out _));
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("a.txt", -1)], out _));
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("a.txt", 1), new ClipboardFileInfo("A.TXT", 1)], out _));
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("x", 1), new ClipboardFileInfo("x/y.txt", 1)], out _));
        Assert.False(ClipboardFileNames.TryValidate([new ClipboardFileInfo("x/y.txt", 1), new ClipboardFileInfo("x", 1)], out _));
        var tooMany = Enumerable.Range(0, ClipboardFileNames.MaxFiles + 1).Select(i => new ClipboardFileInfo($"f{i}", 0)).ToList();
        Assert.False(ClipboardFileNames.TryValidate(tooMany, out _));
        Assert.True(ClipboardFileNames.TryValidate([new ClipboardFileInfo("x/a.txt", 1), new ClipboardFileInfo("x/b.txt", 1)], out var reason), reason);
    }
}

public class DisplayTextTests
{
    [Fact]
    public void NewlinesControlAndBidiCharacters_AreRemoved()
    {
        Assert.Equal("Mario PC", DisplayText.Sanitize("Mario\r\nPC", 64));
        Assert.Equal("abc", DisplayText.Sanitize("a‮b​c", 64));
        Assert.Equal("a b", DisplayText.Sanitize("  a \t\u0001  b ", 64));
    }

    [Fact]
    public void EmptyOrBlank_UsesTheFallback_AndLongTextIsCut()
    {
        Assert.Equal("(senza nome)", DisplayText.Sanitize(null, 64));
        Assert.Equal("x", DisplayText.Sanitize("‮\n", 64, "x"));
        Assert.Equal(10, DisplayText.Sanitize(new string('z', 100), 10).Length);
        Assert.Equal("a", DisplayText.Sanitize("a😀", 2));
    }
}
