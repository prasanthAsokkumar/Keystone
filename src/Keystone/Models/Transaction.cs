using System;
using System.ComponentModel.DataAnnotations.Schema;
using Keystone.Localization;

namespace Keystone.Models;

public enum TransactionType
{
    Income,
    Expense,
}

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum Recurrence
{
    OneTime,
    Monthly,
    Quarterly,
    Yearly,
}

public class Transaction
{
    public int Id { get; set; }

    /// <summary>The date of a one-time entry, or the first occurrence of a monthly one.</summary>
    public DateOnly Date { get; set; }

    public string Description { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>Always positive; <see cref="Type"/> decides the sign.</summary>
    public decimal Amount { get; set; }

    public TransactionType Type { get; set; }

    public Recurrence Recurrence { get; set; }

    /// <summary>Last day a monthly entry can occur on. Null means it repeats indefinitely.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>The household member this entry belongs to. Null means shared by the household.</summary>
    public int? PersonId { get; set; }

    /// <summary>
    /// Generated from a debt's repayment plan rather than entered by the user. Never stored.
    /// </summary>
    [NotMapped]
    public bool IsDebtPayment { get; init; }

    public bool IsIncome => Type == TransactionType.Income;

    /// <summary>Repeats on a schedule: monthly, quarterly or yearly.</summary>
    public bool IsRecurring => Recurrence != Recurrence.OneTime;

    /// <summary>Months between occurrences, or 0 for a one-time entry.</summary>
    public int IntervalMonths => Recurrence switch
    {
        Recurrence.Monthly => 1,
        Recurrence.Quarterly => 3,
        Recurrence.Yearly => 12,
        _ => 0,
    };

    public decimal SignedAmount => IsIncome ? Amount : -Amount;

    /// <summary>What a recurring entry costs or brings in per month on average, e.g. a yearly 1,200 is 100.</summary>
    public decimal SignedMonthlyAmount => IsRecurring ? SignedAmount / IntervalMonths : 0;

    public bool CanStop =>
        IsRecurring && (EndDate is null || EndDate > DateOnly.FromDateTime(DateTime.Today));

    public string RecurrenceName => Recurrence switch
    {
        Recurrence.Monthly => Loc.T("Monthly"),
        Recurrence.Quarterly => Loc.T("Every 3 months"),
        Recurrence.Yearly => Loc.F("Yearly in {0:MMMM}", Date),
        _ => Loc.T("One-time"),
    };

    public string ScheduleText => (Recurrence, EndDate) switch
    {
        (Recurrence.OneTime, _) => "",
        (_, null) => RecurrenceName,
        (_, { } end) => Loc.F("{0} until {1:MMM yyyy}", RecurrenceName, end),
    };
}
