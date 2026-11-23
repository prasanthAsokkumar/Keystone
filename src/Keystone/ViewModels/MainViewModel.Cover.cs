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

/// <summary>One kind of insurance in the cover overview: whether the household has it, and with whom.</summary>
public sealed record CoverRow(CoverItem Item, string Status, bool IsCovered, bool IsPartly, bool IsHidden)
{
    public InsuranceKind Kind => Item.Kind;

    public string Name => InsurancePolicy.KindName(Item.Kind);

    public string NeedText => Loc.T(Item.NeedText);

    public string Hint => Loc.T(Item.Hint);

    public bool IsRequired => Item.Need == CoverNeed.Required;

    public bool IsRecommended => Item.Need == CoverNeed.Recommended;

    /// <summary>Required or recommended, and missing (for someone): shown in orange.</summary>
    public bool IsGap => (!IsCovered || IsPartly) && Item.Need is CoverNeed.Required or CoverNeed.Recommended;

    public bool CanAdd => !IsCovered || IsPartly;
}

/// <summary>
/// The Insurance page's overview of the insurances common in Switzerland: which ones the household has, which
/// are missing, and which don't apply (hidden by the owner, e.g. a car insurance without a car).
/// </summary>
public partial class MainViewModel
{
    private HashSet<InsuranceKind> _hiddenCover = [];

    [ObservableProperty]
    public partial IReadOnlyList<CoverRow> CoverRows { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenCover))]
    public partial IReadOnlyList<CoverRow> HiddenCoverRows { get; set; } = [];

    public bool HasHiddenCover => HiddenCoverRows.Count > 0;

    [ObservableProperty]
    public partial bool ShowHiddenCover { get; set; }

    [RelayCommand]
    private void ToggleHiddenCover() => ShowHiddenCover = !ShowHiddenCover;

    /// <summary>"7 of 9 covered · 1 recommended missing".</summary>
    [ObservableProperty]
    public partial string CoverSummary { get; set; } = "";

    public string HiddenCoverText => Loc.F("Doesn’t apply: {0}", string.Join(", ", HiddenCoverRows.Select(r => r.Name)));

    private void LoadHiddenCover(string kinds) =>
        _hiddenCover = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse<InsuranceKind>(k, out var kind) ? kind : (InsuranceKind?)null)
            .OfType<InsuranceKind>()
            .ToHashSet();

    private void RefreshCover()
    {
        var filter = InsurancePersonFilter ?? PersonOption.Everyone;
        // A household policy covers everyone; a person's own covers them.
        var running = _policies.Where(p => !p.HasEnded(Today) && (filter.IsEveryone || p.PersonId is null || p.PersonId == filter.Id)).ToList();
        var people = filter.IsEveryone ? People.ToList() : People.Where(p => p.Id == filter.Id).ToList();

        var rows = SwissCover.Items.Select(item =>
        {
            var policies = running.Where(p => item.CountsAs.Contains(p.Kind)).ToList();
            var hidden = _hiddenCover.Contains(item.Kind);
            if (item.PerPerson && people.Count > 0 && policies.All(p => p.PersonId is not null))
            {
                var missing = people.Where(person => policies.All(p => p.PersonId != person.Id)).Select(p => p.Name).ToList();
                if (missing.Count > 0 && policies.Count > 0)
                    return new CoverRow(item, Loc.F("{0} · missing for {1}", Insurers(policies), string.Join(", ", missing)), true, true, hidden);
            }
            return policies.Count == 0
                ? new CoverRow(item, Loc.T("Not added yet"), false, false, hidden)
                : new CoverRow(item, $"{Insurers(policies)} · {Loc.F("{0} a year", Format.Money(policies.Sum(p => p.YearlyPremium)))}", true, false, hidden);
        }).ToList();

        CoverRows = rows.Where(r => !r.IsHidden).ToList();
        HiddenCoverRows = rows.Where(r => r.IsHidden).ToList();
        OnPropertyChanged(nameof(HiddenCoverText));
        var shown = CoverRows;
        var gaps = shown.Count(r => r.IsGap);
        CoverSummary = Loc.F("{0} of {1} covered", shown.Count(r => r.IsCovered && !r.IsPartly), shown.Count) +
                       (gaps > 0 ? " · " + Loc.Plural(gaps, "{0} gap to check", "{0} gaps to check") : "");
    }

    private static string Insurers(IEnumerable<InsurancePolicy> policies) =>
        string.Join(", ", policies.Select(p => p.Insurer).Where(i => i != "").Distinct(StringComparer.CurrentCultureIgnoreCase));

    /// <summary>Opens the insurance form for this kind, e.g. from "Car and motorbike: Not added yet".</summary>
    [RelayCommand]
    private void AddCover(CoverRow row)
    {
        NewInsurance();
        PolicyKindIndex = (int)row.Kind;
    }

    /// <summary>"Doesn’t apply", e.g. building insurance when renting; it moves to the hidden line.</summary>
    [RelayCommand]
    private Task HideCoverAsync(CoverRow row) => SetCoverHiddenAsync(row.Kind, true);

    [RelayCommand]
    private Task UnhideCoverAsync(CoverRow row) => SetCoverHiddenAsync(row.Kind, false);

    private async Task SetCoverHiddenAsync(InsuranceKind kind, bool hidden)
    {
        if (hidden)
            _hiddenCover.Add(kind);
        else
            _hiddenCover.Remove(kind);
        RefreshCover();
        try
        {
            await repository.SetHiddenCoverAsync(string.Join(",", _hiddenCover.Order()));
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }
}
