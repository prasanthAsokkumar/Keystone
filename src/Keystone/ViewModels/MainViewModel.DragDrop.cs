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
/// What dropping a dragged row does:
/// <list type="bullet">
/// <item>an entry onto Insurance, Taxes, Income or Expenses (sidebar or tab) changes its category or type;</item>
/// <item>a document onto Insurance, Taxes or Documents decides where it is listed, onto a person whose it is;</item>
/// <item>a policy, contract, task, goal or debt onto another of its list puts it there (the list's own order).</item>
/// </list>
/// Moves between pages can be undone from the notice that follows.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The row being dragged, while a drag is going on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraggingDocument))]
    public partial object? DraggedItem { get; set; }

    /// <summary>While a document is dragged, people to drop it on appear.</summary>
    public bool IsDraggingDocument => DraggedItem is DocumentRow;

    public bool CanDrop(string target, object? targetItem, object item)
    {
        var (kind, value) = target.Split(':', 2) is [var k, var v] ? (k, v) : (target, "");
        switch (item)
        {
            case EntryRow { Transaction: var t } when !t.IsTransfer && !t.IsDebtPayment:
                return kind switch
                {
                    "insurance" => !(t.Type == TransactionType.Expense && Categories.IsInsurance(t.Category)),
                    "taxes" => !(t.Type == TransactionType.Expense && Categories.IsTaxes(t.Category)),
                    "expenses" => t.IsIncome || HasOwnPage(t),
                    "transactions" => HasOwnPage(t),
                    "income" => !t.IsIncome,
                    _ => false,
                };
            case DocumentRow { Document: var d }:
                return kind switch
                {
                    "insurance" => !IsInsuranceDocument(d),
                    "taxes" => !IsTaxDocument(d),
                    "documents" => IsInsuranceDocument(d) || IsTaxDocument(d),
                    "person" => targetItem is PersonOption person && person.Id != d.PersonId,
                    _ => false,
                };
            default:
                return kind == "reorder" && targetItem is not null && targetItem != item && targetItem.GetType() == item.GetType() &&
                       value is "policies" or "contracts" or "tasks" or "goals" or "debts";
        }
    }

    public async Task DropAsync(string target, object? targetItem, object item)
    {
        var (kind, value) = target.Split(':', 2) is [var k, var v] ? (k, v) : (target, "");
        try
        {
            switch (item)
            {
                case EntryRow { Transaction: var t }:
                    await MoveEntryAsync(t, kind);
                    break;
                case DocumentRow { Document: var d } when kind == "person" && targetItem is PersonOption person:
                    await GiveDocumentToAsync(d, person);
                    break;
                case DocumentRow { Document: var d }:
                    await MoveDocumentAsync(d, kind);
                    break;
                default:
                    await ReorderAsync(value, item, targetItem!);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.F("Could not move it: {0}", ex.GetBaseException().Message));
        }
    }

    // ── Entries ─────────────────────────────────────────────────────────────

    private async Task MoveEntryAsync(Transaction entry, string to)
    {
        var (type, category) = (entry.Type, entry.Category);
        string page;
        switch (to)
        {
            case "insurance":
                entry.Type = TransactionType.Expense;
                entry.Category = Categories.IsInsurance(category) ? category : Categories.HealthInsurance;
                page = Loc.T("Insurance");
                break;
            case "taxes":
                entry.Type = TransactionType.Expense;
                entry.Category = Categories.Taxes;
                page = Loc.T("Taxes");
                break;
            case "income":
                entry.Type = TransactionType.Income;
                // An expense category means nothing for money coming in.
                if (!Categories.Income.Contains(category))
                    entry.Category = Categories.Uncategorized;
                page = Loc.T("Income");
                break;
            default: // expenses, transactions
                entry.Type = TransactionType.Expense;
                if (Categories.IsInsurance(category) || Categories.IsTaxes(category) || Categories.Income.Contains(category))
                    entry.Category = Categories.Uncategorized;
                page = Loc.T("Expenses");
                break;
        }

        await repository.UpdateTransactionAsync(entry);
        Recalculate();
        ShowNotice(Loc.F("Moved “{0}” to {1}", entry.Description, page), async () =>
        {
            (entry.Type, entry.Category) = (type, category);
            await repository.UpdateTransactionAsync(entry);
            Recalculate();
        });
    }

    // ── Documents ───────────────────────────────────────────────────────────

    private async Task MoveDocumentAsync(HouseholdDocument document, string to)
    {
        var section = document.Section;
        document.Section = to switch { "insurance" => "Insurance", "taxes" => "Taxes", _ => "None" };
        await repository.UpdateDocumentAsync(document);
        RefreshDocumentRows();
        ShowNotice(Loc.F("Moved “{0}” to {1}", document.Title,
            Loc.T(to switch { "insurance" => "Insurance", "taxes" => "Taxes", _ => "Documents only" })), async () =>
        {
            document.Section = section;
            await repository.UpdateDocumentAsync(document);
            RefreshDocumentRows();
        });

        // A policy with no record under Insurance yet: offer it, filled in, so its notice deadline is reminded.
        if (to == "insurance" && document.Kind == DocumentKind.Policy &&
            !_policies.Any(p => p.DocumentId == document.Id || DuplicateFinder.SameSender(p.Insurer, document.Sender)))
            OpenFollowUp(document, new FollowUp(FollowUpKind.Insurance));
    }

    /// <summary>Makes the document someone else's; its file moves to their folder.</summary>
    private async Task GiveDocumentToAsync(HouseholdDocument document, PersonOption person)
    {
        var (personId, path) = (document.PersonId, document.RelativePath);
        await SetDocumentPersonAsync(document, person.Id);
        ShowNotice(Loc.F("“{0}” is now {1}’s", document.Title, person.ToString()), () => SetDocumentPersonAsync(document, personId, path));
    }

    private async Task SetDocumentPersonAsync(HouseholdDocument document, int? personId, string? backTo = null)
    {
        document.PersonId = personId;
        var name = personId is { } id ? People.FirstOrDefault(p => p.Id == id)?.Name : null;
        document.RelativePath = await Task.Run(() => _documentStore.Refile(document, name));
        await repository.UpdateDocumentAsync(document);
        RefreshDocumentRows();
    }

    // ── Own order ───────────────────────────────────────────────────────────

    /// <summary>Puts a row where it was dropped and keeps the list in that order from now on.</summary>
    private async Task ReorderAsync(string list, object item, object target)
    {
        switch (list)
        {
            case "policies":
                await SaveOrderAsync<InsuranceRow, InsurancePolicy>(InsuranceRows, item, target, r => r.Policy, p => p.Id, (p, o) => p.SortOrder = o);
                break;
            case "contracts":
                await SaveOrderAsync<ContractRow, Contract>(ContractRows, item, target, r => r.Contract, c => c.Id, (c, o) => c.SortOrder = o);
                break;
            case "tasks":
                await SaveOrderAsync<TaskRow, TaskItem>(TaskRows, item, target, r => r.Task, t => t.Id, (t, o) => t.SortOrder = o);
                break;
            case "goals":
                await SaveOrderAsync<GoalRow, SavingsGoal>(GoalRows, item, target, r => r.Goal, g => g.Id, (g, o) => g.SortOrder = o);
                break;
            case "debts":
                await SaveOrderAsync<DebtItemViewModel, Debt>(Debts.ToList(), item, target, d => d.Debt, d => d.Id, (d, o) => d.SortOrder = o);
                break;
        }
        SetSort(list, ManualSort);
    }

    private async Task SaveOrderAsync<TRow, TModel>(IReadOnlyList<TRow> rows, object item, object target,
        Func<TRow, TModel> model, Func<TModel, int> id, Action<TModel, int> setOrder) where TModel : class
    {
        var order = rows.ToList();
        var dragged = (TRow)item;
        var at = order.IndexOf((TRow)target);
        order.Remove(dragged);
        order.Insert(Math.Clamp(at, 0, order.Count), dragged);

        var orders = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++)
        {
            setOrder(model(order[i]), i + 1);
            orders[id(model(order[i]))] = i + 1;
        }
        await repository.SetSortOrdersAsync<TModel>(orders);
    }
}
