using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// Bills paid in parts: a tax bill or another big bill that the sender agreed to take in instalments. In the bill's
/// form the parts are worked out (equal parts, every month or every few months) and can then be changed one by one.
/// Each part is reminded on its own date, ticked off on its own, and has its own expense entry when the bill is in
/// the expenses, so the runway sees the money leave when it really does.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Pay the bill in the form in parts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateDocumentEntry), nameof(DocumentCreateEntryText), nameof(CanTickDocumentPaid))]
    public partial bool DocumentPaysInInstalments { get; set; }

    [ObservableProperty]
    public partial decimal? InstalmentCount { get; set; } = 3;

    [ObservableProperty]
    public partial decimal? InstalmentEveryMonths { get; set; } = 1;

    [ObservableProperty]
    public partial DateTime? InstalmentFirstDate { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<InstalmentRow> InstalmentRows { get; set; } = [];

    [ObservableProperty]
    public partial string InstalmentSummaryText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstalmentDifference))]
    public partial string InstalmentDifferenceText { get; set; } = "";

    public bool HasInstalmentDifference => InstalmentDifferenceText != "";

    /// <summary>"Already paid" is for a bill paid at once; parts are ticked one by one.</summary>
    public bool CanTickDocumentPaid => !DocumentPaysInInstalments;

    public bool CanPayDocumentInInstalments => DocumentIsPayable && !DocumentIsPaid;

    public string DocumentCreateEntryText => DocumentPaysInInstalments
        ? Loc.T("Add each instalment to expenses on its date, so your runway includes them")
        : Loc.T("Add to expenses on the due date, so your runway includes it");

    /// <summary>While a saved plan is put into the form, nothing is worked out again.</summary>
    private bool _loadingInstalments;

    partial void OnDocumentPaysInInstalmentsChanged(bool value)
    {
        if (!value || _loadingInstalments)
            return;
        DocumentIsPaid = false;
        InstalmentFirstDate ??= DocumentDueDate ?? DateTime.Today;
        if (InstalmentRows.Count == 0)
            WorkOutInstalments();
    }

    partial void OnInstalmentCountChanged(decimal? value) => WorkOutInstalments();

    partial void OnInstalmentEveryMonthsChanged(decimal? value) => WorkOutInstalments();

    partial void OnInstalmentFirstDateChanged(DateTime? value) => WorkOutInstalments();

    partial void OnDocumentAmountChanged(decimal? value) => WorkOutInstalments();

    partial void OnDocumentIsPaidChanged(bool value) => OnPropertyChanged(nameof(CanPayDocumentInInstalments));

    /// <summary>
    /// Equal parts from the first date on. Parts already paid stay as they are; the rest of the bill is shared
    /// among the others, which keep their place (and expense entry).
    /// </summary>
    private void WorkOutInstalments()
    {
        if (_loadingInstalments || !DocumentPaysInInstalments)
            return;
        var paid = InstalmentRows.Where(r => r.IsPaid).ToList();
        var open = InstalmentRows.Where(r => !r.IsPaid).ToList();
        var count = Math.Max(Math.Clamp((int)(InstalmentCount ?? 3), 2, InstalmentPlan.MaxParts), paid.Count + 1);
        var every = Math.Clamp((int)(InstalmentEveryMonths ?? 1), 1, 12);
        var first = DateOnly.FromDateTime(InstalmentFirstDate ?? DocumentDueDate ?? DateTime.Today);
        var left = Math.Max(0, (DocumentAmount ?? 0) - paid.Sum(r => r.Amount ?? 0));
        var parts = InstalmentPlan.Split(left, count - paid.Count, first, every);

        var rows = new List<InstalmentRow>(paid);
        for (var i = 0; i < parts.Count; i++)
        {
            var row = i < open.Count ? open[i] : new InstalmentRow(RefreshInstalmentSummary);
            row.Load(InstalmentPlan.DueDate(first, paid.Count + i, every), parts[i].Amount);
            rows.Add(row);
        }
        SetInstalmentRows(rows);
    }

    private void SetInstalmentRows(List<InstalmentRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            rows[i].Number = i + 1;
        InstalmentRows = rows;
        RefreshInstalmentSummary();
    }

    private void RefreshInstalmentSummary()
    {
        var total = InstalmentRows.Sum(r => r.Amount ?? 0);
        InstalmentSummaryText = InstalmentRows.Count == 0
            ? ""
            : Loc.F("{0} instalments · {1} in all", InstalmentRows.Count, Format.Money(total));
        var difference = total - (DocumentAmount ?? 0);
        InstalmentDifferenceText = difference switch
        {
            > 0 => Loc.F("{0} more than the bill, e.g. interest or fees.", Format.Money(difference)),
            < 0 => Loc.F("{0} less than the bill. Add it to a part, or change the amount to pay.", Format.Money(-difference)),
            _ => "",
        };
    }

    /// <summary>Puts a bill's saved plan into its form.</summary>
    private void LoadInstalmentForm(HouseholdDocument document)
    {
        _loadingInstalments = true;
        try
        {
            var parts = document.InstalmentsInOrder;
            DocumentPaysInInstalments = parts.Count > 0;
            InstalmentCount = parts.Count > 0 ? parts.Count : 3;
            InstalmentEveryMonths = parts.Count > 1
                ? Math.Clamp((parts[1].DueDate.Year - parts[0].DueDate.Year) * 12 + parts[1].DueDate.Month - parts[0].DueDate.Month, 1, 12)
                : 1;
            InstalmentFirstDate = (parts.Count > 0 ? parts[0].DueDate : document.DueDate)?.ToDateTime(TimeOnly.MinValue);
            SetInstalmentRows(parts.Select(p => new InstalmentRow(RefreshInstalmentSummary, p)).ToList());
        }
        finally
        {
            _loadingInstalments = false;
        }
    }

    private void ResetInstalmentForm()
    {
        _loadingInstalments = true;
        DocumentPaysInInstalments = false;
        InstalmentCount = 3;
        InstalmentEveryMonths = 1;
        InstalmentFirstDate = null;
        InstalmentRows = [];
        InstalmentSummaryText = "";
        InstalmentDifferenceText = "";
        _loadingInstalments = false;
    }

    /// <summary>The parts as they are in the form, or an error to show.</summary>
    private (List<BillInstalment> Parts, string? Error) InstalmentsFromForm(int documentId)
    {
        if (InstalmentRows.Count < 2)
            return ([], Loc.T("A plan needs at least two instalments."));
        if (InstalmentRows.Any(r => r.DueDate is null || r.Amount is not > 0))
            return ([], Loc.T("Give every instalment a date and an amount."));
        var parts = InstalmentRows.Select(r => new BillInstalment
        {
            Id = r.Id,
            DocumentId = documentId,
            DueDate = DateOnly.FromDateTime(r.DueDate!.Value),
            Amount = r.Amount!.Value,
            PaidOn = r.IsPaid ? r.PaidOn ?? Today : null,
            TransactionId = r.TransactionId,
        }).ToList();
        return (parts, null);
    }

    private static bool HasInstalmentEntries(HouseholdDocument? document) =>
        document?.Instalments.Any(i => i.TransactionId is not null) == true;

    /// <summary>
    /// The category and account the bill's payments are booked in: those of its entry so far (moved to Taxes, say),
    /// else Taxes for a tax bill and Bills for any other.
    /// </summary>
    private (string Category, int? AccountId) BillCategory(HouseholdDocument? existing, HouseholdDocument document)
    {
        var ids = (existing?.Instalments.Select(i => i.TransactionId) ?? []).Prepend(existing?.TransactionId).OfType<int>().ToList();
        var entry = Transactions.FirstOrDefault(t => ids.Contains(t.Id));
        return entry is not null
            ? (entry.Category, entry.AccountId)
            : (IsTaxDocument(document) ? Categories.Taxes : "Bills", null);
    }

    private static string BillDescription(HouseholdDocument document) =>
        document.Sender == "" ? document.Title : $"{document.Sender}: {document.Title}";

    /// <summary>
    /// Gives each part of a bill its own one-time expense, in place of the bill's single one (keeping its category and
    /// account), or takes them away again. Entries of parts taken out of the plan are deleted.
    /// </summary>
    private async Task SyncInstalmentEntriesAsync(HouseholdDocument? existing, HouseholdDocument document, bool wantEntries)
    {
        Transaction? Find(int? id) => id is { } x ? Transactions.FirstOrDefault(t => t.Id == x) : null;
        var like = BillCategory(existing, document);

        HashSet<int> keep = wantEntries ? document.Instalments.Select(i => i.TransactionId).OfType<int>().ToHashSet() : [];
        var before = (existing?.Instalments.Select(i => i.TransactionId) ?? []).Append(existing?.TransactionId).OfType<int>();
        foreach (var id in before.Where(id => !keep.Contains(id)).Distinct().ToList())
        {
            await repository.DeleteTransactionAsync(id);
            if (Find(id) is { } gone)
                Transactions.Remove(gone);
        }
        if (document.HasInstalments)
            document.TransactionId = null;
        if (!wantEntries)
        {
            foreach (var part in document.Instalments)
                part.TransactionId = null;
            Recalculate();
            return;
        }

        var parts = document.InstalmentsInOrder;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var description = $"{BillDescription(document)} ({i + 1}/{parts.Count})";
            if (Find(part.TransactionId) is { } entry)
            {
                (entry.Date, entry.Amount, entry.Description, entry.PersonId) = (part.DueDate, part.Amount, description, document.PersonId);
                await repository.UpdateTransactionAsync(entry);
                continue;
            }
            var added = await repository.AddTransactionAsync(new Transaction
            {
                Date = part.DueDate,
                Description = description,
                Category = like.Category,
                Amount = part.Amount,
                Type = TransactionType.Expense,
                PersonId = document.PersonId,
                AccountId = like.AccountId,
            });
            InsertSorted(added);
            part.TransactionId = added.Id;
        }
        Recalculate();
    }

    /// <summary>Ticks off the next part of a bill paid in instalments; the bill is paid with its last part.</summary>
    private async Task MarkNextInstalmentPaidAsync(HouseholdDocument document)
    {
        if (document.NextInstalment is not { } part)
            return;
        var number = document.NextInstalmentNumber;
        var paidOn = document.PaidOn;
        part.PaidOn = Today;
        if (document.Instalments.All(i => i.IsPaid))
            document.PaidOn = Today;
        try
        {
            await repository.UpdateDocumentAsync(document);
        }
        catch (Exception ex)
        {
            (part.PaidOn, document.PaidOn) = (null, paidOn);
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        RefreshDocumentRows();
        var text = document.IsPaid
            ? Loc.F("Paid the last instalment of “{0}”", document.Title)
            : Loc.F("Paid instalment {0} of {1} · {2} left", number, document.Instalments.Count, Format.Money(document.AmountLeft));
        ShowNotice(text, async () =>
        {
            (part.PaidOn, document.PaidOn) = (null, paidOn);
            await repository.UpdateDocumentAsync(document);
            RefreshDocumentRows();
        });
    }
}

/// <summary>One part in the bill form's instalment list; its date and amount can be changed, and it can be ticked paid.</summary>
public sealed partial class InstalmentRow(Action changed) : ObservableObject
{
    public InstalmentRow(Action changed, BillInstalment saved) : this(changed)
    {
        Id = saved.Id;
        TransactionId = saved.TransactionId;
        PaidOn = saved.PaidOn;
        _loading = true;
        DueDate = saved.DueDate.ToDateTime(TimeOnly.MinValue);
        Amount = saved.Amount;
        IsPaid = saved.IsPaid;
        _loading = false;
    }

    private bool _loading;

    public int Id { get; }

    public int? TransactionId { get; }

    public DateOnly? PaidOn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    public partial int Number { get; set; }

    public string NumberText => $"{Number}.";

    [ObservableProperty]
    public partial DateTime? DueDate { get; set; }

    [ObservableProperty]
    public partial decimal? Amount { get; set; }

    [ObservableProperty]
    public partial bool IsPaid { get; set; }

    internal void Load(DateOnly date, decimal amount)
    {
        _loading = true;
        DueDate = date.ToDateTime(TimeOnly.MinValue);
        Amount = amount;
        _loading = false;
    }

    partial void OnAmountChanged(decimal? value)
    {
        if (!_loading)
            changed();
    }

    partial void OnIsPaidChanged(bool value)
    {
        PaidOn = value ? PaidOn ?? DateOnly.FromDateTime(DateTime.Today) : null;
    }
}
