using System;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace Keystone.Localization;

/// <summary>
/// A translated text in XAML that follows the language setting:
/// <c>Text="{l:T 'Cash today'}"</c>.
/// </summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    // Bound to the language rather than an indexer path, since texts may contain commas and brackets.
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Loc.Language))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<string, string>(_ => Loc.T(Key)),
        };
}
