using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>One entry found in an imported document, which the user can change or leave out before adding.</summary>
public partial class ImportItem : ViewModelBase
{
    public static IReadOnlyList<string> RecurrenceNames =>
        [Loc.T("Once"), Loc.T("Monthly"), Loc.T("Every 3 months"), Loc.T("Yearly")];

    public ImportItem(ExtractedEntry entry, bool isDuplicate)
    {
        Description = entry.Description;
        Amount = entry.Amount;
        IsIncome = entry.IsIncome;
        Date = entry.Date.ToDateTime(TimeOnly.MinValue);
        RecurrenceIndex = (int)entry.Recurrence;
        Category = MatchCategory(entry.Category, entry.IsIncome);
        IsDuplicate = isDuplicate;
        // Probably entered before, e.g. when importing an overlapping bank statement.
        IsSelected = !isDuplicate;
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial decimal? Amount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryOptions), nameof(TypeIndex))]
    public partial bool IsIncome { get; set; }

    /// <summary>Money moved to or from another of the household's accounts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeIndex), nameof(IsNotTransfer))]
    public partial bool IsTransfer { get; set; }

    public bool IsNotTransfer => !IsTransfer;

    /// <summary>For a transfer: the household's other account.</summary>
    [ObservableProperty]
    public partial AccountOption? CounterAccount { get; set; }

    /// <summary>The bank's reference, so the booking isn't imported twice.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Money left the account (a debit on the statement).</summary>
    public bool IsOutgoing { get; init; }

    /// <summary>Transfers are offered for bank statements when there are several accounts.</summary>
    public bool AllowsTransfer { get; init; }

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    /// <summary>Index into <see cref="RecurrenceNames"/>, in <see cref="Models.Recurrence"/> order.</summary>
    [ObservableProperty]
    public partial int RecurrenceIndex { get; set; }

    [ObservableProperty]
    public partial string? Category { get; set; }

    public bool IsDuplicate { get; }

    public IReadOnlyList<string> TypeNames =>
        AllowsTransfer ? [Loc.T("Expense"), Loc.T("Income"), Loc.T("Transfer")] : [Loc.T("Expense"), Loc.T("Income")];

    /// <summary>Index into <see cref="TypeNames"/>.</summary>
    public int TypeIndex
    {
        get => IsTransfer ? 2 : IsIncome ? 1 : 0;
        set
        {
            IsTransfer = value == 2;
            if (value != 2)
                IsIncome = value == 1;
        }
    }

    public IReadOnlyList<string> CategoryOptions => Categories.DisplayOptions(IsIncome);

    public Recurrence Recurrence => RecurrenceIndex is >= 0 and <= (int)Recurrence.Yearly
        ? (Recurrence)RecurrenceIndex
        : Recurrence.OneTime;

    public bool IsValid => Amount is > 0 && !string.IsNullOrWhiteSpace(Description) && Date is not null;

    /// <param name="accountId">The account the statement or document is for; null for the main account.</param>
    public Transaction ToTransaction(int? personId, int? accountId = null)
    {
        var transfer = IsTransfer && CounterAccount?.Account is not null;
        return new Transaction
        {
            Date = DateOnly.FromDateTime(Date ?? DateTime.Today),
            Description = Description.Trim(),
            Category = transfer ? "" : Categories.Normalize(Category ?? ""),
            Amount = Amount ?? 0,
            Type = transfer ? TransactionType.Transfer : IsIncome ? TransactionType.Income : TransactionType.Expense,
            Recurrence = Recurrence,
            PersonId = personId,
            // A transfer goes out of the statement's account, or into it from the other one.
            AccountId = transfer && !IsOutgoing ? CounterAccount!.Account!.Id : accountId,
            ToAccountId = transfer ? IsOutgoing ? CounterAccount!.Account!.Id : accountId : null,
            ExternalId = ExternalId,
        };
    }

    /// <summary>Uses the app's spelling of a suggested category; keeps anything else as the model wrote it.</summary>
    private static string? MatchCategory(string category, bool isIncome)
    {
        if (string.IsNullOrWhiteSpace(category))
            return null;
        var known = isIncome ? Categories.Income : Categories.Expense;
        var match = known.FirstOrDefault(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        return match is null ? category : Categories.Display(match);
    }

    /// <summary>Same amount and direction as an existing entry, on the same date or as the same repeating bill.</summary>
    /// <summary>
    /// An entry with the same amount and direction already happens within 3 days of this date (a one-time one,
    /// or a time of a repeating one), or repeats just as often. Then the document was most likely read before.
    /// </summary>
    public static bool IsAlreadyRecorded(ExtractedEntry entry, IEnumerable<Transaction> existing) =>
        existing.Any(t => !t.IsTransfer && t.Amount == entry.Amount && t.IsIncome == entry.IsIncome &&
                          ((entry.Recurrence != Recurrence.OneTime && t.Recurrence == entry.Recurrence) ||
                           Schedule.RegularDates(t, entry.Date.AddDays(-3), entry.Date.AddDays(3)).Any()));
}
