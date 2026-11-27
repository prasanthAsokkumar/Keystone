using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>One figure for the tax return.</summary>
public sealed record TaxLine(string Label, string Detail, string AmountText, string Note = "", bool IsWarning = false)
{
    public bool HasDetail => Detail != "";

    public bool HasNote => Note != "";
}

public sealed record TaxSection(string Title, IReadOnlyList<TaxLine> Lines, string TotalText = "", string Empty = "")
{
    public bool HasTotal => TotalText != "";

    public bool HasLines => Lines.Count > 0;

    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>A paper to collect for the tax return; <see cref="IsFound"/> when it is already in Documents.</summary>
public sealed record TaxChecklistItem(string Label, string Detail, bool IsFound, HouseholdDocument? Document)
{
    public bool IsMissing => !IsFound;

    public bool HasDocument => Document is not null;
}

/// <summary>The Taxes page: a tax year's income, deductions, assets and papers, gathered for the tax return.</summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaxYearText), nameof(TaxPageNote))]
    public partial int TaxYear { get; set; } = DefaultTaxYear(DateOnly.FromDateTime(DateTime.Today));

    /// <summary>Until midyear the return being prepared is last year's; after that, this year's.</summary>
    private static int DefaultTaxYear(DateOnly today) => today.Month <= 6 ? today.Year - 1 : today.Year;

    partial void OnTaxYearChanged(int value) => RefreshTaxes();

    public string TaxYearText => Loc.F("Tax year {0}", TaxYear);

    public string TaxPageNote => TaxYear >= Today.Year
        ? Loc.F("{0} isn’t over yet: figures include what is planned until 31 December.", TaxYear)
        : Loc.F("Your tax return for {0} is usually due by 31 March {1}.", TaxYear, TaxYear + 1);

    [ObservableProperty]
    public partial PersonOption TaxPersonFilter { get; set; } = PersonOption.Everyone;

    partial void OnTaxPersonFilterChanged(PersonOption value) => RefreshTaxes();

    [ObservableProperty]
    public partial IReadOnlyList<TaxSection> TaxSections { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TaxChecklistItem> TaxChecklist { get; set; } = [];

    [ObservableProperty]
    public partial string TaxChecklistSummary { get; set; } = "";

    [ObservableProperty]
    public partial string TaxCopyStatus { get; set; } = "";

    /// <summary>Puts text on the clipboard; set by the window.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [RelayCommand]
    private void PreviousTaxYear() => TaxYear--;

    [RelayCommand]
    private void NextTaxYear() => TaxYear++;

    private bool InTaxFilter(int? personId) => TaxPersonFilter is not { IsEveryone: false } person || person.Id == personId;

    /// <summary>What entries in a category came to in the tax year, per person (shared entries under null).</summary>
    private List<(int? PersonId, decimal Amount)> SumByPerson(Func<Transaction, bool> match)
    {
        var start = new DateOnly(TaxYear, 1, 1);
        var end = new DateOnly(TaxYear, 12, 31);
        return Schedule.Expand(_entries.Where(t => !t.IsTransfer && match(t) && InTaxFilter(t.PersonId)), start, end)
            .GroupBy(o => o.Source.PersonId)
            .Select(g => (g.Key, g.Sum(o => o.Amount)))
            .ToList();
    }

    private string ByPersonText(List<(int? PersonId, decimal Amount)> amounts) =>
        amounts.Count <= 1
            ? ""
            : string.Join(" · ", amounts.OrderBy(a => PersonName(a.PersonId), StringComparer.CurrentCultureIgnoreCase)
                .Select(a => $"{PersonName(a.PersonId)} {Format.Money(a.Amount)}"));

    internal void RefreshTaxes()
    {
        RefreshOwnPageLists();
        if (_isLoading)
            return;

        var yearEnd = new DateOnly(TaxYear, 12, 31);
        var expense = (string category) => SumByPerson(t => t.Type == TransactionType.Expense && t.Category == category);

        // ── Income ──
        var income = new List<TaxLine>();
        var salary = SumByPerson(t => t.IsIncome && t.Category == "Salary");
        foreach (var (personId, amount) in salary.OrderBy(s => PersonName(s.PersonId), StringComparer.CurrentCultureIgnoreCase))
            income.Add(new TaxLine(Loc.F("Salary · {0}", PersonName(personId)), Loc.T("Net salary paid out; your salary certificate shows the gross figures."),
                Format.Money(amount)));
        var other = SumByPerson(t => t.IsIncome && !t.IsBorrowing && t.Category != "Salary");
        if (other.Count > 0)
            income.Add(new TaxLine(Loc.T("Other income"), ByPersonText(other), Format.Money(other.Sum(o => o.Amount))));

        // ── Deductions ──
        var deductions = new List<TaxLine>();
        foreach (var row in Retirement.Pillar3aRowsFor(TaxPersonFilter ?? PersonOption.Everyone, TaxYear).Where(r => r.Paid > 0))
            deductions.Add(new TaxLine(Loc.F("Pillar 3a · {0}", row.PersonName), Loc.F("Limit {0}", Format.Money(row.Limit)),
                Format.Money(Math.Min(row.Paid, row.Limit)),
                row.IsOver ? Loc.F("{0} was paid; only the limit can be deducted.", Format.Money(row.Paid)) : "", row.IsOver));

        void AddCategory(string category, string label, string note)
        {
            var amounts = expense(category);
            if (amounts.Count > 0)
                deductions.Add(new TaxLine(label, ByPersonText(amounts), Format.Money(amounts.Sum(a => a.Amount)), note));
        }

        AddCategory(Categories.PensionBuyIn, Loc.T("Pension fund buy-ins"),
            Loc.T("Fully deductible. Withdrawing capital within 3 years undoes the deduction."));
        AddCategory(Categories.Childcare, Loc.T("Childcare costs"), Loc.T("Deductible up to a limit set by your canton."));
        AddCategory(Categories.MedicalCosts, Loc.T("Medical costs"), Loc.T("Only the part above 5% of net income can be deducted."));
        AddCategory(Categories.Donations, Loc.T("Donations"), Loc.T("Deductible from CHF 100, up to 20% of net income."));

        var interest = DebtPlans
            .Select(p => (p.Debt.Name, Interest: p.Entries.Where(e => e.Date.Year == TaxYear).Sum(e => e.Interest)))
            .Where(d => d.Interest > 0)
            .ToList();
        if (interest.Count > 0 && TaxPersonFilter is not { IsEveryone: false })
            deductions.Add(new TaxLine(Loc.T("Interest on debts"),
                string.Join(" · ", interest.Select(d => $"{d.Name} {Format.Money(d.Interest)}")),
                Format.Money(interest.Sum(d => d.Interest)), Loc.T("Deductible as debt interest.")));

        AddCategory("Health insurance", Loc.T("Health insurance premiums"),
            Loc.T("Part of the flat insurance deduction, which has a cap."));

        // ── Assets and debts on 31 December ──
        var assets = new List<TaxLine>();
        if (TaxPersonFilter is not { IsEveryone: false })
        {
            foreach (var account in BankAccounts)
                assets.Add(new TaxLine(account.Name, account.Iban == "" ? BankAccount.KindName(account.Kind) : BankAccount.FormatIban(account.Iban),
                    Format.Money(AccountBalance(account, yearEnd))));
            // Investments and other assets at their last value up to 31 December.
            foreach (var asset in Assets.All.Where(a => a.ValueOn(yearEnd) != 0).OrderByDescending(a => a.ValueOn(yearEnd)))
            {
                var asOf = asset.Values.Where(v => v.Date <= yearEnd).OrderBy(v => v.Date).Last().Date;
                assets.Add(new TaxLine(asset.Name, $"{Asset.KindName(asset.Kind)} · {Loc.F("value from {0}", Format.Date(asOf))}",
                    Format.Money(asset.ValueOn(yearEnd)),
                    asset.Kind == AssetKind.Property ? Loc.T("Declare property at its tax value (Steuerwert), not what it would sell for.")
                    : asOf.Year < TaxYear ? Loc.T("Enter its value on 31 December for the tax return.") : "",
                    asOf.Year < TaxYear));
            }
            foreach (var plan in DebtPlans)
            {
                var owed = plan.OutstandingOn(yearEnd);
                if (owed > 0)
                    assets.Add(new TaxLine(Loc.F("Debt: {0}", plan.Debt.Name), "", Format.SignedMoney(-owed)));
            }
        }

        TaxSections =
        [
            new TaxSection(Loc.T("Income"), income, income.Count > 0 ? Format.Money(salary.Sum(s => s.Amount) + other.Sum(o => o.Amount)) : "",
                Loc.T("No income recorded for this year.")),
            new TaxSection(Loc.T("Deductions to claim"), deductions, "",
                Loc.F("No deductions recorded. Use the categories “{0}”, “{1}”, “{2}”, “{3}” and “{4}” for them.",
                    Categories.Display(Categories.Pillar3a), Categories.Display(Categories.PensionBuyIn),
                    Categories.Display(Categories.Childcare), Categories.Display(Categories.MedicalCosts),
                    Categories.Display(Categories.Donations))),
            new TaxSection(Loc.T("Assets and debts on 31 December"), assets,
                assets.Count > 0 ? Format.Money(BankAccounts.Sum(a => AccountBalance(a, yearEnd)) + Assets.AssetsOn(yearEnd) - DebtPlans.Sum(p => p.OutstandingOn(yearEnd))) : "",
                Loc.T("Shown for the whole household.")),
        ];

        RefreshTaxChecklist(salary.Select(s => s.PersonId));
        RefreshTaxEstimate();
    }

    /// <summary>Certificates the tax return needs, found in Documents when their title, sender or notes say what they are.</summary>
    private void RefreshTaxChecklist(IEnumerable<int?> salaried)
    {
        // Year-end papers arrive from December until spring.
        var from = new DateOnly(TaxYear, 12, 1).AddMonths(-11);
        var to = new DateOnly(TaxYear + 1, 5, 31);
        var candidates = Documents.Where(d => d.Date >= from && d.Date <= to).ToList();

        HouseholdDocument? Find(Func<HouseholdDocument, bool> match, params string[] words) =>
            candidates.Where(match)
                .Where(d => words.Length == 0 || words.Any(w =>
                    $"{d.Title} {d.Sender} {d.Notes}".Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                .OrderByDescending(d => d.Date)
                .FirstOrDefault();

        var items = new List<TaxChecklistItem>();
        foreach (var personId in salaried.Where(InTaxFilter).OrderBy(id => PersonName(id), StringComparer.CurrentCultureIgnoreCase))
        {
            var found = Find(d => d.PersonId == personId, "lohnausweis", "salary certificate", "certificat de salaire");
            items.Add(new TaxChecklistItem(Loc.F("Salary certificate · {0}", PersonName(personId)), Loc.T("Lohnausweis, from the employer in January"),
                found is not null, found));
        }

        foreach (var account in Retirement.All.Where(a => a.Pillar == PensionPillar.Pillar3a && InTaxFilter(a.PersonId)))
        {
            var letter = account.Statements.Where(s => s.Date.Year == TaxYear || (s.Date.Year == TaxYear + 1 && s.Date.Month <= 3))
                .Select(s => Documents.FirstOrDefault(d => d.Id == s.DocumentId)).FirstOrDefault(d => d is not null);
            letter ??= Find(d => d.Kind == DocumentKind.Statement, account.Name);
            items.Add(new TaxChecklistItem(Loc.F("Pillar 3a certificate · {0}", account.Name), PersonName(account.PersonId),
                letter is not null, letter));
        }

        if (TaxSections.Any(s => s.Lines.Any(l => l.Label == Loc.T("Pension fund buy-ins"))))
        {
            var found = Find(_ => true, "einkauf", "buy-in", "rachat");
            items.Add(new TaxChecklistItem(Loc.T("Pension fund buy-in confirmation"), "", found is not null, found));
        }

        if (TaxPersonFilter is not { IsEveryone: false })
        {
            foreach (var account in BankAccounts.Where(a => a.Kind != BankAccountKind.Cash))
            {
                var found = Find(d => d.Kind == DocumentKind.Statement, account.Name, "steuerauszug", "zinsausweis", "tax statement");
                items.Add(new TaxChecklistItem(Loc.F("Year-end statement · {0}", account.Name), Loc.T("Steuerauszug or Zinsausweis from the bank"),
                    found is not null, found));
            }
        }

        if (_policies.Any(p => p.Kind is InsuranceKind.HealthBasic && InTaxFilter(p.PersonId)) ||
            TaxSections.Any(s => s.Lines.Any(l => l.Label == Loc.T("Health insurance premiums"))))
        {
            var found = Find(_ => true, "prämienbestätigung", "praemienbestaetigung", "steuerbescheinigung", "premium statement", "attestation");
            items.Add(new TaxChecklistItem(Loc.T("Health insurance premium statement"), Loc.T("From your health insurer"), found is not null, found));
        }

        TaxChecklist = items;
        TaxChecklistSummary = items.Count == 0
            ? Loc.T("Nothing to collect yet: add income, accounts or insurance first.")
            : Loc.F("{0} of {1} in Documents", items.Count(i => i.IsFound), items.Count);
    }

    /// <summary>The summary as plain text, e.g. to paste into notes for the tax return.</summary>
    public string TaxSummaryText()
    {
        var text = new StringBuilder();
        text.AppendLine(TaxYearText);
        foreach (var section in TaxSections.Where(s => s.HasLines))
        {
            text.AppendLine();
            text.AppendLine(section.Title);
            foreach (var line in section.Lines)
                text.AppendLine($"  {line.Label}: {line.AmountText}{(line.HasDetail ? $"  ({line.Detail})" : "")}");
            if (section.HasTotal)
                text.AppendLine($"  {Loc.T("Total")}: {section.TotalText}");
        }
        if (TaxChecklist.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.T("Papers to collect"));
            foreach (var item in TaxChecklist)
                text.AppendLine($"  [{(item.IsFound ? "x" : " ")}] {item.Label}");
        }
        return text.ToString();
    }

    [RelayCommand]
    private async Task CopyTaxSummaryAsync()
    {
        if (CopyToClipboard is null)
            return;
        await CopyToClipboard(TaxSummaryText());
        TaxCopyStatus = Loc.T("Copied. Paste it wherever you prepare your tax return.");
    }

    [RelayCommand]
    private void OpenTaxDocument(HouseholdDocument document) => OpenDocument(document);
}
