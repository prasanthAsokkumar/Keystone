using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum TransactionType
{
    Income,
    Expense,

    /// <summary>Money moved between two of the household's accounts; neither income nor spending.</summary>
    Transfer,
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

    /// <summary>The account the money goes out of or into. Null means the main account.</summary>
    public int? AccountId { get; set; }

    /// <summary>For a transfer, the account the money goes to.</summary>
    public int? ToAccountId { get; set; }

    /// <summary>The bank's reference for an imported booking, so the same statement can't be imported twice.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Single occurrences of a repeating entry that differ from the usual: another amount, or skipped.</summary>
    public List<EntryChange> Changes { get; set; } = [];

    /// <summary>The change made to the occurrence on <paramref name="date"/>, if any.</summary>
    public EntryChange? ChangeOn(DateOnly date)
    {
        foreach (var change in Changes)
            if (change.Date == date)
                return change;
        return null;
    }

    /// <summary>
    /// Generated from a debt's repayment plan rather than entered by the user. Never stored.
    /// </summary>
    [NotMapped]
    public bool IsDebtPayment { get; init; }

    public bool IsIncome => Type == TransactionType.Income;

    /// <summary>Money borrowed under a debt: it adds to cash but isn't earned.</summary>
    public bool IsBorrowing => IsDebtPayment && IsIncome;

    public bool IsTransfer => Type == TransactionType.Transfer;

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

    /// <summary>The effect on the household's cash: transfers move money but don't change it.</summary>
    public decimal SignedAmount => IsIncome ? Amount : IsTransfer ? 0 : -Amount;

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

/// <summary>
/// One occurrence of a repeating entry that differs from the usual, e.g. a 13th salary in December,
/// a smaller paycheck, or a bill skipped once.
/// </summary>
public class EntryChange
{
    public int Id { get; set; }

    public int TransactionId { get; set; }

    /// <summary>The regular date of the occurrence this changes.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The amount this time (positive). Null means it doesn't happen this time.</summary>
    public decimal? Amount { get; set; }

    public string Note { get; set; } = "";

    public bool IsSkipped => Amount is null;
}
