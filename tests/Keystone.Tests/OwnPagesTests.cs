using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Insurance premiums and tax payments, with their papers, are on the Insurance and Taxes pages.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class OwnPagesTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly DateOnly Month = new(Today.Year, Today.Month, 1);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-ownpages-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public OwnPagesTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private Task<Transaction> AddAsync(string description, string category, decimal amount, Recurrence recurrence = Recurrence.Monthly) =>
        _repository.AddTransactionAsync(new Transaction
        {
            Date = Month, Description = description, Category = category, Amount = amount,
            Type = TransactionType.Expense, Recurrence = recurrence,
        });

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    [Fact]
    public async Task PremiumsAndTaxesMoveToTheirOwnPages()
    {
        await AddAsync("Rent", "Rent", 2450);
        await AddAsync("Helsana family", Categories.HealthInsurance, 1080);
        await AddAsync("Household insurance", Categories.OtherInsurance, 640, Recurrence.Yearly);
        await AddAsync("Tax instalments", Categories.Taxes, 1150);
        var vm = await LoadAsync();
        vm.IsRecurringTab = true;

        Assert.Equal(["Rent"], vm.EntryRows.Select(r => r.Description));
        Assert.True(vm.HasMovedExpenses);
        Assert.Equal($"Insurance premiums ({Format.Money(1133.33m)} / month) and tax payments ({Format.Money(1150)} / month) are on their own pages.",
            vm.MovedExpensesText);

        Assert.Equal(["Helsana family", "Household insurance"], vm.InsuranceEntryRows.Select(r => r.Description).Order());
        Assert.Equal($"{Format.Money(1133.33m)} a month", vm.InsuranceEntriesTotal);
        Assert.Equal(["Tax instalments"], vm.TaxEntryRows.Select(r => r.Description));

        // They still count wherever money is added up, e.g. the year's spending.
        var thisMonth = vm.Report.Months.Single(m => m.Month == Month);
        Assert.Equal(2450m + 1080m + 1150m + 640m, thisMonth.Expenses); // the yearly premium falls this month
    }

    [Fact]
    public async Task APremiumAddedFromTheInsurancePageIsAnInsuranceExpense()
    {
        var vm = await LoadAsync();
        vm.CurrentPage = AppPage.Insurance;

        vm.NewInsuranceEntryCommand.Execute(null);
        Assert.True(vm.IsEntryPanelOpen);
        Assert.False(vm.NewIsIncome);
        vm.NewDescription = "Helsana basic";
        vm.NewAmount = 412.35m;
        vm.NewRecurrence = Recurrence.Monthly;
        await vm.SaveTransactionCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.InsuranceEntryRows);
        Assert.Equal(Categories.HealthInsurance, row.Transaction.Category);
        Assert.Equal(AppPage.Insurance, vm.CurrentPage);

        vm.NewTaxEntryCommand.Execute(null);
        vm.NewDescription = "Provisional tax";
        vm.NewAmount = 900;
        await vm.SaveTransactionCommand.ExecuteAsync(null);
        Assert.Equal(Categories.Taxes, Assert.Single(vm.TaxEntryRows).Transaction.Category);
    }

    [Fact]
    public async Task PapersAreSortedToInsuranceOrTaxes()
    {
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = "Mobiliar", Kind = InsuranceKind.HouseholdLiability, Premium = 640 });
        foreach (var (title, sender, kind) in new[]
                 {
                     ("Household policy", "Mobiliar", DocumentKind.Policy),
                     ("Premium November", "Helsana Versicherungen AG", DocumentKind.Bill),
                     ("Claim letter", "Mobiliar", DocumentKind.Letter),
                     ("Tax assessment 2025", "Steuerverwaltung Basel-Stadt", DocumentKind.Notice),
                     ("Provisional tax bill 2026", "Kanton Basel-Stadt", DocumentKind.Bill),
                     ("Electricity Q3", "IWB", DocumentKind.Bill),
                 })
            await _repository.AddDocumentAsync(new HouseholdDocument { Title = title, Sender = sender, Kind = kind, Date = Today, AddedAt = DateTime.Now });

        var vm = await LoadAsync();

        Assert.Equal(["Claim letter", "Household policy", "Premium November"], vm.InsuranceDocumentRows.Select(r => r.Title).Order());
        Assert.Equal(["Provisional tax bill 2026", "Tax assessment 2025"], vm.TaxDocumentRows.Select(r => r.Title).Order());
        // They stay on the Documents page too.
        Assert.Equal(6, vm.Documents.Count);
    }
}
