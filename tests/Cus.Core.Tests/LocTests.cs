using System.Globalization;
using Cus.Core.Localization;

namespace Cus.Core.Tests;

/// <summary>Non cambia <see cref="Loc.Current"/>: è globale e gli altri test, in parallelo, si aspettano l'italiano.</summary>
public class LocTests
{
    [Theory]
    [InlineData("it", AppLanguage.Italian)]
    [InlineData("italian", AppLanguage.Italian)]
    [InlineData(" Italiano ", AppLanguage.Italian)]
    [InlineData("it-IT", AppLanguage.Italian)]
    [InlineData("en", AppLanguage.English)]
    [InlineData("english", AppLanguage.English)]
    [InlineData("EN-us", AppLanguage.English)]
    public void Parse_RecognizesCodesAndInnoSetupNames(string value, AppLanguage expected) =>
        Assert.Equal(expected, Loc.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("item")]
    public void Parse_ReturnsNull_ForUnknownValues(string? value) => Assert.Null(Loc.Parse(value));

    [Fact]
    public void ToCode_RoundTripsThroughParse()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
            Assert.Equal(language, Loc.Parse(Loc.ToCode(language)));
    }

    [Fact]
    public void DefaultLanguage_PrefersTheInstallerChoice_OverWindows()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, Loc.InstallerFileName), "[Setup]\r\nLanguage=english\r\n");

        Assert.Equal(AppLanguage.English, Loc.DefaultLanguage(dir.Path, CultureInfo.GetCultureInfo("it-IT")));
    }

    [Theory]
    [InlineData("it-IT", AppLanguage.Italian)]
    [InlineData("de-DE", AppLanguage.English)]
    public void DefaultLanguage_FallsBackToWindows_WithoutInstallerFile(string culture, AppLanguage expected)
    {
        using var dir = new TempDirectory();

        Assert.Equal(expected, Loc.DefaultLanguage(dir.Path, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void ReadInstallerLanguage_IgnoresUnknownValues()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, Loc.InstallerFileName), "[Setup]\nLanguage=klingon\n");

        Assert.Null(Loc.ReadInstallerLanguage(dir.Path));
    }
}
