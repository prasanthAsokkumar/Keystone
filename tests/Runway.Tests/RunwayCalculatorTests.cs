using Runway.Models;
using Runway.Services;

namespace Runway.Tests;

public class RunwayCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static Transaction Income(int year, int month, decimal amount) =>
        new() { Date = new DateOnly(year, month, 1), Description = "income", Amount = amount, Type = TransactionType.Income };

    private static Transaction Expense(int year, int month, decimal amount) =>
        new() { Date = new DateOnly(year, month, 5), Description = "expense", Amount = amount, Type = TransactionType.Expense };

    [Fact]
    public void BurningCash_ComputesRunwayFromAverageOfLastFullMonths()
    {
        var transactions = new[]
        {
            Income(2026, 7, 1000), Expense(2026, 7, 3000),
            Income(2026, 8, 1000), Expense(2026, 8, 3000),
            Income(2026, 9, 1000), Expense(2026, 9, 3000),
        };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today);

        Assert.Equal(4_000m, summary.CurrentBalance);
        Assert.Equal(-2_000m, summary.ExpectedMonthlyNet);
        Assert.Equal(3, summary.AveragingMonths);
        Assert.False(summary.UsedPartialMonth);
        Assert.Equal(2.0, summary.RunwayMonths!.Value, tolerance: 0.01);
        Assert.Equal(new DateOnly(2026, 12, 15), summary.ZeroCashDate);
    }

    [Fact]
    public void BurningCash_ProjectionEndsAtZeroCashDate()
    {
        var transactions = new[] { Expense(2026, 9, 3000) };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today);

        Assert.Equal(new BalancePoint(Today, 7_000m), summary.Projection[0]);
        Assert.Equal(0m, summary.Projection[^1].Balance);
        Assert.Equal(summary.ZeroCashDate, summary.Projection[^1].Date);
        Assert.All(summary.Projection.SkipLast(1), p => Assert.True(p.Balance > 0));
    }

    [Fact]
    public void CashFlowPositive_HasNoRunwayLimit()
    {
        var transactions = new[] { Income(2026, 9, 5000), Expense(2026, 9, 2000) };

        var summary = RunwayCalculator.Calculate(1_000m, transactions, Today);

        Assert.Null(summary.RunwayMonths);
        Assert.Null(summary.ZeroCashDate);
        Assert.Equal(3_000m, summary.ExpectedMonthlyNet);
        Assert.Equal(13, summary.Projection.Count);
        // One-time averages accrue daily: 365 days at 3,000 per average month.
        Assert.Equal((double)(4_000m + 365 * 3_000m / (365.25m / 12m)), (double)summary.Projection[^1].Balance, tolerance: 0.01);
    }

    [Fact]
    public void NoTransactions_UsesStartingBalanceOnly()
    {
        var summary = RunwayCalculator.Calculate(5_000m, [], Today);

        Assert.Equal(5_000m, summary.CurrentBalance);
        Assert.Null(summary.RunwayMonths);
        Assert.Single(summary.History);
    }

    [Fact]
    public void OnlyCurrentMonthData_FallsBackToPartialMonth()
    {
        var transactions = new[] { Expense(2026, 10, 1000) };

        var summary = RunwayCalculator.Calculate(5_000m, transactions, Today);

        Assert.True(summary.UsedPartialMonth);
        Assert.Equal(1, summary.AveragingMonths);
        Assert.Equal(4.0, summary.RunwayMonths!.Value, tolerance: 0.05);
    }

    [Fact]
    public void AveragesOnlyOverMonthsSinceFirstTransaction()
    {
        // One full month of data should not be diluted across a three-month window.
        var transactions = new[] { Expense(2026, 9, 3000) };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today);

        Assert.Equal(1, summary.AveragingMonths);
        Assert.Equal(-3_000m, summary.AverageOneTimeNet);
    }

    [Fact]
    public void MonthsOutsideLookbackWindow_AffectBalanceButNotBurn()
    {
        var transactions = new[]
        {
            Expense(2025, 6, 50_000), // more than 12 months ago
            Expense(2026, 7, 1000), Expense(2026, 8, 1000), Expense(2026, 9, 1000),
        };

        var summary = RunwayCalculator.Calculate(100_000m, transactions, Today);

        Assert.Equal(47_000m, summary.CurrentBalance);
        Assert.Equal(-250m, summary.ExpectedMonthlyNet); // Jul–Sep spending averaged over 12 months
    }

    [Fact]
    public void FutureTransactions_AreIgnored()
    {
        var transactions = new[] { Expense(2026, 11, 9_999) };

        var summary = RunwayCalculator.Calculate(1_000m, transactions, Today);

        Assert.Equal(1_000m, summary.CurrentBalance);
        Assert.Equal(0m, summary.AverageOneTimeNet);
    }

    [Fact]
    public void NegativeBalance_HasZeroRunway()
    {
        var transactions = new[] { Expense(2026, 9, 2000) };

        var summary = RunwayCalculator.Calculate(1_000m, transactions, Today);

        Assert.Equal(0, summary.RunwayMonths);
        Assert.Single(summary.Projection);
    }

    [Fact]
    public void History_HasOpeningPointAndOnePointPerMonthEndingToday()
    {
        var transactions = new[] { Income(2026, 8, 500), Expense(2026, 9, 200) };

        var summary = RunwayCalculator.Calculate(1_000m, transactions, Today);

        Assert.Equal(
            [
                new BalancePoint(new DateOnly(2026, 7, 31), 1_000m),
                new BalancePoint(new DateOnly(2026, 8, 31), 1_500m),
                new BalancePoint(new DateOnly(2026, 9, 30), 1_300m),
                new BalancePoint(Today, 1_300m),
            ],
            summary.History);
    }

    private static Transaction MonthlyExpense(DateOnly start, decimal amount, DateOnly? end = null) =>
        new()
        {
            Date = start, Description = "rent", Amount = amount, Type = TransactionType.Expense,
            Recurrence = Recurrence.Monthly, EndDate = end,
        };

    [Fact]
    public void MonthlyExpense_CountsEveryPastMonthAndDrivesProjection()
    {
        var transactions = new[] { MonthlyExpense(new DateOnly(2026, 7, 1), 1_000) };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today);

        Assert.Equal(6_000m, summary.CurrentBalance); // Jul, Aug, Sep, Oct paid
        Assert.Equal(1_000m, summary.RecurringMonthlyExpenses);
        Assert.Equal(0m, summary.AverageOneTimeNet);
        Assert.Equal(-1_000m, summary.ExpectedMonthlyNet);
        // Nov–Apr payments use up the 6,000; the account is empty the day April's rent is paid.
        Assert.Equal(new DateOnly(2027, 4, 1), summary.ZeroCashDate);
        Assert.Equal("rent", summary.ZeroCashCause);
    }

    [Fact]
    public void MonthlyExpenseWithEndDate_StopsBurningAfterItEnds()
    {
        var transactions = new[] { MonthlyExpense(new DateOnly(2026, 7, 1), 1_000, end: new DateOnly(2026, 12, 31)) };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today);

        Assert.Null(summary.RunwayMonths);
        Assert.Equal(4_000m, summary.Projection[^1].Balance); // Nov and Dec still paid
        Assert.Equal(13, summary.Projection.Count);
    }

    [Fact]
    public void OneTimeAverage_ExcludesMonthlyEntries()
    {
        var transactions = new[] { MonthlyExpense(new DateOnly(2026, 7, 1), 1_000), Expense(2026, 9, 600) };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today);

        Assert.Equal(-200m, summary.AverageOneTimeNet); // 600 over Jul–Sep, since the first entry
        Assert.Equal(-1_200m, summary.ExpectedMonthlyNet);
    }

    [Fact]
    public void FutureOneTimeEntry_IsAppliedInProjection()
    {
        var transactions = new[] { Expense(2026, 12, 3_000) };

        var summary = RunwayCalculator.Calculate(5_000m, transactions, Today);

        Assert.Equal(5_000m, summary.CurrentBalance);
        Assert.Equal(2_000m, summary.Projection[^1].Balance);
    }

    [Fact]
    public void DebtPayments_ReduceCashAndStopWhenDebtIsPaidOff()
    {
        var debt = new Debt
        {
            Name = "Loan", Balance = 6_000, StartDate = new DateOnly(2026, 8, 1),
            MonthlyPayment = 1_000, FirstPaymentDate = new DateOnly(2026, 8, 15),
        };
        var payments = DebtPlan.Build(debt).ToTransactions().ToList();

        var summary = RunwayCalculator.Calculate(10_000m, payments, Today);

        Assert.Equal(7_000m, summary.CurrentBalance); // Aug, Sep, Oct paid
        Assert.Equal(0m, summary.AverageOneTimeNet);  // scheduled, so not averaged
        Assert.Equal(-1_000m, summary.ExpectedMonthlyNet);
        Assert.Null(summary.RunwayMonths);
        Assert.Equal(4_000m, summary.Projection[^1].Balance); // Nov, Dec, Jan still to pay
    }

    [Fact]
    public void LargePaymentMidMonth_EmptiesCashOnItsDueDate()
    {
        // Income covers costs, but a 40,000 bill on 3 Jan is more than the cash on hand.
        var transactions = new[]
        {
            new Transaction { Date = new DateOnly(2027, 1, 3), Description = "Loan repayment", Amount = 40_000, Type = TransactionType.Expense },
            MonthlyExpense(new DateOnly(2026, 7, 1), 1_000),
            new Transaction
            {
                Date = new DateOnly(2026, 7, 25), Description = "salary", Amount = 2_500,
                Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
            },
        };

        var summary = RunwayCalculator.Calculate(30_000m, transactions, Today);

        Assert.Equal(new DateOnly(2027, 1, 3), summary.ZeroCashDate);
        Assert.Equal("Loan repayment", summary.ZeroCashCause);
    }

    [Fact]
    public void PerDayFigures_UseTheNext12Months()
    {
        var transactions = new[]
        {
            new Transaction
            {
                Date = new DateOnly(2026, 1, 25), Description = "salary", Amount = 7_305,
                Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
            },
            MonthlyExpense(new DateOnly(2026, 1, 1), 3_652.50m),
        };

        var summary = RunwayCalculator.Calculate(0m, transactions, Today);

        Assert.Equal(12 * 7_305m, summary.YearAheadIncome);
        Assert.Equal(240.0, (double)summary.IncomePerDay, tolerance: 0.01);   // 87,660 / 365.25
        Assert.Equal(120.0, (double)summary.ExpensesPerDay, tolerance: 0.01);
        Assert.Equal(120.0, (double)summary.NetPerDay, tolerance: 0.01);
    }

    [Fact]
    public void YearSavings_AddsSavedSoFarAndExpectedRest()
    {
        var transactions = new[]
        {
            new Transaction
            {
                Date = new DateOnly(2026, 1, 25), Description = "salary", Amount = 5_000,
                Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
            },
            MonthlyExpense(new DateOnly(2026, 1, 1), 3_000),
        };

        var summary = RunwayCalculator.Calculate(0m, transactions, Today);

        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), (summary.YearStart, summary.YearEnd));
        Assert.Equal(9 * 5_000m - 10 * 3_000m, summary.YearSavedSoFar); // salary Jan–Sep, rent Jan–Oct
        Assert.Equal(3 * 5_000m - 2 * 3_000m, summary.YearExpectedRest); // salary Oct–Dec, rent Nov–Dec
        Assert.Equal(24_000m, summary.YearSavings);
        Assert.Equal(8.0, summary.YearSavingsMonths!.Value, tolerance: 0.001); // 24,000 / 3,000
    }

    [Fact]
    public void YearSavings_FollowsTheFinancialYearStart()
    {
        var transactions = new[] { MonthlyExpense(new DateOnly(2026, 1, 1), 100) };

        var summary = RunwayCalculator.Calculate(10_000m, transactions, Today, financialYearStartMonth: 4);

        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31)), (summary.YearStart, summary.YearEnd));
        Assert.Equal(-1_200m, summary.YearSavings); // Apr–Mar rent
    }

    [Fact]
    public void MonthsWithoutIncome_DividesCashByLivingCostsExcludingDebt()
    {
        var debt = new Debt
        {
            Name = "Loan", Balance = 12_000, StartDate = Today, MonthlyPayment = 1_000,
            FirstPaymentDate = new DateOnly(2026, 11, 1),
        };
        var transactions = DebtPlan.Build(debt).ToTransactions()
            .Append(MonthlyExpense(new DateOnly(2026, 11, 1), 2_000))
            .ToList();

        var summary = RunwayCalculator.Calculate(20_000m, transactions, Today);

        Assert.Equal(2_000m, summary.LivingCostsPerMonth);
        Assert.Equal(10.0, summary.MonthsWithoutIncome!.Value, tolerance: 0.001);
    }
}
