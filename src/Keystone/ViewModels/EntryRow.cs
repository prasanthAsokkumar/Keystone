using System.Globalization;
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

/// <summary>One row of the Entries table.</summary>
public sealed record EntryRow(Transaction Transaction, string PersonName)
{
    public string DateText => Format.Date(Transaction.Date);

    public string Description => Transaction.Description;

    public string Category => Categories.Display(Transaction.Category);

    public string ScheduleText => Transaction.IsRecurring ? Transaction.ScheduleText : Loc.T("One-time");

    /// <summary>Category and schedule, shown under the description.</summary>
    public string DetailText => string.IsNullOrWhiteSpace(Category) ? ScheduleText : $"{Category} · {ScheduleText}";

    public bool IsIncome => Transaction.IsIncome;

    public string AmountText => Transaction.SignedAmount.ToString("+#,##0.00;-#,##0.00", CultureInfo.CurrentCulture) +
        Transaction.Recurrence switch
        {
            Recurrence.Monthly => Loc.T(" / mo"),
            Recurrence.Quarterly => Loc.T(" / qtr"),
            Recurrence.Yearly => Loc.T(" / yr"),
            _ => "",
        };

    public bool CanStop => Transaction.CanStop;

    public bool IsShared => Transaction.PersonId is null;
}
