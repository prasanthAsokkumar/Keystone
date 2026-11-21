using System.Net;
using System.Text.RegularExpressions;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.Tests;

/// <summary>Language is global, so these tests don't run alongside others that format text.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class LocalizationTests : IDisposable
{
    private static readonly string SourceFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Keystone"));

    public void Dispose() => Loc.Instance.SetLanguage("en");

    /// <summary>Every text the views and code look up has a German translation.</summary>
    [Fact]
    public void EveryTextHasAGermanTranslation()
    {
        const string literal = "\"((?:[^\"\\\\]|\\\\.)*)\"";
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SourceFolder, "*.axaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{l:T '([^']*)'\}"))
                keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value));

        foreach (var file in Directory.EnumerateFiles(SourceFolder, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("Migrations") && !f.EndsWith("German.cs")))
        {
            var code = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(code, @"Loc\.(?:T|F)\(\s*" + literal))
                keys.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(code, @"Loc\.Plural\([^,]+,\s*" + literal + @",\s*" + literal, RegexOptions.Singleline))
                keys.UnionWith([Regex.Unescape(m.Groups[1].Value), Regex.Unescape(m.Groups[2].Value)]);
            foreach (Match m in Regex.Matches(code, @"Translate\(([^)]*)\)"))
                foreach (Match k in Regex.Matches(m.Groups[1].Value, literal))
                    keys.Add(Regex.Unescape(k.Groups[1].Value));
        }

        Assert.True(keys.Count > 200, $"Only found {keys.Count} texts; is the source folder right? {SourceFolder}");
        Assert.Empty(keys.Where(k => !German.Texts.ContainsKey(k)).Order());
    }

    [Fact]
    public void GermanTranslationsKeepTheirPlaceholders()
    {
        foreach (var (english, german) in German.Texts)
        {
            var placeholders = Regex.Matches(english, @"\{\d").Select(m => m.Value).Distinct().Order();
            Assert.Equal(placeholders, Regex.Matches(german, @"\{\d").Select(m => m.Value).Distinct().Order());
        }
    }

    [Fact]
    public void SwitchingLanguage_ChangesTextsAndFormats()
    {
        Loc.Instance.SetLanguage("de");
        Assert.Equal("Übersicht", Loc.T("Overview"));
        Assert.Equal("Das Geld reicht bis 03.01.2027", Loc.F("Cash runs out on {0}", new DateOnly(2027, 1, 3).ToString("d")));
        Assert.Equal("Jährlich im März", new Transaction { Date = new DateOnly(2026, 3, 1), Recurrence = Recurrence.Yearly }.RecurrenceName);

        Loc.Instance.SetLanguage("en");
        Assert.Equal("Overview", Loc.T("Overview"));
        Assert.Equal("Yearly in March", new Transaction { Date = new DateOnly(2026, 3, 1), Recurrence = Recurrence.Yearly }.RecurrenceName);
    }

    [Fact]
    public void UnknownText_FallsBackToEnglish()
    {
        Loc.Instance.SetLanguage("de");
        Assert.Equal("Not translated yet", Loc.T("Not translated yet"));
    }

    [Fact]
    public void Categories_AreShownTranslatedButStoredInEnglish()
    {
        Loc.Instance.SetLanguage("de");
        Assert.Equal("Miete", Categories.Display("Rent"));
        Assert.Equal("Rent", Categories.Normalize("Miete"));
        Assert.Equal("Rent", Categories.Normalize(" rent "));
        Assert.Equal("Hobbies", Categories.Normalize("Hobbies"));
        Assert.Equal("Hobbies", Categories.Display("Hobbies"));
    }
}

[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection;
