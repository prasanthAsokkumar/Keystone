using Runway.Models;
using Runway.Services;

namespace Runway.Tests;

public class DebtPlanTests
{
    private static readonly DateOnly Start = new(2026, 10, 9);

    private static Debt Installments(decimal balance, decimal payment, decimal rate = 0) => new()
    {
        Name = "Loan", Balance = balance, StartDate = Start, AnnualInterestRate = rate,
        MonthlyPayment = payment, FirstPaymentDate = new DateOnly(2026, 11, 1),
    };

    [Fact]
    public void InterestFree_PaysOffWithSmallerFinalPayment()
    {
        var plan = DebtPlan.Build(Installments(1_000, 300));

        Assert.Equal([300m, 300m, 300m, 100m], plan.Entries.Select(e => e.Payment));
        Assert.True(plan.PaysOff);
        Assert.Equal(new DateOnly(2027, 2, 1), plan.PayoffDate);
    }

    [Fact]
    public void WithInterest_ChargesMonthlyInterestOnRemainingBalance()
    {
        var plan = DebtPlan.Build(Installments(1_000, 500, rate: 12)); // 1% a month

        Assert.Equal([10m, 5.10m, 0.15m], plan.Entries.Select(e => e.Interest));
        Assert.Equal([500m, 500m, 15.25m], plan.Entries.Select(e => e.Payment));
        Assert.Equal(15.25m, plan.InterestAfter(Start));
    }

    [Fact]
    public void LumpSum_PaysBalancePlusInterestOnDueDate()
    {
        var debt = new Debt
        {
            Name = "Tax bill", Balance = 2_000, StartDate = Start, AnnualInterestRate = 6,
            FirstPaymentDate = new DateOnly(2027, 1, 9),
        };

        var entry = Assert.Single(DebtPlan.Build(debt).Entries);

        Assert.Equal(new DateOnly(2027, 1, 9), entry.Date);
        Assert.Equal(30.15m, entry.Interest); // 3 months at 0.5%, compounded
        Assert.Equal(2_030.15m, entry.Payment);
    }

    [Fact]
    public void ExtraPayment_ReducesBalanceAndBringsPayoffForward()
    {
        var debt = Installments(1_000, 300);
        debt.ExtraPayments.Add(new DebtPayment { Date = new DateOnly(2026, 11, 1), Amount = 400 });

        var plan = DebtPlan.Build(debt);

        Assert.Equal([(400m, true), (300m, false), (300m, false)], plan.Entries.Select(e => (e.Payment, e.IsExtra)));
        Assert.Equal(new DateOnly(2026, 12, 1), plan.PayoffDate);
    }

    [Fact]
    public void PaymentNotCoveringInterest_NeverPaysOff()
    {
        var plan = DebtPlan.Build(Installments(10_000, 100, rate: 12));

        Assert.False(plan.PaysOff);
        Assert.Null(plan.PayoffDate);
        Assert.Equal(DebtPlan.MaxPayments, plan.Entries.Count);
    }

    [Fact]
    public void OutstandingAndPaymentsLeft_FollowTheSchedule()
    {
        var plan = DebtPlan.Build(Installments(1_000, 300));
        var midDecember = new DateOnly(2026, 12, 15);

        Assert.Equal(1_000m, plan.OutstandingOn(Start));
        Assert.Equal(400m, plan.OutstandingOn(midDecember));
        Assert.Equal(2, plan.PaymentsAfter(midDecember));
    }

    [Fact]
    public void Transactions_AreDebtRepaymentExpenses()
    {
        var transaction = DebtPlan.Build(Installments(300, 300)).ToTransactions().Single();

        Assert.True(transaction.IsDebtPayment);
        Assert.Equal(TransactionType.Expense, transaction.Type);
        Assert.Equal(Categories.DebtRepayment, transaction.Category);
        Assert.Equal("Loan payment", transaction.Description);
    }

    private static Debt PrivateLoan(decimal balance, decimal monthlyRepayment = 0) => new()
    {
        Name = "Private loan", Balance = balance, StartDate = new DateOnly(2026, 10, 1),
        AnnualInterestRate = Debt.AnnualRateFromMonthlyPer1000(25), MonthlyPayment = monthlyRepayment,
        FirstPaymentDate = new DateOnly(2026, 11, 1), IsInterestOnly = true,
    };

    [Fact]
    public void InterestOnly_RateConvertsFromPer1000()
    {
        var debt = PrivateLoan(5_000);

        Assert.Equal(30m, debt.AnnualInterestRate);
        Assert.Equal(25m, debt.MonthlyInterestPer1000);
        Assert.False(debt.IsLumpSum);
    }

    [Fact]
    public void InterestOnly_WithoutRepayment_ChargesInterestUntilRepaid()
    {
        var plan = DebtPlan.Build(PrivateLoan(5_000));

        Assert.All(plan.Entries, e => Assert.Equal((125m, 125m, 5_000m), (e.Payment, e.Interest, e.BalanceAfter)));
        Assert.False(plan.PaysOff);
        Assert.True(plan.IsOpenEnded);
        Assert.Equal("Private loan interest", plan.ToTransactions().First().Description);
    }

    [Fact]
    public void InterestOnly_RepaymentOnPaymentDay_LowersNextMonthsInterest()
    {
        var debt = PrivateLoan(5_000);
        debt.ExtraPayments.Add(new DebtPayment { Date = new DateOnly(2026, 11, 1), Amount = 1_000 });

        var plan = DebtPlan.Build(debt);

        Assert.Equal(
            [(125m, false, 5_000m), (1_000m, true, 4_000m), (100m, false, 4_000m)],
            plan.Entries.Take(3).Select(e => (e.Payment, e.IsExtra, e.BalanceAfter)));
        Assert.True(plan.IsOpenEnded);
    }

    [Fact]
    public void InterestOnly_RepayingEverything_EndsTheDebt()
    {
        var debt = PrivateLoan(2_000);
        debt.ExtraPayments.Add(new DebtPayment { Date = new DateOnly(2026, 11, 15), Amount = 2_000 });

        var plan = DebtPlan.Build(debt);

        Assert.True(plan.PaysOff);
        Assert.False(plan.IsOpenEnded);
        Assert.Equal(new DateOnly(2026, 11, 15), plan.PayoffDate);
        Assert.Equal(50m, plan.InterestAfter(debt.StartDate));
    }

    [Fact]
    public void InterestOnly_WithPlannedRepayment_PaysInterestPlusPrincipal()
    {
        var plan = DebtPlan.Build(PrivateLoan(5_000, monthlyRepayment: 1_000));

        Assert.Equal([125m, 100m, 75m, 50m, 25m], plan.Entries.Select(e => e.Interest));
        Assert.Equal([1_125m, 1_100m, 1_075m, 1_050m, 1_025m], plan.Entries.Select(e => e.Payment));
        Assert.Equal(new DateOnly(2027, 3, 1), plan.PayoffDate);
    }
}
