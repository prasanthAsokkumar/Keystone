using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// Insurance premiums and tax payments live on the Insurance and Taxes pages, with the papers that belong to
/// them, instead of among the other expenses. They still count everywhere money is added up (runway, Year).
/// </summary>
public partial class MainViewModel
{
    /// <summary>An expense shown on its own page instead of the Expenses tab.</summary>
    private static bool HasOwnPage(Transaction transaction) =>
        transaction.Type == TransactionType.Expense &&
        (Categories.IsInsurance(transaction.Category) || Categories.IsTaxes(transaction.Category));

    private static readonly string[] InsuranceWords =
        ["insurance", "versicherung", "krankenkasse", "assurance", "assicurazione", "prämie", "praemie", "police"];

    /// <summary>
    /// Common Swiss insurers, so their bills and letters land under Insurance before a policy with them is added.
    /// Matched as whole words of the sender (Zurich and CSS are left out: a city, and too short to be sure).
    /// </summary>
    private static readonly string[] SwissInsurers =
    [
        "Helsana", "Swica", "Sanitas", "Visana", "Concordia", "KPT", "Atupri", "ÖKK", "OKK", "Sympany", "Assura",
        "Groupe Mutuel", "EGK", "Agrisano", "Aquilana", "Sumiswalder", "Mobiliar", "Allianz", "Generali", "Baloise",
        "Bâloise", "Helvetia", "Vaudoise", "Smile", "Wefox", "TCS", "Coop Rechtsschutz", "Protekta", "Orion", "Dextra",
        "Suva", "Swiss Life", "Pax", "Elvia", "Emmental Versicherung", "GVB", "AXA",
    ];

    private static bool FromSwissInsurer(HouseholdDocument document) =>
        SwissInsurers.Any(name => System.Text.RegularExpressions.Regex.IsMatch(document.Sender,
            $@"(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(name)}(?![\p{{L}}])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private static readonly string[] TaxWords = ["tax", "steuer", "impôt", "impot", "imposta", "imposte"];

    private static bool Mentions(HouseholdDocument document, string[] words) =>
        words.Any(w => document.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                       document.Sender.Contains(w, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>A policy, a paper from an insurer you have a policy with, or one that names insurance.</summary>
    private bool IsInsuranceDocument(HouseholdDocument document) => document.Section switch
    {
        "Insurance" => true,
        "Taxes" or "None" => false,
        _ => SeemsInsurance(document),
    };

    /// <summary>Where a dragged document was put decides; otherwise its type, sender and title.</summary>
    private bool IsTaxDocument(HouseholdDocument document) => document.Section switch
    {
        "Taxes" => true,
        "Insurance" or "None" => false,
        _ => SeemsTax(document),
    };

    private bool SeemsInsurance(HouseholdDocument document) =>
        document.Kind == DocumentKind.Policy ||
        _policies.Any(p => p.DocumentId == document.Id || DuplicateFinder.SameSender(p.Insurer, document.Sender)) ||
        LinkedEntry(document) is { } entry && Categories.IsInsurance(entry.Category) ||
        Mentions(document, InsuranceWords) || FromSwissInsurer(document);

    /// <summary>A paper from the tax office or one that names taxes, e.g. "Tax assessment 2025".</summary>
    private bool SeemsTax(HouseholdDocument document) =>
        LinkedEntry(document) is { } entry && Categories.IsTaxes(entry.Category) || Mentions(document, TaxWords);

    private Transaction? LinkedEntry(HouseholdDocument document) =>
        document.TransactionId is { } id ? Transactions.FirstOrDefault(t => t.Id == id) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsuranceEntryRows))]
    public partial IReadOnlyList<EntryRow> InsuranceEntryRows { get; set; } = [];

    public bool HasInsuranceEntryRows => InsuranceEntryRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEndedInsuranceEntryRows), nameof(EndedPremiumsText))]
    public partial IReadOnlyList<EntryRow> EndedInsuranceEntryRows { get; set; } = [];

    public bool HasEndedInsuranceEntryRows => EndedInsuranceEntryRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndedPremiumsText))]
    public partial bool ShowEndedPremiums { get; set; }

    public string EndedPremiumsText => ShowEndedPremiums
        ? Loc.T("Hide ended premiums")
        : Loc.Plural(EndedInsuranceEntryRows.Count, "Show {0} ended premium", "Show {0} ended premiums");

    [RelayCommand]
    private void ToggleEndedPremiums() => ShowEndedPremiums = !ShowEndedPremiums;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsuranceDocumentRows))]
    public partial IReadOnlyList<DocumentRow> InsuranceDocumentRows { get; set; } = [];

    public bool HasInsuranceDocumentRows => InsuranceDocumentRows.Count > 0;

    /// <summary>Open bills from insurers (premiums, deductibles) and from the tax office, on their pages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsuranceBillRows))]
    public partial IReadOnlyList<DocumentRow> InsuranceBillRows { get; set; } = [];

    public bool HasInsuranceBillRows => InsuranceBillRows.Count > 0;

    [ObservableProperty]
    public partial string InsuranceBillsText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaxBillRows))]
    public partial IReadOnlyList<DocumentRow> TaxBillRows { get; set; } = [];

    public bool HasTaxBillRows => TaxBillRows.Count > 0;

    [ObservableProperty]
    public partial string TaxBillsText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaxEntryRows))]
    public partial IReadOnlyList<EntryRow> TaxEntryRows { get; set; } = [];

    public bool HasTaxEntryRows => TaxEntryRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaxDocumentRows))]
    public partial IReadOnlyList<DocumentRow> TaxDocumentRows { get; set; } = [];

    public bool HasTaxDocumentRows => TaxDocumentRows.Count > 0;

    /// <summary>"CHF 1'720 a month · CHF 640 one-time this year".</summary>
    [ObservableProperty]
    public partial string InsuranceEntriesTotal { get; set; } = "";

    [ObservableProperty]
    public partial string TaxEntriesTotal { get; set; } = "";

    /// <summary>On the Expenses tab: where the premiums and tax payments went.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMovedExpenses))]
    public partial string MovedExpensesText { get; set; } = "";

    public bool HasMovedExpenses => MovedExpensesText != "" && (IsExpensesTab || IsRecurringTab);

    private static decimal MonthlyShare(IEnumerable<Transaction> entries) =>
        entries.Where(t => t.IsRunning(DateOnly.FromDateTime(DateTime.Today))).Sum(t => t.Amount / t.IntervalMonths);

    private string EntriesTotalText(IReadOnlyCollection<Transaction> entries)
    {
        var oneTime = entries.Where(t => !t.IsRecurring && t.Date >= Summary.YearStart && t.Date <= Summary.YearEnd).Sum(t => t.Amount);
        return oneTime > 0
            ? Loc.F("{0} a month · {1} one-time in {2}", Format.Money(MonthlyShare(entries)), Format.Money(oneTime),
                FinancialYearReport.LabelFor(Summary.YearStart))
            : Loc.F("{0} a month", Format.Money(MonthlyShare(entries)));
    }

    /// <summary>Rebuilds the premium and tax lists, their papers, and the note on the Expenses tab.</summary>
    private void RefreshOwnPageLists()
    {
        var names = People.ToDictionary(p => p.Id, p => p.Name);
        string PersonName(int? id) => id is { } key && names.TryGetValue(key, out var name) ? name : Loc.T(PersonOption.SharedName);
        IReadOnlyList<EntryRow> Rows(string list, IEnumerable<Transaction> entries) => Sorted(list, entries
            .OrderByDescending(t => t.CanStop).ThenByDescending(t => t.Date)
            .Select(t => new EntryRow(t, PersonName(t.PersonId), AccountText(t))), null, EntrySortKeys);
        DocumentRow Row(HouseholdDocument d) => new(d, PersonName(d.PersonId), _documentStore.Exists(d), Today);
        // Open bills have their own list above the papers, soonest due first.
        IReadOnlyList<DocumentRow> Papers(string list, IEnumerable<HouseholdDocument> documents) => Sorted(list, documents
            .Where(d => !d.IsOpenBill)
            .OrderByDescending(d => d.Date).ThenByDescending(d => d.Id)
            .Select(Row), null, DocumentSortKeys);
        IReadOnlyList<DocumentRow> Bills(IEnumerable<HouseholdDocument> documents) => documents
            .Where(d => d.IsOpenBill)
            .OrderBy(d => d.NextDueDate ?? DateOnly.MaxValue).ThenBy(d => d.Date)
            .Select(Row).ToList();
        string BillsText(IReadOnlyList<DocumentRow> rows) => rows.Count == 0
            ? ""
            : Loc.Plural(rows.Count, "{0} bill · {1}", "{0} bills · {1}", Format.Money(rows.Sum(r => r.Document.AmountLeft))) +
              (rows.Count(r => r.IsOverdue) is > 0 and var overdue ? Loc.F(" · {0} overdue", overdue) : "");

        var insurance = Transactions.Where(t => HasOwnPage(t) && Categories.IsInsurance(t.Category)).ToList();
        var taxes = Transactions.Where(t => HasOwnPage(t) && Categories.IsTaxes(t.Category)).ToList();

        var insuranceFilter = InsurancePersonFilter ?? PersonOption.Everyone;
        // Premiums being paid or starting later; the ended ones (old premiums, one-time payments made) apart.
        var premiums = Rows("premiums", insurance.Where(insuranceFilter.Matches));
        InsuranceEntryRows = premiums.Where(r => !r.IsEnded).OrderBy(r => r.IsUpcoming).ToList();
        EndedInsuranceEntryRows = premiums.Where(r => r.IsEnded).ToList();
        InsuranceEntriesTotal = EntriesTotalText(insurance.Where(insuranceFilter.Matches).ToList());
        var insuranceDocuments = Documents.Where(d => (insuranceFilter.IsEveryone || d.PersonId == insuranceFilter.Id) && IsInsuranceDocument(d)).ToList();
        InsuranceDocumentRows = Papers("insurancePapers", insuranceDocuments);
        InsuranceBillRows = Bills(insuranceDocuments);
        SetSectionShown(BillsSection, InsuranceBillRows.Count > 0);
        RefreshHealthCosts();
        InsuranceBillsText = BillsText(InsuranceBillRows);

        var taxFilter = TaxPersonFilter ?? PersonOption.Everyone;
        TaxEntryRows = Rows("taxPayments", taxes.Where(taxFilter.Matches));
        TaxEntriesTotal = EntriesTotalText(taxes.Where(taxFilter.Matches).ToList());
        var taxDocuments = Documents.Where(d => (taxFilter.IsEveryone || d.PersonId == taxFilter.Id) && IsTaxDocument(d)).ToList();
        TaxDocumentRows = Papers("taxPapers", taxDocuments);
        TaxBillRows = Bills(taxDocuments);
        TaxBillsText = BillsText(TaxBillRows);

        MovedExpensesText = (insurance.Count, taxes.Count) switch
        {
            (0, 0) => "",
            (_, 0) => Loc.F("Insurance premiums ({0} / month) are on the Insurance page.", Format.Money(MonthlyShare(insurance))),
            (0, _) => Loc.F("Tax payments ({0} / month) are on the Taxes page.", Format.Money(MonthlyShare(taxes))),
            _ => Loc.F("Insurance premiums ({0} / month) and tax payments ({1} / month) are on their own pages.",
                Format.Money(MonthlyShare(insurance)), Format.Money(MonthlyShare(taxes))),
        };
        OnPropertyChanged(nameof(HasMovedExpenses));
    }

    [RelayCommand]
    private void ShowInsurance() => CurrentPage = AppPage.Insurance;

    [RelayCommand]
    private void ShowTaxes() => CurrentPage = AppPage.Taxes;

    /// <summary>Opens the entry form for a premium, e.g. the monthly health insurance.</summary>
    [RelayCommand]
    private void NewInsuranceEntry() => NewEntryIn(Categories.HealthInsurance);

    /// <summary>Opens the entry form for a tax payment, e.g. a provisional instalment.</summary>
    [RelayCommand]
    private void NewTaxEntry() => NewEntryIn(Categories.Taxes);

    private void NewEntryIn(string category)
    {
        NewEntry();
        NewIsTransfer = false;
        NewIsIncome = false;
        NewCategory = Categories.Display(category);
    }
}
