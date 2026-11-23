using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Income, one-time expenses, recurring expenses and transfers are kept apart on the Transactions page, each with its own totals.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class TransactionTabsTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-tabs-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public TransactionTabsTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private async Task<MainViewModel> LoadAsync()
    {
        var month = new DateOnly(Today.Year, Today.Month, 1);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = month, Description = "Novatec salary", Category = "Salary", Amount = 7850,
            Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = month, Description = "Rent", Category = "Rent", Amount = 2450,
            Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = month, Description = "Household insurance", Category = "Other insurance", Amount = 1200,
            Type = TransactionType.Expense, Recurrence = Recurrence.Yearly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today, Description = "Sofa", Category = "Other", Amount = 900, Type = TransactionType.Expense,
        });
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        vm.CurrentPage = AppPage.Transactions;
        return vm;
    }

    [Fact]
    public async Task EachTabShowsOnlyItsOwnEntriesAndTotals()
    {
        var vm = await LoadAsync();

        Assert.True(vm.IsIncomeTab);
        Assert.Equal(["Novatec salary"], vm.EntryRows.Select(r => r.Description));
        Assert.Equal($"{Format.Money(7850)} a month coming in", vm.TransactionsTabTotal);
        Assert.Equal("New income", vm.NewEntryText);
        Assert.False(vm.ShowTransfersTab); // one account, no transfers

        vm.IsExpensesTab = true;
        // One-time spending only; rent comes back every month and is on the Recurring tab.
        Assert.Equal(["Sofa"], vm.EntryRows.Select(r => r.Description));
        Assert.StartsWith($"{Format.Money(900)} one-time in", vm.TransactionsTabTotal);
        Assert.Equal($"Recurring costs ({Format.Money(2450)} a month) are on the Recurring tab", vm.TransactionsTabDetail);
        Assert.False(vm.ShowScheduleFilter);
        Assert.Equal($"1 of 1 entry   ·   One-time: {Format.Money(900)}", vm.EntriesSummaryText);
        Assert.DoesNotContain("Salary", vm.CategoryFilters);
        Assert.DoesNotContain("Rent", vm.CategoryFilters);

        vm.IsRecurringTab = true;
        // The insurance premium is on the Insurance page instead.
        Assert.Equal(["Rent"], vm.EntryRows.Select(r => r.Description));
        Assert.Equal($"{Format.Money(2450)} a month going out", vm.TransactionsTabTotal);
        Assert.Equal($"{Format.Money(29400)} a year · 1 recurring cost now running", vm.TransactionsTabDetail);
        Assert.Contains("Rent", vm.CategoryFilters);
        Assert.True(vm.ShowScheduleFilter);
        Assert.Equal($"1 of 1 entry   ·   Repeating: {Format.Money(2450)} / month", vm.EntriesSummaryText);
        Assert.Equal("New recurring expense", vm.NewEntryText);
        vm.NewEntryCommand.Execute(null);
        Assert.True(vm.NewIsMonthly);
        Assert.False(vm.NewIsIncome);

        vm.IsExpensesTab = true;
        Assert.Equal("New expense", vm.NewEntryText);
    }

    [Fact]
    public async Task ANewEntryIsOfTheOpenTabsKind()
    {
        var vm = await LoadAsync();

        vm.IsExpensesTab = true;
        vm.NewEntryCommand.Execute(null);
        Assert.False(vm.NewIsIncome);

        vm.IsIncomeTab = true;
        vm.NewEntryCommand.Execute(null);
        Assert.True(vm.NewIsIncome);

        // An income saved while the Expenses tab is open shows up on its own tab.
        vm.IsExpensesTab = true;
        vm.NewEntryCommand.Execute(null);
        vm.NewIsIncome = true;
        vm.NewDescription = "Bonus";
        vm.NewAmount = 3000;
        await vm.SaveTransactionCommand.ExecuteAsync(null);
        Assert.True(vm.IsIncomeTab);
        Assert.Contains(vm.EntryRows, r => r.Description == "Bonus");
    }

    [Fact]
    public async Task EditingAnEntryOpensItsTab()
    {
        var vm = await LoadAsync();
        vm.EditTransactionCommand.Execute(vm.Transactions.Single(t => t.Description == "Rent"));
        Assert.True(vm.IsRecurringTab);
        vm.EditTransactionCommand.Execute(vm.Transactions.Single(t => t.Description == "Sofa"));
        Assert.True(vm.IsExpensesTab);
    }

    [Fact]
    public async Task TransfersGetATabWithASecondAccount()
    {
        var vm = await LoadAsync();
        await _repository.SaveBankAccountAsync(new BankAccount { Name = "Savings", Kind = BankAccountKind.Savings });
        var again = new MainViewModel(_repository);
        await again.LoadAsync();
        again.CurrentPage = AppPage.Transactions;

        Assert.True(again.ShowTransfersTab);
        again.IsTransfersTab = true;
        Assert.Empty(again.EntryRows);
        Assert.StartsWith("No transfers yet.", again.EntriesEmptyText);
        again.NewEntryCommand.Execute(null);
        Assert.True(again.NewIsTransfer);
        Assert.Equal("New transfer", again.NewEntryText);
    }

    [Fact]
    public async Task DroppingAnExpenseOnTheOtherTabChangesHowOftenItComes()
    {
        var vm = await LoadAsync();
        var sofa = vm.Transactions.Single(t => t.Description == "Sofa");
        var row = new EntryRow(sofa, "", "");
        Assert.True(vm.CanDrop("recurring", null, row));
        Assert.False(vm.CanDrop("expenses", null, row));

        await vm.DropAsync("recurring", null, row);
        Assert.Equal(Recurrence.Monthly, sofa.Recurrence);
        vm.IsRecurringTab = true;
        Assert.Contains(vm.EntryRows, r => r.Description == "Sofa");

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(Recurrence.OneTime, sofa.Recurrence);
    }
}
