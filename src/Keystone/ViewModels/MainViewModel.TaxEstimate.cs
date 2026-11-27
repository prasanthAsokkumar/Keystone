using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// A rough estimate of the tax for a year, and whether the instalments paid are enough.
/// <para>
/// Swiss tax depends on the canton, the commune, the church and many deductions Keystone doesn't know, so it
/// doesn't use tariffs. It learns the household's rate instead: the total tax of a past year (from its assessment or
/// final bill) divided by what Keystone counted for that year, income minus the deductions it knows. The same rate
/// on this year's figures gives the estimate; since both years are counted the same way, what Keystone leaves out
/// mostly cancels. Without an assessment, a rate entered by hand is used. A guide, not advice.
/// </para>
/// </summary>
public partial class MainViewModel
{
    private Dictionary<int, decimal> _taxAssessments = [];
    private decimal? _taxRate;
    private bool _loadingTaxInputs;

    private void LoadTaxSettings(string assessments, decimal? rate)
    {
        try
        {
            _taxAssessments = assessments == ""
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, decimal>>(assessments)!
                    .Where(p => int.TryParse(p.Key, out _))
                    .ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);
        }
        catch (JsonException)
        {
            _taxAssessments = [];
        }
        _taxRate = rate;
    }

    /// <summary>What Keystone counts for a year: income (not borrowing), and the deductions it knows of.</summary>
    internal (decimal Income, decimal Deductions) TaxBaseFor(int year)
    {
        var start = new DateOnly(year, 1, 1);
        var occurrences = Schedule.Expand(_entries.Where(t => !t.IsTransfer), start, new DateOnly(year, 12, 31));
        decimal Spent(string category) => occurrences.Where(o => o.Source.Type == TransactionType.Expense && o.Source.Category == category).Sum(o => o.Amount);

        var income = occurrences.Where(o => o.Source.IsIncome && !o.Source.IsBorrowing).Sum(o => o.Amount);
        var pillar3a = Retirement.Pillar3aRowsFor(PersonOption.Everyone, year).Sum(r => Math.Min(r.Paid, r.Limit));
        var medical = Math.Max(0, Spent(Categories.MedicalCosts) - income * 0.05m); // only above 5% of income
        var interest = DebtPlans.Sum(p => p.Entries.Where(e => e.Date.Year == year).Sum(e => e.Interest));
        var deductions = pillar3a + Spent(Categories.PensionBuyIn) + Spent(Categories.Childcare) + Spent(Categories.Donations) + medical + interest;
        return (income, deductions);
    }

    private decimal TaxableFor(int year) => TaxBaseFor(year) is var b ? Math.Max(0, b.Income - b.Deductions) : 0;

    /// <summary>The year whose assessment gives the rate: the latest one before the tax year that Keystone has figures for.</summary>
    private int? RateYear => _taxAssessments.Keys.Where(y => y < TaxYear && TaxableFor(y) > 0).OrderDescending().Cast<int?>().FirstOrDefault();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaxEstimate))]
    public partial string TaxEstimateText { get; set; } = "";

    public bool HasTaxEstimate => TaxEstimateText != "";

    /// <summary>"14.2% from your 2025 assessment, on CHF 131'000 income after deductions".</summary>
    [ObservableProperty]
    public partial string TaxEstimateDetail { get; set; } = "";

    [ObservableProperty]
    public partial string TaxPaidText { get; set; } = "";

    /// <summary>"About CHF 5'100 more to pay" or "About CHF 800 back".</summary>
    [ObservableProperty]
    public partial string TaxBalanceText { get; set; } = "";

    [ObservableProperty]
    public partial bool TaxBalanceIsOwed { get; set; }

    public bool HasTaxBalance => TaxBalanceText != "";

    partial void OnTaxBalanceTextChanged(string value) => OnPropertyChanged(nameof(HasTaxBalance));

    /// <summary>The year whose assessment can be entered: the one before the tax year shown.</summary>
    public int TaxReferenceYear => TaxYear - 1;

    public string TaxReferenceLabel => Loc.F("Total tax for {0}", TaxReferenceYear);

    /// <summary>The total tax of <see cref="TaxReferenceYear"/>, from its assessment or final bill.</summary>
    [ObservableProperty]
    public partial decimal? TaxReferenceAmount { get; set; }

    /// <summary>A rate by hand, in percent, for when there is no assessment yet.</summary>
    [ObservableProperty]
    public partial decimal? TaxRateInput { get; set; }

    partial void OnTaxReferenceAmountChanged(decimal? value)
    {
        if (_loadingTaxInputs)
            return;
        if (value is > 0)
            _taxAssessments[TaxReferenceYear] = value.Value;
        else
            _taxAssessments.Remove(TaxReferenceYear);
        SaveTaxSettings();
    }

    partial void OnTaxRateInputChanged(decimal? value)
    {
        if (_loadingTaxInputs)
            return;
        _taxRate = value is > 0 ? value : null;
        SaveTaxSettings();
    }

    private async void SaveTaxSettings()
    {
        RefreshTaxEstimate();
        try
        {
            await repository.SetTaxAssessmentsAsync(JsonSerializer.Serialize(
                _taxAssessments.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value)));
            await repository.SetTaxRateAsync(_taxRate);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    private void RefreshTaxEstimate()
    {
        _loadingTaxInputs = true;
        TaxReferenceAmount = _taxAssessments.TryGetValue(TaxReferenceYear, out var reference) ? reference : null;
        TaxRateInput = _taxRate;
        _loadingTaxInputs = false;
        OnPropertyChanged(nameof(TaxReferenceLabel));

        var taxable = TaxableFor(TaxYear);
        var yearEnd = new DateOnly(TaxYear, 12, 31);
        var payments = Schedule.Expand(_entries.Where(t => t.Type == TransactionType.Expense && Categories.IsTaxes(t.Category)),
            new DateOnly(TaxYear, 1, 1), yearEnd);
        var paid = payments.Sum(o => o.Amount);
        var paidSoFar = payments.Where(o => o.Date <= Today).Sum(o => o.Amount);
        TaxPaidText = paid == 0
            ? Loc.F("No tax payments recorded for {0}", TaxYear)
            : paid == paidSoFar
                ? Loc.F("{0} paid in instalments in {1}", Format.Money(paid), TaxYear)
                : Loc.F("{0} in instalments in {1}, {2} of it paid so far", Format.Money(paid), TaxYear, Format.Money(paidSoFar));

        decimal? estimate;
        if (_taxAssessments.TryGetValue(TaxYear, out var assessed))
        {
            // The assessment for this very year has come: no need to estimate.
            estimate = assessed;
            TaxEstimateDetail = Loc.F("From your assessment for {0}", TaxYear);
        }
        else if (RateYear is { } year)
        {
            var rate = _taxAssessments[year] / TaxableFor(year);
            estimate = Math.Round(rate * taxable, 0);
            TaxEstimateDetail = Loc.F("{0} of {1} income after deductions, the rate of your {2} tax",
                rate.ToString("P1", CultureInfo.CurrentCulture), Format.Money(taxable), year);
        }
        else if (_taxRate is { } percent)
        {
            estimate = Math.Round(percent / 100 * taxable, 0);
            TaxEstimateDetail = Loc.F("{0}% of {1} income after deductions, the rate you entered",
                percent.ToString("0.#", CultureInfo.CurrentCulture), Format.Money(taxable));
        }
        else
        {
            estimate = null;
            // A tax entered for a year Keystone has no income for can't give a rate.
            TaxEstimateDetail = _taxAssessments.Keys.Any(y => y < TaxYear)
                ? Loc.F("Keystone has no income recorded for {0} to work out your rate from. Enter a rate by hand instead.",
                    _taxAssessments.Keys.Where(y => y < TaxYear).Max())
                : Loc.T("Enter last year’s total tax, or a rate, to see an estimate.");
        }

        TaxEstimateText = estimate is { } e && taxable > 0 ? $"≈ {Format.Money(e)}" : "";
        if (estimate is not { } total || taxable <= 0)
        {
            TaxBalanceText = "";
            return;
        }
        var balance = total - paid;
        TaxBalanceIsOwed = balance > 0;
        TaxBalanceText = Math.Abs(balance) < Math.Max(200, total * 0.03m)
            ? Loc.T("Your instalments are about right")
            : balance > 0
                ? Loc.F("About {0} more to pay", Format.Money(balance))
                : Loc.F("About {0} back", Format.Money(-balance));
    }
}
