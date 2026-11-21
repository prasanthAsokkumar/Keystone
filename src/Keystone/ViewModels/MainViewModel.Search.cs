using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>A search hit; <see cref="Open"/> shows it.</summary>
public sealed record SearchResult(string Kind, string Title, string Detail, Action Open)
{
    public bool HasDetail => Detail != "";
}

/// <summary>Searching entries, documents, tasks, notes, insurance and accounts from one box.</summary>
public partial class MainViewModel
{
    private const int MaxResultsPerKind = 5;

    [ObservableProperty]
    public partial string GlobalSearch { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchResults), nameof(ShowsNoSearchResults))]
    public partial IReadOnlyList<SearchResult> SearchResults { get; set; } = [];

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool ShowsNoSearchResults => SearchResults.Count == 0 && GlobalSearch.Trim().Length >= 2;

    partial void OnGlobalSearchChanged(string value)
    {
        SearchResults = Search(value);
        OnPropertyChanged(nameof(ShowsNoSearchResults));
    }

    /// <summary>Results for a query of two or more characters, grouped by kind, a few of each.</summary>
    public IReadOnlyList<SearchResult> Search(string query)
    {
        query = query.Trim();
        if (query.Length < 2)
            return [];

        bool Has(params string?[] texts) => texts.Any(t => t?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
        var results = new List<SearchResult>();

        results.AddRange(Transactions
            .Where(t => Has(t.Description, Categories.Display(t.Category), t.Category))
            .OrderByDescending(t => t.Date)
            .Take(MaxResultsPerKind)
            .Select(t => new SearchResult(Loc.T("Entry"), t.Description,
                $"{Format.Date(t.Date)} · {(t.IsTransfer ? Format.Money(t.Amount) : Format.SignedMoney(t.SignedAmount))}",
                () => EditTransactionCommand.Execute(t))));

        results.AddRange(Documents
            .Where(d => Has(d.Title, d.Sender, d.Notes))
            .OrderByDescending(d => d.Date)
            .Take(MaxResultsPerKind)
            .Select(d => new SearchResult(HouseholdDocument.KindName(d.Kind), d.Title,
                string.Join(" · ", new[] { d.Sender, Format.Date(d.Date) }.Where(p => p != "")),
                () =>
                {
                    CurrentPage = AppPage.Documents;
                    EditDocumentCommand.Execute(d);
                })));

        results.AddRange(_tasks
            .Where(t => Has(t.Title, t.Notes))
            .OrderBy(t => t.IsDone)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .Take(MaxResultsPerKind)
            .Select(t => new SearchResult(Loc.T("Task"), t.Title, new TaskRow(t, PersonName(t.PersonId), Today).DueText,
                () =>
                {
                    CurrentPage = AppPage.Tasks;
                    EditTaskCommand.Execute(t);
                })));

        results.AddRange(_notes
            .Where(n => Has(n.Title, n.Body, n.Project))
            .OrderByDescending(n => n.UpdatedAt)
            .Take(MaxResultsPerKind)
            .Select(n => new SearchResult(Loc.T("Note"), n.Title.Trim() == "" ? Loc.T("Untitled note") : n.Title,
                n.Project,
                () =>
                {
                    CurrentPage = AppPage.Notes;
                    NoteSearch = "";
                    NoteProjectFilter = AllProjects;
                    SelectedNoteItem = NoteItems.FirstOrDefault(i => i.Note == n);
                })));

        results.AddRange(_policies
            .Where(p => Has(p.Insurer, p.PolicyNumber, p.Notes, InsurancePolicy.KindName(p.Kind)))
            .Take(MaxResultsPerKind)
            .Select(p => new SearchResult(Loc.T("Insurance"), p.Insurer, InsurancePolicy.KindName(p.Kind),
                () =>
                {
                    CurrentPage = AppPage.Insurance;
                    EditInsuranceCommand.Execute(p);
                })));

        results.AddRange(_pensionAccounts
            .Where(a => Has(a.Name, a.Notes))
            .Take(MaxResultsPerKind)
            .Select(a => new SearchResult(Loc.T("Retirement"), a.Name, PensionAccount.PillarName(a.Pillar),
                () =>
                {
                    CurrentPage = AppPage.Retirement;
                    EditPensionAccountCommand.Execute(a);
                })));

        results.AddRange(BankAccounts
            .Where(a => Has(a.Name, a.Iban, BankAccount.FormatIban(a.Iban)))
            .Take(MaxResultsPerKind)
            .Select(a => new SearchResult(Loc.T("Account"), a.Name, BankAccount.KindName(a.Kind),
                () =>
                {
                    CurrentPage = AppPage.Settings;
                    EditBankAccountCommand.Execute(a);
                })));

        return results;
    }

    [RelayCommand]
    private void OpenSearchResult(SearchResult result)
    {
        GlobalSearch = "";
        result.Open();
    }

    [RelayCommand]
    private void OpenFirstSearchResult()
    {
        if (SearchResults.FirstOrDefault() is { } first)
            OpenSearchResult(first);
    }

    [RelayCommand]
    private void ClearSearch() => GlobalSearch = "";
}
