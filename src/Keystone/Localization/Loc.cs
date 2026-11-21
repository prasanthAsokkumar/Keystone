using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Keystone.Localization;

public sealed record AppLanguage(string Code, string Name, string Culture)
{
    public override string ToString() => Name;
}

/// <summary>
/// Translates the app's texts. The English text is the key, so code and views stay readable and
/// anything without a translation simply shows in English. Changing <see cref="Language"/>
/// updates every bound text at once.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static IReadOnlyList<AppLanguage> Languages { get; } =
    [
        new("en", "English", "en-CH"),
        new("de", "Deutsch", "de-CH"),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations =
        new Dictionary<string, IReadOnlyDictionary<string, string>> { ["de"] = German.Texts };

    // Declared after the lists above: static fields initialise in order, and the constructor uses them.
    public static Loc Instance { get; } = new();

    private Loc() => Apply(SystemLanguage().Code);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, for texts built in code.</summary>
    public event EventHandler? LanguageChanged;

    public string Language { get; private set; } = "en";

    public CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    public string this[string key] => Translate(key);

    /// <summary>German on a German Mac, otherwise English.</summary>
    public static AppLanguage SystemLanguage() =>
        Languages.FirstOrDefault(l => l.Code == CultureInfo.InstalledUICulture.TwoLetterISOLanguageName) ?? Languages[0];

    public static AppLanguage Find(string? code) =>
        Languages.FirstOrDefault(l => l.Code == code) ?? SystemLanguage();

    /// <summary>Switches the language; an empty code means the Mac's language.</summary>
    public void SetLanguage(string? code)
    {
        var language = string.IsNullOrEmpty(code) ? SystemLanguage() : Find(code);
        if (language.Code == Language)
            return;

        Apply(language.Code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(string code)
    {
        Language = code;
        Culture = CultureInfo.GetCultureInfo(Find(code).Culture);
        // Dates, month names and amounts follow the app's language.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    private string Translate(string key) =>
        Translations.TryGetValue(Language, out var texts) && texts.TryGetValue(key, out var text) ? text : key;

    /// <summary>The text in the current language.</summary>
    public static string T(string key) => Instance.Translate(key);

    /// <summary>The text in the current language with {0}, {1}… filled in.</summary>
    public static string F(string key, params object?[] args) => string.Format(Instance.Culture, T(key), args);

    /// <summary>Chooses the singular or plural text by <paramref name="count"/>, which is passed as {0}.</summary>
    public static string Plural(int count, string one, string many, params object?[] more) =>
        F(count == 1 ? one : many, [count, .. more]);

    /// <summary>Every translation of a text, for recognising built-in values typed in either language.</summary>
    public static IEnumerable<string> AllTranslations(string key) =>
        Translations.Values.Select(t => t.TryGetValue(key, out var text) ? text : key).Prepend(key);
}
