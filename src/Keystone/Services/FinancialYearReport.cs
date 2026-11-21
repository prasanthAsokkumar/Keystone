using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Keystone.Models;
using static Keystone.Services.Schedule;
using Keystone.Localization;

namespace Keystone.Services;

public sealed record MonthTotals(DateOnly Month, decimal Income, decimal Expenses, bool IsPlanned)
{
    public decimal Net => Income - Expenses;
}

public sealed record CategoryTotal(string Category, decimal Amount, double Share);

public sealed record FinancialYearReport
{
    public static readonly FinancialYearReport Empty = new();

    /// <summary>Calendar year the financial year starts in.</summary>
    public int StartYear { get; init; }

    public int StartMonth { get; init; } = 1;

    public DateOnly Start => new(StartYear == 0 ? 1 : StartYear, StartMonth, 1);

    public DateOnly End => Start.AddYears(1).AddDays(-1);

    /// <summary>"2026" for calendar years, "FY 2026/27" when the year starts in another month.</summary>
    public string Label => LabelFor(Start);

    /// <summary>"2026" for calendar years, "FY 2026/27" for years starting in another month.</summary>
    public static string LabelFor(DateOnly yearStart) =>
        yearStart.Month == 1 ? $"{yearStart.Year}" : Loc.F("FY {0}/{1:00}", yearStart.Year, (yearStart.Year + 1) % 100);

    public string RangeText =>
        $"{Start.ToString("MMM yyyy", CultureInfo.CurrentCulture)} – {End.ToString("MMM yyyy", CultureInfo.CurrentCulture)}";

    public IReadOnlyList<MonthTotals> Months { get; init; } = [];

    public IReadOnlyList<CategoryTotal> ExpensesByCategory { get; init; } = [];

    public decimal TotalIncome => Months.Sum(m => m.Income);

    public decimal TotalExpenses => Months.Sum(m => m.Expenses);

    public decimal Net => TotalIncome - TotalExpenses;

    /// <summary>Part of the totals that comes from entries dated after today.</summary>
    public decimal PlannedExpenses { get; init; }

    public static int StartYearContaining(DateOnly date, int startMonth) =>
        date.Month >= startMonth ? date.Year : date.Year - 1;

    /// <summary>
    /// Income and expenses for one financial year, month by month. Entries dated after
    /// <paramref name="today"/> (scheduled monthly ones and future one-time ones) are included as planned.
    /// </summary>
    public static FinancialYearReport Build(
        IEnumerable<Transaction> transactions, int startYear, int startMonth, DateOnly today)
    {
        transactions = transactions.Where(t => !t.IsTransfer); // money moved between own accounts
        var start = new DateOnly(startYear, startMonth, 1);
        var end = start.AddYears(1).AddDays(-1);
        var occurrences = Expand(transactions, start, end);
        var currentMonth = MonthStart(today);

        var months = Enumerable.Range(0, 12)
            .Select(i =>
            {
                var month = start.AddMonths(i);
                var inMonth = occurrences.Where(o => MonthStart(o.Date) == month).ToList();
                return new MonthTotals(
                    month,
                    inMonth.Where(o => o.Source.IsIncome).Sum(o => o.Source.Amount),
                    inMonth.Where(o => !o.Source.IsIncome).Sum(o => o.Source.Amount),
                    IsPlanned: month > currentMonth);
            })
            .ToList();

        var expenses = occurrences.Where(o => !o.Source.IsIncome).ToList();
        var totalExpenses = expenses.Sum(o => o.Source.Amount);
        var byCategory = expenses
            .GroupBy(o => string.IsNullOrWhiteSpace(o.Source.Category) ? Categories.Uncategorized : o.Source.Category.Trim(),
                StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var amount = g.Sum(o => o.Source.Amount);
                return new CategoryTotal(g.Key, amount, totalExpenses == 0 ? 0 : (double)(amount / totalExpenses));
            })
            .OrderByDescending(c => c.Amount)
            .ToList();

        return new FinancialYearReport
        {
            StartYear = startYear,
            StartMonth = startMonth,
            Months = months,
            ExpensesByCategory = byCategory,
            PlannedExpenses = expenses.Where(o => o.Date > today).Sum(o => o.Source.Amount),
        };
    }
}
