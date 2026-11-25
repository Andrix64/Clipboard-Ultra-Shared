using System.Globalization;
using Cus.Core.Abstractions;

namespace Cus.Core.Tests;

public class DisplayTextFormattingTests
{
    [Theory]
    [InlineData("relazione_finale_molto_lunga_v3.pdf", 20)]
    [InlineData("foto delle vacanze in montagna 2026.jpeg", 16)]
    [InlineData("archivio_progetto_completo.tar.gz", 18)]
    [InlineData("a.docx", 6)]
    public void MiddleEllipsis_KeepsTheExtension_AndFitsTheLimit(string name, int max)
    {
        var shortened = DisplayText.MiddleEllipsis(name, max);

        Assert.True(shortened.Length <= max, $"'{shortened}' supera {max} caratteri");
        Assert.EndsWith(Path.GetExtension(name), shortened);
        Assert.StartsWith(name[..Math.Min(3, name.Length)], shortened);
    }

    [Fact]
    public void MiddleEllipsis_PutsTheDotsInTheMiddle()
    {
        Assert.Equal("relazione_…ga_v3.pdf", DisplayText.MiddleEllipsis("relazione_finale_molto_lunga_v3.pdf", 20));
    }

    [Fact]
    public void MiddleEllipsis_LeavesShortNamesAlone()
    {
        Assert.Equal("note.txt", DisplayText.MiddleEllipsis("note.txt", 20));
        Assert.Equal("note.txt", DisplayText.MiddleEllipsis("note.txt", 8));
    }

    [Fact]
    public void MiddleEllipsis_HandlesDotfilesAndNamesWithoutExtension()
    {
        var dotfile = DisplayText.MiddleEllipsis(".configurazione_molto_lunga", 12);
        var plain = DisplayText.MiddleEllipsis("cartella_senza_estensione_lunga", 12);

        Assert.Equal(12, dotfile.Length);
        Assert.StartsWith(".config", dotfile);
        Assert.Contains("…", dotfile);
        Assert.Equal(12, plain.Length);
        Assert.Contains("…", plain);
        Assert.EndsWith("ga", plain);
    }

    [Fact]
    public void MiddleEllipsis_DoesNotKeepAnExtensionLongerThanHalfTheRoom()
    {
        var shortened = DisplayText.MiddleEllipsis("documento.estensionelunghissima", 10);

        Assert.True(shortened.Length <= 10);
        Assert.Contains("…", shortened);
    }

    [Fact]
    public void FormatSize_UsesBinaryUnits()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
        try
        {
            Assert.Equal("0 B", DisplayText.FormatSize(0));
            Assert.Equal("512 B", DisplayText.FormatSize(512));
            Assert.Equal("1,0 KB", DisplayText.FormatSize(1024));
            Assert.Equal("4,5 MB", DisplayText.FormatSize((long)(4.5 * 1024 * 1024)));
            Assert.Equal("12,4 MB", DisplayText.FormatSize((long)(12.4 * 1024 * 1024)));
            Assert.Equal("120 MB", DisplayText.FormatSize(120L * 1024 * 1024));
            Assert.Equal("3,0 GB", DisplayText.FormatSize(3L * 1024 * 1024 * 1024));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Andrea\r\nConsenti", "Andrea Consenti")]
    [InlineData("PC‮gnp.exe", "PCgnp.exe")]
    [InlineData("ㅤㅤ", "(senza nome)")]
    [InlineData("Luㅤca", "Lu ca")]
    [InlineData("⠀", "(senza nome)")]
    [InlineData("  doppi    spazi  ", "doppi spazi")]
    public void Sanitize_RemovesLineBreaksControlsBidiAndBlankLookingLetters(string input, string expected)
    {
        Assert.Equal(expected, DisplayText.Sanitize(input, 64));
    }

    [Fact]
    public void Sanitize_KeepsAccents_ButLimitsStackedCombiningMarks()
    {
        Assert.Equal("caffé", DisplayText.Sanitize("caffé", 64));
        var zalgo = "a" + new string('̶', 50) + "b";

        Assert.Equal("a̶̶b", DisplayText.Sanitize(zalgo, 64));
    }

    [Fact]
    public void Sanitize_CutsAtTheLimit_WithoutSplittingASurrogatePair()
    {
        Assert.Equal("ab", DisplayText.Sanitize("ab\U0001F600", 3));
        Assert.Equal("fallback", DisplayText.Sanitize(null, 10, "fallback"));
    }
}
