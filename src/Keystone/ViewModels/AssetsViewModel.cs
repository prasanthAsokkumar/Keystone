using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>One asset on the Assets page: what it is, whose, what it is worth and how that changed.</summary>
public sealed record AssetRow(Asset Asset, string PersonName, DateOnly Today)
{
    public string Name => Asset.Name;

    public string DetailText => string.Join(" · ", new[] { Asset.KindName(Asset.Kind), Asset.Institution }.Where(s => s != ""));

    public bool IsShared => Asset.PersonId is null;

    public bool IsSold => Asset.SoldOn is { } sold && sold <= Today;

    public decimal Value => Asset.ValueOn(Today);

    public string ValueText => IsSold ? Loc.F("Sold {0}", Format.Date(Asset.SoldOn!.Value)) : Format.Money(Value);

    /// <summary>"as of 31 Oct 2026", or a nudge when the last value is over a year old.</summary>
    public string AsOfText => Asset.Latest is not { } latest
        ? Loc.T("No value yet")
        : IsStale ? Loc.F("Value from {0}: time to update it", Format.Date(latest.Date)) : Loc.F("as of {0}", Format.Date(latest.Date));

    public bool IsStale => !IsSold && (Asset.Latest is null || Asset.Latest.Date < Today.AddYears(-1));

    private (AssetValue Last, AssetValue Before)? LastTwo =>
        Asset.Values.OrderBy(v => v.Date).ThenBy(v => v.Id).TakeLast(2).ToList() is [var before, var last] ? (last, before) : null;

    /// <summary>"+CHF 1'200 since 30 Sep 2026" against the value before.</summary>
    public string ChangeText => LastTwo is { } two && !IsSold
        ? Loc.F("{0} since {1}", Format.SignedMoney(two.Last.Value - two.Before.Value), Format.Date(two.Before.Date))
        : "";

    public bool HasChange => ChangeText != "";

    public bool IsUp => LastTwo is { } two && two.Last.Value > two.Before.Value;

    public bool IsDown => LastTwo is { } two && two.Last.Value < two.Before.Value;
}

/// <summary>One value in an asset's history, in its form.</summary>
public sealed record AssetValueRow(AssetValue Entry, string ChangeText)
{
    public string DateText => Format.Date(Entry.Date);

    public string ValueText => Format.Money(Entry.Value);

    public string Note => Entry.Note;

    public bool HasNote => Entry.Note != "";
}

/// <summary>
/// The Assets page: investments, crypto, property, vehicles and other things of value, each with its value over
/// time. They count in net worth (Planning) and in the Taxes page's assets on 31 December.
/// </summary>
/// <remarks>Its own view model; shared things (people, notices, net worth) come from the main one.</remarks>
public sealed partial class AssetsViewModel(MainViewModel main) : ViewModelBase
{
    /// <summary>Every asset, sold ones too.</summary>
    internal List<Asset> All { get; } = [];

    public IReadOnlyList<string> AssetKindNames => Enum.GetValues<AssetKind>().Select(Asset.KindName).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssetRows))]
    public partial IReadOnlyList<AssetRow> AssetRows { get; set; } = [];

    public bool HasAssetRows => AssetRows.Count > 0;

    [ObservableProperty]
    public partial string AssetsTotalText { get; set; } = "";

    [ObservableProperty]
    public partial string AssetsChangeText { get; set; } = "";

    [ObservableProperty]
    public partial bool AssetsAreDown { get; set; }

    /// <summary>"Securities CHF 48'000 · Crypto CHF 3'200 · Vehicle CHF 14'000".</summary>
    [ObservableProperty]
    public partial string AssetsByKindText { get; set; } = "";

    [ObservableProperty]
    public partial string NetWorthWithAssetsText { get; set; } = "";

    internal async Task LoadAssetsAsync()
    {
        All.Clear();
        All.AddRange(await main.repository.GetAssetsAsync());
        RefreshAssets();
    }

    /// <summary>What all assets were worth on a day (sold ones no longer count).</summary>
    internal decimal AssetsOn(DateOnly date) => All.Sum(a => a.ValueOn(date));

    internal void RefreshAssets()
    {
        AssetRows = main.Sorted("assets", All
                .Where(a => main.InView(a.PersonId))
                .OrderBy(a => a.SoldOn is { } sold && sold <= MainViewModel.Today)
                .ThenByDescending(a => a.ValueOn(MainViewModel.Today))
                .Select(a => new AssetRow(a, a.PersonId is null ? Loc.T(PersonOption.SharedName) : main.PersonName(a.PersonId), MainViewModel.Today)),
            r => r.Asset.SortOrder,
            new SortKey<AssetRow>("name", r => r.Name),
            new SortKey<AssetRow>("kind", r => r.DetailText),
            new SortKey<AssetRow>("person", r => r.PersonName),
            new SortKey<AssetRow>("value", r => r.Value, DescendingFirst: true));

        var now = AssetsOn(MainViewModel.Today);
        var yearAgo = AssetsOn(MainViewModel.Today.AddYears(-1));
        AssetsTotalText = Format.Money(now);
        AssetsChangeText = All.Count == 0 ? Loc.T("Add a securities account, crypto, property or a car")
            : Loc.F("{0} in the last 12 months", Format.SignedMoney(now - yearAgo));
        AssetsAreDown = now < yearAgo;
        AssetsByKindText = string.Join(" · ", All.Where(a => a.ValueOn(MainViewModel.Today) != 0)
            .GroupBy(a => a.Kind).OrderByDescending(g => g.Sum(a => a.ValueOn(MainViewModel.Today)))
            .Select(g => $"{Asset.KindName(g.Key)} {Format.Money(g.Sum(a => a.ValueOn(MainViewModel.Today)))}"));
        NetWorthWithAssetsText = Format.Money(main.Planning.NetWorthOn(MainViewModel.Today));
    }

    /// <summary>After an asset changed: the page, net worth and the tax year's assets.</summary>
    private void AssetsChanged()
    {
        RefreshAssets();
        main.Planning.RefreshNetWorth();
        main.RefreshTaxes();
    }

    // ── The form ────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsAssetPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssetFormTitle), nameof(IsEditingAsset))]
    public partial Asset? EditingAsset { get; set; }

    public string AssetFormTitle => Loc.T(EditingAsset is null ? "Add asset" : "Edit asset");

    public bool IsEditingAsset => EditingAsset is not null;

    [ObservableProperty]
    public partial string AssetName { get; set; } = "";

    [ObservableProperty]
    public partial int AssetKindIndex { get; set; }

    [ObservableProperty]
    public partial string AssetInstitution { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption AssetPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial string AssetNotes { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? AssetSoldOn { get; set; }

    /// <summary>A new value to record, e.g. from today's depot statement.</summary>
    [ObservableProperty]
    public partial decimal? AssetValueAmount { get; set; }

    [ObservableProperty]
    public partial DateTime? AssetValueDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string AssetValueNote { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssetValueRows))]
    public partial IReadOnlyList<AssetValueRow> AssetValueRows { get; set; } = [];

    public bool HasAssetValueRows => AssetValueRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssetError))]
    public partial string? AssetError { get; set; }

    public bool HasAssetError => !string.IsNullOrEmpty(AssetError);

    /// <summary>Property counts in the tax return at its tax value, not what it would sell for.</summary>
    public bool AssetIsProperty => AssetKindIndex == (int)AssetKind.Property;

    partial void OnAssetKindIndexChanged(int value) => OnPropertyChanged(nameof(AssetIsProperty));

    [RelayCommand]
    internal void NewAsset()
    {
        main.CloseOtherPanels();
        EditingAsset = null;
        AssetName = "";
        AssetKindIndex = 0;
        AssetInstitution = "";
        AssetPerson = PersonOption.Shared;
        AssetNotes = "";
        AssetSoldOn = null;
        AssetValueAmount = null;
        AssetValueDate = DateTime.Today;
        AssetValueNote = "";
        AssetValueRows = [];
        AssetError = null;
        IsAssetPanelOpen = true;
    }

    /// <summary>Opens an asset to change it or record its new value (the value field is ready for today's).</summary>
    [RelayCommand]
    private void EditAsset(Asset asset)
    {
        NewAsset();
        EditingAsset = asset;
        AssetName = asset.Name;
        AssetKindIndex = (int)asset.Kind;
        AssetInstitution = asset.Institution;
        AssetPerson = main.PersonOptions.FirstOrDefault(o => o.Id == asset.PersonId) ?? PersonOption.Shared;
        AssetNotes = asset.Notes;
        AssetSoldOn = asset.SoldOn?.ToDateTime(TimeOnly.MinValue);
        RefreshAssetValueRows(asset);
    }

    private void RefreshAssetValueRows(Asset asset)
    {
        var values = asset.Values.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id).ToList();
        AssetValueRows = values.Select((v, i) => new AssetValueRow(v,
            i + 1 < values.Count ? Format.SignedMoney(v.Value - values[i + 1].Value) : "")).ToList();
    }

    [RelayCommand]
    private async Task SaveAssetAsync()
    {
        AssetError = null;
        if (string.IsNullOrWhiteSpace(AssetName))
        {
            AssetError = Loc.T("Enter a name, e.g. “ETF depot” or “Car”.");
            return;
        }
        if (EditingAsset is null && AssetValueAmount is null)
        {
            AssetError = Loc.T("Enter what it is worth today.");
            return;
        }

        var isNew = EditingAsset is null;
        var asset = EditingAsset ?? new Asset();
        asset.Name = AssetName.Trim();
        asset.Kind = Enum.IsDefined((AssetKind)AssetKindIndex) ? (AssetKind)AssetKindIndex : AssetKind.Other;
        asset.Institution = AssetInstitution.Trim();
        asset.PersonId = AssetPerson.Id;
        asset.Notes = AssetNotes.Trim();
        asset.SoldOn = AssetSoldOn is { } sold ? DateOnly.FromDateTime(sold) : null;

        try
        {
            if (isNew)
            {
                asset.Values = [];
                await main.repository.SaveAsync(asset, isNew: true);
                All.Add(asset);
            }
            else
            {
                // Save the asset's own fields; its values are saved one by one below.
                var values = asset.Values;
                asset.Values = [];
                await main.repository.SaveAsync(asset, isNew: false);
                asset.Values = values;
            }

            if (AssetValueAmount is { } amount)
            {
                var date = DateOnly.FromDateTime(AssetValueDate ?? DateTime.Today);
                // One value per day: a second one on the same day replaces it.
                var sameDay = asset.Values.FirstOrDefault(v => v.Date == date);
                var entry = sameDay ?? new AssetValue { AssetId = asset.Id, Date = date };
                entry.Value = amount;
                entry.Note = AssetValueNote.Trim();
                await main.repository.SaveAsync(entry, isNew: sameDay is null);
                if (sameDay is null)
                    asset.Values.Add(entry);
            }
        }
        catch (Exception ex)
        {
            AssetError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        IsAssetPanelOpen = false;
        EditingAsset = null;
        AssetsChanged();
    }

    [RelayCommand]
    private async Task DeleteAssetValueAsync(AssetValueRow row)
    {
        if (EditingAsset is not { } asset)
            return;
        try
        {
            await main.repository.DeleteAsync<AssetValue>(v => v.Id == row.Entry.Id);
        }
        catch (Exception ex)
        {
            AssetError = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }
        asset.Values.Remove(row.Entry);
        RefreshAssetValueRows(asset);
        AssetsChanged();
    }

    [RelayCommand]
    private async Task DeleteAssetAsync(Asset asset)
    {
        try
        {
            await main.repository.DeleteAsync<Asset>(a => a.Id == asset.Id);
        }
        catch (Exception ex)
        {
            main.ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }
        All.Remove(asset);
        if (EditingAsset == asset)
            IsAssetPanelOpen = false;
        AssetsChanged();
        main.ShowNotice(Loc.F("Deleted “{0}”", asset.Name), async () =>
        {
            // Back with its values, under the same ids.
            await main.repository.SaveAsync(asset, isNew: true);
            All.Add(asset);
            AssetsChanged();
        });
    }

    internal void CloseAssetPanel()
    {
        IsAssetPanelOpen = false;
        EditingAsset = null;
    }
}
