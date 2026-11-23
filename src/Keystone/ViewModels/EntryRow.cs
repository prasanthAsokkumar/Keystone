using System;
using System.Globalization;
using System.Linq;
using Keystone.Models;
using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>A choice in a person picker. <see cref="Id"/> is null for the shared household.</summary>
public sealed record PersonOption(int? Id, string Name, bool IsEveryone = false)
{
    public const string SharedName = "Shared";

    public static PersonOption Everyone { get; } = new(null, "Everyone", IsEveryone: true);

    public static PersonOption Shared { get; } = new(null, SharedName);

    public bool Matches(Transaction transaction) => IsEveryone || transaction.PersonId == Id;

    /// <summary>"Everyone" and "Shared" are shown in the app's language; people's names as they are.</summary>
    public override string ToString() => Id is null ? Loc.T(Name) : Name;
}

/// <summary>Where an entry stands today: paid now, being replaced, starting later, or over.</summary>
public enum EntryPhase { Current, Ending, Upcoming, Ended }

/// <summary>One row of the Entries table.</summary>
public sealed record EntryRow(Transaction Transaction, string PersonName, string AccountText = "")
{
    public string DateText => Format.Date(Transaction.Date);

    public string Description => Transaction.Description;

    public string Category => Categories.Display(Transaction.Category);

    public string ScheduleText => Transaction.IsRecurring ? Transaction.ScheduleText : Loc.T("One-time");

    /// <summary>Category (or "Transfer"), schedule and account, shown under the description.</summary>
    public string DetailText => string.Join(" · ",
        new[] { Transaction.IsTransfer ? Loc.T("Transfer") : Category, ScheduleText, ChangesText, AccountText }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>"1 time changed" when single times differ from the usual.</summary>
    public string ChangesText => Transaction.IsRecurring && Transaction.Changes.Count > 0
        ? Loc.Plural(Transaction.Changes.Count, "{0} time changed", "{0} times changed")
        : "";

    public bool IsTransfer => Transaction.IsTransfer;

    public bool IsIncome => Transaction.IsIncome;

    /// <summary>Without a sign: income, expenses and transfers each have their own tab.</summary>
    public string AmountText => Transaction.Amount.ToString("#,##0.00", CultureInfo.CurrentCulture) +
        Transaction.Recurrence switch
        {
            Recurrence.Monthly => Loc.T(" / mo"),
            Recurrence.Quarterly => Loc.T(" / qtr"),
            Recurrence.Yearly => Loc.T(" / yr"),
            _ => "",
        };

    public bool CanStop => Transaction.CanStop;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>
    /// A repeating entry is current, ending (an end date still ahead, e.g. replaced by a new premium), upcoming
    /// (starts later, e.g. next year's premium) or ended. A one-time entry is upcoming until its date, then ended.
    /// </summary>
    public EntryPhase Phase => Transaction switch
    {
        { IsRecurring: false } t => t.Date >= Today ? EntryPhase.Upcoming : EntryPhase.Ended,
        { EndDate: { } end } when end < Today => EntryPhase.Ended,
        { Date: var start } when start > Today => EntryPhase.Upcoming,
        { EndDate: not null } => EntryPhase.Ending,
        _ => EntryPhase.Current,
    };

    /// <summary>"Since 01 Jan 2026", "Until 31 Dec 2026", "From 01 Jan 2027", "Ended 31 Dec 2025", or for a one-time payment "Due …"/"Paid …".</summary>
    public string PhaseText => (Phase, Transaction.IsRecurring) switch
    {
        (EntryPhase.Current, _) => Loc.F("Since {0}", Format.Date(Transaction.Date)),
        (EntryPhase.Ending, _) => Loc.F("Until {0}", Format.Date(Transaction.EndDate!.Value)),
        (EntryPhase.Upcoming, true) => Loc.F("From {0}", Format.Date(Transaction.Date)),
        (EntryPhase.Upcoming, false) => Loc.F("Due {0}", Format.Date(Transaction.Date)),
        (EntryPhase.Ended, true) => Loc.F("Ended {0}", Format.Date(Transaction.EndDate!.Value)),
        _ => Loc.F("Paid {0}", Format.Date(Transaction.Date)),
    };

    public bool IsCurrent => Phase == EntryPhase.Current;

    public bool IsEnding => Phase == EntryPhase.Ending;

    public bool IsUpcoming => Phase == EntryPhase.Upcoming;

    public bool IsEnded => Phase == EntryPhase.Ended;

    public bool IsShared => Transaction.PersonId is null;
}
