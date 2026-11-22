using System;
using System.Collections.Generic;
using System.Linq;

namespace Keystone.Models;

public class Debt
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Amount owed on <see cref="StartDate"/>.</summary>
    public decimal Balance { get; set; }

    /// <summary>The day <see cref="Balance"/> was recorded. Only payments after it are scheduled.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Yearly interest in percent, e.g. 4.5. Zero for interest-free debt.</summary>
    public decimal AnnualInterestRate { get; set; }

    /// <summary>
    /// Installment paid every month. Zero means the whole debt is paid at once.
    /// For an interest-only debt, the principal repaid each month on top of the interest; zero means none planned.
    /// </summary>
    public decimal MonthlyPayment { get; set; }

    /// <summary>First installment date, or the due date when paid at once.</summary>
    public DateOnly FirstPaymentDate { get; set; }

    /// <summary>
    /// Interest is paid every month on what is still owed, and the principal is repaid whenever
    /// there is money for it, like a private loan charging 25 per 1,000 a month.
    /// </summary>
    public bool IsInterestOnly { get; set; }

    public List<DebtPayment> ExtraPayments { get; set; } = [];

    /// <summary>Everything borrowed: the recorded balance plus later borrowing.</summary>
    public decimal TotalBorrowed => Balance + ExtraPayments.Where(p => p.Date >= StartDate).Sum(p => p.Borrowed);

    public bool IsLumpSum => !IsInterestOnly && MonthlyPayment <= 0;

    /// <summary>Monthly interest per 1,000 owed, e.g. 25 for 30% a year.</summary>
    public decimal MonthlyInterestPer1000 => AnnualInterestRate * 10m / 12m;

    public static decimal AnnualRateFromMonthlyPer1000(decimal per1000) => per1000 * 12m / 10m;
}

/// <summary>A payment made on top of the regular plan.</summary>
public class DebtPayment
{
    public int Id { get; set; }

    public int DebtId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Paid towards the debt. Negative when more was borrowed from the same lender.</summary>
    public decimal Amount { get; set; }

    public bool IsBorrowing => Amount < 0;

    public decimal Borrowed => IsBorrowing ? -Amount : 0;
}
