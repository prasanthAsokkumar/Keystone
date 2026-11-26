using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>The Overview: what needs attention, what is coming up, and this month's money.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class OverviewDashboardTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-overview-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public OverviewDashboardTests()
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
    public async Task NothingDueMeansAllClear()
    {
        var vm = await LoadAsync();
        Assert.False(vm.HasAttention);
        Assert.Equal("All clear: nothing needs your attention right now.", vm.AttentionSummary);
        Assert.False(vm.HasComingUp);
    }

    [Fact]
    public async Task OverdueBillsTasksAndDeadlinesNeedAttention()
    {
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Electricity Q3", Sender = "IWB", Kind = DocumentKind.Bill, Amount = 190m, Date = Today.AddDays(-40),
            DueDate = Today.AddDays(-3), RelativePath = "a.pdf",
        });
        await _repository.AddTaskAsync(new TaskItem { Title = "Send tax return", DueDate = Today.AddDays(-1), CreatedAt = DateTime.Now });
        await _repository.AddTaskAsync(new TaskItem { Title = "Later", DueDate = Today.AddDays(10), CreatedAt = DateTime.Now });
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Helsana", Kind = InsuranceKind.HealthBasic, ContractEnd = Today.AddDays(40), NoticeMonths = 1,
        });
        await _repository.SaveAsync(new Appointment
        {
            Title = "Dentist", Start = DateTime.Today.AddDays(1).AddHours(9), End = DateTime.Today.AddDays(1).AddHours(10),
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now,
        }, isNew: true);
        var vm = await LoadAsync();

        var texts = vm.AttentionRows.Select(r => r.Text).ToList();
        Assert.Equal($"1 bill overdue · {Format.Money(190)}", texts[0]); // overdue bills first
        Assert.True(vm.AttentionRows[0].IsUrgent);
        Assert.Contains("1 task to do today · 1 overdue", texts);
        Assert.DoesNotContain(texts, t => t.Contains("Later"));
        Assert.Contains(texts, t => t.StartsWith("Cancel Helsana by"));

        // "Open" goes where it is dealt with.
        vm.OpenAttentionCommand.Execute(vm.AttentionRows.Single(r => r.Kind == AttentionKind.Deadline));
        Assert.Equal(AppPage.Insurance, vm.CurrentPage);

        // Tomorrow's appointment and the notice deadline are coming up.
        Assert.Contains(vm.ComingUpRows, r => r.Kind == AttentionKind.Appointment && r.Title == "Dentist" && r.TimeText == "09:00");
        Assert.Contains(vm.ComingUpRows, r => r.Kind == AttentionKind.Deadline && r.Title == "Last day to cancel Helsana");
        Assert.Equal(vm.ComingUpRows.OrderBy(r => r.Date).Select(r => r.Date), vm.ComingUpRows.Select(r => r.Date));
    }

    [Fact]
    public async Task ThisMonthAddsUpMoneyInAndOut()
    {
        var start = new DateOnly(Today.Year, Today.Month, 1);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = start, Description = "Salary", Category = "Salary", Amount = 7000, Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = start, Description = "Rent", Category = "Rent", Amount = 2450, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();

        Assert.Equal(Format.Money(7000), vm.MonthInText);
        Assert.Equal(Format.Money(2450), vm.MonthOutText);
        Assert.Equal(Format.SignedMoney(4550), vm.MonthNetText);
        Assert.False(vm.MonthNetIsNegative);
        Assert.Equal(["week", "attention", "money", "upcoming", "chart"], vm.OverviewSectionOrder);
    }
}
