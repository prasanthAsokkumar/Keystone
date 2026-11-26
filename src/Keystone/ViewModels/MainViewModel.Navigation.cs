using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>A tab over pages that share one place in the sidebar, e.g. "Entries | Year".</summary>
public sealed record PageTab(AppPage Page, string Title, bool IsSelected);

/// <summary>A choice in the "+ New" menu.</summary>
public sealed record NewMenuItem(string Key, string Title, string Hint, AppPage Page);

/// <summary>
/// Navigation: the sidebar has a few places, some holding several pages shown as tabs (Transactions: entries and
/// the year; Debts &amp; assets; Retirement: accounts and the plan for 65; Agenda: tasks and appointments). Each place
/// reopens the tab last used. One "+ New" menu adds anything from anywhere.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Pages shown as tabs of one sidebar place, in tab order.</summary>
    private static readonly AppPage[][] PageGroups =
    [
        [AppPage.Transactions, AppPage.Year],
        [AppPage.Debts, AppPage.Assets, AppPage.NetWorth],
        [AppPage.Retirement, AppPage.RetirementPlan],
        [AppPage.Tasks, AppPage.Appointments],
    ];

    private readonly Dictionary<int, AppPage> _lastPageInGroup = [];

    private static AppPage[]? GroupOf(AppPage page) => PageGroups.FirstOrDefault(g => g.Contains(page));

    private static string TabTitle(AppPage page) => Loc.T(page switch
    {
        AppPage.Transactions => "Entries",
        AppPage.Year => "Year",
        AppPage.Debts => "Debts",
        AppPage.Assets => "Assets",
        AppPage.NetWorth => "Net worth",
        AppPage.Retirement => "Accounts",
        AppPage.RetirementPlan => "Plan for 65",
        AppPage.Tasks => "Tasks",
        AppPage.Appointments => "Appointments",
        _ => "",
    });

    public IReadOnlyList<PageTab> PageTabs =>
        GroupOf(CurrentPage) is { } group ? group.Select(p => new PageTab(p, TabTitle(p), p == CurrentPage)).ToList() : [];

    public bool HasPageTabs => GroupOf(CurrentPage) is not null;

    [RelayCommand]
    private void GoToPage(AppPage page) => CurrentPage = page;

    /// <summary>The page a sidebar place opens: the tab used last, else its first.</summary>
    private AppPage PageOfGroup(int group) => _lastPageInGroup.TryGetValue(group, out var page) ? page : PageGroups[group][0];

    private bool InGroup(int group) => PageGroups[group].Contains(CurrentPage);

    public bool IsTransactionsPlace { get => InGroup(0); set { if (value && !InGroup(0)) CurrentPage = PageOfGroup(0); } }

    public bool IsWealthPlace { get => InGroup(1); set { if (value && !InGroup(1)) CurrentPage = PageOfGroup(1); } }

    public bool IsRetirementPlace { get => InGroup(2); set { if (value && !InGroup(2)) CurrentPage = PageOfGroup(2); } }

    public bool IsAgendaPlace { get => InGroup(3); set { if (value && !InGroup(3)) CurrentPage = PageOfGroup(3); } }

    /// <summary>After the page changed: the tabs, the sidebar places, and the tab to reopen.</summary>
    private void NavigationChanged()
    {
        for (var i = 0; i < PageGroups.Length; i++)
            if (PageGroups[i].Contains(CurrentPage))
                _lastPageInGroup[i] = CurrentPage;
        OnPropertyChanged(nameof(PageTabs));
        OnPropertyChanged(nameof(HasPageTabs));
        OnPropertyChanged(nameof(IsTransactionsPlace));
        OnPropertyChanged(nameof(IsWealthPlace));
        OnPropertyChanged(nameof(IsRetirementPlace));
        OnPropertyChanged(nameof(IsAgendaPlace));
        OnPropertyChanged(nameof(NewMenuItems));
        OnPropertyChanged(nameof(ShowsPersonViewBar));
    }

    // ── + New ────────────────────────────────────────────────────────────────

    /// <summary>Everything that can be added, the current page's things first.</summary>
    public IReadOnlyList<NewMenuItem> NewMenuItems
    {
        get
        {
            var items = new List<NewMenuItem>
            {
                new("entry", Loc.T("Income or expense"), Loc.T("A payment, once or repeating"), AppPage.Transactions),
                new("document", Loc.T("Bill or document"), Loc.T("File a PDF, scan or photo"), AppPage.Documents),
                new("task", Loc.T("Task"), Loc.T("Something to do, with a due date"), AppPage.Tasks),
                new("appointment", Loc.T("Appointment"), Loc.T("In step with your calendar"), AppPage.Appointments),
                new("note", Loc.T("Note"), Loc.T("An idea or a plan"), AppPage.Notes),
                new("insurance", Loc.T("Insurance policy"), Loc.T("Premium and notice deadline"), AppPage.Insurance),
                new("contract", Loc.T("Contract"), Loc.T("Mobile, internet, gym, leasing…"), AppPage.Contracts),
                new("date", Loc.T("Important date"), Loc.T("Passport, MFK, vignette…"), AppPage.Contracts),
                new("medicine", Loc.T("Medicine"), Loc.T("For someone in the household"), AppPage.Household),
                new("member", Loc.T("Household member"), Loc.T("With contacts, health and work"), AppPage.Household),
                new("debt", Loc.T("Debt"), Loc.T("A loan and its repayments"), AppPage.Debts),
                new("asset", Loc.T("Asset"), Loc.T("Securities, crypto, property, a car"), AppPage.Assets),
                new("goal", Loc.T("Savings goal"), Loc.T("What you’re saving for"), AppPage.Planning),
            };
            var here = GroupOf(CurrentPage) ?? [CurrentPage];
            return items.Where(i => here.Contains(i.Page)).Concat(items.Where(i => !here.Contains(i.Page))).ToList();
        }
    }

    /// <summary>Asks the window to open the "+ New" menu (⌘N).</summary>
    public event Action? NewMenuRequested;

    /// <summary>Asks the window to pick files to file as documents.</summary>
    public event Action? PickDocumentsRequested;

    /// <summary>Asks the window to put the cursor in the new-task box.</summary>
    public event Action? FocusNewTaskRequested;

    [RelayCommand]
    private void OpenNewMenu() => NewMenuRequested?.Invoke();

    /// <summary>Opens the form for a new thing on its own page, so it's seen there once saved.</summary>
    [RelayCommand]
    private void CreateNew(NewMenuItem item)
    {
        ClosePanel();
        if (item.Key != "entry" || CurrentPage is not (AppPage.Overview or AppPage.Transactions))
            CurrentPage = item.Page;
        switch (item.Key)
        {
            case "entry": NewEntry(); break;
            case "document": PickDocumentsRequested?.Invoke(); break;
            case "task": FocusNewTaskRequested?.Invoke(); break;
            case "appointment": NewAppointment(); break;
            case "note": NewNoteCommand.Execute(null); break;
            case "insurance": NewInsurance(); break;
            case "contract": DeadlinesTab = 0; NewContract(); break;
            case "date": DeadlinesTab = 1; NewImportantDate(); break;
            case "medicine": NewMedicine(); break;
            case "member": NewMember(); break;
            case "debt": NewDebt(); break;
            case "asset": NewAsset(); break;
            case "goal": PlanningTab = 0; NewGoal(); break;
        }
    }
}

/// <summary>Settings in tabs, so each part is quick to find.</summary>
public partial class MainViewModel
{
    /// <summary>0 general, 1 accounts, 2 household, 3 reminders &amp; calendar, 4 documents &amp; AI, 5 backup &amp; lock.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneralTab), nameof(IsSettingsAccountsTab), nameof(IsSettingsHouseholdTab),
        nameof(IsSettingsRemindersTab), nameof(IsSettingsDocumentsTab), nameof(IsSettingsSecurityTab))]
    public partial int SettingsTab { get; set; }

    public bool IsSettingsGeneralTab { get => SettingsTab == 0; set { if (value) SettingsTab = 0; } }
    public bool IsSettingsAccountsTab { get => SettingsTab == 1; set { if (value) SettingsTab = 1; } }
    public bool IsSettingsHouseholdTab { get => SettingsTab == 2; set { if (value) SettingsTab = 2; } }
    public bool IsSettingsRemindersTab { get => SettingsTab == 3; set { if (value) SettingsTab = 3; } }
    public bool IsSettingsDocumentsTab { get => SettingsTab == 4; set { if (value) SettingsTab = 4; } }
    public bool IsSettingsSecurityTab { get => SettingsTab == 5; set { if (value) SettingsTab = 5; } }
}
