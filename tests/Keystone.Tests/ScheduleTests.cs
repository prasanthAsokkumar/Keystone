using Keystone.Models;
using Keystone.Services;

namespace Keystone.Tests;

public class ScheduleTests
{
    [Fact]
    public void Monthly_KeepsMonthEndDayAcrossShortMonths()
    {
        var rent = new Transaction { Date = new DateOnly(2026, 1, 31), Amount = 1, Recurrence = Recurrence.Monthly };

        var dates = Schedule.Expand([rent], new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)).Select(o => o.Date);

        Assert.Equal(
            [new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30)],
            dates);
    }

    [Fact]
    public void Monthly_RespectsEndDateAndRangeStart()
    {
        var bill = new Transaction
        {
            Date = new DateOnly(2026, 1, 10), Amount = 1, Recurrence = Recurrence.Monthly,
            EndDate = new DateOnly(2026, 5, 10),
        };

        var dates = Schedule.Expand([bill], new DateOnly(2026, 3, 1), new DateOnly(2026, 12, 31)).Select(o => o.Date);

        Assert.Equal([new DateOnly(2026, 3, 10), new DateOnly(2026, 4, 10), new DateOnly(2026, 5, 10)], dates);
    }

    [Fact]
    public void OneTime_IsIncludedOnlyWithinRange()
    {
        var inside = new Transaction { Date = new DateOnly(2026, 6, 1), Amount = 1 };
        var outside = new Transaction { Date = new DateOnly(2027, 1, 1), Amount = 1 };

        var occurrences = Schedule.Expand([inside, outside], new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(inside, Assert.Single(occurrences).Source);
    }

    [Fact]
    public void Yearly_RepeatsOnTheSameDayEachYear()
    {
        var insurance = new Transaction { Date = new DateOnly(2025, 3, 15), Amount = 1200, Recurrence = Recurrence.Yearly };

        var dates = Schedule.Expand([insurance], new DateOnly(2026, 1, 1), new DateOnly(2028, 12, 31)).Select(o => o.Date);

        Assert.Equal([new DateOnly(2026, 3, 15), new DateOnly(2027, 3, 15), new DateOnly(2028, 3, 15)], dates);
        Assert.Equal(-100m, new Transaction { Type = TransactionType.Expense, Amount = 1200, Recurrence = Recurrence.Yearly }.SignedMonthlyAmount);
    }

    [Fact]
    public void Quarterly_RepeatsEveryThreeMonths()
    {
        var tax = new Transaction { Date = new DateOnly(2026, 1, 31), Amount = 1, Recurrence = Recurrence.Quarterly };

        var dates = Schedule.Expand([tax], new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).Select(o => o.Date);

        Assert.Equal(
            [new DateOnly(2026, 1, 31), new DateOnly(2026, 4, 30), new DateOnly(2026, 7, 31), new DateOnly(2026, 10, 31)],
            dates);
    }
}
