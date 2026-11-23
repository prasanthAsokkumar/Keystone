using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>A choice in the statement form's account picker; <see cref="Account"/> is null for a new account.</summary>
public sealed record PensionAccountOption(PensionAccount? Account, string Name)
{
    public override string ToString() => Account is null ? Loc.T("New account…") : Name;
}

/// <summary>
/// The Retirement page: Swiss retirement provision per household member, kept up to date from
/// the yearly letters of the pension fund, 3a providers and AHV.
/// </summary>
public partial class MainViewModel
{
    private readonly List<PensionAccount> _pensionAccounts = [];
    private CancellationTokenSource? _statementCancellation;

    public IReadOnlyList<string> PillarNames => Enum.GetValues<PensionPillar>().Select(PensionAccount.PillarName).ToList();

    [ObservableProperty]
    public partial PersonOption RetirementPersonFilter { get; set; } = PersonOption.Everyone;

    partial void OnRetirementPersonFilterChanged(PersonOption value) => RefreshRetirement();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPensionAccounts))]
    public partial IReadOnlyList<PensionAccountRow> PensionAccountRows { get; set; } = [];

    public bool HasPensionAccounts => PensionAccountRows.Count > 0;

    public string RetirementEmptyText => _pensionAccounts.Count == 0
        ? Loc.T("No retirement accounts yet. Drop your pension fund certificate or 3a statement here, or click Add statement.")
        : Loc.T("No accounts for this person.");

    [ObservableProperty]
    public partial string RetirementSavingsText { get; set; } = "";

    [ObservableProperty]
    public partial string RetirementSavingsDetail { get; set; } = "";

    [ObservableProperty]
    public partial string ExpectedPensionText { get; set; } = "";

    [ObservableProperty]
    public partial string ExpectedPensionDetail { get; set; } = "";

    [ObservableProperty]
    public partial string BuyInText { get; set; } = "";

    [ObservableProperty]
    public partial string BuyInDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPillar3aRows))]
    public partial IReadOnlyList<Pillar3aRow> Pillar3aRows { get; set; } = [];

    public bool HasPillar3aRows => Pillar3aRows.Count > 0;

    public string Pillar3aTitle => Loc.F("Pillar 3a in {0}", Today.Year);

    public string Pillar3aHint => Loc.F(
        "Payments up to the limit are deducted from your taxable income, if paid by 31 December. Add them as expenses in the category “{0}”.",
        Categories.Display(Categories.Pillar3a));

    /// <summary>Short notes on the Swiss rules, shown beside the accounts.</summary>
    public IReadOnlyList<string> RetirementRules =>
    [
        Loc.T("1st pillar, AHV: the state pension. Your AHV office can project your pension (“Rentenvorausberechnung”)."),
        Loc.T("2nd pillar, pension fund: you and your employer save for you. Your pension certificate (“Vorsorgeausweis”) comes every year."),
        Loc.F("Pillar 3a: up to {0} a year with a pension fund, or 20% of net earned income up to {1} without one ({2}). Payments are deducted from taxable income.",
            Format.Money(SwissPension.Pillar3aLimit(Today.Year, true)), Format.Money(SwissPension.Pillar3aLimit(Today.Year, false)),
            Math.Min(Today.Year, SwissPension.LatestKnownLimitYear)),
        Loc.T("Missed 3a years from 2025 on can be paid in later, up to 10 years back."),
        Loc.F("Retirement age is {0}. 3a money can be drawn from {1}; pension funds pay a pension or, often, part of it as capital.",
            SwissPension.RetirementAge, SwissPension.Pillar3aEarliestWithdrawalAge),
        Loc.T("Capital withdrawals are taxed separately at a lower rate. Spreading them over several years usually saves tax."),
    ];

    private async Task LoadRetirementAsync()
    {
        _pensionAccounts.Clear();
        _pensionAccounts.AddRange(await repository.GetPensionAccountsAsync());
        RefreshRetirement();
    }

    private string PersonName(int? personId) =>
        personId is { } id && People.FirstOrDefault(p => p.Id == id) is { } person ? person.Name : PersonOption.Shared.ToString();

    /// <summary>Rebuilds the account list, totals and 3a progress.</summary>
    private void RefreshRetirement()
    {
        var filter = RetirementPersonFilter ?? PersonOption.Everyone;
        var accounts = _pensionAccounts
            .Where(a => filter.IsEveryone || a.PersonId == filter.Id)
            .OrderBy(a => a.Pillar)
            .ThenBy(a => PersonName(a.PersonId), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        PensionAccountRows = Sorted("pensionAccounts", accounts.Select(a => new PensionAccountRow(a, PersonName(a.PersonId))), null,
            new SortKey<PensionAccountRow>("name", r => r.Name),
            new SortKey<PensionAccountRow>("person", r => r.PersonName),
            new SortKey<PensionAccountRow>("pillar", r => (int)r.Account.Pillar),
            new SortKey<PensionAccountRow>("balance", r => r.Latest?.Balance, DescendingFirst: true));
        OnPropertyChanged(nameof(RetirementEmptyText));

        // Savings: the latest letter of each account that holds money.
        var saving = accounts.Where(a => PensionAccount.HasSavings(a.Pillar) && a.Statements.Count > 0).ToList();
        var total = saving.Sum(a => a.Statements[^1].Balance);
        var change = saving.Where(a => a.Statements.Count > 1).Sum(a => a.Statements[^1].Balance - a.Statements[^2].Balance);
        RetirementSavingsText = Format.Money(total);
        RetirementSavingsDetail = saving.Count == 0
            ? Loc.T("Add a statement to see your savings")
            : saving.Any(a => a.Statements.Count > 1)
                ? Loc.F("{0} since the previous letters", Format.SignedMoney(change))
                : Loc.Plural(saving.Count, "From {0} account", "From {0} accounts");

        // Pension: what the pension fund and AHV letters project from 65.
        var latest = accounts.Where(a => a.Statements.Count > 0).Select(a => (a.Pillar, Statement: a.Statements[^1])).ToList();
        var pension = latest.Sum(l => l.Statement.ProjectedPension ?? 0);
        var capital = latest.Where(l => l.Pillar == PensionPillar.PensionFund).Sum(l => l.Statement.ProjectedCapital ?? 0);
        var hasAhv = latest.Any(l => l.Pillar == PensionPillar.Ahv && l.Statement.ProjectedPension > 0);
        ExpectedPensionText = pension > 0 ? Loc.F("{0} / month", Format.Money(pension / 12)) : "–";
        ExpectedPensionDetail = pension == 0
            ? Loc.T("Shown once a letter includes a projected pension")
            : (capital > 0 ? Loc.F("Pension fund savings at {0}: {1}", SwissPension.RetirementAge, Format.Money(capital)) + "\n" : "") +
              (hasAhv ? Loc.T("Pension fund and AHV, as projected") : Loc.T("Pension fund only. Add your AHV projection for the full picture."));

        var buyIn = latest.Where(l => l.Pillar == PensionPillar.PensionFund).Sum(l => l.Statement.BuyInPotential ?? 0);
        BuyInText = buyIn > 0 ? Format.Money(buyIn) : "–";
        BuyInDetail = buyIn > 0
            ? Loc.T("Voluntary pension fund payments are deducted from taxable income. Withdrawing capital within 3 years undoes the deduction.")
            : Loc.T("Your pension fund certificate shows how much you can buy in.");

        RefreshPillar3a(filter);
    }

    /// <summary>For each person with a 3a account or 3a payments, what is paid in this year against the limit.</summary>
    private void RefreshPillar3a(PersonOption filter)
    {
        Pillar3aRows = Pillar3aRowsFor(filter, Today.Year);
        OnPropertyChanged(nameof(Pillar3aTitle));
        ScheduleReminders();
    }

    private IReadOnlyList<Pillar3aRow> Pillar3aRowsForReminders() => Pillar3aRowsFor(PersonOption.Everyone, Today.Year);

    /// <summary>Pillar 3a payments in <paramref name="year"/> per person, against that year's limit.</summary>
    internal IReadOnlyList<Pillar3aRow> Pillar3aRowsFor(PersonOption filter, int year)
    {
        var payments = Schedule.Expand(
                Transactions.Where(t => t.Type == TransactionType.Expense && t.Category == Categories.Pillar3a),
                new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
            .ToList();

        var personIds = _pensionAccounts.Where(a => a.Pillar == PensionPillar.Pillar3a).Select(a => a.PersonId)
            .Concat(payments.Select(p => p.Source.PersonId))
            .Distinct()
            .Where(id => filter.IsEveryone || id == filter.Id)
            .OrderBy(id => PersonName(id), StringComparer.CurrentCultureIgnoreCase);

        return personIds.Select(id =>
        {
            var hasPensionFund = _pensionAccounts.Any(a => a.PersonId == id && a.Pillar == PensionPillar.PensionFund);
            var paid = payments.Where(p => p.Source.PersonId == id).Sum(p => p.Amount);
            var paidByToday = payments.Where(p => p.Source.PersonId == id && p.Date <= Today).Sum(p => p.Amount);
            return new Pillar3aRow(id, PersonName(id), paid, paidByToday, SwissPension.Pillar3aLimit(year, hasPensionFund), hasPensionFund);
        }).ToList();
    }

    /// <summary>Opens a new entry for a 3a payment of what is left to pay in this year.</summary>
    [RelayCommand]
    private void AddPillar3aPayment(Pillar3aRow row)
    {
        NewEntry();
        NewIsIncome = false;
        NewDescription = Loc.T("Pillar 3a");
        NewCategory = Categories.Display(Categories.Pillar3a);
        NewAmount = row.Left > 0 ? row.Left : null;
        NewPerson = PersonOptions.FirstOrDefault(o => o.Id == row.PersonId) ?? PersonOption.Shared;
    }

    // ── Statement panel ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsStatementPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatementFormTitle), nameof(CanChooseStatementAccount))]
    public partial PensionStatement? EditingStatement { get; set; }

    public string StatementFormTitle => Loc.T(EditingStatement is null ? "Add statement" : "Edit statement");

    /// <summary>The account can't be changed when editing a statement.</summary>
    public bool CanChooseStatementAccount => EditingStatement is null;

    [ObservableProperty]
    public partial IReadOnlyList<PensionAccountOption> StatementAccountOptions { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewStatementAccount), nameof(StatementPillar), nameof(StatementShowsBalance),
        nameof(StatementShowsPension), nameof(StatementShowsPensionFund), nameof(StatementBalanceLabel))]
    public partial PensionAccountOption? StatementAccount { get; set; }

    public bool IsNewStatementAccount => StatementAccount is { Account: null };

    [ObservableProperty]
    public partial string NewPensionAccountName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatementPillar), nameof(StatementShowsBalance), nameof(StatementShowsPension),
        nameof(StatementShowsPensionFund), nameof(StatementBalanceLabel))]
    public partial int NewPensionPillarIndex { get; set; } = (int)PensionPillar.PensionFund;

    [ObservableProperty]
    public partial PersonOption NewPensionPerson { get; set; } = PersonOption.Shared;

    public PensionPillar StatementPillar => StatementAccount?.Account?.Pillar ?? (PensionPillar)Math.Clamp(NewPensionPillarIndex, 0, 4);

    public bool StatementShowsBalance => PensionAccount.HasSavings(StatementPillar);

    public bool StatementShowsPension => StatementPillar is PensionPillar.PensionFund or PensionPillar.Ahv;

    public bool StatementShowsPensionFund => StatementPillar == PensionPillar.PensionFund;

    public string StatementBalanceLabel => Loc.T(StatementPillar == PensionPillar.PensionFund
        ? "Retirement savings today"
        : "Balance");

    [ObservableProperty]
    public partial DateTime? StatementDate { get; set; }

    [ObservableProperty]
    public partial decimal? StatementBalance { get; set; }

    [ObservableProperty]
    public partial decimal? StatementPaidIn { get; set; }

    [ObservableProperty]
    public partial decimal? StatementProjectedCapital { get; set; }

    [ObservableProperty]
    public partial decimal? StatementProjectedPension { get; set; }

    [ObservableProperty]
    public partial decimal? StatementBuyIn { get; set; }

    /// <summary>The letter to file with the statement; only for a new statement.</summary>
    private string? _statementLetterPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatementLetter))]
    public partial string StatementLetterName { get; set; } = "";

    public bool HasStatementLetter => StatementLetterName != "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatementAiStatus))]
    public partial string StatementAiStatus { get; set; } = "";

    public bool HasStatementAiStatus => StatementAiStatus != "";

    [ObservableProperty]
    public partial bool IsStatementAiBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatementError))]
    public partial string? StatementError { get; set; }

    public bool HasStatementError => !string.IsNullOrEmpty(StatementError);

    private void RefreshStatementAccountOptions(PensionAccount? select)
    {
        StatementAccountOptions =
        [
            .. _pensionAccounts
                .OrderBy(a => a.Pillar).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new PensionAccountOption(a, $"{a.Name} · {PersonName(a.PersonId)}")),
            new PensionAccountOption(null, ""),
        ];
        StatementAccount = StatementAccountOptions.FirstOrDefault(o => o.Account == select && select is not null)
                           ?? StatementAccountOptions[^1];
    }

    /// <summary>The person new accounts are for: the one being looked at, else the first household member.</summary>
    private PersonOption DefaultPensionPerson =>
        RetirementPersonFilter is { IsEveryone: false } person ? person : PersonOptions.FirstOrDefault(o => o.Id is not null) ?? PersonOption.Shared;

    [RelayCommand]
    private void NewStatement() => OpenStatementPanel(PensionAccountRows.FirstOrDefault()?.Account);

    [RelayCommand]
    private void AddStatementTo(PensionAccount account) => OpenStatementPanel(account);

    private void OpenStatementPanel(PensionAccount? account)
    {
        CloseOtherPanels();
        ResetStatementForm();
        RefreshStatementAccountOptions(account);
        NewPensionPerson = DefaultPensionPerson;
        // Most letters show the figures at the end of last year.
        StatementDate = new DateTime(Today.Year - 1, 12, 31);
        IsStatementPanelOpen = true;
    }

    /// <summary>Opens the form for a dropped letter and lets the local AI read its figures.</summary>
    public async Task StartNewStatementAsync(string path)
    {
        OpenStatementPanel(PensionAccountRows.FirstOrDefault()?.Account);
        _statementLetterPath = path;
        StatementLetterName = Path.GetFileName(path);
        if (IsImportEnabled && DocumentReader.IsSupported(path))
            await FillStatementAsync(path);
    }

    /// <summary>Attaches a letter chosen in the form.</summary>
    public async Task AttachStatementLetterAsync(string path)
    {
        _statementLetterPath = path;
        StatementLetterName = Path.GetFileName(path);
        if (IsImportEnabled && DocumentReader.IsSupported(path))
            await FillStatementAsync(path);
    }

    private async Task FillStatementAsync(string path)
    {
        _statementCancellation?.Cancel();
        var cancellation = _statementCancellation = new CancellationTokenSource();
        IsStatementAiBusy = true;
        StatementAiStatus = Loc.F("Reading the letter with {0}. You can type while it works…", AiModel);
        try
        {
            var content = await DocumentReader.ReadAsync(path);
            var details = await _ai.ExtractPensionStatementAsync(content, AiEndpoint, AiModel, HouseholdNames, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;
            ApplyStatementDetails(details);
            StatementAiStatus = Loc.T("Figures filled in by your local AI. Check them against the letter before saving.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            StatementAiStatus = Loc.F("Couldn’t read the figures automatically: {0}", ex.Message);
        }
        finally
        {
            if (_statementCancellation == cancellation)
                IsStatementAiBusy = false;
        }
    }

    /// <summary>Fills what the model found, picking the matching account or preparing a new one.</summary>
    public void ApplyStatementDetails(PensionStatementDetails details)
    {
        var person = MatchPersonOption(details.Person);
        if (person is not null)
            NewPensionPerson = person;

        if (CanChooseStatementAccount && (details.Pillar is not null || details.Provider != ""))
        {
            var match = _pensionAccounts
                .Where(a => details.Pillar is null || a.Pillar == details.Pillar ||
                            (a.Pillar is PensionPillar.PensionFund or PensionPillar.VestedBenefits &&
                             details.Pillar is PensionPillar.PensionFund or PensionPillar.VestedBenefits))
                .Where(a => details.Provider == "" ||
                            a.Name.Contains(details.Provider, StringComparison.CurrentCultureIgnoreCase) ||
                            details.Provider.Contains(a.Name, StringComparison.CurrentCultureIgnoreCase))
                .Where(a => person is null || a.PersonId == person.Id)
                .OrderBy(a => a.PersonId == DefaultPensionPerson.Id ? 0 : 1)
                .FirstOrDefault();
            if (match is not null && (details.Provider != "" || _pensionAccounts.Count(a => a.Pillar == match.Pillar) == 1))
            {
                StatementAccount = StatementAccountOptions.First(o => o.Account == match);
            }
            else
            {
                StatementAccount = StatementAccountOptions[^1];
                if (details.Provider != "" && NewPensionAccountName == "")
                    NewPensionAccountName = details.Provider;
                if (details.Pillar is { } pillar)
                    NewPensionPillarIndex = (int)pillar;
            }
        }

        if (details.Date is { } date)
            StatementDate = date.ToDateTime(TimeOnly.MinValue);
        StatementBalance ??= details.Balance;
        StatementPaidIn ??= details.PaidIn;
        StatementProjectedCapital ??= details.ProjectedCapital;
        StatementProjectedPension ??= details.ProjectedPension;
        StatementBuyIn ??= details.BuyInPotential;
    }

    [RelayCommand]
    private void EditStatement(PensionStatement statement)
    {
        var account = _pensionAccounts.First(a => a.Id == statement.AccountId);
        CloseOtherPanels();
        ResetStatementForm();
        EditingStatement = statement;
        RefreshStatementAccountOptions(account);
        StatementDate = statement.Date.ToDateTime(TimeOnly.MinValue);
        StatementBalance = PensionAccount.HasSavings(account.Pillar) ? statement.Balance : null;
        StatementPaidIn = statement.PaidIn;
        StatementProjectedCapital = statement.ProjectedCapital;
        StatementProjectedPension = statement.ProjectedPension;
        StatementBuyIn = statement.BuyInPotential;
        IsStatementPanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveStatementAsync()
    {
        StatementError = null;
        var pillar = StatementPillar;
        if (IsNewStatementAccount && string.IsNullOrWhiteSpace(NewPensionAccountName))
        {
            StatementError = Loc.T("Enter the name of the pension fund, bank or insurer.");
            return;
        }
        if (StatementDate is null)
        {
            StatementError = Loc.T("Enter the date the figures are valid on.");
            return;
        }
        if (PensionAccount.HasSavings(pillar) ? StatementBalance is null or < 0 : StatementProjectedPension is not > 0)
        {
            StatementError = Loc.T(PensionAccount.HasSavings(pillar)
                ? "Enter the balance from the letter."
                : "Enter the projected pension from the letter.");
            return;
        }

        var account = StatementAccount?.Account ?? new PensionAccount
        {
            Name = NewPensionAccountName.Trim(),
            Pillar = pillar,
            PersonId = NewPensionPerson.Id,
        };
        var date = DateOnly.FromDateTime(StatementDate.Value);
        var showsPensionFund = pillar == PensionPillar.PensionFund;
        var statement = EditingStatement ?? new PensionStatement();
        var values = new PensionStatement
        {
            Id = statement.Id,
            Date = date,
            Balance = PensionAccount.HasSavings(pillar) ? StatementBalance ?? 0 : 0,
            PaidIn = StatementPaidIn is > 0 ? StatementPaidIn : null,
            ProjectedCapital = showsPensionFund && StatementProjectedCapital is > 0 ? StatementProjectedCapital : null,
            ProjectedPension = StatementShowsPension && StatementProjectedPension is > 0 ? StatementProjectedPension : null,
            BuyInPotential = showsPensionFund && StatementBuyIn is > 0 ? StatementBuyIn : null,
            DocumentId = statement.DocumentId,
        };

        try
        {
            if (account.Id == 0)
            {
                await repository.SavePensionAccountAsync(account);
                _pensionAccounts.Add(account);
            }
            values.AccountId = account.Id;

            if (_statementLetterPath is { } letter)
                values.DocumentId = await FileStatementLetterAsync(letter, account, date);

            await repository.SavePensionStatementAsync(values);
        }
        catch (Exception ex)
        {
            StatementError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        account.Statements.Remove(statement);
        account.Statements.Add(values);
        account.Statements.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Id.CompareTo(b.Id));
        CloseStatementPanel();
        RefreshRetirement();
        if (EditingPensionAccount == account)
            RefreshStatementHistory();
        CurrentPage = AppPage.Retirement;
    }

    /// <summary>Files the letter in Documents as a statement for the account's person.</summary>
    private async Task<int> FileStatementLetterAsync(string path, PensionAccount account, DateOnly date)
    {
        var document = new HouseholdDocument
        {
            Kind = DocumentKind.Statement,
            Title = Loc.F("{0} statement {1}", PensionAccount.PillarShortName(account.Pillar), date.Year),
            Sender = account.Name,
            PersonId = account.PersonId,
            Date = date,
            AddedAt = DateTime.Now,
        };
        var personName = account.PersonId is null ? null : PersonName(account.PersonId);
        document.RelativePath = await Task.Run(() => _documentStore.Add(path, document, personName));
        await repository.AddDocumentAsync(document);
        Documents.Add(document);
        RefreshDocumentRows();
        return document.Id;
    }

    /// <summary>Opens the letter a statement was read from.</summary>
    [RelayCommand]
    private void OpenStatementLetter(PensionStatement statement)
    {
        if (Documents.FirstOrDefault(d => d.Id == statement.DocumentId) is { } document)
            OpenDocument(document);
    }

    private void CloseStatementPanel()
    {
        _statementCancellation?.Cancel();
        _statementCancellation = null;
        IsStatementPanelOpen = false;
        ResetStatementForm();
    }

    private void ResetStatementForm()
    {
        EditingStatement = null;
        _statementLetterPath = null;
        StatementLetterName = "";
        NewPensionAccountName = "";
        NewPensionPillarIndex = (int)PensionPillar.PensionFund;
        StatementBalance = null;
        StatementPaidIn = null;
        StatementProjectedCapital = null;
        StatementProjectedPension = null;
        StatementBuyIn = null;
        StatementAiStatus = "";
        IsStatementAiBusy = false;
        StatementError = null;
    }

    // ── Account panel ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsPensionAccountPanelOpen { get; set; }

    [ObservableProperty]
    public partial PensionAccount? EditingPensionAccount { get; set; }

    [ObservableProperty]
    public partial string PensionAccountName { get; set; } = "";

    [ObservableProperty]
    public partial int PensionPillarIndex { get; set; }

    [ObservableProperty]
    public partial PersonOption PensionAccountPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial string PensionAccountNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPensionAccountError))]
    public partial string? PensionAccountError { get; set; }

    public bool HasPensionAccountError => !string.IsNullOrEmpty(PensionAccountError);

    [ObservableProperty]
    public partial IReadOnlyList<PensionStatementRow> StatementHistory { get; set; } = [];

    [RelayCommand]
    private void EditPensionAccount(PensionAccount account)
    {
        CloseOtherPanels();
        EditingPensionAccount = account;
        PensionAccountName = account.Name;
        PensionPillarIndex = (int)account.Pillar;
        PensionAccountPerson = PersonOptions.FirstOrDefault(o => o.Id == account.PersonId) ?? PersonOption.Shared;
        PensionAccountNotes = account.Notes;
        PensionAccountError = null;
        RefreshStatementHistory();
        IsPensionAccountPanelOpen = true;
    }

    private void RefreshStatementHistory()
    {
        if (EditingPensionAccount is not { } account)
            return;
        StatementHistory = account.Statements
            .Select((s, i) => new PensionStatementRow(s, account.Pillar, i > 0 ? account.Statements[i - 1] : null))
            .Reverse()
            .ToList();
    }

    [RelayCommand]
    private async Task SavePensionAccountAsync()
    {
        if (EditingPensionAccount is not { } account)
            return;
        PensionAccountError = null;
        if (string.IsNullOrWhiteSpace(PensionAccountName))
        {
            PensionAccountError = Loc.T("Enter the name of the pension fund, bank or insurer.");
            return;
        }

        var (name, pillar, personId, notes) = (account.Name, account.Pillar, account.PersonId, account.Notes);
        account.Name = PensionAccountName.Trim();
        account.Pillar = (PensionPillar)Math.Clamp(PensionPillarIndex, 0, 4);
        account.PersonId = PensionAccountPerson.Id;
        account.Notes = PensionAccountNotes.Trim();
        try
        {
            await repository.SavePensionAccountAsync(account);
        }
        catch (Exception ex)
        {
            (account.Name, account.Pillar, account.PersonId, account.Notes) = (name, pillar, personId, notes);
            PensionAccountError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        ClosePensionAccountPanel();
        RefreshRetirement();
    }

    /// <summary>Removes the account and its figures. Letters stay in Documents.</summary>
    [RelayCommand]
    private async Task DeletePensionAccountAsync(PensionAccount account)
    {
        try
        {
            await repository.DeletePensionAccountAsync(account.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        _pensionAccounts.Remove(account);
        if (EditingPensionAccount == account)
            ClosePensionAccountPanel();
        RefreshRetirement();
    }

    [RelayCommand]
    private async Task DeleteStatementAsync(PensionStatement statement)
    {
        try
        {
            await repository.DeletePensionStatementAsync(statement.Id);
        }
        catch (Exception ex)
        {
            PensionAccountError = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        _pensionAccounts.FirstOrDefault(a => a.Id == statement.AccountId)?.Statements.Remove(statement);
        RefreshStatementHistory();
        RefreshRetirement();
    }

    private void ClosePensionAccountPanel()
    {
        IsPensionAccountPanelOpen = false;
        EditingPensionAccount = null;
        StatementHistory = [];
        PensionAccountError = null;
    }

    private void CloseRetirementPanels()
    {
        if (IsStatementPanelOpen)
            CloseStatementPanel();
        if (IsPensionAccountPanelOpen)
            ClosePensionAccountPanel();
    }
}

/// <summary>One account on the Retirement page, with its latest letter.</summary>
public sealed record PensionAccountRow(PensionAccount Account, string PersonName)
{
    internal PensionStatement? Latest => Account.Statements.Count > 0 ? Account.Statements[^1] : null;

    private PensionStatement? Previous => Account.Statements.Count > 1 ? Account.Statements[^2] : null;

    public string Name => Account.Name;

    public string PillarText => PensionAccount.PillarName(Account.Pillar);

    public bool IsShared => Account.PersonId is null;

    public bool HasStatement => Latest is not null;

    /// <summary>The balance, or the projected pension for AHV.</summary>
    public string ValueText => Latest switch
    {
        null => Loc.T("No statement yet"),
        _ when !PensionAccount.HasSavings(Account.Pillar) =>
            Latest.ProjectedPension is { } pension ? Loc.F("{0} / month", Format.Money(pension / 12)) : "–",
        _ => Format.Money(Latest.Balance),
    };

    public string AsOfText => Latest is null ? "" : Loc.F("As of {0}", Format.Date(Latest.Date));

    public bool HasChange => PensionAccount.HasSavings(Account.Pillar) && Previous is not null;

    public decimal Change => HasChange ? Latest!.Balance - Previous!.Balance : 0;

    public bool IsChangeNegative => Change < 0;

    public string ChangeText
    {
        get
        {
            if (!HasChange)
                return "";
            var percent = Previous!.Balance > 0 ? $" ({Change / Previous.Balance:+0.0%;−0.0%})" : "";
            return Loc.F("{0}{1} since {2}", Format.SignedMoney(Change), percent, Format.Date(Previous.Date));
        }
    }

    /// <summary>Pension fund projections, e.g. "At 65: CHF 480,000 · CHF 32,640 a year".</summary>
    public string DetailText
    {
        get
        {
            if (Latest is null)
                return "";
            var parts = new List<string>();
            if (Latest.ProjectedCapital is { } capital)
                parts.Add(Loc.F("At {0}: {1}", SwissPension.RetirementAge, Format.Money(capital)));
            if (Latest.ProjectedPension is { } pension && PensionAccount.HasSavings(Account.Pillar))
                parts.Add(Loc.F("{0} a year", Format.Money(pension)));
            if (Latest.BuyInPotential is { } buyIn)
                parts.Add(Loc.F("Buy-in possible: {0}", Format.Money(buyIn)));
            if (Latest.PaidIn is { } paidIn)
                parts.Add(Loc.F("Paid in: {0}", Format.Money(paidIn)));
            return string.Join(" · ", parts);
        }
    }

    public bool HasDetail => DetailText != "";
}

/// <summary>One letter in an account's history.</summary>
public sealed record PensionStatementRow(PensionStatement Statement, PensionPillar Pillar, PensionStatement? Previous)
{
    public string DateText => Format.Date(Statement.Date);

    public string ValueText => PensionAccount.HasSavings(Pillar)
        ? Format.Money(Statement.Balance)
        : Statement.ProjectedPension is { } pension ? Loc.F("{0} / month", Format.Money(pension / 12)) : "–";

    public string ChangeText => PensionAccount.HasSavings(Pillar) && Previous is not null
        ? Format.SignedMoney(Statement.Balance - Previous.Balance)
        : "";

    public bool IsChangeNegative => Previous is not null && Statement.Balance < Previous.Balance;

    public bool HasLetter => Statement.DocumentId is not null;
}

/// <summary>A person's pillar 3a payments this year against the limit.</summary>
public sealed record Pillar3aRow(int? PersonId, string PersonName, decimal Paid, decimal PaidByToday, decimal Limit, bool HasPensionFund)
{
    public decimal Left => Math.Max(0, Limit - Paid);

    public double Percent => Limit == 0 ? 0 : (double)Math.Min(1, Paid / Limit) * 100;

    public bool IsFull => Paid >= Limit;

    public bool IsOver => Paid > Limit;

    public string PaidText => Loc.F("{0} of {1}", Format.Money(Paid), Format.Money(Limit));

    public string StatusText => IsOver
        ? Loc.F("{0} over the limit; only the limit can be deducted", Format.Money(Paid - Limit))
        : IsFull
            ? Loc.T("Limit reached")
            : Loc.F("{0} left to pay in", Format.Money(Left));

    public string LimitNote => HasPensionFund
        ? Loc.T("Limit with a pension fund")
        : Loc.T("Without a pension fund: 20% of net earned income, up to this limit");
}
