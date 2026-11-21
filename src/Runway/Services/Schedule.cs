using System;
using System.Collections.Generic;
using Runway.Models;

namespace Runway.Services;

/// <summary>A single dated cash movement, either a one-time entry or one occurrence of a recurring entry.</summary>
public readonly record struct Occurrence(DateOnly Date, Transaction Source)
{
    public decimal SignedAmount => Source.SignedAmount;
}

public static class Schedule
{
    /// <summary>All occurrences dated within [<paramref name="from"/>, <paramref name="to"/>], in date order.</summary>
    public static List<Occurrence> Expand(IEnumerable<Transaction> transactions, DateOnly from, DateOnly to)
    {
        var occurrences = new List<Occurrence>();
        foreach (var transaction in transactions)
        {
            if (!transaction.IsRecurring)
            {
                if (transaction.Date >= from && transaction.Date <= to)
                    occurrences.Add(new Occurrence(transaction.Date, transaction));
                continue;
            }

            var last = transaction.EndDate is { } end && end < to ? end : to;

            // Always offset from the start date so a 31st keeps landing on month ends (Feb 28, Mar 31, ...).
            var step = transaction.IntervalMonths;
            for (var i = 0; ; i++)
            {
                var date = transaction.Date.AddMonths(i * step);
                if (date > last)
                    break;
                if (date >= from)
                    occurrences.Add(new Occurrence(date, transaction));
            }
        }

        occurrences.Sort((a, b) => a.Date.CompareTo(b.Date));
        return occurrences;
    }

    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    public static int MonthsBetween(DateOnly start, DateOnly end) =>
        (end.Year - start.Year) * 12 + end.Month - start.Month;
}
