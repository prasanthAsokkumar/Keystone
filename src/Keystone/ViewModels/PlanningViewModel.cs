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

/// <summary>A savings goal with its progress.</summary>
public sealed record GoalRow(SavingsGoal Goal, decimal Saved, string AccountName, DateOnly Today)
{
    public string Name => Goal.Name;

    public double Percent => Goal.Target <= 0 ? 0 : (double)Math.Min(1, Math.Max(0, Saved / Goal.Target)) * 100;

    public bool IsReached => Saved >= Goal.Target;

    public string ProgressText => Loc.F("{0} of {1}", Format.Money(Saved), Format.Money(Goal.Target));

    public int? MonthsLeft => Goal.TargetDate is { } date
        ? Math.Max(1, (date.Year - Today.Year) * 12 + date.Month - Today.Month)
        : null;

    /// <summary>What to put aside each month to get there in time.</summary>
    public decimal? MonthlyNeeded => MonthsLeft is { } months && !IsReached ? Math.Ceiling((Goal.Target - Saved) / months) : null;

    public string PlanText => IsReached
        ? Loc.T("Reached")
        : MonthlyNeeded is { } monthly
            ? Loc.F("{0} a month until {1}", Format.Money(monthly), Format.Month(Goal.TargetDate!.Value))
            : Loc.F("{0} to go", Format.Money(Goal.Target - Saved));

    public string SourceText => AccountName != "" ? Loc.F("In {0}", AccountName) : Loc.T("Tracked by hand");

    public bool CanPlanTransfer => !IsReached && Goal.AccountId is not null && MonthlyNeeded is > 0;
}

/// <summary>A scenario change as listed.</summary>
public sealed record ScenarioChangeRow(ScenarioChange Change, string Text);

/// <summary>A person in the retirement plan.</summary>
public sealed record RetirementPersonRow(PersonRetirement Plan, Person? Person)
{
    public string Name => Plan.Name == "" ? PersonOption.Shared.ToString() : Plan.Name;

    public string RetirementText => Plan.RetirementYear is { } year
        ? Loc.F("Retires in {0}, in {1} years", year, Math.Max(0, year - DateTime.Today.Year))
        : Loc.T("Enter the year of birth to plan");

    public string PensionText => Loc.F("{0} / month", Format.Money(Plan.PensionPerMonth));

    public string CapitalText => Format.Money(Plan.CapitalAtRetirement);

    public IReadOnlyList<string> WithdrawalLines => Plan.Withdrawals
        .Select(w => Loc.F("{0}: at {1}, in {2} · about {3}", w.Account, w.Age, w.Year, Format.Money(w.Amount)))
        .ToList();

    public bool HasWithdrawals => Plan.Withdrawals.Count > 0;

    /// <summary>One 3a account can only be taken out whole; several let the money come out over several years.</summary>
    public string WithdrawalHint => Plan.Withdrawals.Count switch
    {
        0 => "",
        1 => Loc.T("With one 3a account, all of it is taken out in one year. Opening a second and third account lets you spread it and pay less tax."),
        _ => Loc.T("One account a year keeps each withdrawal’s tax lower."),
    };
}

/// <summary>The Planning page: savings goals, what-if scenarios, the retirement gap and net worth.</summary>
/// <remarks>Its own view model; shared things (entries, accounts, the runway) come from the main one.</remarks>
public sealed partial class PlanningViewModel(MainViewModel main) : ViewModelBase
{
    /// <summary>0: goals, 1: what if. (The retirement plan and net worth have their own pages.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoalsTab), nameof(IsScenarioTab))]
    public partial int PlanningTab { get; set; }

    public bool IsGoalsTab { get => PlanningTab == 0; set { if (value) PlanningTab = 0; } }

    public bool IsScenarioTab { get => PlanningTab == 1; set { if (value) PlanningTab = 1; } }

    internal void RefreshPlanning()
    {
        if (main._isLoading)
            return;
        if (BirthYearRows.Count != main.People.Count)
            RefreshBirthYearRows();
        RefreshGoals();
        RefreshScenario();
        RefreshRetirementGap();
        RefreshNetWorth();
    }

    // ── Savings goals ───────────────────────────────────────────────────────

    /// <summary>Every savings goal.</summary>
    internal List<SavingsGoal> Goals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoalRows))]
    public partial IReadOnlyList<GoalRow> GoalRows { get; set; } = [];

    public bool HasGoalRows => GoalRows.Count > 0;

    public string EmergencyFundText => main.Summary.LivingCostsPerMonth > 0
        ? Loc.F("Emergency fund: 3 months of living costs is {0}", Format.Money(Math.Ceiling(main.Summary.LivingCostsPerMonth * 3 / 100) * 100))
        : "";

    internal async Task LoadGoalsAsync()
    {
        Goals.Clear();
        Goals.AddRange(await main.repository.GetAllAsync<SavingsGoal>());
    }

    private decimal GoalSaved(SavingsGoal goal) =>
        goal.AccountId is { } id && main.BankAccounts.FirstOrDefault(a => a.Id == id) is { } account
            ? main.AccountBalance(account, MainViewModel.Today)
            : goal.Saved;

    internal void RefreshGoals()
    {
        GoalRows = main.Sorted("goals", Goals.OrderBy(g => g.TargetDate ?? DateOnly.MaxValue).ThenBy(g => g.CreatedAt)
            .Select(g => new GoalRow(g, GoalSaved(g), main.BankAccounts.FirstOrDefault(a => a.Id == g.AccountId)?.Name ?? "", MainViewModel.Today)),
            r => r.Goal.SortOrder,
            new SortKey<GoalRow>("name", r => r.Goal.Name),
            new SortKey<GoalRow>("target", r => r.Goal.Target, DescendingFirst: true),
            new SortKey<GoalRow>("saved", r => r.Saved, DescendingFirst: true),
            new SortKey<GoalRow>("date", r => r.Goal.TargetDate));
        OnPropertyChanged(nameof(EmergencyFundText));
    }

    [ObservableProperty]
    public partial bool IsGoalPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoalFormTitle))]
    public partial SavingsGoal? EditingGoal { get; set; }

    public string GoalFormTitle => Loc.T(EditingGoal is null ? "Add savings goal" : "Edit savings goal");

    [ObservableProperty]
    public partial string GoalName { get; set; } = "";

    [ObservableProperty]
    public partial decimal? GoalTarget { get; set; }

    [ObservableProperty]
    public partial DateTime? GoalDate { get; set; }

    /// <summary>The account the money is in; "Track by hand" when <see cref="AccountOption.Account"/> is null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoalTracksByHand))]
    public partial GoalAccountOption? GoalAccount { get; set; }

    public IReadOnlyList<GoalAccountOption> GoalAccountOptions =>
        [new GoalAccountOption(null), .. main.BankAccounts.Select(a => new GoalAccountOption(a))];

    public bool GoalTracksByHand => GoalAccount?.Account is null;

    [ObservableProperty]
    public partial decimal? GoalSavedByHand { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoalError))]
    public partial string? GoalError { get; set; }

    public bool HasGoalError => !string.IsNullOrEmpty(GoalError);

    [RelayCommand]
    internal void NewGoal() => OpenGoalPanel(null, "", null);

    /// <summary>A goal of three months' living costs, the usual advice for an emergency fund.</summary>
    [RelayCommand]
    private void NewEmergencyFund() =>
        OpenGoalPanel(null, Loc.T("Emergency fund"), Math.Ceiling(main.Summary.LivingCostsPerMonth * 3 / 100) * 100);

    [RelayCommand]
    private void EditGoal(SavingsGoal goal) => OpenGoalPanel(goal, goal.Name, goal.Target);

    private void OpenGoalPanel(SavingsGoal? goal, string name, decimal? target)
    {
        main.CloseOtherPanels();
        EditingGoal = goal;
        GoalName = name;
        GoalTarget = target;
        GoalDate = goal?.TargetDate?.ToDateTime(TimeOnly.MinValue);
        OnPropertyChanged(nameof(GoalAccountOptions));
        // An existing goal keeps its account; a new one is suggested a savings account.
        GoalAccount = goal is not null
            ? GoalAccountOptions.FirstOrDefault(o => o.Account?.Id == goal.AccountId) ?? GoalAccountOptions[0]
            : GoalAccountOptions.FirstOrDefault(o => o.Account?.Kind == BankAccountKind.Savings) ?? GoalAccountOptions[0];
        GoalSavedByHand = goal?.Saved;
        GoalError = null;
        IsGoalPanelOpen = true;
    }

    [RelayCommand]
    private async Task SaveGoalAsync()
    {
        GoalError = null;
        if (string.IsNullOrWhiteSpace(GoalName) || GoalTarget is not > 0)
        {
            GoalError = Loc.T("Enter a name and how much you want to save.");
            return;
        }

        var existing = EditingGoal;
        var goal = new SavingsGoal
        {
            Id = existing?.Id ?? 0,
            Name = GoalName.Trim(),
            Target = GoalTarget.Value,
            TargetDate = GoalDate is { } date ? DateOnly.FromDateTime(date) : null,
            AccountId = GoalAccount?.Account?.Id,
            Saved = GoalAccount?.Account is null ? Math.Max(0, GoalSavedByHand ?? 0) : 0,
            CreatedAt = existing?.CreatedAt ?? DateTime.Now,
        };
        try
        {
            await main.repository.SaveAsync(goal, existing is null);
        }
        catch (Exception ex)
        {
            GoalError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (existing is not null)
            Goals.Remove(existing);
        Goals.Add(goal);
        ClosePlanningPanels();
        RefreshGoals();
    }

    [RelayCommand]
    private async Task DeleteGoalAsync(SavingsGoal goal)
    {
        if (await main.Deadlines.DeleteRecordAsync<SavingsGoal>(g => g.Id == goal.Id))
        {
            Goals.Remove(goal);
            RefreshGoals();
        }
    }

    /// <summary>Adds a monthly transfer from the main account into the goal's account, so the plan is in the runway.</summary>
    [RelayCommand]
    private async Task PlanGoalTransferAsync(GoalRow row)
    {
        if (row.MonthlyNeeded is not { } monthly || row.Goal.AccountId is not { } to || main.PrimaryAccount is not { } from || from.Id == to)
        {
            main.ErrorMessage = Loc.T("Choose a savings account other than the main account for this goal.");
            return;
        }

        try
        {
            var entry = await main.repository.AddTransactionAsync(new Transaction
            {
                Date = new DateOnly(MainViewModel.Today.Year, MainViewModel.Today.Month, 1).AddMonths(1),
                EndDate = row.Goal.TargetDate,
                Description = Loc.F("Saving for {0}", row.Goal.Name),
                Amount = monthly,
                Type = TransactionType.Transfer,
                Recurrence = Recurrence.Monthly,
                AccountId = from.Id,
                ToAccountId = to,
            });
            main.InsertSorted(entry);
        }
        catch (Exception ex)
        {
            main.ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        main.Recalculate();
    }

    // ── What if ─────────────────────────────────────────────────────────────

    private List<ScenarioChange> _scenario = [];

    public IReadOnlyList<string> ScenarioKindNames =>
        MainViewModel.Translate("Change an entry to a share", "Stop an entry", "Add a monthly amount", "Add a one-time amount");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScenarioNeedsEntry), nameof(ScenarioNeedsPercent), nameof(ScenarioNeedsAmount))]
    public partial int ScenarioKindIndex { get; set; }

    public bool ScenarioNeedsEntry => ScenarioKindIndex is 0 or 1;

    public bool ScenarioNeedsPercent => ScenarioKindIndex == 0;

    public bool ScenarioNeedsAmount => ScenarioKindIndex is 2 or 3;

    /// <summary>Repeating entries that can be changed or stopped.</summary>
    public IReadOnlyList<ScenarioEntryOption> ScenarioEntries => main.Transactions
        .Where(t => t.IsRecurring && t.CanStop && !t.IsTransfer)
        .OrderByDescending(t => t.IsIncome).ThenByDescending(t => t.SignedMonthlyAmount < 0 ? -t.SignedMonthlyAmount : t.SignedMonthlyAmount)
        .Select(t => new ScenarioEntryOption(t))
        .ToList();

    [ObservableProperty]
    public partial ScenarioEntryOption? ScenarioEntry { get; set; }

    [ObservableProperty]
    public partial decimal? ScenarioPercent { get; set; } = 80;

    [ObservableProperty]
    public partial decimal? ScenarioAmount { get; set; }

    [ObservableProperty]
    public partial bool ScenarioIsIncome { get; set; }

    [ObservableProperty]
    public partial string ScenarioDescription { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? ScenarioFrom { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScenarioError))]
    public partial string? ScenarioError { get; set; }

    public bool HasScenarioError => !string.IsNullOrEmpty(ScenarioError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScenarioChanges))]
    public partial IReadOnlyList<ScenarioChangeRow> ScenarioChangeRows { get; set; } = [];

    public bool HasScenarioChanges => ScenarioChangeRows.Count > 0;

    /// <summary>The runway with the changes, to compare with <see cref="Summary"/>.</summary>
    [ObservableProperty]
    public partial RunwaySummary ScenarioSummary { get; set; } = RunwaySummary.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ScenarioCompareRow> ScenarioComparison { get; set; } = [];

    internal void LoadScenario(string json) => _scenario = Scenario.Load(json);

    [RelayCommand]
    private async Task AddScenarioChangeAsync()
    {
        ScenarioError = null;
        var from = DateOnly.FromDateTime(ScenarioFrom ?? DateTime.Today);
        ScenarioChange change;
        switch (ScenarioKindIndex)
        {
            case 0 or 1 when ScenarioEntry is null:
                ScenarioError = Loc.T("Choose the entry that changes.");
                return;
            case 0:
                change = new ScenarioChange(ScenarioChangeKind.ChangeEntry, from, ScenarioEntry!.Transaction.Id,
                    Math.Clamp(ScenarioPercent ?? 100, 0, 1000));
                break;
            case 1:
                change = new ScenarioChange(ScenarioChangeKind.StopEntry, from, ScenarioEntry!.Transaction.Id);
                break;
            default:
                if (ScenarioAmount is not > 0 || string.IsNullOrWhiteSpace(ScenarioDescription))
                {
                    ScenarioError = Loc.T("Enter what it is and the amount.");
                    return;
                }
                change = new ScenarioChange(ScenarioKindIndex == 2 ? ScenarioChangeKind.AddMonthly : ScenarioChangeKind.AddOnce, from,
                    Amount: ScenarioAmount.Value, IsIncome: ScenarioIsIncome, Description: ScenarioDescription.Trim());
                break;
        }

        _scenario.Add(change);
        ScenarioAmount = null;
        ScenarioDescription = "";
        await SaveScenarioAsync();
        RefreshScenario();
    }

    [RelayCommand]
    private async Task RemoveScenarioChangeAsync(ScenarioChangeRow row)
    {
        _scenario.Remove(row.Change);
        await SaveScenarioAsync();
        RefreshScenario();
    }

    [RelayCommand]
    private async Task ClearScenarioAsync()
    {
        _scenario.Clear();
        await SaveScenarioAsync();
        RefreshScenario();
    }

    private async Task SaveScenarioAsync()
    {
        try
        {
            await main.repository.SetScenarioAsync(Scenario.Save(_scenario));
        }
        catch (Exception ex)
        {
            ScenarioError = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    private void RefreshScenario()
    {
        // Changes to entries that no longer exist are dropped.
        _scenario.RemoveAll(c => c.EntryId is { } id && main.Transactions.All(t => t.Id != id));
        ScenarioChangeRows = _scenario.Select(c => new ScenarioChangeRow(c, Scenario.Describe(c, main.Transactions.ToList()))).ToList();
        OnPropertyChanged(nameof(ScenarioEntries));
        if (ScenarioEntry is not null && ScenarioEntries.All(e => e.Transaction != ScenarioEntry.Transaction))
            ScenarioEntry = null;

        ScenarioSummary = _scenario.Count == 0
            ? main.Summary
            : RunwayCalculator.Calculate(main.TotalOpeningBalance, Scenario.Apply(main._entries, _scenario), MainViewModel.Today,
                financialYearStartMonth: main.FinancialYearStartIndex + 1);

        string Runway(RunwaySummary s) => s.RunwayMonths is { } months ? Loc.F("{0:0.#} months", months) : Loc.T("No limit");
        decimal InAYear(RunwaySummary s) => s.Projection.FirstOrDefault(p => p.Date >= MainViewModel.Today.AddYears(1)).Balance is var b && b != 0
            ? b
            : s.Projection.Count > 0 ? s.Projection[^1].Balance : s.CurrentBalance;
        ScenarioComparison =
        [
            new ScenarioCompareRow(Loc.T("Your runway"), Runway(main.Summary), Runway(ScenarioSummary)),
            new ScenarioCompareRow(Loc.T("Every day"), Format.SignedMoney(main.Summary.NetPerDay), Format.SignedMoney(ScenarioSummary.NetPerDay)),
            new ScenarioCompareRow(Loc.F("Saving in {0}", main.Summary.YearEnd.Year), Format.SignedMoney(main.Summary.YearSavings),
                Format.SignedMoney(ScenarioSummary.YearSavings)),
            new ScenarioCompareRow(Loc.T("Cash in a year"), Format.Money(InAYear(main.Summary)), Format.Money(InAYear(ScenarioSummary))),
        ];
    }

    // ── Retirement gap ──────────────────────────────────────────────────────

    [ObservableProperty]
    public partial decimal? RetirementNeedPercent { get; set; } = 80;

    partial void OnRetirementNeedPercentChanged(decimal? value)
    {
        if (main._isLoading)
            return;
        _ = SaveRetirementNeedAsync();
        RefreshRetirementGap();
    }

    private async Task SaveRetirementNeedAsync()
    {
        try
        {
            await main.repository.SetRetirementNeedAsync((int)Math.Clamp(RetirementNeedPercent ?? 80, 10, 150));
        }
        catch (Exception ex)
        {
            main.ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    [ObservableProperty]
    public partial RetirementGap? RetirementPlan { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<RetirementPersonRow> RetirementPeople { get; set; } = [];

    public string RetirementNeedText => RetirementPlan is { } plan ? Loc.F("{0} / month", Format.Money(plan.NeedPerMonth)) : "–";

    public string RetirementIncomeText => RetirementPlan is { } plan
        ? Loc.F("{0} / month", Format.Money(plan.PensionPerMonth + plan.CapitalPerMonth))
        : "–";

    public string RetirementIncomeDetail => RetirementPlan is { } plan
        ? Loc.F("Pensions {0} · savings {1} over {2} years", Format.Money(plan.PensionPerMonth), Format.Money(plan.CapitalPerMonth),
            RetirementPlanner.CapitalYears)
        : "";

    public string RetirementGapText => RetirementPlan is { } plan
        ? plan.GapPerMonth > 0 ? Loc.F("{0} / month", Format.Money(plan.GapPerMonth)) : Loc.T("Covered")
        : "–";

    public bool HasRetirementGap => RetirementPlan is { GapPerMonth: > 0 };

    public double RetirementCoveredPercent => RetirementPlan is { } plan ? (double)plan.CoveredShare * 100 : 0;

    public bool HasRetirementData => main.Retirement.All.Any(a => a.Statements.Count > 0);

    private void RefreshRetirementGap()
    {
        // Shared entries have no person, which a dictionary can't use as a key; a lookup can.
        var pillar3a = main.Retirement.Pillar3aRowsFor(PersonOption.Everyone, MainViewModel.Today.Year);
        RetirementPlan = RetirementPlanner.Plan(main.Summary.LivingCostsPerMonth, (int)(RetirementNeedPercent ?? 80), main.People, main.Retirement.All,
            id => pillar3a.Where(r => r.PersonId == id).Sum(r => r.Paid), MainViewModel.Today.Year);
        RetirementPeople = RetirementPlan.People
            .Select(p => new RetirementPersonRow(p, main.People.FirstOrDefault(x => x.Id == p.PersonId)))
            .ToList();
        OnPropertyChanged(nameof(RetirementNeedText));
        OnPropertyChanged(nameof(RetirementIncomeText));
        OnPropertyChanged(nameof(RetirementIncomeDetail));
        OnPropertyChanged(nameof(RetirementGapText));
        OnPropertyChanged(nameof(HasRetirementGap));
        OnPropertyChanged(nameof(RetirementCoveredPercent));
        OnPropertyChanged(nameof(HasRetirementData));
    }

    /// <summary>Each household member's year of birth, editable on the retirement tab.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BirthYearRow> BirthYearRows { get; set; } = [];

    internal void RefreshBirthYearRows() =>
        BirthYearRows = main.People.Select(p => new BirthYearRow(p, year => _ = SetBirthYearAsync(p, year))).ToList();

    /// <summary>Sets a person's year of birth, which the retirement plan counts from.</summary>
    public async Task SetBirthYearAsync(Person person, int? year)
    {
        var previous = person.BirthYear;
        person.BirthYear = year is >= 1900 and <= 2100 ? year : null;
        try
        {
            await main.repository.UpdatePersonAsync(person);
        }
        catch (Exception ex)
        {
            person.BirthYear = previous;
            main.ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        RefreshRetirementGap();
    }

    // ── Net worth ───────────────────────────────────────────────────────────

    private const int NetWorthMonths = 24;

    [ObservableProperty]
    public partial IReadOnlyList<BalancePoint> NetWorthHistory { get; set; } = [];

    [ObservableProperty]
    public partial string NetWorthTodayText { get; set; } = "";

    [ObservableProperty]
    public partial string NetWorthChangeText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsNetWorthDown { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TaxLine> NetWorthParts { get; set; } = [];

    /// <summary>Cash in all accounts, retirement savings (as of each account's latest letter), other assets, minus debts.</summary>
    public decimal NetWorthOn(DateOnly date) => CashOn(date) + RetirementSavingsOn(date) + main.Assets.AssetsOn(date) - DebtsOn(date);

    private decimal CashOn(DateOnly date) => main.BankAccounts.Sum(a => main.AccountBalance(a, date));

    private decimal RetirementSavingsOn(DateOnly date) => main.Retirement.All
        .Where(a => PensionAccount.HasSavings(a.Pillar))
        .Sum(a => a.Statements.LastOrDefault(s => s.Date <= date)?.Balance ?? 0);

    private decimal DebtsOn(DateOnly date) => main.DebtPlans.Sum(p => p.Debt.StartDate <= date ? p.OutstandingOn(date) : 0);

    internal void RefreshNetWorth()
    {
        var first = new DateOnly(MainViewModel.Today.Year, MainViewModel.Today.Month, 1).AddMonths(-NetWorthMonths + 1);
        var points = new List<BalancePoint>();
        for (var month = first; month <= MainViewModel.Today; month = month.AddMonths(1))
        {
            var end = month.AddMonths(1).AddDays(-1);
            var date = end < MainViewModel.Today ? end : MainViewModel.Today;
            points.Add(new BalancePoint(date, Math.Round(NetWorthOn(date), 2)));
        }
        NetWorthHistory = points;

        var now = NetWorthOn(MainViewModel.Today);
        var yearAgo = NetWorthOn(MainViewModel.Today.AddYears(-1));
        NetWorthTodayText = Format.Money(now);
        NetWorthChangeText = Loc.F("{0} in the last 12 months", Format.SignedMoney(now - yearAgo));
        IsNetWorthDown = now < yearAgo;
        NetWorthParts =
        [
            new TaxLine(Loc.T("Cash in accounts"), Loc.Plural(main.BankAccounts.Count, "{0} account", "{0} accounts"), Format.Money(CashOn(MainViewModel.Today))),
            new TaxLine(Loc.T("Retirement savings"), Loc.T("Pension fund, vested benefits, pillar 3a and 3b"), Format.Money(RetirementSavingsOn(MainViewModel.Today))),
            new TaxLine(Loc.T("Investments and other assets"), Loc.T("Securities, crypto, property, vehicles…"), Format.Money(main.Assets.AssetsOn(MainViewModel.Today))),
            new TaxLine(Loc.T("Debts"), "", Format.SignedMoney(-DebtsOn(MainViewModel.Today))),
        ];
    }

    internal void ClosePlanningPanels()
    {
        IsGoalPanelOpen = false;
        EditingGoal = null;
        main.Assets.CloseAssetPanel();
        main.CloseHouseholdPanels();
    }
}

/// <summary>A repeating entry offered in the what-if form.</summary>
public sealed record ScenarioEntryOption(Transaction Transaction)
{
    public override string ToString() =>
        $"{Transaction.Description} ({Format.SignedMoney(Transaction.SignedAmount)} · {Transaction.RecurrenceName})";
}

/// <summary>One figure now and with the scenario's changes.</summary>
public sealed record ScenarioCompareRow(string Label, string Now, string WithChanges)
{
    public bool IsDifferent => Now != WithChanges;
}

/// <summary>A household member's year of birth, saved when changed.</summary>
public sealed partial class BirthYearRow(Person person, Action<int?> save) : ObservableObject
{
    public string Name => person.Name;

    public decimal? Year
    {
        get => person.BirthYear;
        set
        {
            var year = value is { } v ? (int?)Math.Round(v) : null;
            if (year == person.BirthYear)
                return;
            save(year);
            OnPropertyChanged();
        }
    }
}

/// <summary>Where a goal's money is kept; no account means tracked by hand.</summary>
public sealed record GoalAccountOption(BankAccount? Account)
{
    public override string ToString() => Account?.Name ?? Loc.T("Track by hand");
}
