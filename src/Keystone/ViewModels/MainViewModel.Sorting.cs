using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>A column a list can be sorted by. <see cref="DescendingFirst"/>: amounts start with the largest.</summary>
public sealed record SortKey<T>(string Key, Func<T, IComparable?> Value, bool DescendingFirst = false);

/// <summary>
/// Every list sorts by clicking a column header; clicking it again reverses the order. Each list remembers its
/// order (saved in settings). Lists that can be arranged by dragging use the "manual" order until a header is clicked.
/// </summary>
public partial class MainViewModel
{
    public const string ManualSort = "manual";

    /// <summary>List name → "key" (ascending) or "-key" (descending), e.g. "transactions" → "-amount".</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, string> ListSorts { get; set; } = new Dictionary<string, string>();

    private void LoadListSorts(string json)
    {
        try
        {
            ListSorts = string.IsNullOrWhiteSpace(json)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            ListSorts = new Dictionary<string, string>();
        }
    }

    private string? SortOf(string list) => ListSorts.TryGetValue(list, out var sort) ? sort : null;

    /// <summary>Clicking a header: sort by that column, or reverse it when it already is.</summary>
    /// <param name="target">"list:key", e.g. "transactions:amount".</param>
    [RelayCommand]
    private void SortList(string target)
    {
        var (list, key) = target.Split(':', 2) is [var l, var k] ? (l, k) : (target, "");
        var current = SortOf(list);
        var next = current == key ? "-" + key
            : current == "-" + key ? key
            : DescendingFirst(list, key) ? "-" + key : key;
        SetSort(list, next);
    }

    private void SetSort(string list, string sort)
    {
        ListSorts = new Dictionary<string, string>(ListSorts) { [list] = sort };
        RefreshSortedList(list);
        if (!_isLoading)
            _ = SaveListSortsAsync();
    }

    private async Task SaveListSortsAsync()
    {
        try
        {
            await repository.SetListSortsAsync(JsonSerializer.Serialize(ListSorts));
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    /// <summary>Amounts and dates of money start with the largest or newest; names and due dates with A or the soonest.</summary>
    private static bool DescendingFirst(string list, string key) =>
        key is "amount" or "premium" or "cost" or "price" or "saved" or "target" or "balance" or "outstanding" ||
        key == "date" && list is "transactions" or "documents" or "premiums" or "taxPayments" or "insurancePapers" or "taxPapers";

    private void RefreshSortedList(string list)
    {
        switch (list)
        {
            case "transactions": RefreshEntryRows(); break;
            case "documents": RefreshDocumentRows(); break;
            case "tasks": Tasks.RefreshTaskRows(); break;
            case "appointments": Appointments.RefreshAppointmentRows(); break;
            case "contracts": Deadlines.RefreshContracts(); break;
            case "dates": Deadlines.RefreshImportantDates(); break;
            case "warranties": Deadlines.RefreshWarranties(); break;
            case "goals": Planning.RefreshGoals(); break;
            case "pensionAccounts": Retirement.RefreshRetirement(); break;
            case "policies": RefreshInsurance(); break;
            case "debts": SortDebts(); break;
            case "assets": Assets.RefreshAssets(); break;
            case "medicines" or "sickLeaves": RefreshHousehold(); break;
            default: RefreshOwnPageLists(); break; // premiums, insurance papers, tax payments, tax papers
        }
    }

    /// <summary>
    /// The list in its chosen order. Without a choice, or with an unknown column, the list keeps the order it was
    /// built in. "manual" orders by where rows were dragged to, keeping the built order among rows never moved.
    /// </summary>
    internal IReadOnlyList<T> Sorted<T>(string list, IEnumerable<T> items, Func<T, int>? manualOrder, params SortKey<T>[] keys)
    {
        var sort = SortOf(list);
        if (sort is null)
            return manualOrder is null ? items.ToList() : items.OrderBy(manualOrder).ToList();
        if (sort == ManualSort)
            return manualOrder is null ? items.ToList() : items.OrderBy(manualOrder).ToList();

        var descending = sort.StartsWith('-');
        var key = keys.FirstOrDefault(k => k.Key == sort.TrimStart('-'));
        if (key is null)
            return items.ToList();
        var comparer = Comparer<IComparable?>.Create((a, b) => CompareValues(a, b, descending));
        return items.OrderBy(key.Value, comparer).ToList();
    }

    /// <summary>Text compares as people read it (ignoring case); missing values go last either way round.</summary>
    private static int CompareValues(IComparable? a, IComparable? b, bool descending) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string x, string y) => (descending ? -1 : 1) * string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase),
        _ => (descending ? -1 : 1) * a.CompareTo(b),
    };

    /// <summary>Columns of entry lists: Transactions, premiums and tax payments.</summary>
    private static readonly SortKey<EntryRow>[] EntrySortKeys =
    [
        new("description", r => r.Description),
        new("person", r => r.PersonName),
        new("date", r => r.Transaction.Date, DescendingFirst: true),
        new("amount", r => r.Transaction.IsRecurring ? r.Transaction.Amount / r.Transaction.IntervalMonths : r.Transaction.Amount, DescendingFirst: true),
    ];

    /// <summary>Columns of document lists: Documents, insurance papers and tax papers.</summary>
    private static readonly SortKey<DocumentRow>[] DocumentSortKeys =
    [
        new("title", r => r.Title),
        new("person", r => r.PersonName),
        new("date", r => r.Document.Date, DescendingFirst: true),
        new("amount", r => r.HasAmount ? r.Document.Amount : null, DescendingFirst: true),
    ];

    /// <summary>Debts are cards in a collection; they are put in the chosen order where they are.</summary>
    private void SortDebts()
    {
        var sorted = Sorted("debts", Debts.OrderBy(d => d.Debt.Id), d => d.Debt.SortOrder,
            new SortKey<DebtItemViewModel>("name", d => d.Name),
            new SortKey<DebtItemViewModel>("outstanding", d => d.Outstanding, DescendingFirst: true),
            new SortKey<DebtItemViewModel>("payoff", d => d.Plan?.PayoffDate));
        for (var i = 0; i < sorted.Count; i++)
        {
            var from = Debts.IndexOf(sorted[i]);
            if (from != i)
                Debts.Move(from, i);
        }
    }
}
