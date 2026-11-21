using System;
using System.Globalization;
using Avalonia.Data.Converters;

using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>Display formatting shared by the view models.</summary>
public static class Format
{
    public static string Money(decimal value) => value.ToString("C0", CultureInfo.CurrentCulture);

    public static string SignedMoney(decimal value) => value < 0 ? $"−{Money(-value)}" : $"+{Money(value)}";

    /// <summary>"03 Jan 2027" in English, "3. Jan. 2027" in German.</summary>
    public static string Date(DateOnly date) =>
        date.ToString(Loc.Instance.Language == "de" ? "d. MMM yyyy" : "dd MMM yyyy", CultureInfo.CurrentCulture);

    public static string Month(DateOnly date) => date.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Faded rows for things that have ended, e.g. a cancelled insurance policy.</summary>
    public static FuncValueConverter<bool, double> EndedOpacityConverter { get; } = new(ended => ended ? 0.55 : 1);

    /// <summary>First letter of a name, for avatar circles.</summary>
    public static FuncValueConverter<string?, string> InitialConverter { get; } =
        new(name => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpper(CultureInfo.CurrentCulture));
}
