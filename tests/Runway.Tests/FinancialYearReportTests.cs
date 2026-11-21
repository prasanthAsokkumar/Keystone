using Runway.Models;
using Runway.Services;

namespace Runway.Tests;

public class FinancialYearReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static Transaction Entry(
        DateOnly date, decimal amount, TransactionType type, string category = "", Recurrence recurrence = Recurrence.OneTime) =>
        new() { Date = date, Amount = amount, Type = type, Category = category, Recurrence = recurrence, Description = "x" };

    [Fact]
    public void CalendarYear_IncludesMonthlyEntriesAndMarksFutureMonthsPlanned()
    {
        var salary = Entry(new DateOnly(2026, 1, 25), 5_000, TransactionType.Income, "Salary", Recurrence.Monthly);

        var report = FinancialYearReport.Build([salary], 2026, 1, Today);

        Assert.Equal("2026", report.Label);
        Assert.Equal(12, report.Months.Count);
        Assert.Equal(60_000m, report.TotalIncome);
        Assert.Equal(
            [false, false, false, false, false, false, false, false, false, false, true, true],
            report.Months.Select(m => m.IsPlanned));
    }

    [Fact]
    public void YearStartingInApril_SpansIntoNextCalendarYear()
    {
        var transactions = new[]
        {
            Entry(new DateOnly(2026, 3, 31), 100, TransactionType.Expense), // previous financial year
            Entry(new DateOnly(2026, 4, 1), 200, TransactionType.Expense),
            Entry(new DateOnly(2027, 3, 31), 300, TransactionType.Expense),
        };

        var report = FinancialYearReport.Build(transactions, 2026, 4, Today);

        Assert.Equal("FY 2026/27", report.Label);
        Assert.Equal(new DateOnly(2026, 4, 1), report.Months[0].Month);
        Assert.Equal(new DateOnly(2027, 3, 1), report.Months[11].Month);
        Assert.Equal(500m, report.TotalExpenses);
        Assert.Equal(300m, report.PlannedExpenses);
    }

    [Fact]
    public void ExpensesByCategory_GroupsCaseInsensitivelyAndSortsByAmount()
    {
        var transactions = new[]
        {
            Entry(new DateOnly(2026, 1, 1), 1_000, TransactionType.Expense, "Rent", Recurrence.Monthly),
            Entry(new DateOnly(2026, 1, 1), 400, TransactionType.Expense, "Health insurance", Recurrence.Monthly),
            Entry(new DateOnly(2026, 5, 1), 600, TransactionType.Expense, "rent"),
            Entry(new DateOnly(2026, 6, 1), 300, TransactionType.Expense, ""),
            Entry(new DateOnly(2026, 6, 1), 9_999, TransactionType.Income, "Salary"),
        };

        var report = FinancialYearReport.Build(transactions, 2026, 1, Today);

        Assert.Equal(["Rent", "Health insurance", Categories.Uncategorized], report.ExpensesByCategory.Select(c => c.Category));
        Assert.Equal(12_600m, report.ExpensesByCategory[0].Amount);
        Assert.Equal(1.0, report.ExpensesByCategory.Sum(c => c.Share), precision: 9);
    }

    [Theory]
    [InlineData(2026, 2, 10, 4, 2025)]
    [InlineData(2026, 4, 1, 4, 2026)]
    [InlineData(2026, 12, 31, 1, 2026)]
    public void StartYearContaining_UsesStartMonth(int year, int month, int day, int startMonth, int expected) =>
        Assert.Equal(expected, FinancialYearReport.StartYearContaining(new DateOnly(year, month, day), startMonth));
}
