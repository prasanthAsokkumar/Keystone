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
            .Where(d => Has(d.Title, d.Sender, d.Notes, DocumentContent(d.Id)))
            .OrderByDescending(d => Has(d.Title, d.Sender, d.Notes))
            .ThenByDescending(d => d.Date)
            .Take(MaxResultsPerKind)
            .Select(d => new SearchResult(HouseholdDocument.KindName(d.Kind), d.Title,
                // Found inside the document: show where.
                Has(d.Title, d.Sender, d.Notes) ? string.Join(" · ", new[] { d.Sender, Format.Date(d.Date) }.Where(p => p != ""))
                    : Snippet(DocumentContent(d.Id) ?? "", query),
                () =>
                {
                    CurrentPage = AppPage.Documents;
                    EditDocumentCommand.Execute(d);
                })));

        results.AddRange(Tasks.All
            .Where(t => Has(t.Title, t.Notes))
            .OrderBy(t => t.IsDone)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .Take(MaxResultsPerKind)
            .Select(t => new SearchResult(Loc.T("Task"), t.Title, new TaskRow(t, PersonName(t.PersonId), Today).DueText,
                () =>
                {
                    CurrentPage = AppPage.Tasks;
                    Tasks.EditTaskCommand.Execute(t);
                })));

        var now = DateTime.Now;
        results.AddRange(Appointments.All
            .Where(a => Has(a.Title, a.Location, a.Notes))
            // Upcoming ones first, soonest first; then the most recent past ones.
            .OrderBy(a => a.HasPassed(now))
            .ThenBy(a => a.HasPassed(now) ? -a.Start.Ticks : a.Start.Ticks)
            .Take(MaxResultsPerKind)
            .Select(a => new SearchResult(Loc.T("Appointment"), a.Title, new AppointmentRow(a, PersonName(a.PersonId), now).DetailText,
                () =>
                {
                    CurrentPage = AppPage.Appointments;
                    Appointments.EditAppointmentCommand.Execute(a);
                })));

        results.AddRange(Notes.All
            .Where(n => Has(n.Title, n.Body, n.Project))
            .OrderByDescending(n => n.UpdatedAt)
            .Take(MaxResultsPerKind)
            .Select(n => new SearchResult(Loc.T("Note"), n.Title.Trim() == "" ? Loc.T("Untitled note") : n.Title,
                n.Project,
                () =>
                {
                    CurrentPage = AppPage.Notes;
                    Notes.Show(n);
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

        results.AddRange(Retirement.All
            .Where(a => Has(a.Name, a.Notes))
            .Take(MaxResultsPerKind)
            .Select(a => new SearchResult(Loc.T("Retirement"), a.Name, PensionAccount.PillarName(a.Pillar),
                () =>
                {
                    CurrentPage = AppPage.Retirement;
                    Retirement.EditPensionAccountCommand.Execute(a);
                })));

        results.AddRange(Deadlines.Contracts
            .Where(c => Has(c.Provider, c.CustomerNumber, c.Notes, Contract.KindName(c.Kind)))
            .Take(MaxResultsPerKind)
            .Select(c => new SearchResult(Loc.T("Contract"), c.Provider, Contract.KindName(c.Kind),
                () =>
                {
                    CurrentPage = AppPage.Contracts;
                    Deadlines.DeadlinesTab = 0;
                    Deadlines.EditContractCommand.Execute(c);
                })));

        results.AddRange(Deadlines.Dates
            .Where(d => Has(d.Title, d.Notes, ImportantDate.KindName(d.Kind)))
            .Take(MaxResultsPerKind)
            .Select(d => new SearchResult(Loc.T("Date"), d.Title, Format.Date(d.NextDate(Today)),
                () =>
                {
                    CurrentPage = AppPage.Contracts;
                    Deadlines.DeadlinesTab = 1;
                    Deadlines.EditImportantDateCommand.Execute(d);
                })));

        results.AddRange(Deadlines.Warranties
            .Where(w => Has(w.Item, w.Store, w.Notes))
            .Take(MaxResultsPerKind)
            .Select(w => new SearchResult(Loc.T("Warranty"), w.Item, Loc.F("Until {0}", Format.Date(w.ExpiresOn)),
                () =>
                {
                    CurrentPage = AppPage.Contracts;
                    Deadlines.DeadlinesTab = 2;
                    Deadlines.EditWarrantyCommand.Execute(w);
                })));

        results.AddRange(Planning.Goals
            .Where(g => Has(g.Name))
            .Take(MaxResultsPerKind)
            .Select(g => new SearchResult(Loc.T("Savings goal"), g.Name, Format.Money(g.Target),
                () =>
                {
                    CurrentPage = AppPage.Planning;
                    Planning.PlanningTab = 0;
                    Planning.EditGoalCommand.Execute(g);
                })));

        results.AddRange(BankAccounts
            .Where(a => Has(a.Name, a.Iban, BankAccount.FormatIban(a.Iban)))
            .Take(MaxResultsPerKind)
            .Select(a => new SearchResult(Loc.T("Account"), a.Name, BankAccount.KindName(a.Kind),
                () =>
                {
                    CurrentPage = AppPage.Settings;
                    SettingsTab = 1;
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
