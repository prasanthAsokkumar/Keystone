using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>One person's line in Health costs: premiums, cost-sharing bills, and how much more can still come.</summary>
public sealed record HealthCostRow(HealthCostLine Line, string PersonName, bool IsPastYear)
{
    public string PremiumsText => Format.Money(Line.PremiumsYear);

    /// <summary>"CHF 3'300 paid so far", while the year runs.</summary>
    public string PremiumsDetail => !IsPastYear && Line.PremiumsPaid != Line.PremiumsYear
        ? Loc.F("{0} paid so far", Format.Money(Line.PremiumsPaid))
        : "";

    public string CostSharingText => Format.Money(Line.CostSharing);

    public string CostSharingDetail => Line.CostSharingOpen > 0 ? Loc.F("{0} still to pay", Format.Money(Line.CostSharingOpen)) : "";

    public bool HasCostSharingOpen => Line.CostSharingOpen > 0;

    public string TotalText => Format.Money(Line.Total);

    /// <summary>"CHF 1'780 of max. CHF 3'200", or "Max. reached" once the deductible and 10% share are used up.</summary>
    public string MaxText => Line.MaxCostSharing is not { } max
        ? ""
        : Line.CostSharing >= max
            ? Loc.T("Max. reached: no more cost sharing this year")
            : Loc.F("{0} of max. {1}", Format.Money(Line.CostSharing), Format.Money(max));

    public bool HasMax => MaxText != "";

    /// <summary>How much of the yearly maximum is used, 0 to 1, for the bar.</summary>
    public double MaxUsed => Line.MaxCostSharing is > 0 and var max ? (double)Math.Min(1m, Line.CostSharing / max) : 0;
}

/// <summary>
/// The Insurance page's "Health costs" section: per calendar year and person, the premiums for health insurance and
/// the cost-sharing bills (Leistungsabrechnungen) the insurer sent, with the year's maximum when the deductible is known.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthYearText), nameof(CanShowNextHealthYear))]
    public partial int HealthYear { get; set; } = DateTime.Today.Year;

    partial void OnHealthYearChanged(int value) => RefreshHealthCosts();

    public string HealthYearText => HealthYear.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool CanShowNextHealthYear => HealthYear < Today.Year + 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHealthCostRows))]
    public partial IReadOnlyList<HealthCostRow> HealthCostRows { get; set; } = [];

    public bool HasHealthCostRows => HealthCostRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHealthStatements), nameof(HealthStatementsText))]
    public partial IReadOnlyList<DocumentRow> HealthStatementRows { get; set; } = [];

    public bool HasHealthStatements => HealthStatementRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthStatementsText))]
    public partial bool ShowHealthStatements { get; set; }

    public string HealthStatementsText => ShowHealthStatements
        ? Loc.T("Hide the cost-sharing bills")
        : Loc.Plural(HealthStatementRows.Count, "Show {0} cost-sharing bill", "Show {0} cost-sharing bills");

    /// <summary>"CHF 6'420 in all · CHF 4'944 premiums · CHF 1'476 cost sharing".</summary>
    [ObservableProperty]
    public partial string HealthSummary { get; set; } = "";

    [RelayCommand]
    private void PreviousHealthYear() => HealthYear--;

    [RelayCommand]
    private void NextHealthYear()
    {
        if (CanShowNextHealthYear)
            HealthYear++;
    }

    [RelayCommand]
    private void ToggleHealthStatements() => ShowHealthStatements = !ShowHealthStatements;

    private void RefreshHealthCosts()
    {
        var filter = InsurancePersonFilter ?? PersonOption.Everyone;
        var lines = HealthCosts.ForYear(HealthYear, Today, Transactions, Documents, _policies, People.ToList())
            .Where(l => filter.IsEveryone || l.PersonId == filter.Id)
            .ToList();
        HealthCostRows = lines
            .Select(l => new HealthCostRow(l, l.PersonId is null ? Loc.T(PersonOption.SharedName) : PersonName(l.PersonId), HealthYear < Today.Year))
            .ToList();

        var premiums = lines.Sum(l => l.PremiumsYear);
        var sharing = lines.Sum(l => l.CostSharing);
        HealthSummary = lines.Count == 0
            ? Loc.T("No health insurance premiums or cost-sharing bills this year.")
            : Loc.F("{0} in all · {1} premiums · {2} cost sharing", Format.Money(premiums + sharing), Format.Money(premiums), Format.Money(sharing));

        HealthStatementRows = HealthCosts.Statements(HealthYear, Documents, _policies)
            .Where(d => filter.IsEveryone || d.PersonId == filter.Id)
            .Select(d => new DocumentRow(d, d.PersonId is null ? Loc.T(PersonOption.SharedName) : PersonName(d.PersonId), _documentStore.Exists(d), Today))
            .ToList();
    }
}
