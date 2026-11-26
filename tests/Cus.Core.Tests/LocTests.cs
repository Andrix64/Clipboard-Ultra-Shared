using System.Globalization;
using System.Text.RegularExpressions;
using Cus.Core.Localization;
using Cus.Core.Net;

namespace Cus.Core.Tests;

/// <summary>Non cambia <see cref="Loc.Current"/>: è globale e gli altri test, in parallelo, si aspettano l'italiano.</summary>
public partial class LocTests
{
    [Fact]
    public void Available_ListsItalianAndEnglish_WithTheirOwnNames()
    {
        Assert.Contains(new LanguageInfo("it", "Italiano"), Loc.Available);
        Assert.Contains(new LanguageInfo("en", "English"), Loc.Available);
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData(" IT ", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("en", "en")]
    [InlineData("EN-us", "en")]
    public void Parse_RecognizesCodesAndCultures(string value, string expected) =>
        Assert.Equal(expected, Loc.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xx")]
    [InlineData("item")]
    public void Parse_ReturnsNull_ForUnknownValues(string? value) => Assert.Null(Loc.Parse(value));

    [Fact]
    public void Get_FillsPlaceholders_AndFallsBackToTheKey()
    {
        Assert.Equal("Profile: x", Loc.Get("en", "startup.profile", "x"));
        Assert.Equal("Profilo: x", Loc.Get("it", "startup.profile", "x"));
        Assert.Equal("no.such.key", Loc.Get("it", "no.such.key"));
    }

    [Fact]
    public void Get_FormatsNumbersInTheLanguageOfTheText_NotTheSystemCulture()
    {
        var system = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Contains("1.234.567", Loc.Get("it", "content.imageBytes", 1234567));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            Assert.Contains("1,234,567", Loc.Get("en", "content.imageBytes", 1234567));
        }
        finally
        {
            CultureInfo.CurrentCulture = system;
        }
    }

    [Fact]
    public void DefaultLanguage_PrefersTheInstallerChoice_OverWindows()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, Loc.InstallerFileName), "[Setup]\r\nLanguage=en\r\n");

        Assert.Equal("en", Loc.DefaultLanguage(dir.Path, CultureInfo.GetCultureInfo("it-IT")));
    }

    [Theory]
    [InlineData("it-IT", "it")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", Loc.FallbackCode)]
    public void DefaultLanguage_FallsBackToWindows_WithoutInstallerFile(string culture, string expected)
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

    /// <summary>Ogni file di lingua ha tutte le chiavi della lingua di riserva, e niente di più.</summary>
    [Fact]
    public void EveryLanguage_HasTheSameKeys()
    {
        var reference = Loc.Keys(Loc.FallbackCode).ToHashSet();
        foreach (var language in Loc.Available)
        {
            var keys = Loc.Keys(language.Code).ToHashSet();
            Assert.True(reference.SetEquals(keys),
                $"{language.Code}.json: mancano [{string.Join(", ", reference.Except(keys))}], in più [{string.Join(", ", keys.Except(reference))}]");
        }
    }

    /// <summary>Stessi segnaposto in tutte le lingue: uno mancante perde un dato, uno in più fa saltare string.Format.</summary>
    [Fact]
    public void EveryTranslation_UsesTheSamePlaceholders_AndIsAValidFormat()
    {
        foreach (var key in Loc.Keys(Loc.FallbackCode))
        {
            var expected = Placeholders(Loc.Template(Loc.FallbackCode, key)!);
            foreach (var language in Loc.Available)
            {
                var template = Loc.Template(language.Code, key)!;
                Assert.True(expected.SetEquals(Placeholders(template)), $"{language.Code}.json, '{key}': segnaposto diversi da {Loc.FallbackCode}.json");
                if (expected.Count > 0)
                    _ = string.Format(CultureInfo.InvariantCulture, template, Enumerable.Repeat<object?>("x", expected.Max() + 1).ToArray());
            }
        }
    }

    /// <summary>Ogni chiave usata nel codice esiste nei file di lingua (una chiave sbagliata comparirebbe a schermo così com'è).</summary>
    [Fact]
    public void EveryKeyUsedInTheSources_Exists()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var known = Loc.Keys(Loc.FallbackCode).ToHashSet();
        var used = files.SelectMany(f => KeyUsage().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value)).ToHashSet();

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(known));
    }

    [Fact]
    public void WireTexts_AreRecognizedInEveryLanguage()
    {
        var english = Loc.Get("en", "wire.alreadyInSession", "andrea");
        var italian = Loc.Get("it", "wire.alreadyInSession", "andrea");

        Assert.True(WireTexts.AlreadyInSession.Matches(english));
        Assert.True(WireTexts.AlreadyInSession.Matches(italian));
        // Loc.Current è l'italiano nei test: il testo inglese ricevuto si mostra in italiano.
        Assert.Equal(italian, WireTexts.Localize(english));
        Assert.Equal("testo sconosciuto", WireTexts.Localize("testo sconosciuto"));
    }

    private static HashSet<int> Placeholders(string template) =>
        [.. PlaceholderPattern().Matches(template.Replace("{{", "").Replace("}}", "")).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cus.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cus.slnx non trovato sopra la cartella dei test.");
    }

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Loc.T("chiave"...), {ui:Tr chiave}, new("chiave") dei WireText.</summary>
    [GeneratedRegex("""(?:Loc\.T\(\s*"|\{ui:Tr\s+|WireText\w*\s*=\s*new\(\s*")(?<key>[A-Za-z]\w*\.[\w.]+)""")]
    private static partial Regex KeyUsage();
}
