using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Models;
using Keystone.Localization;

namespace Keystone.Services;

public sealed record DebtPlanEntry(DateOnly Date, decimal Payment, decimal Interest, decimal BalanceAfter, bool IsExtra);

/// <summary>The full repayment schedule of one debt: regular payments, extra payments and interest.</summary>
public sealed class DebtPlan
{
    /// <summary>Upper bound on regular payments, so debts whose payments never cover the interest still end.</summary>
    public const int MaxPayments = 600;

    private DebtPlan(Debt debt, List<DebtPlanEntry> entries, bool paysOff)
    {
        Debt = debt;
        Entries = entries;
        PaysOff = paysOff;
    }

    public Debt Debt { get; }

    public IReadOnlyList<DebtPlanEntry> Entries { get; }

    /// <summary>False when the payments don't keep up with the interest, or no repayment is planned.</summary>
    public bool PaysOff { get; }

    /// <summary>An interest-only debt with no planned repayment: interest runs until it is repaid.</summary>
    public bool IsOpenEnded => !PaysOff && Debt.IsInterestOnly && Debt.MonthlyPayment <= 0;

    public DateOnly? PayoffDate => PaysOff ? Entries.Count > 0 ? Entries[^1].Date : Debt.StartDate : null;

    public decimal OutstandingOn(DateOnly date) =>
        Entries.LastOrDefault(e => e.Date <= date)?.BalanceAfter ?? Debt.Balance;

    public decimal InterestAfter(DateOnly date) => Entries.Where(e => e.Date > date).Sum(e => e.Interest);

    public int PaymentsAfter(DateOnly date) => Entries.Count(e => !e.IsExtra && e.Date > date);

    /// <summary>Every payment as an expense, so cash history and projections include it.</summary>
    public IEnumerable<Transaction> ToTransactions() =>
        Entries.Select(e => e.Payment < 0
            ? new Transaction
            {
                // Borrowed money comes in: income for the cash, though not earnings.
                Date = e.Date,
                Description = Loc.F("{0} (borrowed)", Debt.Name),
                Category = Categories.DebtRepayment,
                Amount = -e.Payment,
                Type = TransactionType.Income,
                IsDebtPayment = true,
            }
            : new Transaction
        {
            Date = e.Date,
            Description = e.IsExtra ? Loc.F("{0} (extra payment)", Debt.Name)
                : Debt.IsInterestOnly && e.Payment == e.Interest ? Loc.F("{0} interest", Debt.Name)
                : Loc.F("{0} payment", Debt.Name),
            Category = Categories.DebtRepayment,
            Amount = e.Payment,
            Type = TransactionType.Expense,
            IsDebtPayment = true,
        });

    /// <remarks>
    /// Interest compounds monthly and is charged on each regular payment date. A debt paid at once
    /// is charged interest for the whole months between <see cref="Debt.StartDate"/> and its due date.
    /// Extra payments reduce the balance on their date without interest.
    /// An interest-only debt is charged simple interest on what was owed before each payment date, so
    /// principal repaid on a payment date lowers the next month's interest, not that day's.
    /// </remarks>
    public static DebtPlan Build(Debt debt)
    {
        var monthlyRate = debt.AnnualInterestRate / 100m / 12m;
        var balance = debt.Balance;
        var entries = new List<DebtPlanEntry>();
        var extras = debt.ExtraPayments
            .Where(p => p.Date >= debt.StartDate)
            .OrderBy(p => p.Date)
            .ThenBy(p => p.Id)
            .ToList();
        var nextExtra = 0;

        void ApplyExtrasUpTo(DateOnly date, bool inclusive = true)
        {
            while (nextExtra < extras.Count
                && (extras[nextExtra].Date < date || inclusive && extras[nextExtra].Date == date))
            {
                var extra = extras[nextExtra++];
                if (extra.IsBorrowing)
                {
                    // Borrowed more: the debt grows by that amount (a negative payment).
                    balance += extra.Borrowed;
                    entries.Add(new DebtPlanEntry(extra.Date, extra.Amount, 0, balance, IsExtra: true));
                    continue;
                }
                if (balance <= 0)
                    continue;
                var paid = Math.Min(extra.Amount, balance);
                balance -= paid;
                entries.Add(new DebtPlanEntry(extra.Date, paid, 0, balance, IsExtra: true));
            }
        }

        // Once paid off, the plan only goes on if more is borrowed later.
        bool BorrowsLater() => extras.Skip(nextExtra).Any(e => e.IsBorrowing);

        var regularPayments = 0;
        for (var month = 0; (balance > 0 || BorrowsLater()) && regularPayments < MaxPayments; month++)
        {
            var due = debt.IsLumpSum ? debt.FirstPaymentDate : debt.FirstPaymentDate.AddMonths(month);

            // Extra payments made on a payment day count before that day's regular payment,
            // except interest-only, where that day's interest is still owed on the earlier balance.
            ApplyExtrasUpTo(due, inclusive: !debt.IsInterestOnly);
            if (balance <= 0)
            {
                if (!debt.IsLumpSum && BorrowsLater())
                    continue;
                break;
            }

            if (due <= debt.StartDate)
            {
                if (debt.IsLumpSum)
                    break;
                continue; // Already reflected in the recorded balance.
            }

            if (debt.IsInterestOnly)
            {
                var interestDue = Math.Round(balance * monthlyRate, 2);
                var principal = Math.Min(debt.MonthlyPayment, balance);
                balance -= principal;
                regularPayments++;
                entries.Add(new DebtPlanEntry(due, interestDue + principal, interestDue, balance, IsExtra: false));
                continue;
            }

            decimal interest;
            if (debt.IsLumpSum)
            {
                var months = Schedule.MonthsBetween(debt.StartDate, due);
                interest = Math.Round(balance * ((decimal)Math.Pow(1 + (double)monthlyRate, months) - 1), 2);
            }
            else
            {
                interest = Math.Round(balance * monthlyRate, 2);
            }

            balance += interest;
            var payment = debt.IsLumpSum ? balance : Math.Min(debt.MonthlyPayment, balance);
            balance -= payment;
            regularPayments++;
            entries.Add(new DebtPlanEntry(due, payment, interest, balance, IsExtra: false));

            if (debt.IsLumpSum)
                break;
        }

        ApplyExtrasUpTo(DateOnly.MaxValue);
        return new DebtPlan(debt, entries, paysOff: balance <= 0);
    }
}
