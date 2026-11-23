using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum AssetKind { Securities, Crypto, Property, Vehicle, Valuables, LoanGiven, Other }

/// <summary>
/// Something the household owns that isn't cash in an account or retirement savings: a securities account, crypto,
/// a flat, a car. Its worth is kept as a list of values over time, entered when a statement or estimate comes.
/// </summary>
public class Asset
{
    public int Id { get; set; }

    /// <summary>Where the row was put by dragging; 0 for rows never moved by hand.</summary>
    public int SortOrder { get; set; }

    public string Name { get; set; } = "";

    public AssetKind Kind { get; set; }

    /// <summary>The bank, broker or exchange, e.g. "PostFinance", "Swissquote"; empty for a car or a painting.</summary>
    public string Institution { get; set; } = "";

    /// <summary>The household member it belongs to; null for the whole household.</summary>
    public int? PersonId { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>Sold or given away on this day; it counts until then.</summary>
    public DateOnly? SoldOn { get; set; }

    public List<AssetValue> Values { get; set; } = [];

    /// <summary>The value on a day: the latest one entered on or before it, and nothing once sold.</summary>
    public decimal ValueOn(DateOnly date) =>
        SoldOn is { } sold && sold <= date ? 0 : Values.Where(v => v.Date <= date).OrderBy(v => v.Date).ThenBy(v => v.Id).LastOrDefault()?.Value ?? 0;

    public AssetValue? Latest => Values.OrderBy(v => v.Date).ThenBy(v => v.Id).LastOrDefault();

    public static string KindName(AssetKind kind) => Loc.T(kind switch
    {
        AssetKind.Securities => "Securities account",
        AssetKind.Crypto => "Crypto",
        AssetKind.Property => "Property",
        AssetKind.Vehicle => "Vehicle",
        AssetKind.Valuables => "Valuables",
        AssetKind.LoanGiven => "Money lent",
        _ => "Other",
    });
}

/// <summary>What an asset was worth on a day, e.g. from a depot statement or the car's resale value.</summary>
public class AssetValue
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public DateOnly Date { get; set; }

    public decimal Value { get; set; }

    public string Note { get; set; } = "";
}
