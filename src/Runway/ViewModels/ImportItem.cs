using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Runway.Localization;
using Runway.Models;
using Runway.Services;

namespace Runway.ViewModels;

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

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    /// <summary>Index into <see cref="RecurrenceNames"/>, in <see cref="Models.Recurrence"/> order.</summary>
    [ObservableProperty]
    public partial int RecurrenceIndex { get; set; }

    [ObservableProperty]
    public partial string? Category { get; set; }

    public bool IsDuplicate { get; }

    public static IReadOnlyList<string> TypeNames => [Loc.T("Expense"), Loc.T("Income")];

    /// <summary>Index into <see cref="TypeNames"/>.</summary>
    public int TypeIndex
    {
        get => IsIncome ? 1 : 0;
        set => IsIncome = value == 1;
    }

    public IReadOnlyList<string> CategoryOptions => Categories.DisplayOptions(IsIncome);

    public Recurrence Recurrence => RecurrenceIndex is >= 0 and <= (int)Recurrence.Yearly
        ? (Recurrence)RecurrenceIndex
        : Recurrence.OneTime;

    public bool IsValid => Amount is > 0 && !string.IsNullOrWhiteSpace(Description) && Date is not null;

    public Transaction ToTransaction(int? personId) => new()
    {
        Date = DateOnly.FromDateTime(Date ?? DateTime.Today),
        Description = Description.Trim(),
        Category = Categories.Normalize(Category ?? ""),
        Amount = Amount ?? 0,
        Type = IsIncome ? TransactionType.Income : TransactionType.Expense,
        Recurrence = Recurrence,
        PersonId = personId,
    };

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
    public static bool IsAlreadyRecorded(ExtractedEntry entry, IEnumerable<Transaction> existing) =>
        existing.Any(t => t.Amount == entry.Amount && t.IsIncome == entry.IsIncome &&
                          (t.Date == entry.Date || (t.IsRecurring && entry.Recurrence == t.Recurrence &&
                                                    t.Description.Equals(entry.Description, StringComparison.OrdinalIgnoreCase))));
}
