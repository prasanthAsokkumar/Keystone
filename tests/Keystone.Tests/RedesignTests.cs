using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>
/// The simpler app: grouped places with tabs, one "+ New" menu, the week strip and the choice of what needs
/// attention, one list of notice deadlines, papers kept under Documents, one person's view, shorter forms and
/// settings in tabs.
/// </summary>
[Collection(nameof(LanguageCollection))]
public sealed class RedesignTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-redesign-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public RedesignTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        vm.RefreshDashboard();
        return vm;
    }

    [Fact]
    public async Task PagesThatBelongTogetherAreTabsOfOnePlace()
    {
        var vm = await LoadAsync();
        vm.IsWealthPlace = true;
        Assert.Equal(AppPage.Debts, vm.CurrentPage);
        Assert.Equal("Debts & assets", vm.PageTitle);
        Assert.Equal(["Debts", "Assets", "Net worth"], vm.PageTabs.Select(t => t.Title));

        vm.GoToPageCommand.Execute(AppPage.NetWorth);
        Assert.True(vm.IsWealthPlace);
        Assert.True(vm.PageTabs.Single(t => t.IsSelected).Page == AppPage.NetWorth);

        // Going elsewhere and back reopens the tab last used.
        vm.IsAgendaPlace = true;
        Assert.Equal(AppPage.Tasks, vm.CurrentPage);
        Assert.False(vm.IsWealthPlace);
        vm.IsWealthPlace = true;
        Assert.Equal(AppPage.NetWorth, vm.CurrentPage);

        vm.CurrentPage = AppPage.Notes;
        Assert.False(vm.HasPageTabs);
    }

    [Fact]
    public async Task NewMenuPutsThisPagesThingsFirstAndOpensTheirForm()
    {
        var vm = await LoadAsync();
        await _repository.AddPersonAsync(new Person { Name = "Anna" });
        vm = await LoadAsync();

        vm.CurrentPage = AppPage.Household;
        Assert.Equal(["medicine", "member"], vm.NewMenuItems.Take(2).Select(i => i.Key));

        vm.CurrentPage = AppPage.Overview;
        vm.CreateNewCommand.Execute(vm.NewMenuItems.Single(i => i.Key == "medicine"));
        Assert.Equal(AppPage.Household, vm.CurrentPage);
        Assert.True(vm.IsMedicinePanelOpen);

        vm.CreateNewCommand.Execute(vm.NewMenuItems.Single(i => i.Key == "date"));
        Assert.Equal(AppPage.Contracts, vm.CurrentPage);
        Assert.True(vm.IsImportantDatePanelOpen);
        Assert.False(vm.IsMedicinePanelOpen);

        var asked = false;
        vm.PickDocumentsRequested += () => asked = true;
        vm.CreateNewCommand.Execute(vm.NewMenuItems.Single(i => i.Key == "document"));
        Assert.True(asked);
        Assert.Equal(AppPage.Documents, vm.CurrentPage);
    }

    [Fact]
    public async Task TheWeekStripShowsEachDaysThings()
    {
        await _repository.AddTaskAsync(new TaskItem { Title = "Overdue task", DueDate = Today.AddDays(-2), CreatedAt = DateTime.Now });
        await _repository.SaveAsync(new Appointment
        {
            Title = "Dentist", Start = DateTime.Today.AddDays(2).AddHours(14), End = DateTime.Today.AddDays(2).AddHours(15),
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now,
        }, isNew: true);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddDays(3), Description = "Rent", Category = "Rent", Amount = 2450, Type = TransactionType.Expense,
        });
        var vm = await LoadAsync();

        Assert.Equal(7, vm.WeekDays.Count);
        Assert.True(vm.WeekDays[0].IsToday);
        Assert.Contains(vm.WeekDays[0].Items, i => i.IsTask && i.Text == "Overdue task"); // overdue shows today
        Assert.Contains(vm.WeekDays[2].Items, i => i.IsAppointment && i.Text == "14:00 Dentist");
        Assert.Contains(vm.WeekDays[3].Items, i => i.IsMoney && i.Text == "Rent");
        Assert.Equal("1 appointment · 1 task · 1 payment or bill", vm.WeekSummary);
    }

    [Fact]
    public async Task HiddenAttentionKindsLeaveTheListAndAreRemembered()
    {
        await _repository.AddTaskAsync(new TaskItem { Title = "Send tax return", DueDate = Today, CreatedAt = DateTime.Now });
        var vm = await LoadAsync();
        Assert.Contains(vm.AttentionRows, r => r.Kind == AttentionKind.Task);

        vm.AttentionFilters.Single(f => f.Key == "tasks").IsShown = false;
        Assert.DoesNotContain(vm.AttentionRows, r => r.Kind == AttentionKind.Task);
        Assert.Equal(" · 1 kind hidden", vm.HiddenAttentionText);
        Assert.DoesNotContain(vm.WeekDays[0].Items, i => i.IsTask);

        await Task.Delay(200); // the setting saves in the background
        var reloaded = await LoadAsync();
        Assert.False(reloaded.AttentionFilters.Single(f => f.Key == "tasks").IsShown);
        Assert.DoesNotContain(reloaded.AttentionRows, r => r.Kind == AttentionKind.Task);
    }

    [Fact]
    public async Task MoneyLastsIsADate()
    {
        await _repository.SetStartingBalanceAsync(5_000m);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddDays(1), Description = "Rent", Category = "Rent", Amount = 1_500, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();
        Assert.NotNull(vm.Summary.ZeroCashDate);
        Assert.Equal($"until {vm.Summary.ZeroCashDate!.Value:MMM yyyy}", vm.MoneyLastsText);
        Assert.EndsWith("from today", vm.MoneyLastsDetail);
    }

    [Fact]
    public async Task ContractsAndInsuranceDeadlinesAreOneList()
    {
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Helsana", Kind = InsuranceKind.HealthBasic, ContractEnd = Today.AddDays(70), NoticeMonths = 1,
        });
        await _repository.SaveAsync(new Contract
        {
            Provider = "Swisscom", Kind = ContractKind.Mobile, ContractEnd = Today.AddDays(200), NoticeMonths = 2,
        }, isNew: true);
        var vm = await LoadAsync();

        Assert.Equal(["Helsana", "Swisscom"], vm.AllDeadlineRows.Select(r => r.Name));
        Assert.True(vm.AllDeadlineRows[0].IsInsurance);
        Assert.Equal("Insurance", vm.AllDeadlineRows[0].SourceText);
        Assert.Equal(Format.Date(vm.AllDeadlineRows[0].CancelBy), vm.ContractDeadlineText);

        vm.OpenNoticeDeadlineCommand.Execute(vm.AllDeadlineRows[0]);
        Assert.Equal(AppPage.Insurance, vm.CurrentPage);
        Assert.True(vm.IsInsurancePanelOpen);
    }

    [Fact]
    public async Task OnePersonsViewNarrowsEveryPage()
    {
        await _repository.AddPersonAsync(new Person { Name = "Anna" });
        await _repository.AddPersonAsync(new Person { Name = "Lukas" });
        var vm = await LoadAsync();
        var anna = vm.People.Single(p => p.Name == "Anna");
        var lukas = vm.People.Single(p => p.Name == "Lukas");
        await _repository.SaveAsync(new Contract { Provider = "Swisscom", Kind = ContractKind.Mobile, PersonId = anna.Id }, isNew: true);
        await _repository.SaveAsync(new Contract { Provider = "Sunrise", Kind = ContractKind.Mobile, PersonId = lukas.Id }, isNew: true);
        vm = await LoadAsync();

        vm.ViewPerson = vm.ViewPersonOptions.Single(o => o.Name == "Anna");
        Assert.True(vm.IsPersonView);
        Assert.Equal(anna.Id, vm.PersonFilter.Id);
        Assert.Equal(anna.Id, vm.DocumentPersonFilter.Id);
        Assert.Equal(anna.Id, vm.TaskPersonFilter.Id);
        Assert.Equal(anna.Id, vm.SelectedMemberId);
        Assert.Equal(["Swisscom"], vm.ContractRows.Select(r => r.Contract.Provider));
        vm.CurrentPage = AppPage.Contracts;
        Assert.True(vm.ShowsPersonViewBar);
        Assert.Equal("Showing Anna’s things only", vm.PersonViewText);

        vm.ShowEveryoneCommand.Execute(null);
        Assert.True(vm.PersonFilter.IsEveryone);
        Assert.Equal(2, vm.ContractRows.Count);
        Assert.False(vm.ShowsPersonViewBar);
    }

    [Fact]
    public async Task InsurancePapersAreShownUnderDocuments()
    {
        for (var i = 0; i < 5; i++)
            await _repository.AddDocumentAsync(new HouseholdDocument
            {
                Title = $"Police {i}", Sender = "Helsana", Kind = DocumentKind.Policy, Date = Today.AddDays(-i), RelativePath = $"p{i}.pdf",
            });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Electricity", Sender = "IWB", Kind = DocumentKind.Bill, Date = Today, RelativePath = "e.pdf",
        });
        var vm = await LoadAsync();

        Assert.Equal(3, vm.RecentInsuranceDocumentRows.Count);
        Assert.Equal("Police 0", vm.RecentInsuranceDocumentRows[0].Title);
        Assert.Equal("Show all 5 insurance papers in Documents →", vm.InsurancePapersLinkText);

        vm.ShowPapersCommand.Execute("insurance");
        Assert.Equal(AppPage.Documents, vm.CurrentPage);
        Assert.Equal(5, vm.DocumentRows.Count);
        Assert.DoesNotContain(vm.DocumentRows, r => r.Title == "Electricity");
    }

    [Fact]
    public async Task OptionalFieldsUnfoldOnlyWhenTheyHaveSomething()
    {
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = "Mobiliar", Kind = InsuranceKind.HouseholdLiability, PolicyNumber = "123" });
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = "AXA", Kind = InsuranceKind.Car });
        var vm = await LoadAsync();

        vm.NewInsuranceCommand.Execute(null);
        Assert.False(vm.InsuranceMoreOpen);
        vm.ClosePanelCommand.Execute(null);

        vm.EditInsuranceCommand.Execute(vm.InsuranceRows.Single(r => r.Policy.Insurer == "Mobiliar").Policy);
        Assert.True(vm.InsuranceMoreOpen);
        vm.ClosePanelCommand.Execute(null);

        vm.EditInsuranceCommand.Execute(vm.InsuranceRows.Single(r => r.Policy.Insurer == "AXA").Policy);
        Assert.False(vm.InsuranceMoreOpen);
    }

    [Fact]
    public async Task CalendarSettingsOpenOnTheirTab()
    {
        var vm = await LoadAsync();
        vm.ShowCalendarSettingsCommand.Execute(null);
        Assert.Equal(AppPage.Settings, vm.CurrentPage);
        Assert.True(vm.IsSettingsRemindersTab);
        Assert.False(vm.IsSettingsGeneralTab);
    }
}
