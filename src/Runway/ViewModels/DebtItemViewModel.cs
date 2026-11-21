using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runway.Localization;
using Runway.Models;
using Runway.Services;

namespace Runway.ViewModels;

/// <summary>One debt card on the Debts tab.</summary>
public partial class DebtItemViewModel(
    Debt debt,
    Func<DebtItemViewModel, Task> recordExtraPayment,
    Func<DebtItemViewModel, Task> delete) : ViewModelBase
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public Debt Debt { get; } = debt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outstanding))]
    [NotifyPropertyChangedFor(nameof(OutstandingText))]
    [NotifyPropertyChangedFor(nameof(PaidPercent))]
    [NotifyPropertyChangedFor(nameof(PayoffText))]
    [NotifyPropertyChangedFor(nameof(InterestText))]
    [NotifyPropertyChangedFor(nameof(PaysOff))]
    [NotifyPropertyChangedFor(nameof(HasPayoffProblem))]
    public partial DebtPlan? Plan { get; set; }

    [ObservableProperty]
    public partial decimal? ExtraAmount { get; set; }

    public string Name => Debt.Name;

    public decimal Outstanding => Plan?.OutstandingOn(Today) ?? Debt.Balance;

    public bool PaysOff => Plan?.PaysOff ?? true;

    /// <summary>Payments don't cover the interest. An open-ended interest-only debt is not a problem.</summary>
    public bool HasPayoffProblem => Plan is { PaysOff: false, IsOpenEnded: false };

    public string ExtraPaymentLabel => Loc.T(Debt.IsInterestOnly ? "Repay" : "Extra payment");

    public string ExtraPaymentTitle => Loc.T(Debt.IsInterestOnly ? "Repay principal today" : "Extra payment today");

    public string OutstandingText => Format.Money(Outstanding);

    public string TermsText
    {
        get
        {
            if (Debt.IsInterestOnly)
            {
                var terms = Loc.F("{0:0.##} interest per 1,000 / month from {1}",
                    Debt.MonthlyInterestPer1000, Format.Date(Debt.FirstPaymentDate));
                return Debt.MonthlyPayment > 0
                    ? Loc.F("{0} · repaying {1} / month", terms, Format.Money(Debt.MonthlyPayment))
                    : terms;
            }

            var rate = Debt.AnnualInterestRate > 0
                ? Loc.F("{0:0.##}% interest", Debt.AnnualInterestRate)
                : Loc.T("no interest");
            return Debt.IsLumpSum
                ? Loc.F("Pay at once on {0} · {1}", Format.Date(Debt.FirstPaymentDate), rate)
                : Loc.F("{0} / month from {1} · {2}", Format.Money(Debt.MonthlyPayment), Format.Date(Debt.FirstPaymentDate), rate);
        }
    }

    /// <summary>Share of the recorded balance already repaid.</summary>
    public double PaidPercent =>
        Debt.Balance <= 0 ? 100 : Math.Clamp((double)(1 - Outstanding / Debt.Balance) * 100, 0, 100);

    public string PayoffText
    {
        get
        {
            if (Plan is null)
                return "";
            if (Plan.IsOpenEnded && Outstanding > 0)
                return Loc.F("Open until repaid · {0} interest / month at this balance", Format.Money(NextInterest));
            if (!Plan.PaysOff)
                return Loc.T("Payments don’t cover the interest. This debt never gets paid off.");
            if (Outstanding <= 0)
                return Loc.F("Paid off {0}", Format.Date(Plan.PayoffDate!.Value));

            var left = Plan.PaymentsAfter(Today);
            return Debt.IsLumpSum
                ? Loc.F("Due {0}", Format.Date(Plan.PayoffDate!.Value))
                : Loc.Plural(left, "Paid off ~{1} · {0} payment left", "Paid off ~{1} · {0} payments left",
                    Format.Month(Plan.PayoffDate!.Value));
        }
    }

    private decimal NextInterest => Math.Round(Outstanding * Debt.AnnualInterestRate / 100m / 12m, 2);

    public string InterestText => Plan is { PaysOff: true } plan && plan.InterestAfter(Today) > 0
        ? Loc.F("{0} interest still to pay", Format.Money(plan.InterestAfter(Today)))
        : "";

    [RelayCommand]
    private Task RecordExtraPaymentAsync() => recordExtraPayment(this);

    [RelayCommand]
    private Task DeleteAsync() => delete(this);
}
