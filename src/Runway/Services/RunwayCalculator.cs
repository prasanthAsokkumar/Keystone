using System;
using System.Collections.Generic;
using System.Linq;
using Runway.Models;
using static Runway.Services.Schedule;

namespace Runway.Services;

public readonly record struct BalancePoint(DateOnly Date, decimal Balance);

public sealed record RunwaySummary
{
    public static readonly RunwaySummary Empty = new();

    private const decimal DaysPerYear = 365.25m;

    public decimal CurrentBalance { get; init; }

    /// <summary>Income from recurring entries due in the coming month.</summary>
    public decimal RecurringMonthlyIncome { get; init; }

    /// <summary>Expenses from recurring entries and debt payments due in the coming month.</summary>
    public decimal RecurringMonthlyExpenses { get; init; }

    /// <summary>Average monthly net of one-time entries over the averaging window (negative = spending).</summary>
    public decimal AverageOneTimeNet { get; init; }

    /// <summary>Expected cash flow for the coming month (negative = cash is shrinking).</summary>
    public decimal ExpectedMonthlyNet { get; init; }

    /// <summary>Average monthly income of one-time entries over the averaging window.</summary>
    public decimal AverageOneTimeIncome { get; init; }

    /// <summary>Average monthly spending of one-time entries over the averaging window (positive).</summary>
    public decimal AverageOneTimeExpenses { get; init; }

    /// <summary>Months until cash hits zero, or null when cash is not shrinking.</summary>
    public double? RunwayMonths { get; init; }

    public DateOnly? ZeroCashDate { get; init; }

    /// <summary>The biggest payment due on <see cref="ZeroCashDate"/>, when a payment is what empties the account.</summary>
    public string? ZeroCashCause { get; init; }

    /// <summary>Expected income over the next 12 months: scheduled entries plus the one-time average.</summary>
    public decimal YearAheadIncome { get; init; }

    /// <summary>Expected spending over the next 12 months, debt payments included (positive).</summary>
    public decimal YearAheadExpenses { get; init; }

    /// <summary>Expected spending over the next 12 months without debt payments: the cost of living.</summary>
    public decimal YearAheadLivingCosts { get; init; }

    public decimal IncomePerDay => YearAheadIncome / DaysPerYear;

    public decimal ExpensesPerDay => YearAheadExpenses / DaysPerYear;

    public decimal NetPerDay => IncomePerDay - ExpensesPerDay;

    public decimal LivingCostsPerMonth => YearAheadLivingCosts / 12m;

    /// <summary>Months the current cash would pay for living costs with no income at all.</summary>
    public double? MonthsWithoutIncome => CurrentBalance <= 0 ? 0
        : LivingCostsPerMonth > 0 ? (double)(CurrentBalance / LivingCostsPerMonth) : null;

    /// <summary>First and last day of the financial year containing today.</summary>
    public DateOnly YearStart { get; init; }

    public DateOnly YearEnd { get; init; }

    /// <summary>Net cash flow from the start of the financial year up to today.</summary>
    public decimal YearSavedSoFar { get; init; }

    /// <summary>Expected net cash flow from tomorrow to the end of the financial year.</summary>
    public decimal YearExpectedRest { get; init; }

    public decimal YearSavings => YearSavedSoFar + YearExpectedRest;

    /// <summary>Months of living costs this year's savings would pay for.</summary>
    public double? YearSavingsMonths => YearSavings <= 0 || LivingCostsPerMonth <= 0
        ? null : (double)(YearSavings / LivingCostsPerMonth);

    /// <summary>Number of months the one-time average was taken over.</summary>
    public int AveragingMonths { get; init; }

    /// <summary>True when there were no complete months, so the current partial month was used.</summary>
    public bool UsedPartialMonth { get; init; }

    public IReadOnlyList<BalancePoint> History { get; init; } = [];

    public IReadOnlyList<BalancePoint> Projection { get; init; } = [];
}

public static class RunwayCalculator
{
    /// <summary>Months of projection shown when cash never runs out.</summary>
    private const int SustainableProjectionMonths = 12;

    /// <summary>Average days per month, for turning monthly averages into daily ones.</summary>
    private const decimal DaysPerMonth = 365.25m / 12m;

    /// <param name="startingBalance">Cash on hand before the first transaction.</param>
    /// <param name="transactions">All recorded entries. Occurrences after <paramref name="today"/> count only toward the projection.</param>
    /// <param name="today">The reference date for "now".</param>
    /// <param name="lookbackMonths">How many complete months to average one-time entries over.</param>
    /// <param name="historyMonths">How many months of balance history to return, including the current one.</param>
    /// <param name="maxProjectionMonths">How far ahead to walk the projection.</param>
    /// <param name="financialYearStartMonth">Month the financial year starts in, for this year's savings.</param>
    public static RunwaySummary Calculate(
        decimal startingBalance,
        IReadOnlyCollection<Transaction> transactions,
        DateOnly today,
        int lookbackMonths = 12,
        int historyMonths = 12,
        int maxProjectionMonths = 60,
        int financialYearStartMonth = 1)
    {
        var firstDate = transactions.Count > 0 ? transactions.Min(t => t.Date) : today;
        var past = Expand(transactions, firstDate, today);
        var balance = startingBalance + past.Sum(o => o.SignedAmount);

        var (oneTimeIncome, oneTimeExpenses, months, usedPartialMonth) = AverageOneTime(past, today, lookbackMonths);
        var averageOneTimeNet = oneTimeIncome - oneTimeExpenses;

        var nextMonth = Expand(transactions.Where(IsScheduled), today.AddDays(1), today.AddMonths(1));
        var recurringIncome = nextMonth.Where(o => o.Source.IsIncome).Sum(o => o.Source.Amount);
        var recurringExpenses = nextMonth.Where(o => !o.Source.IsIncome).Sum(o => o.Source.Amount);

        var projection = Project(transactions, balance, averageOneTimeNet, today, maxProjectionMonths);

        // The next 12 months: scheduled entries exactly, plus a year of the one-time averages.
        var yearAhead = Expand(transactions, today.AddDays(1), today.AddYears(1));
        var yearAheadIncome = yearAhead.Where(o => o.Source.IsIncome).Sum(o => o.Source.Amount) + oneTimeIncome * 12;
        var yearAheadExpenses = yearAhead.Where(o => !o.Source.IsIncome).Sum(o => o.Source.Amount) + oneTimeExpenses * 12;
        var yearAheadDebt = yearAhead.Where(o => o.Source.IsDebtPayment).Sum(o => o.Source.Amount);

        // This financial year: what already happened, plus what is expected until it ends.
        var yearStart = new DateOnly(FinancialYearReport.StartYearContaining(today, financialYearStartMonth), financialYearStartMonth, 1);
        var yearEnd = yearStart.AddYears(1).AddDays(-1);
        var savedSoFar = past.Where(o => o.Date >= yearStart).Sum(o => o.SignedAmount);
        var expectedRest = Expand(transactions, today.AddDays(1), yearEnd).Sum(o => o.SignedAmount)
            + averageOneTimeNet / DaysPerMonth * (yearEnd.DayNumber - today.DayNumber);

        return new RunwaySummary
        {
            CurrentBalance = balance,
            RecurringMonthlyIncome = recurringIncome,
            RecurringMonthlyExpenses = recurringExpenses,
            AverageOneTimeNet = averageOneTimeNet,
            AverageOneTimeIncome = oneTimeIncome,
            AverageOneTimeExpenses = oneTimeExpenses,
            ExpectedMonthlyNet = recurringIncome - recurringExpenses + averageOneTimeNet,
            RunwayMonths = projection.RunwayMonths,
            ZeroCashDate = projection.ZeroCashDate,
            ZeroCashCause = projection.ZeroCashCause,
            YearAheadIncome = yearAheadIncome,
            YearAheadExpenses = yearAheadExpenses,
            YearAheadLivingCosts = yearAheadExpenses - yearAheadDebt,
            YearStart = yearStart,
            YearEnd = yearEnd,
            YearSavedSoFar = savedSoFar,
            YearExpectedRest = Math.Round(expectedRest, 2),
            AveragingMonths = months,
            UsedPartialMonth = usedPartialMonth,
            History = BuildHistory(startingBalance, past, today, historyMonths),
            Projection = projection.Points,
        };
    }

    /// <summary>
    /// Average monthly net of one-time entries over the last complete months, but not before the first entry.
    /// Recurring entries and debt payments are excluded because the projection schedules them exactly.
    /// </summary>
    private static (decimal Income, decimal Expenses, int Months, bool UsedPartialMonth) AverageOneTime(
        List<Occurrence> past, DateOnly today, int lookbackMonths)
    {
        var currentMonth = MonthStart(today);
        var windowEnd = currentMonth;
        var windowStart = currentMonth.AddMonths(-lookbackMonths);
        if (past.Count > 0 && MonthStart(past[0].Date) > windowStart)
            windowStart = MonthStart(past[0].Date);

        var months = MonthsBetween(windowStart, windowEnd);
        var usedPartialMonth = false;
        if (months <= 0)
        {
            windowStart = currentMonth;
            windowEnd = currentMonth.AddMonths(1);
            months = 1;
            usedPartialMonth = true;
        }

        var oneTime = past.Where(o => !IsScheduled(o.Source) && o.Date >= windowStart && o.Date < windowEnd).ToList();
        var income = oneTime.Where(o => o.Source.IsIncome).Sum(o => o.Source.Amount);
        var expenses = oneTime.Where(o => !o.Source.IsIncome).Sum(o => o.Source.Amount);

        return (income / months, expenses / months, months, usedPartialMonth);
    }

    private sealed record ProjectionResult(
        List<BalancePoint> Points, double? RunwayMonths, DateOnly? ZeroCashDate, string? ZeroCashCause = null);

    /// <summary>
    /// Walks forward day by day: scheduled entries (monthly, future one-time and debt payments) land on
    /// their exact dates, and the average of unscheduled one-time entries is spread evenly over the days.
    /// Day steps matter: a large bill due mid-month empties the account on that day, not on an
    /// averaged-out date earlier in the month.
    /// </summary>
    private static ProjectionResult Project(
        IReadOnlyCollection<Transaction> transactions, decimal balance, decimal averageOneTimeNet,
        DateOnly today, int maxProjectionMonths)
    {
        var points = new List<BalancePoint> { new(today, balance) };
        if (balance <= 0)
            return new ProjectionResult(points, 0, today);

        var horizon = today.AddMonths(maxProjectionMonths);
        var byDay = Expand(transactions, today.AddDays(1), horizon).ToLookup(o => o.Date);
        var dailyOneTime = averageOneTimeNet / DaysPerMonth;
        var running = balance;
        var nextPointMonth = 1;
        var lastYearStart = horizon.AddYears(-1);
        var balanceAtLastYearStart = balance;

        for (var day = today.AddDays(1); day <= horizon; day = day.AddDays(1))
        {
            var due = byDay[day].ToList();
            running += dailyOneTime + due.Sum(o => o.SignedAmount);

            if (running <= 0)
            {
                var biggest = due.Select(o => o.Source).Where(t => !t.IsIncome).MaxBy(t => t.Amount);
                var cause = biggest is not null && biggest.Amount > -dailyOneTime ? biggest.Description : null;
                points.Add(new BalancePoint(day, 0));
                return new ProjectionResult(points, (day.DayNumber - today.DayNumber) / (double)DaysPerMonth, day, cause);
            }

            if (day == lastYearStart)
                balanceAtLastYearStart = running;
            if (day == today.AddMonths(nextPointMonth))
            {
                points.Add(new BalancePoint(day, Math.Round(running, 2)));
                nextPointMonth++;
            }
        }

        var lastYearNet = running - balanceAtLastYearStart;
        if (lastYearNet >= 0)
        {
            // Sustainable: keep the chart readable rather than showing years of growth.
            return new ProjectionResult(points.Take(SustainableProjectionMonths + 1).ToList(), null, null);
        }

        // Still burning past the horizon: extrapolate at the last year's rate.
        var runway = maxProjectionMonths + (double)(running / (-lastYearNet / 12));
        return new ProjectionResult(points, runway, today.AddDays((int)Math.Round(runway * (double)DaysPerMonth)));
    }

    private static bool IsScheduled(Transaction transaction) => transaction.IsRecurring || transaction.IsDebtPayment;

    /// <summary>Balance at the end of each month (today for the current month).</summary>
    private static List<BalancePoint> BuildHistory(
        decimal startingBalance, List<Occurrence> past, DateOnly today, int historyMonths)
    {
        var currentMonth = MonthStart(today);
        if (past.Count == 0)
            return [new BalancePoint(today, startingBalance)];

        var firstMonth = MonthStart(past[0].Date);
        var earliestShown = currentMonth.AddMonths(-(historyMonths - 1));
        if (firstMonth < earliestShown)
            firstMonth = earliestShown;

        decimal BalanceAt(DateOnly date) =>
            startingBalance + past.Where(o => o.Date <= date).Sum(o => o.SignedAmount);

        var points = new List<BalancePoint>();
        var openingDate = firstMonth.AddDays(-1);
        points.Add(new BalancePoint(openingDate, BalanceAt(openingDate)));

        for (var month = firstMonth; month <= currentMonth; month = month.AddMonths(1))
        {
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var pointDate = monthEnd < today ? monthEnd : today;
            points.Add(new BalancePoint(pointDate, BalanceAt(pointDate)));
        }

        return points;
    }
}
