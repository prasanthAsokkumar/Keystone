using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

public sealed record CategoryRow(string Category, string AmountText, string ShareText, double Percent);

public enum AppPage
{
    Overview,
    Transactions,
    Documents,
    Tasks,
    Appointments,
    Notes,
    Debts,
    Assets,
    Insurance,
    Contracts,
    Retirement,
    Taxes,
    Planning,
    Household,
    Year,
    Settings,
}

/// <summary>A payment or income in the next 30 days, for the Overview's "Coming up" list.</summary>
public sealed record UpcomingRow(string DayText, string MonthText, string Description, string DetailText, string AmountText, bool IsIncome);

public enum DebtRepayment
{
    Monthly,
    AllAtOnce,
    InterestOnly,
}

public partial class MainViewModel : ViewModelBase
{
    private readonly KeystoneRepository repository;

    public MainViewModel(KeystoneRepository repository)
    {
        this.repository = repository;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private bool _isLoading;

    /// <summary>User entries plus generated debt payments; what all calculations run on.</summary>
    private List<Transaction> _entries = [];

    public ObservableCollection<Transaction> Transactions { get; } = [];

    public IReadOnlyList<string> MonthNames =>
        CultureInfo.CurrentCulture.DateTimeFormat.MonthNames.Take(12).ToList();

    // ── Settings ────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial decimal? StartingBalance { get; set; } = 0m;

    /// <summary>Zero-based index into <see cref="MonthNames"/> for the financial year start.</summary>
    [ObservableProperty]
    public partial int FinancialYearStartIndex { get; set; }

    // ── Navigation and side panel ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsOverviewPage), nameof(IsTransactionsPage), nameof(IsDocumentsPage), nameof(IsTasksPage), nameof(IsAppointmentsPage), nameof(IsNotesPage), nameof(IsDebtsPage), nameof(IsAssetsPage), nameof(IsRetirementPage), nameof(IsInsurancePage), nameof(IsTaxesPage), nameof(IsContractsPage), nameof(IsPlanningPage), nameof(IsHouseholdPage),
        nameof(IsYearPage), nameof(IsSettingsPage), nameof(PageTitle), nameof(PageSubtitle), nameof(DropHint),
        nameof(DropFilesAsDocument), nameof(DropDetailText), nameof(NewEntryText))]
    public partial AppPage CurrentPage { get; set; }

    public bool IsOverviewPage { get => CurrentPage == AppPage.Overview; set { if (value) CurrentPage = AppPage.Overview; } }

    public bool IsTransactionsPage { get => CurrentPage == AppPage.Transactions; set { if (value) CurrentPage = AppPage.Transactions; } }

    public bool IsDocumentsPage { get => CurrentPage == AppPage.Documents; set { if (value) CurrentPage = AppPage.Documents; } }

    public bool IsTasksPage { get => CurrentPage == AppPage.Tasks; set { if (value) CurrentPage = AppPage.Tasks; } }

    public bool IsAppointmentsPage { get => CurrentPage == AppPage.Appointments; set { if (value) CurrentPage = AppPage.Appointments; } }

    public bool IsNotesPage { get => CurrentPage == AppPage.Notes; set { if (value) CurrentPage = AppPage.Notes; } }

    public bool IsDebtsPage { get => CurrentPage == AppPage.Debts; set { if (value) CurrentPage = AppPage.Debts; } }

    public bool IsAssetsPage { get => CurrentPage == AppPage.Assets; set { if (value) CurrentPage = AppPage.Assets; } }

    public bool IsRetirementPage { get => CurrentPage == AppPage.Retirement; set { if (value) CurrentPage = AppPage.Retirement; } }

    public bool IsInsurancePage { get => CurrentPage == AppPage.Insurance; set { if (value) CurrentPage = AppPage.Insurance; } }

    public bool IsContractsPage { get => CurrentPage == AppPage.Contracts; set { if (value) CurrentPage = AppPage.Contracts; } }

    public bool IsPlanningPage { get => CurrentPage == AppPage.Planning; set { if (value) CurrentPage = AppPage.Planning; } }

    public bool IsHouseholdPage { get => CurrentPage == AppPage.Household; set { if (value) CurrentPage = AppPage.Household; } }

    public bool IsTaxesPage { get => CurrentPage == AppPage.Taxes; set { if (value) CurrentPage = AppPage.Taxes; } }

    public bool IsYearPage { get => CurrentPage == AppPage.Year; set { if (value) CurrentPage = AppPage.Year; } }

    public bool IsSettingsPage { get => CurrentPage == AppPage.Settings; set { if (value) CurrentPage = AppPage.Settings; } }

    public string PageTitle => CurrentPage switch
    {
        AppPage.Transactions => Loc.T("Transactions"),
        AppPage.Documents => Loc.T("Documents"),
        AppPage.Tasks => Loc.T("Tasks"),
        AppPage.Appointments => Loc.T("Appointments"),
        AppPage.Notes => Loc.T("Notes"),
        AppPage.Debts => Loc.T("Debts"),
        AppPage.Assets => Loc.T("Assets"),
        AppPage.Retirement => Loc.T("Retirement"),
        AppPage.Insurance => Loc.T("Insurance"),
        AppPage.Taxes => Loc.T("Taxes"),
        AppPage.Contracts => Loc.T("Contracts & dates"),
        AppPage.Planning => Loc.T("Planning"),
        AppPage.Household => Loc.T("Household"),
        AppPage.Year => Loc.T("Year overview"),
        AppPage.Settings => Loc.T("Settings"),
        _ => Greeting,
    };

    public string PageSubtitle => CurrentPage switch
    {
        AppPage.Transactions => Loc.T("What comes in and what goes out, each on its own tab"),
        AppPage.Documents => Loc.T("Bills, letters and notices for everyone in the household"),
        AppPage.Tasks => Loc.T("What needs to be done, and by when"),
        AppPage.Appointments => Loc.T("Doctor’s visits, meetings and everything else with a time, in step with your calendar"),
        AppPage.Notes => Loc.T("Thoughts, ideas and plans for your projects"),
        AppPage.Debts => Loc.T("What you owe and when it’s paid off"),
        AppPage.Assets => Loc.T("Investments, crypto, property and other things of value"),
        AppPage.Retirement => Loc.T("Your Swiss retirement provision: AHV, pension fund and pillar 3a"),
        AppPage.Insurance => Loc.T("What your policies cost, and by when to cancel them"),
        AppPage.Taxes => Loc.T("Everything you need for your tax return, gathered in one place"),
        AppPage.Contracts => Loc.T("Contracts, important dates and warranties, with their deadlines"),
        AppPage.Planning => Loc.T("Savings goals, what-if scenarios, retirement and net worth"),
        AppPage.Household => Loc.T("Everyone at home: contacts, health, medicines, work and sick days"),
        AppPage.Year => Loc.T("Income and spending month by month"),
        AppPage.Settings => Loc.T("Accounts, reminders, calendar, backup, household and language"),
        _ => Loc.F("{0:dddd, d MMMM} · Your household at a glance", Today),
    };

    /// <summary>The side panel with the add/edit entry form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsEntryPanelOpen { get; set; }

    /// <summary>The side panel with the add debt form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsDebtPanelOpen { get; set; }

    /// <summary>The side panel listing entries found in an imported document.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsImportPanelOpen { get; set; }

    public bool IsPanelOpen => IsEntryPanelOpen || IsDebtPanelOpen || IsImportPanelOpen || IsDocumentPanelOpen || IsTaskPanelOpen ||
                               IsStatementPanelOpen || IsPensionAccountPanelOpen || IsBulkPanelOpen ||
                               IsInsurancePanelOpen || IsAccountPanelOpen || IsContractPanelOpen ||
                               IsImportantDatePanelOpen || IsWarrantyPanelOpen || IsGoalPanelOpen || IsAppointmentPanelOpen ||
                               IsAssetPanelOpen || IsPersonPanelOpen || IsMedicinePanelOpen || IsEmploymentPanelOpen ||
                               IsSickLeavePanelOpen;

    /// <summary>
    /// A dropped file is filed as a document on the Documents page, or when import is off;
    /// elsewhere it is read for income and expenses.
    /// </summary>
    public bool DropFilesAsDocument => IsDocumentsPage || IsRetirementPage || !IsImportEnabled;

    public string DropHint => IsRetirementPage
        ? Loc.T("Drop to add a pension statement")
        : DropFilesAsDocument
            ? Loc.T("Drop to add to your documents")
            : Loc.T("Drop to find income and expenses");

    public string DropDetailText => DropFilesAsDocument
        ? Loc.F("Saved to {0}", DocumentsFolder)
        : Loc.F("{0}. Read on this Mac by your local model.", DocumentReader.SupportedFilesText);

    private void CloseOtherPanels()
    {
        FollowUpHint = "";
        if (EditingTransaction is not null)
            ResetForm();
        IsEntryPanelOpen = false;
        IsDebtPanelOpen = false;
        CloseImport();
        if (IsDocumentPanelOpen)
            CloseDocumentPanel();
        CloseTaskPanel();
        CloseAppointmentPanel();
        CloseRetirementPanels();
        CloseInsurancePanel();
        CloseAccountPanel();
        CloseDeadlinePanels();
        ClosePlanningPanels();
        if (IsBulkPanelOpen)
            CloseBulkPanel();
    }

    [RelayCommand]
    private void NewEntry()
    {
        ErrorMessage = null;
        if (EditingTransaction is not null)
            ResetForm();
        IsDebtPanelOpen = false;
        IsImportPanelOpen = false;
        CloseDocumentPanel();
        CloseTaskPanel();
        CloseAppointmentPanel();
        CloseRetirementPanels();
        CloseInsurancePanel();
        CloseAccountPanel();
        CloseDeadlinePanels();
        ClosePlanningPanels();
        if (IsBulkPanelOpen)
            CloseBulkPanel();
        // On the Transactions page, a new entry is of the open tab's kind.
        if (IsTransactionsPage)
        {
            NewIsTransfer = TransactionsTab == 2 && HasSeveralAccounts;
            NewIsIncome = TransactionsTab == 0;
            // The Recurring tab is for costs that come back; the Expenses tab for one-time spending.
            if (TransactionsTab == 3)
                NewRecurrence = Recurrence.Monthly;
            else if (TransactionsTab == 1)
                NewRecurrence = Recurrence.OneTime;
        }
        IsEntryPanelOpen = true;
    }

    [RelayCommand]
    private void ShowTransactions() => CurrentPage = AppPage.Transactions;

    [RelayCommand]
    private void NewDebt()
    {
        DebtError = null;
        if (EditingDebt is not null)
            ResetDebtForm();
        IsEntryPanelOpen = false;
        IsImportPanelOpen = false;
        CloseDocumentPanel();
        CloseTaskPanel();
        CloseAppointmentPanel();
        CloseRetirementPanels();
        CloseInsurancePanel();
        CloseAccountPanel();
        CloseDeadlinePanels();
        ClosePlanningPanels();
        if (IsBulkPanelOpen)
            CloseBulkPanel();
        IsDebtPanelOpen = true;
    }

    [RelayCommand]
    private void ClosePanel()
    {
        FollowUpHint = "";
        if (EditingTransaction is not null)
            ResetForm();
        ErrorMessage = null;
        DebtError = null;
        if (EditingDebt is not null)
            ResetDebtForm();
        IsEntryPanelOpen = false;
        IsDebtPanelOpen = false;
        CloseImport();
        CloseDocumentPanel();
        CloseTaskPanel();
        CloseAppointmentPanel();
        CloseRetirementPanels();
        CloseInsurancePanel();
        CloseAccountPanel();
        CloseDeadlinePanels();
        ClosePlanningPanels();
        if (IsBulkPanelOpen)
            CloseBulkPanel();
    }

    // ── Runway dashboard ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(BalanceText), nameof(NetPositionText), nameof(NetWorthText), nameof(RunwayText), nameof(RunwayDetailText),
        nameof(RunwayCauseText), nameof(HasRunwayCause), nameof(RunwayStatusText), nameof(RunwayIsCritical),
        nameof(RunwayIsWarning), nameof(RunwayIsHealthy), nameof(RunwayBarValue))]
    [NotifyPropertyChangedFor(
        nameof(IncomePerDayText), nameof(ExpensesPerDayText), nameof(NetPerDayText), nameof(NetPerDayIsNegative),
        nameof(YearSavingsTitle), nameof(YearSavingsText), nameof(YearSavingsIsNegative), nameof(YearSavingsDetailText),
        nameof(YearSavingsCoverText), nameof(WithoutIncomeText), nameof(WithoutIncomeDetailText))]
    public partial RunwaySummary Summary { get; set; } = RunwaySummary.Empty;

    public string BalanceText => Format.Money(Summary.CurrentBalance);

    public string RunwayText => Summary.RunwayMonths switch
    {
        null => Loc.T("No limit"),
        <= 0 => Loc.F("{0:0.0} months", 0),
        { } months => Loc.F("{0:0.0} months", months),
    };

    public string RunwayDetailText => Summary switch
    {
        { CurrentBalance: <= 0 } => Loc.T("You’re out of cash"),
        { ZeroCashDate: { } date } => Loc.F("Cash runs out on {0}", Format.Date(date)),
        _ => Loc.T("Your income covers your spending"),
    };

    public string RunwayCauseText => Summary.ZeroCashCause is { } cause ? Loc.F("That’s the day “{0}” is due", cause) : "";

    public bool HasRunwayCause => Summary.ZeroCashCause is not null;

    public bool RunwayIsCritical => Summary.RunwayMonths is < 3;

    public bool RunwayIsWarning => Summary.RunwayMonths is >= 3 and < 12;

    public bool RunwayIsHealthy => Summary.RunwayMonths is null or >= 12;

    public string RunwayStatusText => Loc.T(RunwayIsCritical ? "Critical" : RunwayIsWarning ? "Watch closely" : "Healthy");

    /// <summary>Runway on a 0–12 month scale, for the bar under the headline.</summary>
    public double RunwayBarValue => Math.Clamp(Summary.RunwayMonths ?? 12, 0, 12);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpcoming))]
    public partial IReadOnlyList<UpcomingRow> Upcoming { get; set; } = [];

    public bool HasUpcoming => Upcoming.Count > 0;

    private void RefreshUpcoming()
    {
        var names = People.ToDictionary(p => p.Id, p => p.Name);
        Upcoming = Schedule.Expand(_entries, Today.AddDays(1), Today.AddDays(30))
            .Select(o =>
            {
                var t = o.Source;
                var category = t.IsTransfer ? AccountText(t) is { Length: > 0 } accounts ? $"{Loc.T("Transfer")} · {accounts}" : Loc.T("Transfer")
                    : Categories.Display(t.Category);
                var detail = t.IsDebtPayment ? Loc.T("Debt")
                    : t.PersonId is { } id && names.TryGetValue(id, out var name) ? $"{category} · {name}".TrimStart(' ', '·')
                    : category;
                return new UpcomingRow(
                    o.Date.Day.ToString(CultureInfo.CurrentCulture),
                    o.Date.ToString("MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture),
                    t.Description,
                    string.IsNullOrWhiteSpace(detail) ? t.RecurrenceName : detail,
                    t.IsTransfer ? Format.Money(o.Amount) : Format.SignedMoney(o.SignedAmount),
                    t.IsIncome);
            })
            .ToList();
    }

    public string IncomePerDayText => Format.SignedMoney(Summary.IncomePerDay);

    public string ExpensesPerDayText => Format.SignedMoney(-Summary.ExpensesPerDay);

    public string NetPerDayText => Format.SignedMoney(Summary.NetPerDay);

    public bool NetPerDayIsNegative => Summary.NetPerDay < 0;

    public string YearSavingsTitle => Loc.F("Saving in {0}", FinancialYearReport.LabelFor(Summary.YearStart));

    public string YearSavingsText => Format.SignedMoney(Summary.YearSavings);

    public bool YearSavingsIsNegative => Summary.YearSavings < 0;

    public string YearSavingsDetailText =>
        Loc.F("{0} so far, {1} more expected by {2}",
            Format.SignedMoney(Summary.YearSavedSoFar), Format.SignedMoney(Summary.YearExpectedRest), Format.Date(Summary.YearEnd));

    public string YearSavingsCoverText => Summary.YearSavingsMonths is { } months
        ? Loc.F("Enough to live on for {0:0.0} months", months)
        : Loc.T("Not enough to cover any months of living costs");

    public string WithoutIncomeText => Summary.MonthsWithoutIncome switch
    {
        null => "–",
        { } months => Loc.F("{0:0.0} months", months),
    };

    public string WithoutIncomeDetailText => Summary.LivingCostsPerMonth > 0
        ? Loc.F("Your cash covers living costs of {0} / month. Debt repayments not included.", Format.Money(Summary.LivingCostsPerMonth))
        : Loc.T("No living costs recorded yet.");

    public string AveragingText => Summary.UsedPartialMonth
        ? Loc.T("One-time entries are averaged over this month so far.")
        : Loc.Plural(Summary.AveragingMonths, "One-time entries are averaged over the last month.",
            "One-time entries are averaged over the last {0} months.");

    /// <summary>The balance in the user's bank account today, used to correct the starting balance.</summary>
    [ObservableProperty]
    public partial decimal? BankBalance { get; set; }


    // ── Financial year ──────────────────────────────────────────────────────

    [ObservableProperty]
    public partial int SelectedYearStart { get; set; } = Today.Year;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YearIncomeText))]
    [NotifyPropertyChangedFor(nameof(YearExpensesText))]
    [NotifyPropertyChangedFor(nameof(YearNetText))]
    [NotifyPropertyChangedFor(nameof(YearPlannedText))]
    [NotifyPropertyChangedFor(nameof(CategoryRows))]
    [NotifyPropertyChangedFor(nameof(HasCategoryRows))]
    public partial FinancialYearReport Report { get; set; } = FinancialYearReport.Empty;

    public string YearIncomeText => Format.Money(Report.TotalIncome);

    public string YearExpensesText => Format.Money(Report.TotalExpenses);

    public string YearNetText => Format.SignedMoney(Report.Net);

    public string YearPlannedText => Report.PlannedExpenses > 0
        ? Loc.F("Includes {0} planned after today", Format.Money(Report.PlannedExpenses))
        : Loc.T("All recorded");

    public IReadOnlyList<CategoryRow> CategoryRows => Report.ExpensesByCategory
        .Select(c => new CategoryRow(Categories.Display(c.Category), Format.Money(c.Amount), c.Share.ToString("P0", CultureInfo.CurrentCulture), c.Share * 100))
        .ToList();

    public bool HasCategoryRows => Report.ExpensesByCategory.Count > 0;

    // ── Debts ───────────────────────────────────────────────────────────────

    public ObservableCollection<DebtItemViewModel> Debts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasDebts), nameof(TotalDebtText), nameof(NetPositionText), nameof(NetWorthText), nameof(MonthlyDebtPaymentsText),
        nameof(DebtFreeText), nameof(DebtInterestText), nameof(HasNeverEndingDebt), nameof(HasDebtWithoutEnd))]
    public partial IReadOnlyList<DebtPlan> DebtPlans { get; set; } = [];

    private decimal TotalDebt => DebtPlans.Sum(p => p.OutstandingOn(Today));

    public bool HasDebts => DebtPlans.Count > 0;

    public string TotalDebtText => Format.Money(TotalDebt);

    public string NetPositionText => Loc.F("Cash minus debt: {0}", Format.SignedMoney(Summary.CurrentBalance - TotalDebt));

    public string NetWorthText => Format.SignedMoney(Summary.CurrentBalance - TotalDebt);

    public string DatabasePath { get; } = KeystoneDbContext.DefaultDatabasePath();

    public string MonthlyDebtPaymentsText => Format.Money(DebtPlans
        .SelectMany(p => p.Entries)
        .Where(e => !e.IsExtra && e.Date > Today && e.Date <= Today.AddMonths(1))
        .Sum(e => e.Payment));

    /// <summary>True when some debt's payments don't cover its interest.</summary>
    public bool HasNeverEndingDebt => DebtPlans.Any(p => !p.PaysOff && !p.IsOpenEnded);

    /// <summary>Some debt has no payoff date, either never-ending or open-ended interest-only.</summary>
    public bool HasDebtWithoutEnd => DebtPlans.Any(p => !p.PaysOff);

    public string DebtFreeText =>
        TotalDebt <= 0 ? Loc.T("No debt")
        : HasNeverEndingDebt ? Loc.T("Never")
        : HasDebtWithoutEnd ? Loc.T("When repaid")
        : Format.Month(DebtPlans.Max(p => p.PayoffDate!.Value));

    /// <summary>Debts without a payoff date are left out; their interest has no end.</summary>
    public string DebtInterestText =>
        Format.Money(DebtPlans.Where(p => p.PaysOff).Sum(p => p.InterestAfter(Today)));

    [ObservableProperty]
    public partial string NewDebtName { get; set; } = "";

    [ObservableProperty]
    public partial decimal? NewDebtBalance { get; set; }

    [ObservableProperty]
    public partial decimal? NewDebtRate { get; set; } = 0m;

    /// <summary>Monthly interest per 1,000 owed, for interest-only debts.</summary>
    [ObservableProperty]
    public partial decimal? NewDebtInterestPer1000 { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewDebtIsMonthly), nameof(NewDebtIsLumpSum), nameof(NewDebtIsInterestOnly))]
    [NotifyPropertyChangedFor(nameof(NewDebtIsNotInterestOnly), nameof(DebtDateLabel), nameof(DebtPaymentLabel))]
    public partial DebtRepayment NewDebtRepayment { get; set; }

    public bool NewDebtIsMonthly
    {
        get => NewDebtRepayment == DebtRepayment.Monthly;
        set { if (value) NewDebtRepayment = DebtRepayment.Monthly; }
    }

    public bool NewDebtIsLumpSum
    {
        get => NewDebtRepayment == DebtRepayment.AllAtOnce;
        set { if (value) NewDebtRepayment = DebtRepayment.AllAtOnce; }
    }

    public bool NewDebtIsInterestOnly
    {
        get => NewDebtRepayment == DebtRepayment.InterestOnly;
        set { if (value) NewDebtRepayment = DebtRepayment.InterestOnly; }
    }

    public bool NewDebtIsNotInterestOnly => !NewDebtIsInterestOnly;

    [ObservableProperty]
    public partial decimal? NewDebtPayment { get; set; }

    [ObservableProperty]
    public partial DateTime? NewDebtDate { get; set; } = DateTime.Today.AddMonths(1);

    public string DebtDateLabel => NewDebtRepayment switch
    {
        DebtRepayment.AllAtOnce => Loc.T("Due on"),
        DebtRepayment.InterestOnly => Loc.T("Next interest payment on"),
        _ => Loc.T("First payment on"),
    };

    /// <summary>The debt being changed in the debt form, or null when adding one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingDebt), nameof(DebtFormTitle), nameof(DebtSubmitText), nameof(DebtBalanceLabel))]
    public partial DebtItemViewModel? EditingDebt { get; set; }

    public bool IsEditingDebt => EditingDebt is not null;

    public string DebtFormTitle => Loc.T(IsEditingDebt ? "Edit debt" : "Add debt");

    public string DebtSubmitText => Loc.T(IsEditingDebt ? "Save changes" : "Add debt");

    /// <summary>When editing, the amount is what was owed on the day the debt was recorded; payments since then are counted on top.</summary>
    public string DebtBalanceLabel => EditingDebt is { } item
        ? Loc.F("Amount owed on {0}", Format.Date(item.Debt.StartDate))
        : Loc.T("Amount still owed today");

    /// <summary>Extra payments and borrowing recorded for the debt being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDebtPaymentRows))]
    public partial IReadOnlyList<DebtPaymentRow> DebtPaymentRows { get; set; } = [];

    public bool HasDebtPaymentRows => DebtPaymentRows.Count > 0;

    public string DebtPaymentLabel =>
        Loc.T(NewDebtIsInterestOnly ? "Principal repaid each month (optional)" : "Monthly payment");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDebtError))]
    public partial string? DebtError { get; set; }

    public bool HasDebtError => !string.IsNullOrEmpty(DebtError);

    // ── Entries table ───────────────────────────────────────────────────────

    /// <summary>
    /// Income, one-time expenses, recurring expenses (monthly, quarterly, yearly) and transfers are kept apart, each
    /// on its own tab with its own totals: 0 income, 1 expenses, 2 transfers, 3 recurring.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIncomeTab), nameof(IsExpensesTab), nameof(IsTransfersTab), nameof(IsRecurringTab),
        nameof(NewEntryText), nameof(HasMovedExpenses), nameof(ShowScheduleFilter))]
    public partial int TransactionsTab { get; set; }

    public bool IsIncomeTab { get => TransactionsTab == 0; set { if (value) TransactionsTab = 0; } }

    public bool IsExpensesTab { get => TransactionsTab == 1; set { if (value) TransactionsTab = 1; } }

    public bool IsTransfersTab { get => TransactionsTab == 2; set { if (value) TransactionsTab = 2; } }

    public bool IsRecurringTab { get => TransactionsTab == 3; set { if (value) TransactionsTab = 3; } }

    /// <summary>Expenses are all one-time; the other tabs can be filtered by how often.</summary>
    public bool ShowScheduleFilter => !IsExpensesTab;

    /// <summary>Transfers only matter with more than one account, or once there are some.</summary>
    public bool ShowTransfersTab => HasSeveralAccounts || Transactions.Any(t => t.IsTransfer);

    public string NewEntryText => Loc.T(!IsTransactionsPage ? "New entry"
        : TransactionsTab switch { 0 => "New income", 2 => "New transfer", 3 => "New recurring expense", _ => "New expense" });

    /// <summary>The headline of the open tab, e.g. "CHF 12'050 a month coming in".</summary>
    [ObservableProperty]
    public partial string TransactionsTabTotal { get; set; } = "";

    [ObservableProperty]
    public partial string TransactionsTabDetail { get; set; } = "";

    partial void OnTransactionsTabChanged(int value)
    {
        CategoryFilter = AllCategories; // income and expense categories differ
        if (IsExpensesTab)
            ScheduleFilterIndex = 0; // the filter is hidden there: every expense on it is one-time
        RefreshEntryRows();
    }

    private static int TabOf(Transaction transaction) =>
        transaction.IsIncome ? 0 : transaction.IsTransfer ? 2 : transaction.IsRecurring ? 3 : 1;

    /// <summary>Premiums and tax payments are on the Insurance and Taxes pages, not the Expenses tab.</summary>
    private bool IsOnTab(Transaction transaction) => TabOf(transaction) == TransactionsTab && !HasOwnPage(transaction);

    public IReadOnlyList<string> ScheduleFilters =>
        Translate("Any schedule", "Repeating", "Monthly", "Every 3 months", "Yearly", "One-time");


    private static string AllCategories => Loc.T("All categories");

    private static IReadOnlyList<string> Translate(params string[] keys) => keys.Select(Loc.T).ToList();

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";


    [ObservableProperty]
    public partial int ScheduleFilterIndex { get; set; }


    [ObservableProperty]
    public partial PersonOption PersonFilter { get; set; } = PersonOption.Everyone;

    [ObservableProperty]
    public partial string CategoryFilter { get; set; } = AllCategories;

    [ObservableProperty]
    public partial IReadOnlyList<string> CategoryFilters { get; set; } = [AllCategories];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntryRows), nameof(EntriesEmptyText))]
    public partial IReadOnlyList<EntryRow> EntryRows { get; set; } = [];

    [ObservableProperty]
    public partial string EntriesSummaryText { get; set; } = "";

    public bool HasEntryRows => EntryRows.Count > 0;

    public string EntriesEmptyText => !Transactions.Any(IsOnTab)
        ? Loc.T(TransactionsTab switch
        {
            0 => "No income yet. Add your salary and other money coming in with New income.",
            2 => "No transfers yet. Add money moved between your own accounts with New transfer.",
            3 => "No recurring expenses yet. Add rent, subscriptions and other costs that come back with New recurring expense.",
            _ => "No one-time expenses yet. Add a purchase, a repair or a bill with New expense. Costs that come back are on the Recurring tab.",
        })
        : Loc.T("No entries match these filters.");

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        ScheduleFilterIndex = 0;
        PersonFilter = PersonOption.Everyone;
        CategoryFilter = AllCategories;
    }

    partial void OnSearchTextChanged(string value) => RefreshEntryRows();


    partial void OnScheduleFilterIndexChanged(int value) => RefreshEntryRows();


    partial void OnPersonFilterChanged(PersonOption value) => RefreshEntryRows();

    partial void OnCategoryFilterChanged(string value) => RefreshEntryRows();

    private void RefreshEntryRows()
    {
        var categories = Transactions.Where(IsOnTab).Select(t => Categories.Display(t.Category)).Where(c => c.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
        CategoryFilters = [AllCategories, .. categories];
        if (!CategoryFilters.Contains(CategoryFilter))
            CategoryFilter = AllCategories; // Re-enters this method with a valid filter.

        var names = People.ToDictionary(p => p.Id, p => p.Name);
        var search = SearchText.Trim();
        var matches = Transactions.Where(t =>
            IsOnTab(t) &&
            (ScheduleFilterIndex switch
            {
                1 => t.IsRecurring,
                2 => t.Recurrence == Recurrence.Monthly,
                3 => t.Recurrence == Recurrence.Quarterly,
                4 => t.Recurrence == Recurrence.Yearly,
                5 => !t.IsRecurring,
                _ => true,
            }) &&
            (PersonFilter ?? PersonOption.Everyone).Matches(t) &&
            (AccountFilter.Account is not { } filterAccount || Touches(t, filterAccount.Id)) &&
            (CategoryFilter is null || CategoryFilter == AllCategories ||
             string.Equals(Categories.Display(t.Category), CategoryFilter, StringComparison.CurrentCultureIgnoreCase)) &&
            (search.Length == 0 ||
                t.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                t.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase)));

        // Newest first unless a column header says otherwise.
        EntryRows = Sorted("transactions", matches
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => new EntryRow(t, t.PersonId is { } id && names.TryGetValue(id, out var name) ? name : Loc.T(PersonOption.SharedName),
                AccountText(t))), null, EntrySortKeys);

        // Recurring and one-time amounts can't be added up meaningfully, so they're totalled apart.
        // Quarterly and yearly entries count as their monthly share, e.g. a yearly 1,200 as 100 a month.
        static decimal Monthly(IEnumerable<Transaction> entries) =>
            entries.Where(t => t.IsRunning(DateOnly.FromDateTime(DateTime.Today))).Sum(t => t.Amount / t.IntervalMonths);
        var onTab = Transactions.Where(IsOnTab).ToList();
        var count = Loc.Plural(onTab.Count, "{1} of {0} entry", "{1} of {0} entries", EntryRows.Count);
        var listedMonthly = Format.Money(Monthly(EntryRows.Select(r => r.Transaction)));
        var listedOneTime = Format.Money(EntryRows.Where(r => !r.Transaction.IsRecurring).Sum(r => r.Transaction.Amount));
        // Each expense tab has one kind only, so it shows only that total.
        EntriesSummaryText = TransactionsTab switch
        {
            1 => Loc.F("{0}   ·   One-time: {1}", count, listedOneTime),
            3 => Loc.F("{0}   ·   Repeating: {1} / month", count, listedMonthly),
            _ => Loc.F("{0}   ·   Repeating: {1} / month   ·   One-time: {2}", count, listedMonthly, listedOneTime),
        };

        // The headline counts every entry on the tab, whatever the filters, for this financial year.
        var yearOneTime = onTab.Where(t => !t.IsRecurring && t.Date >= Summary.YearStart && t.Date <= Summary.YearEnd)
            .Sum(t => t.Amount);
        var year = FinancialYearReport.LabelFor(Summary.YearStart);
        switch (TransactionsTab)
        {
            case 1:
                // One-time spending only: what this financial year has (and will have) cost.
                TransactionsTabTotal = Loc.F("{0} one-time in {1}", Format.Money(yearOneTime), year);
                TransactionsTabDetail = Loc.F("Recurring costs ({0} a month) are on the Recurring tab",
                    Format.Money(Monthly(Transactions.Where(t => TabOf(t) == 3 && !HasOwnPage(t)))));
                break;
            case 3:
                var running = onTab.Count(t => t.IsRunning(Today));
                TransactionsTabTotal = Loc.F("{0} a month going out", Format.Money(Monthly(onTab)));
                TransactionsTabDetail = Loc.F("{0} a year · {1}", Format.Money(Monthly(onTab) * 12),
                    Loc.Plural(running, "{0} recurring cost now running", "{0} recurring costs now running"));
                break;
            default:
                TransactionsTabTotal = Loc.F(TransactionsTab == 0 ? "{0} a month coming in" : "{0} a month moved between accounts",
                    Format.Money(Monthly(onTab)));
                TransactionsTabDetail = Loc.F(TransactionsTab == 0
                    ? "Repeating income now running, plus {0} one-time in {1}"
                    : "Repeating transfers now running, plus {0} one-time in {1}", Format.Money(yearOneTime), year);
                break;
        }
        OnPropertyChanged(nameof(ShowTransfersTab));
        RefreshOwnPageLists();
        if (TransactionsTab == 2 && !ShowTransfersTab)
            TransactionsTab = 0;
    }

    /// <summary>"Savings → Main account" for a transfer, the account for others, when there are several.</summary>
    private string AccountText(Transaction transaction)
    {
        if (!HasSeveralAccounts)
            return "";
        string Name(int? id) => BankAccounts.FirstOrDefault(a => a.Id == id)?.Name ?? "";
        var from = Name(AccountIdOf(transaction));
        return transaction.IsTransfer ? $"{from} → {Name(transaction.ToAccountId)}" : from;
    }

    // ── Household ───────────────────────────────────────────────────────────

    public ObservableCollection<Person> People { get; } = [];

    /// <summary>Choices for an entry's person: shared, then each household member.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PersonOption> PersonOptions { get; set; } = [PersonOption.Shared];

    /// <summary>Choices for filtering the Entries table.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PersonOption> PersonFilters { get; set; } = [PersonOption.Everyone, PersonOption.Shared];

    [ObservableProperty]
    public partial string NewPersonName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHouseholdError))]
    public partial string? HouseholdError { get; set; }

    public bool HasHouseholdError => !string.IsNullOrEmpty(HouseholdError);

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        HouseholdError = null;
        var name = NewPersonName.Trim();
        if (name.Length == 0)
        {
            HouseholdError = Loc.T("Enter a name.");
            return;
        }
        if (name.Equals(PersonOption.SharedName, StringComparison.CurrentCultureIgnoreCase) ||
            People.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            HouseholdError = Loc.F("“{0}” is already in the list.", name);
            return;
        }

        var person = new Person { Name = name };
        try
        {
            await repository.AddPersonAsync(person);
        }
        catch (Exception ex)
        {
            HouseholdError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        var index = 0;
        while (index < People.Count && string.Compare(People[index].Name, name, StringComparison.CurrentCultureIgnoreCase) < 0)
            index++;
        People.Insert(index, person);
        NewPersonName = "";
        RefreshPersonOptions();
        RefreshBirthYearRows();
        RefreshHousehold();
    }

    /// <summary>Removes a person. Their entries become shared, so no money disappears from the plan.</summary>
    [RelayCommand]
    private async Task DeletePersonAsync(Person person)
    {
        HouseholdError = null;
        try
        {
            await repository.DeletePersonAsync(person.Id);
        }
        catch (Exception ex)
        {
            HouseholdError = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        foreach (var transaction in Transactions.Where(t => t.PersonId == person.Id))
            transaction.PersonId = null;
        foreach (var document in Documents.Where(d => d.PersonId == person.Id))
            document.PersonId = null;
        foreach (var task in _tasks.Where(t => t.PersonId == person.Id))
            task.PersonId = null;
        foreach (var account in _pensionAccounts.Where(a => a.PersonId == person.Id))
            account.PersonId = null;
        foreach (var policy in _policies.Where(p => p.PersonId == person.Id))
            policy.PersonId = null;
        foreach (var contract in _contracts.Where(c => c.PersonId == person.Id))
            contract.PersonId = null;
        foreach (var date in _dates.Where(d => d.PersonId == person.Id))
            date.PersonId = null;
        foreach (var warranty in _warranties.Where(w => w.PersonId == person.Id))
            warranty.PersonId = null;
        // Medicines, jobs and sick days are the person's own: they go with them.
        _medicines.RemoveAll(m => m.PersonId == person.Id);
        _employments.RemoveAll(j => j.PersonId == person.Id);
        _sickLeaves.RemoveAll(l => l.PersonId == person.Id);
        People.Remove(person);
        RefreshPersonOptions();
        RefreshEntryRows();
        RefreshDocumentRows();
        RefreshTaskRows();
        RefreshRetirement();
        RefreshInsurance();
        RefreshTaxes();
        RefreshDeadlines();
        RefreshBirthYearRows();
        RefreshPlanning();
        RefreshHousehold();
        RefreshDashboard();
        ScheduleReminders();
    }

    private IReadOnlyList<string> HouseholdNames => People.Select(p => p.Name).ToList();

    /// <summary>The household member a name read from a document refers to, if it clearly is one.</summary>
    private PersonOption? MatchPersonOption(string name) =>
        PersonMatcher.Match(name, People) is { } person ? PersonOptions.FirstOrDefault(o => o.Id == person.Id) : null;

    private void RefreshPersonOptions()
    {
        var members = People.Select(p => new PersonOption(p.Id, p.Name)).ToList();
        var newPersonId = NewPerson?.Id;
        var filterId = PersonFilter?.IsEveryone == true ? (int?)null : PersonFilter?.Id;
        var filterIsEveryone = PersonFilter is null or { IsEveryone: true };
        var documentPersonId = DocumentPerson?.Id;
        var documentFilterId = DocumentPersonFilter?.IsEveryone == true ? (int?)null : DocumentPersonFilter?.Id;
        var documentFilterIsEveryone = DocumentPersonFilter is null or { IsEveryone: true };
        var newTaskPersonId = NewTaskPerson?.Id;
        var appointmentPersonId = AppointmentPerson?.Id;
        var appointmentFilterId = AppointmentPersonFilter?.IsEveryone == true ? (int?)null : AppointmentPersonFilter?.Id;
        var appointmentFilterIsEveryone = AppointmentPersonFilter is null or { IsEveryone: true };
        var taskPersonId = TaskPerson?.Id;
        var taskFilterId = TaskPersonFilter?.IsEveryone == true ? (int?)null : TaskPersonFilter?.Id;
        var taskFilterIsEveryone = TaskPersonFilter is null or { IsEveryone: true };
        var insuranceFilterId = InsurancePersonFilter?.IsEveryone == true ? (int?)null : InsurancePersonFilter?.Id;
        var insuranceFilterIsEveryone = InsurancePersonFilter is null or { IsEveryone: true };
        var taxFilterId = TaxPersonFilter?.IsEveryone == true ? (int?)null : TaxPersonFilter?.Id;
        var taxFilterIsEveryone = TaxPersonFilter is null or { IsEveryone: true };
        var policyPersonId = PolicyPerson?.Id;
        var contractPersonId = ContractPerson?.Id;
        var datePersonId = ImportantDatePerson?.Id;
        var warrantyPersonId = WarrantyPerson?.Id;
        var retirementFilterId = RetirementPersonFilter?.IsEveryone == true ? (int?)null : RetirementPersonFilter?.Id;
        var retirementFilterIsEveryone = RetirementPersonFilter is null or { IsEveryone: true };
        var newPensionPersonId = NewPensionPerson?.Id;
        var pensionAccountPersonId = PensionAccountPerson?.Id;

        PersonOptions = [PersonOption.Shared, .. members];
        PersonFilters = [PersonOption.Everyone, PersonOption.Shared, .. members];

        // Point the selections at the new option objects, falling back when a person was removed.
        NewPerson = PersonOptions.FirstOrDefault(o => o.Id == newPersonId) ?? PersonOption.Shared;
        DocumentPerson = PersonOptions.FirstOrDefault(o => o.Id == documentPersonId) ?? PersonOption.Shared;
        DocumentPersonFilter = documentFilterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == documentFilterId) ?? PersonOption.Everyone;
        PersonFilter = filterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == filterId) ?? PersonOption.Everyone;
        NewTaskPerson = PersonOptions.FirstOrDefault(o => o.Id == newTaskPersonId) ?? PersonOption.Shared;
        TaskPerson = PersonOptions.FirstOrDefault(o => o.Id == taskPersonId) ?? PersonOption.Shared;
        AppointmentPerson = PersonOptions.FirstOrDefault(o => o.Id == appointmentPersonId) ?? PersonOption.Shared;
        AppointmentPersonFilter = appointmentFilterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == appointmentFilterId) ?? PersonOption.Everyone;
        TaskPersonFilter = taskFilterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == taskFilterId) ?? PersonOption.Everyone;
        RetirementPersonFilter = retirementFilterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == retirementFilterId) ?? PersonOption.Everyone;
        NewPensionPerson = PersonOptions.FirstOrDefault(o => o.Id == newPensionPersonId) ?? PersonOption.Shared;
        PolicyPerson = PersonOptions.FirstOrDefault(o => o.Id == policyPersonId) ?? PersonOption.Shared;
        ContractPerson = PersonOptions.FirstOrDefault(o => o.Id == contractPersonId) ?? PersonOption.Shared;
        ImportantDatePerson = PersonOptions.FirstOrDefault(o => o.Id == datePersonId) ?? PersonOption.Shared;
        WarrantyPerson = PersonOptions.FirstOrDefault(o => o.Id == warrantyPersonId) ?? PersonOption.Shared;
        InsurancePersonFilter = insuranceFilterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == insuranceFilterId) ?? PersonOption.Everyone;
        TaxPersonFilter = taxFilterIsEveryone
            ? PersonOption.Everyone
            : PersonFilters.FirstOrDefault(o => !o.IsEveryone && o.Id == taxFilterId) ?? PersonOption.Everyone;
        foreach (var item in BulkItems)
            item.Person = PersonOptions.FirstOrDefault(o => o.Id == item.Person.Id) ?? PersonOption.Shared;
        PensionAccountPerson = PersonOptions.FirstOrDefault(o => o.Id == pensionAccountPersonId) ?? PersonOption.Shared;
    }

    // ── New entry form ──────────────────────────────────────────────────────

    [ObservableProperty]
    public partial DateTime? NewDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial DateTime? NewEndDate { get; set; }

    [ObservableProperty]
    public partial string NewDescription { get; set; } = "";

    [ObservableProperty]
    public partial string? NewCategory { get; set; }

    [ObservableProperty]
    public partial decimal? NewAmount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryOptions))]
    public partial bool NewIsIncome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(DateLabel), nameof(NewIsOneTime), nameof(NewIsMonthly), nameof(NewIsQuarterly),
        nameof(NewIsYearly), nameof(NewIsRecurring), nameof(RecurrenceHint), nameof(CanChangeOneTime))]
    public partial Recurrence NewRecurrence { get; set; }

    public bool NewIsOneTime { get => NewRecurrence == Recurrence.OneTime; set { if (value) NewRecurrence = Recurrence.OneTime; } }

    public bool NewIsMonthly { get => NewRecurrence == Recurrence.Monthly; set { if (value) NewRecurrence = Recurrence.Monthly; } }

    public bool NewIsQuarterly { get => NewRecurrence == Recurrence.Quarterly; set { if (value) NewRecurrence = Recurrence.Quarterly; } }

    public bool NewIsYearly { get => NewRecurrence == Recurrence.Yearly; set { if (value) NewRecurrence = Recurrence.Yearly; } }

    public bool NewIsRecurring => NewRecurrence != Recurrence.OneTime;

    public string RecurrenceHint => NewRecurrence switch
    {
        Recurrence.Quarterly => Loc.T("Repeats every 3 months from the start date."),
        Recurrence.Yearly => Loc.T("Repeats once a year on the same day, e.g. insurance or taxes."),
        _ => "",
    };

    [ObservableProperty]
    public partial PersonOption NewPerson { get; set; } = PersonOption.Shared;

    public IReadOnlyList<string> CategoryOptions => Categories.DisplayOptions(NewIsIncome);

    public string DateLabel => NewRecurrence switch
    {
        Recurrence.Yearly => Loc.T("Due on"),
        Recurrence.OneTime => Loc.T("Date"),
        _ => Loc.T("Starts on"),
    };

    /// <summary>The entry loaded into the form for editing, or null when adding a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SubmitText))]
    public partial Transaction? EditingTransaction { get; set; }

    public bool IsEditing => EditingTransaction is not null;

    public string FormTitle => Loc.T(IsEditing ? "Edit entry" : "Add entry");

    public string SubmitText => Loc.T(IsEditing ? "Save changes" : "Add");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // ── Commands ────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            var settings = await repository.GetSettingsAsync();
            LoadListSorts(settings.ListSorts);
            LoadHiddenCover(settings.HiddenCover);
            LoadPageLayout(settings.PageLayout);
            LoadTaxSettings(settings.TaxAssessments, settings.TaxRate);
            _checkedPapers = settings.CheckedPapers.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => int.TryParse(id, out var n) ? n : 0).Where(n => n > 0).ToHashSet();
            ApplyLockSettings(settings.LockEnabled, settings.LockAfterMinutes);
            ApplyReminderSettings(settings.RemindersEnabled, settings.ReminderHour);
            LoadScenario(settings.ScenarioJson);
            RetirementNeedPercent = settings.RetirementNeedPercent;
            StartingBalance = settings.StartingBalance;
            FinancialYearStartIndex = settings.FinancialYearStartMonth - 1;
            SelectedLanguage = Loc.Find(string.IsNullOrEmpty(settings.Language) ? null : settings.Language);
            Loc.Instance.SetLanguage(settings.Language);
            IsImportEnabled = settings.AiImportEnabled;
            AiEndpoint = settings.AiEndpoint;
            AiModel = settings.AiModel;
            if (IsImportEnabled)
                _ = _ai.WarmUpAsync(AiEndpoint, AiModel);
            SelectedYearStart = FinancialYearReport.StartYearContaining(Today, settings.FinancialYearStartMonth);

            People.Clear();
            foreach (var person in await repository.GetPeopleAsync())
                People.Add(person);
            RefreshPersonOptions();

            await LoadAccountsAsync(settings);
            await LoadDocumentsAsync(settings);
            await LoadInsuranceAsync();
            await LoadAssetsAsync();
            await LoadDeadlinesAsync();
            await LoadGoalsAsync();
            await LoadTasksAsync();
            await LoadAppointmentsAsync(settings);
            await LoadNotesAsync();
            await LoadRetirementAsync();
            await LoadHouseholdAsync();

            var transactions = await repository.GetTransactionsAsync();
            Transactions.Clear();
            transactions.Sort(CompareForList);
            foreach (var transaction in transactions)
                Transactions.Add(transaction);

            Debts.Clear();
            foreach (var debt in await repository.GetDebtsAsync())
                Debts.Add(CreateDebtItem(debt));

            Recalculate();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not load data: {0}", ex.Message);
        }
        finally
        {
            _isLoading = false;
        }

        StartBackups();
        StartInbox();
        RefreshTaxes();
        RefreshDeadlines();
        RefreshPlanning();
        ScheduleReminders();
        _ = StartCalendarSyncAsync();
    }

    [RelayCommand]
    private async Task SaveTransactionAsync()
    {
        ErrorMessage = null;

        if (NewAmount is not > 0)
        {
            ErrorMessage = Loc.T("Enter an amount greater than zero.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewDescription))
        {
            ErrorMessage = Loc.T("Enter a description.");
            return;
        }

        var fromAccount = NewAccount?.Account ?? PrimaryAccount;
        var toAccount = NewToAccount?.Account;
        if (NewIsTransfer && (toAccount is null || toAccount == fromAccount))
        {
            ErrorMessage = Loc.T("Choose two different accounts for a transfer.");
            return;
        }

        var date = DateOnly.FromDateTime(NewDate ?? DateTime.Today);
        DateOnly? endDate = NewIsRecurring && NewEndDate is { } end ? DateOnly.FromDateTime(end) : null;
        if (endDate < date)
        {
            ErrorMessage = Loc.T("The end date must be on or after the start date.");
            return;
        }

        var transaction = new Transaction
        {
            Id = EditingTransaction?.Id ?? 0,
            Date = date,
            Description = NewDescription.Trim(),
            Category = NewIsTransfer ? "" : Categories.Normalize(NewCategory ?? ""),
            Amount = NewAmount.Value,
            Type = NewIsTransfer ? TransactionType.Transfer : NewIsIncome ? TransactionType.Income : TransactionType.Expense,
            Recurrence = NewRecurrence,
            EndDate = endDate,
            PersonId = NewPerson.Id,
            AccountId = fromAccount?.Id,
            ToAccountId = NewIsTransfer ? toAccount?.Id : null,
            ExternalId = EditingTransaction?.ExternalId,
        };
        // Changes to single times stay with the entry while their dates still come up in its schedule.
        if (EditingTransaction is { Changes.Count: > 0 } previous && transaction.IsRecurring)
        {
            var dates = Schedule.RegularDates(transaction, transaction.Date, previous.Changes.Max(c => c.Date)).ToHashSet();
            transaction.Changes = previous.Changes.Where(c => dates.Contains(c.Date)).ToList();
        }

        try
        {
            if (EditingTransaction is null)
                await repository.AddTransactionAsync(transaction);
            else
            {
                await repository.UpdateTransactionAsync(transaction);
                await repository.KeepEntryChangesAsync(transaction.Id, transaction.Changes.Select(c => c.Id).ToList());
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (EditingTransaction is not null)
            Transactions.Remove(EditingTransaction);
        InsertSorted(transaction);
        TransactionsTab = TabOf(transaction); // so the entry just saved is in view
        var wasEditing = EditingTransaction is not null;
        ResetForm();
        Recalculate();
        if (wasEditing)
            IsEntryPanelOpen = false; // New entries keep the panel open for quick repeat entry.
    }

    /// <summary>Loads an entry into the form so it can be changed.</summary>
    [RelayCommand]
    private void EditTransaction(Transaction transaction)
    {
        ErrorMessage = null;
        EditingTransaction = transaction;
        TransactionsTab = TabOf(transaction);
        NewIsIncome = transaction.IsIncome;
        NewIsTransfer = transaction.IsTransfer;
        NewAccount = AccountOptions.FirstOrDefault(o => o.Account!.Id == AccountIdOf(transaction)) ?? NewAccount;
        if (transaction.ToAccountId is { } toId)
            NewToAccount = AccountOptions.FirstOrDefault(o => o.Account!.Id == toId) ?? NewToAccount;
        NewRecurrence = transaction.Recurrence;
        NewDescription = transaction.Description;
        NewCategory = Categories.Display(transaction.Category);
        NewAmount = transaction.Amount;
        NewDate = transaction.Date.ToDateTime(TimeOnly.MinValue);
        NewEndDate = transaction.EndDate?.ToDateTime(TimeOnly.MinValue);
        NewPerson = PersonOptions.FirstOrDefault(o => o.Id == transaction.PersonId) ?? PersonOption.Shared;
        IsDebtPanelOpen = false;
        IsImportPanelOpen = false;
        CloseDocumentPanel();
        CloseTaskPanel();
        CloseAppointmentPanel();
        CloseRetirementPanels();
        CloseInsurancePanel();
        CloseAccountPanel();
        CloseDeadlinePanels();
        ClosePlanningPanels();
        if (IsBulkPanelOpen)
            CloseBulkPanel();
        IsEntryPanelOpen = true;
    }

    [RelayCommand]
    private void CancelEdit() => ClosePanel();

    /// <summary>Clears the form for the next entry. Type, schedule, date and person stay for quick repeat entry.</summary>
    private void ResetForm()
    {
        if (EditingTransaction is not null)
        {
            NewDate = DateTime.Today;
            NewCategory = null;
            EditingTransaction = null;
        }

        NewDescription = "";
        NewAmount = null;
        NewEndDate = null;
    }

    [RelayCommand]
    private async Task DeleteTransactionAsync(Transaction transaction)
    {
        try
        {
            await repository.DeleteTransactionAsync(transaction.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        if (EditingTransaction == transaction)
            ClosePanel();
        var linked = Documents.Where(d => d.TransactionId == transaction.Id).Select(d => d.Id).ToArray();
        var parts = Documents.SelectMany(d => d.Instalments).Where(i => i.TransactionId == transaction.Id).ToArray();
        Transactions.Remove(transaction);
        UnlinkDocumentsFrom(transaction.Id);
        Recalculate();
        ShowNotice(Loc.F("Deleted “{0}”", transaction.Description), UndoDeleteTransaction(transaction, linked, parts));
    }

    /// <summary>Ends a monthly entry today, keeping its past months in the history.</summary>
    [RelayCommand]
    private async Task StopTransactionAsync(Transaction transaction)
    {
        try
        {
            await repository.SetEndDateAsync(transaction.Id, Today);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not stop: {0}", ex.Message);
            return;
        }

        transaction.EndDate = Today;
        if (EditingTransaction == transaction)
            NewEndDate = DateTime.Today;
        Recalculate();
    }

    [RelayCommand]
    private async Task SaveStartingBalanceAsync()
    {
        try
        {
            await repository.SetStartingBalanceAsync(StartingBalance ?? 0m);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save starting balance: {0}", ex.Message);
            return;
        }

        Recalculate();
    }

    [RelayCommand]
    private async Task AddDebtAsync()
    {
        DebtError = null;
        var date = DateOnly.FromDateTime(NewDebtDate ?? DateTime.Today);

        string? error = (NewDebtName, NewDebtBalance, NewDebtRate, NewDebtPayment) switch
        {
            _ when string.IsNullOrWhiteSpace(NewDebtName) => Loc.T("Enter a name for the debt."),
            (_, not > 0, _, _) => Loc.T("Enter the amount you still owe."),
            (_, _, < 0, _) when !NewDebtIsInterestOnly => Loc.T("The interest rate can’t be negative."),
            _ when NewDebtIsInterestOnly && NewDebtInterestPer1000 is not > 0 => Loc.T("Enter the interest per 1,000 per month."),
            _ when NewDebtIsInterestOnly && NewDebtPayment < 0 => Loc.T("The monthly repayment can’t be negative."),
            (_, _, _, not > 0) when NewDebtIsMonthly => Loc.T("Enter the monthly payment."),
            _ when EditingDebt is null && date <= Today => Loc.F("“{0}” must be after today.", DebtDateLabel),
            _ when EditingDebt is { } editing && date <= editing.Debt.StartDate =>
                Loc.F("“{0}” must be after {1}.", DebtDateLabel, Format.Date(editing.Debt.StartDate)),
            _ => null,
        };
        if (error is not null)
        {
            DebtError = error;
            return;
        }

        if (EditingDebt is { } item)
        {
            await SaveDebtChangesAsync(item, date);
            return;
        }

        var debt = new Debt
        {
            Name = NewDebtName.Trim(),
            Balance = NewDebtBalance!.Value,
            StartDate = Today,
            AnnualInterestRate = NewDebtIsInterestOnly
                ? Debt.AnnualRateFromMonthlyPer1000(NewDebtInterestPer1000!.Value)
                : NewDebtRate ?? 0m,
            MonthlyPayment = NewDebtIsLumpSum ? 0m : NewDebtPayment ?? 0m,
            FirstPaymentDate = date,
            IsInterestOnly = NewDebtIsInterestOnly,
        };

        try
        {
            await repository.AddDebtAsync(debt);
        }
        catch (Exception ex)
        {
            DebtError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        Debts.Add(CreateDebtItem(debt));
        ResetDebtForm();
        IsDebtPanelOpen = false;
        Recalculate();
    }

    private async Task SaveDebtChangesAsync(DebtItemViewModel item, DateOnly date)
    {
        var changed = new Debt
        {
            Id = item.Debt.Id,
            Name = NewDebtName.Trim(),
            Balance = NewDebtBalance!.Value,
            StartDate = item.Debt.StartDate,
            AnnualInterestRate = NewDebtIsInterestOnly
                ? Debt.AnnualRateFromMonthlyPer1000(NewDebtInterestPer1000!.Value)
                : NewDebtRate ?? 0m,
            MonthlyPayment = NewDebtIsLumpSum ? 0m : NewDebtPayment ?? 0m,
            FirstPaymentDate = date,
            IsInterestOnly = NewDebtIsInterestOnly,
            ExtraPayments = item.Debt.ExtraPayments,
        };

        try
        {
            await repository.UpdateDebtAsync(changed);
        }
        catch (Exception ex)
        {
            DebtError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        var index = Debts.IndexOf(item);
        if (index >= 0)
            Debts[index] = CreateDebtItem(changed);
        ResetDebtForm();
        IsDebtPanelOpen = false;
        Recalculate();
    }

    private void ResetDebtForm()
    {
        EditingDebt = null;
        DebtPaymentRows = [];
        NewDebtName = "";
        NewDebtBalance = null;
        NewDebtPayment = null;
        NewDebtRate = 0m;
        NewDebtInterestPer1000 = null;
        NewDebtRepayment = DebtRepayment.Monthly;
        NewDebtDate = DateTime.Today.AddMonths(1);
    }

    /// <summary>Loads a debt into the form so its amount, terms or dates can be corrected.</summary>
    private void EditDebt(DebtItemViewModel item)
    {
        NewDebt();
        var debt = item.Debt;
        EditingDebt = item;
        NewDebtName = debt.Name;
        NewDebtBalance = debt.Balance;
        NewDebtRepayment = debt.IsInterestOnly ? DebtRepayment.InterestOnly
            : debt.IsLumpSum ? DebtRepayment.AllAtOnce
            : DebtRepayment.Monthly;
        NewDebtRate = debt.IsInterestOnly ? 0m : debt.AnnualInterestRate;
        NewDebtInterestPer1000 = debt.IsInterestOnly ? Math.Round(debt.MonthlyInterestPer1000, 2) : null;
        NewDebtPayment = debt.MonthlyPayment > 0 ? debt.MonthlyPayment : null;
        NewDebtDate = debt.FirstPaymentDate.ToDateTime(TimeOnly.MinValue);
        RefreshDebtPaymentRows();
    }

    private void RefreshDebtPaymentRows() =>
        DebtPaymentRows = EditingDebt?.Debt.ExtraPayments
            .OrderBy(p => p.Date).ThenBy(p => p.Id)
            .Select(p => new DebtPaymentRow(p, Format.Date(p.Date),
                p.IsBorrowing ? Loc.F("Borrowed {0}", Format.Money(p.Borrowed)) : Loc.F("Repaid {0}", Format.Money(p.Amount))))
            .ToList() ?? [];

    /// <summary>Removes an extra payment or borrowing recorded by mistake.</summary>
    [RelayCommand]
    private async Task RemoveDebtPaymentAsync(DebtPaymentRow row)
    {
        if (EditingDebt is not { } item)
            return;
        try
        {
            await repository.DeleteDebtPaymentAsync(row.Payment.Id);
        }
        catch (Exception ex)
        {
            DebtError = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        item.Debt.ExtraPayments.Remove(row.Payment);
        RefreshDebtPaymentRows();
        Recalculate();
    }

    private DebtItemViewModel CreateDebtItem(Debt debt) =>
        new(debt, RecordExtraPaymentAsync, BorrowMoreAsync, EditDebt, DeleteDebtAsync);

    /// <summary>Records money borrowed again from the same lender: the debt grows and the money comes in.</summary>
    private async Task BorrowMoreAsync(DebtItemViewModel item)
    {
        DebtError = null;
        if (item.BorrowAmount is not > 0)
        {
            DebtError = Loc.T("Enter the amount you borrowed.");
            return;
        }

        var payment = new DebtPayment { DebtId = item.Debt.Id, Date = Today, Amount = -item.BorrowAmount.Value };
        try
        {
            await repository.AddDebtPaymentAsync(payment);
        }
        catch (Exception ex)
        {
            DebtError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        item.Debt.ExtraPayments.Add(payment);
        item.BorrowAmount = null;
        Recalculate();
    }

    private async Task RecordExtraPaymentAsync(DebtItemViewModel item)
    {
        DebtError = null;
        if (item.ExtraAmount is not > 0)
        {
            DebtError = Loc.T("Enter the amount of the extra payment.");
            return;
        }

        var payment = new DebtPayment
        {
            DebtId = item.Debt.Id,
            Date = Today,
            Amount = Math.Min(item.ExtraAmount.Value, item.Outstanding),
        };

        try
        {
            await repository.AddDebtPaymentAsync(payment);
        }
        catch (Exception ex)
        {
            DebtError = Loc.F("Could not save payment: {0}", ex.Message);
            return;
        }

        item.Debt.ExtraPayments.Add(payment);
        item.ExtraAmount = null;
        Recalculate();
    }

    private async Task DeleteDebtAsync(DebtItemViewModel item)
    {
        var index = Debts.IndexOf(item);
        try
        {
            await repository.DeleteDebtAsync(item.Debt.Id);
        }
        catch (Exception ex)
        {
            DebtError = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        Debts.Remove(item);
        if (EditingDebt == item)
            ClosePanel();
        Recalculate();
        ShowNotice(Loc.F("Deleted “{0}”", item.Name), UndoDeleteDebt(item.Debt, index));
    }

    [RelayCommand]
    private void PreviousYear() => SelectedYearStart--;

    [RelayCommand]
    private void NextYear() => SelectedYearStart++;

    [RelayCommand]
    private void CurrentYear() =>
        SelectedYearStart = FinancialYearReport.StartYearContaining(Today, FinancialYearStartIndex + 1);

    partial void OnSelectedYearStartChanged(int value) => RebuildReport();

    partial void OnFinancialYearStartIndexChanged(int oldValue, int newValue)
    {
        if (newValue is < 0 or > 11)
            return;

        Recalculate();
        if (!_isLoading)
            _ = SaveFinancialYearStartAsync(newValue + 1);
    }

    private async Task SaveFinancialYearStartAsync(int month)
    {
        try
        {
            await repository.SetFinancialYearStartMonthAsync(month);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save financial year start: {0}", ex.Message);
        }
    }

    // ── Document import ─────────────────────────────────────────────────────

    private readonly LocalAiClient _ai = new();
    private CancellationTokenSource? _importCancellation;

    public ObservableCollection<ImportItem> ImportItems { get; } = [];

    [ObservableProperty]
    public partial string ImportFileName { get; set; } = "";

    /// <summary>What the import is doing right now, shown while the model works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddImported))]
    public partial bool IsImporting { get; set; }

    [ObservableProperty]
    public partial string ImportStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportError))]
    public partial string? ImportError { get; set; }

    public bool HasImportError => !string.IsNullOrEmpty(ImportError);

    /// <summary>A note about the result, e.g. that a long document was only partly read.</summary>
    [ObservableProperty]
    public partial string ImportNote { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption ImportPerson { get; set; } = PersonOption.Shared;

    public bool HasImportItems => ImportItems.Count > 0;

    public bool CanAddImported => !IsImporting && ImportItems.Any(i => i.IsSelected);

    public string AddImportedText
    {
        get
        {
            var count = ImportItems.Count(i => i.IsSelected);
            return Loc.Plural(count, "Add {0} entry", "Add {0} entries");
        }
    }

    public string SupportedFilesText => DocumentReader.SupportedFilesText;

    public static bool CanImport(string path) => DocumentReader.IsSupported(path);

    /// <summary>Reads a dropped or picked file with the local model and lists what it found for review.</summary>
    public async Task ImportDocumentAsync(string path)
    {
        if (!IsImportEnabled)
            return;

        _importCancellation?.Cancel();
        var cancellation = _importCancellation = new CancellationTokenSource();

        if (EditingTransaction is not null)
            ResetForm();
        IsEntryPanelOpen = false;
        IsDebtPanelOpen = false;
        CloseDocumentPanel();
        IsImportPanelOpen = true;
        IsBankImport = false;
        SetImportItems([]);
        ImportError = null;
        ImportNote = "";
        ImportFileName = Path.GetFileName(path);
        IsImporting = true;

        try
        {
            ImportStatus = Loc.T("Reading the document…");
            var document = await DocumentReader.ReadAsync(path);
            ImportStatus = Loc.F("Looking for entries with {0}. This runs on your Mac and can take a minute…", AiModel);
            var personBefore = ImportPerson;
            var (entries, person) = await _ai.ExtractEntriesAsync(document, AiEndpoint, AiModel, Today, HouseholdNames, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            if (ImportPerson == personBefore && MatchPersonOption(person) is { } match)
                ImportPerson = match;

            SetImportItems(entries.Select(e => new ImportItem(e, ImportItem.IsAlreadyRecorded(e, Transactions))));
            var duplicates = ImportItems.Count(i => i.IsDuplicate);
            ImportNote = string.Join(" ", new[]
            {
                entries.Count == 0 ? Loc.T("No income or expenses were found in this document.") : "",
                duplicates > 0
                    ? Loc.Plural(duplicates, "{0} looks like an entry you already have and is unticked.",
                        "{0} look like entries you already have and are unticked.")
                    : "",
                document.WasShortened ? Loc.T("The document is long, so only its first part was read.") : "",
            }.Where(n => n != ""));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closed or replaced by another import.
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            ImportError = ex is InvalidOperationException or NotSupportedException
                ? ex.Message
                : Loc.F("Could not read {0}: {1}", ImportFileName, ex.Message);
        }
        finally
        {
            if (_importCancellation == cancellation)
                IsImporting = false;
        }
    }

    [RelayCommand]
    private async Task AddImportedAsync()
    {
        ImportError = null;
        var selected = ImportItems.Where(i => i.IsSelected).ToList();
        if (selected.FirstOrDefault(i => !i.IsValid) is { } invalid)
        {
            ImportError = Loc.F("“{0}” needs a description, an amount above zero and a date.", invalid.Description);
            return;
        }

        var added = 0;
        try
        {
            foreach (var item in selected)
            {
                var transaction = await repository.AddTransactionAsync(
                    item.ToTransaction(ImportPerson.Id, IsBankImport ? ImportAccount?.Account?.Id : null));
                InsertSorted(transaction);
                ImportItems.Remove(item);
                added++;
            }
        }
        catch (Exception ex)
        {
            ImportError = Loc.F("Could not save: {0}", ex.Message);
        }
        finally
        {
            if (added > 0)
                Recalculate();
            OnImportSelectionChanged();
        }

        if (ImportError is null)
        {
            if (added > 0)
                await FinishBankImportAsync();
            CloseImport();
            CurrentPage = AppPage.Transactions;
        }
    }

    private void CloseImport()
    {
        _importCancellation?.Cancel();
        _importCancellation = null;
        IsImporting = false;
        IsImportPanelOpen = false;
        IsBankImport = false;
        _importStatement = null;
        SetImportItems([]);
    }

    private void SetImportItems(IEnumerable<ImportItem> items)
    {
        foreach (var item in ImportItems)
            item.PropertyChanged -= ImportItem_PropertyChanged;
        ImportItems.Clear();
        foreach (var item in items)
        {
            item.PropertyChanged += ImportItem_PropertyChanged;
            ImportItems.Add(item);
        }

        OnImportSelectionChanged();
    }

    private void ImportItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImportItem.IsSelected) or nameof(ImportItem.IsIncome) or nameof(ImportItem.IsTransfer)
            or nameof(ImportItem.Amount))
            OnImportSelectionChanged();
    }

    private void OnImportSelectionChanged()
    {
        OnPropertyChanged(nameof(HasImportItems));
        OnPropertyChanged(nameof(CanAddImported));
        OnPropertyChanged(nameof(AddImportedText));
        OnPropertyChanged(nameof(ImportSelectionText));
        OnPropertyChanged(nameof(ImportExpensesOnlyText));
        OnPropertyChanged(nameof(ImportIncomeOnlyText));
    }

    private static bool IsImportExpense(ImportItem item) => !item.IsIncome && !item.IsTransfer;

    private static bool IsImportIncome(ImportItem item) => item.IsIncome && !item.IsTransfer;

    public string ImportExpensesOnlyText => Loc.F("Expenses only ({0})", ImportItems.Count(i => IsImportExpense(i) && !i.IsDuplicate));

    public string ImportIncomeOnlyText => Loc.F("Income only ({0})", ImportItems.Count(i => IsImportIncome(i) && !i.IsDuplicate));

    /// <summary>"52 of 68 ticked · CHF 2'782 out · CHF 0 in".</summary>
    public string ImportSelectionText
    {
        get
        {
            var selected = ImportItems.Where(i => i.IsSelected).ToList();
            return Loc.F("{0} of {1} ticked · {2} out · {3} in", selected.Count, ImportItems.Count,
                Format.Money(selected.Where(IsImportExpense).Sum(i => i.Amount ?? 0)),
                Format.Money(selected.Where(IsImportIncome).Sum(i => i.Amount ?? 0)));
        }
    }

    /// <summary>
    /// Ticks a whole group at once: all, only the expenses, only the income, or none. Bookings already in
    /// Keystone stay unticked, and transfers count as neither income nor expense.
    /// </summary>
    [RelayCommand]
    private void SelectImported(string which)
    {
        foreach (var item in ImportItems)
            item.IsSelected = !item.IsDuplicate && which switch
            {
                "all" => true,
                "expenses" => IsImportExpense(item),
                "income" => IsImportIncome(item),
                _ => false,
            };
    }

    // ── Language ────────────────────────────────────────────────────────────

    public IReadOnlyList<AppLanguage> Languages => Loc.Languages;

    [ObservableProperty]
    public partial AppLanguage SelectedLanguage { get; set; } = Loc.SystemLanguage();

    partial void OnSelectedLanguageChanged(AppLanguage value)
    {
        if (value is null || _isLoading)
            return;

        Loc.Instance.SetLanguage(value.Code);
        _ = SaveLanguageAsync(value.Code);
    }

    private async Task SaveLanguageAsync(string code)
    {
        try
        {
            await repository.SetLanguageAsync(code);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    /// <summary>Rebuilds every text made in code; texts in the views follow the language by themselves.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Lists of choices are rebuilt, which can clear their selection; keep what was chosen.
        var selections = (TransactionsTab, ScheduleFilterIndex, FinancialYearStartIndex,
            DocumentKindFilterIndex, DocumentStatusFilterIndex, NewRecurrence, DocumentKindIndex, TaskStatusFilterIndex,
            NewPensionPillarIndex, PensionPillarIndex, PolicyKindIndex, PolicyRecurrenceIndex, AccountKindIndex,
            LockDelayIndex, ReminderHourIndex, DeadlinesTab, PlanningTab, ContractKindIndex, ContractRecurrenceIndex,
            ImportantDateKindIndex, ImportantDateRepeatIndex, ScenarioKindIndex, TaskRepeatIndex,
            AppointmentStatusFilterIndex, AppointmentReminderIndex, AssetKindIndex,
            PersonBloodTypeIndex, EmploymentReportByIndex, EmploymentReportTimeIndex);
        CategoryFilter = AllCategories;
        OnPropertyChanged(string.Empty);
        (TransactionsTab, ScheduleFilterIndex, FinancialYearStartIndex,
            DocumentKindFilterIndex, DocumentStatusFilterIndex, NewRecurrence, DocumentKindIndex, TaskStatusFilterIndex,
            NewPensionPillarIndex, PensionPillarIndex, PolicyKindIndex, PolicyRecurrenceIndex, AccountKindIndex,
            LockDelayIndex, ReminderHourIndex, DeadlinesTab, PlanningTab, ContractKindIndex, ContractRecurrenceIndex,
            ImportantDateKindIndex, ImportantDateRepeatIndex, ScenarioKindIndex, TaskRepeatIndex,
            AppointmentStatusFilterIndex, AppointmentReminderIndex, AssetKindIndex,
            PersonBloodTypeIndex, EmploymentReportByIndex, EmploymentReportTimeIndex) = selections;

        RefreshPersonOptions();
        RefreshDocumentRows();
        RefreshCover();
        RefreshTaskRows();
        RefreshAppointmentRows();
        RefreshCalendarState();
        RefreshNoteProjects();
        RefreshNoteItems();
        foreach (var item in BulkItems)
            item.RefreshTexts();
        RefreshBulkTexts();
        RefreshAccountOptions();
        RefreshInsurance();
        RefreshBackupStatus();
        Recalculate();
        RefreshTaxes();
        RefreshDeadlines();
        ScheduleReminders();
    }

    // ── Local AI settings ───────────────────────────────────────────────────

    /// <summary>Turned on in Settings. When off, the import button is hidden and dropped files are ignored.</summary>
    [ObservableProperty]
    public partial bool IsImportEnabled { get; set; }

    partial void OnIsImportEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(DropFilesAsDocument));
        OnPropertyChanged(nameof(DropHint));
        if (!value)
            CloseImport();
        if (!_isLoading)
            _ = SaveImportEnabledAsync(value);
        if (value && !_isLoading)
            _ = _ai.WarmUpAsync(AiEndpoint, AiModel);
    }

    private async Task SaveImportEnabledAsync(bool enabled)
    {
        try
        {
            await repository.SetAiImportEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            SetAiStatus(Loc.F("Could not save: {0}", ex.Message), isError: true);
        }
    }

    [ObservableProperty]
    public partial string AiEndpoint { get; set; } = AppSettings.DefaultAiEndpoint;

    [ObservableProperty]
    public partial string AiModel { get; set; } = AppSettings.DefaultAiModel;

    [ObservableProperty]
    public partial IReadOnlyList<string> InstalledModels { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiStatus))]
    public partial string AiStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool AiStatusIsError { get; set; }

    public bool HasAiStatus => AiStatus != "";

    public string AiSetupCommand => $"docker compose exec ollama ollama pull {AppSettings.DefaultAiModel}";

    /// <summary>Saves the endpoint and model, then checks Ollama is running and has the model.</summary>
    [RelayCommand]
    private async Task SaveAiSettingsAsync()
    {
        AiEndpoint = string.IsNullOrWhiteSpace(AiEndpoint) ? AppSettings.DefaultAiEndpoint : AiEndpoint.Trim();
        AiModel = string.IsNullOrWhiteSpace(AiModel) ? AppSettings.DefaultAiModel : AiModel.Trim();
        if (!Uri.TryCreate(AiEndpoint, UriKind.Absolute, out _))
        {
            SetAiStatus(Loc.T("Enter an address like http://localhost:11434."), isError: true);
            return;
        }

        try
        {
            await repository.SetAiSettingsAsync(AiEndpoint, AiModel);
            SetAiStatus(Loc.T("Checking Ollama…"), isError: false);
            InstalledModels = await _ai.ListModelsAsync(AiEndpoint);
            if (InstalledModels.Contains(AiModel) || InstalledModels.Contains(AiModel + ":latest"))
            {
                SetAiStatus(Loc.F("Ready. Ollama is running and {0} is installed.", AiModel), isError: false);
                _ = _ai.WarmUpAsync(AiEndpoint, AiModel);
            }
            else
                SetAiStatus(Loc.F("Ollama is running, but {0} isn’t installed. Run: ollama pull {0}", AiModel), isError: true);
        }
        catch (Exception ex)
        {
            SetAiStatus(ex.Message, isError: true);
        }
    }

    private void SetAiStatus(string text, bool isError)
    {
        AiStatus = text;
        AiStatusIsError = isError;
    }

    private void InsertSorted(Transaction transaction)
    {
        var index = 0;
        while (index < Transactions.Count && CompareForList(Transactions[index], transaction) < 0)
            index++;
        Transactions.Insert(index, transaction);
    }

    /// <summary>Monthly entries first, then newest first.</summary>
    private static int CompareForList(Transaction a, Transaction b)
    {
        var byRecurrence = b.IsRecurring.CompareTo(a.IsRecurring);
        if (byRecurrence != 0)
            return byRecurrence;

        var byDate = b.Date.CompareTo(a.Date);
        return byDate != 0 ? byDate : b.Id.CompareTo(a.Id);
    }

    private void Recalculate()
    {
        RefreshRetirement();
        foreach (var item in Debts)
            item.Plan = DebtPlan.Build(item.Debt);
        DebtPlans = Debts.Select(d => d.Plan!).ToList();

        // Debt payments count as expenses alongside the user's own entries.
        _entries = Transactions.Concat(DebtPlans.SelectMany(p => p.ToTransactions())).ToList();
        Summary = RunwayCalculator.Calculate(
            TotalOpeningBalance, _entries, Today, financialYearStartMonth: FinancialYearStartIndex + 1);
        RefreshAccounts();
        RebuildReport();
        RefreshEntryRows();
        RefreshUpcoming();
        RefreshDashboard();
        RefreshTaxes();
        RefreshPlanning();
    }

    private void RebuildReport() =>
        Report = FinancialYearReport.Build(_entries, SelectedYearStart, FinancialYearStartIndex + 1, Today);
}
