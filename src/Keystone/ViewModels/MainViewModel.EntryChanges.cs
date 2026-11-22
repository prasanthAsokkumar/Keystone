using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>One date of a repeating entry to pick when changing a single time.</summary>
public sealed record OccurrenceOption(DateOnly Date, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A single time of a repeating entry that differs from the usual, listed in the entry form.</summary>
public sealed record EntryChangeRow(EntryChange Change, string DateText, string AmountText, string Note)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// Changing one time of a repeating entry: a 13th salary, a bonus, a smaller paycheck or a bill skipped once,
/// without touching every other month.
/// </summary>
public partial class MainViewModel
{
    /// <summary>How far back and ahead the form offers dates to change.</summary>
    private const int ChangeMonthsBack = 12, ChangeMonthsAhead = 24;

    public bool CanChangeOneTime => EditingTransaction is { IsRecurring: true } && NewIsRecurring;

    [ObservableProperty]
    public partial IReadOnlyList<OccurrenceOption> ChangeDateOptions { get; set; } = [];

    [ObservableProperty]
    public partial OccurrenceOption? ChangeDate { get; set; }

    [ObservableProperty]
    public partial decimal? ChangeAmount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeAmountEnabled))]
    public partial bool ChangeSkip { get; set; }

    public bool ChangeAmountEnabled => !ChangeSkip;

    [ObservableProperty]
    public partial string ChangeNote { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntryChanges))]
    public partial IReadOnlyList<EntryChangeRow> EntryChangeRows { get; set; } = [];

    public bool HasEntryChanges => EntryChangeRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangeError))]
    public partial string? ChangeError { get; set; }

    public bool HasChangeError => !string.IsNullOrEmpty(ChangeError);

    partial void OnEditingTransactionChanged(Transaction? value) => RefreshEntryChanges(selectNext: true);

    partial void OnChangeDateChanged(OccurrenceOption? value)
    {
        ChangeError = null;
        if (value is null || EditingTransaction is not { } entry)
            return;
        var change = entry.ChangeOn(value.Date);
        ChangeSkip = change is { IsSkipped: true };
        ChangeAmount = change?.Amount ?? entry.Amount;
        ChangeNote = change?.Note ?? "";
    }

    private void RefreshEntryChanges(bool selectNext = false)
    {
        OnPropertyChanged(nameof(CanChangeOneTime));
        ChangeError = null;
        if (EditingTransaction is not { IsRecurring: true } entry)
        {
            ChangeDateOptions = [];
            EntryChangeRows = [];
            ChangeDate = null;
            return;
        }

        var selected = ChangeDate?.Date;
        ChangeDateOptions = Schedule.RegularDates(entry, Today.AddMonths(-ChangeMonthsBack), Today.AddMonths(ChangeMonthsAhead))
            .Select(date => new OccurrenceOption(date, entry.ChangeOn(date) switch
            {
                { IsSkipped: true } => Loc.F("{0} · skipped", Format.Date(date)),
                { Amount: { } amount } => Loc.F("{0} · {1}", Format.Date(date), Format.Money(amount)),
                _ => Format.Date(date),
            }))
            .ToList();
        EntryChangeRows = entry.Changes
            .OrderBy(c => c.Date)
            .Select(c => new EntryChangeRow(c, Format.Date(c.Date),
                c.Amount is { } amount
                    ? Loc.F("{0} instead of {1}", Format.Money(amount), Format.Money(entry.Amount))
                    : Loc.T("Skipped this time"),
                c.Note))
            .ToList();

        // Keep the picked date, or start at the next time it happens.
        ChangeDate = (selectNext ? null : ChangeDateOptions.FirstOrDefault(o => o.Date == selected))
                     ?? ChangeDateOptions.FirstOrDefault(o => o.Date > Today)
                     ?? ChangeDateOptions.LastOrDefault();
        OnChangeDateChanged(ChangeDate);
    }

    /// <summary>Saves the change to the picked date, or removes it when the amount is back to the usual.</summary>
    [RelayCommand]
    private async Task SaveEntryChangeAsync()
    {
        ChangeError = null;
        if (EditingTransaction is not { IsRecurring: true } entry || ChangeDate is null)
            return;
        if (!ChangeSkip && ChangeAmount is not > 0)
        {
            ChangeError = Loc.T("Enter the amount for this time, or tick “Skip this time”.");
            return;
        }

        var existing = entry.ChangeOn(ChangeDate.Date);
        var backToUsual = !ChangeSkip && ChangeAmount == entry.Amount && string.IsNullOrWhiteSpace(ChangeNote);
        try
        {
            if (backToUsual)
            {
                if (existing is not null)
                {
                    await repository.DeleteAsync<EntryChange>(c => c.Id == existing.Id);
                    entry.Changes.Remove(existing);
                }
            }
            else
            {
                var change = existing ?? new EntryChange { TransactionId = entry.Id, Date = ChangeDate.Date };
                change.Amount = ChangeSkip ? null : ChangeAmount;
                change.Note = ChangeNote.Trim();
                await repository.SaveAsync(change, isNew: existing is null);
                if (existing is null)
                    entry.Changes.Add(change);
            }
        }
        catch (Exception ex)
        {
            ChangeError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        RefreshEntryChanges();
        Recalculate();
    }

    [RelayCommand]
    private async Task RemoveEntryChangeAsync(EntryChangeRow row)
    {
        if (EditingTransaction is not { } entry)
            return;
        try
        {
            await repository.DeleteAsync<EntryChange>(c => c.Id == row.Change.Id);
        }
        catch (Exception ex)
        {
            ChangeError = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        entry.Changes.Remove(row.Change);
        RefreshEntryChanges();
        Recalculate();
    }
}
