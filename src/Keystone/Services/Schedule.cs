using System;
using System.Collections.Generic;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>
/// A single dated cash movement, either a one-time entry or one occurrence of a recurring entry.
/// <see cref="Amount"/> is this occurrence's own amount, which a change to a single month can make differ from the entry's.
/// </summary>
public readonly record struct Occurrence(DateOnly Date, Transaction Source, decimal Amount)
{
    public Occurrence(DateOnly date, Transaction source) : this(date, source, source.Amount) { }

    /// <summary>The effect on the household's cash: transfers move money but don't change it.</summary>
    public decimal SignedAmount => Source.IsIncome ? Amount : Source.IsTransfer ? 0 : -Amount;
}

public static class Schedule
{
    /// <summary>The regular dates of a repeating entry within the range, ignoring changes to single months.</summary>
    public static IEnumerable<DateOnly> RegularDates(Transaction transaction, DateOnly from, DateOnly to)
    {
        if (!transaction.IsRecurring)
        {
            if (transaction.Date >= from && transaction.Date <= to)
                yield return transaction.Date;
            yield break;
        }

        var last = transaction.EndDate is { } end && end < to ? end : to;
        for (var i = 0; ; i++)
        {
            var date = transaction.Date.AddMonths(i * transaction.IntervalMonths);
            if (date > last)
                yield break;
            if (date >= from)
                yield return date;
        }
    }

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
                if (date < from)
                    continue;
                var change = transaction.Changes.Count == 0 ? null : transaction.ChangeOn(date);
                if (change is { IsSkipped: true })
                    continue;
                occurrences.Add(new Occurrence(date, transaction, change?.Amount ?? transaction.Amount));
            }
        }

        occurrences.Sort((a, b) => a.Date.CompareTo(b.Date));
        return occurrences;
    }

    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    public static int MonthsBetween(DateOnly start, DateOnly end) =>
        (end.Year - start.Year) * 12 + end.Month - start.Month;
}
