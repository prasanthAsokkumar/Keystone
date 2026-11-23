using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Assets with values over time, in net worth and the tax year; and the tax estimate from the household's own rate.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class AssetsAndTaxEstimateTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-assets-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public AssetsAndTaxEstimateTests()
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
        return vm;
    }

    [Fact]
    public async Task AnAssetKeepsItsValuesAndCountsInNetWorth()
    {
        var vm = await LoadAsync();
        var before = vm.NetWorthOn(Today);

        vm.NewAssetCommand.Execute(null);
        vm.AssetName = "ETF depot";
        vm.AssetKindIndex = (int)AssetKind.Securities;
        vm.AssetInstitution = "Swissquote";
        vm.AssetValueAmount = 40000m;
        vm.AssetValueDate = Today.AddMonths(-3).ToDateTime(TimeOnly.MinValue);
        await vm.SaveAssetCommand.ExecuteAsync(null);
        Assert.Null(vm.AssetError);

        // A new value from today's statement.
        var asset = vm.AssetRows.Single().Asset;
        vm.EditAssetCommand.Execute(asset);
        vm.AssetValueAmount = 43500m;
        vm.AssetValueDate = DateTime.Today;
        await vm.SaveAssetCommand.ExecuteAsync(null);

        var row = vm.AssetRows.Single();
        Assert.Equal(Format.Money(43500), row.ValueText);
        Assert.Equal($"{Format.SignedMoney(3500)} since {Format.Date(Today.AddMonths(-3))}", row.ChangeText);
        Assert.True(row.IsUp);
        Assert.Equal(before + 43500m, vm.NetWorthOn(Today));
        Assert.Equal(Format.Money(43500), vm.AssetsTotalText);

        // Read back from the database with both values.
        var again = await LoadAsync();
        Assert.Equal(2, again.AssetRows.Single().Asset.Values.Count);

        // Sold: no longer counts.
        vm.EditAssetCommand.Execute(asset);
        vm.AssetSoldOn = DateTime.Today;
        await vm.SaveAssetCommand.ExecuteAsync(null);
        Assert.Equal(0m, vm.AssetsOn(Today));
        Assert.Equal(40000m, vm.AssetsOn(Today.AddDays(-1))); // yesterday it still had the older value
    }

    [Fact]
    public async Task TheTaxYearListsAssetsOn31December()
    {
        var vm = await LoadAsync();
        vm.TaxYear = Today.Year - 1;
        vm.NewAssetCommand.Execute(null);
        vm.AssetName = "Flat";
        vm.AssetKindIndex = (int)AssetKind.Property;
        vm.AssetValueAmount = 650000m;
        vm.AssetValueDate = new DateTime(Today.Year - 1, 6, 30);
        await vm.SaveAssetCommand.ExecuteAsync(null);

        var line = vm.TaxSections.Single(s => s.Title == "Assets and debts on 31 December").Lines.Single(l => l.Label == "Flat");
        Assert.Equal(Format.Money(650000), line.AmountText);
        Assert.Equal("Declare property at its tax value (Steuerwert), not what it would sell for.", line.Note);
    }

    [Fact]
    public async Task TheEstimateUsesLastYearsRateAndComparesWithInstalments()
    {
        // Last year and this year: salary 120'000, a 3a payment of 7'000; this year provisional instalments of 1'200 a month.
        foreach (var year in new[] { Today.Year - 1, Today.Year })
        {
            await _repository.AddTransactionAsync(new Transaction
            {
                Date = new DateOnly(year, 1, 25), EndDate = new DateOnly(year, 12, 31), Description = "Salary", Category = "Salary",
                Amount = 10000, Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
            });
            await _repository.AddTransactionAsync(new Transaction
            {
                Date = new DateOnly(year, 12, 1), Description = "3a", Category = Categories.Pillar3a, Amount = 7000, Type = TransactionType.Expense,
            });
        }
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(Today.Year, 1, 31), EndDate = new DateOnly(Today.Year, 12, 31), Description = "Tax instalments",
            Category = Categories.Taxes, Amount = 1200, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();
        vm.TaxYear = Today.Year;

        Assert.False(vm.HasTaxEstimate);
        Assert.Equal("Enter last year’s total tax, or a rate, to see an estimate.", vm.TaxEstimateDetail);

        // Last year's tax was 16'950 on 113'000 (120'000 minus 7'000): a rate of 15%.
        vm.TaxReferenceAmount = 16950m;
        Assert.Equal($"≈ {Format.Money(16950)}", vm.TaxEstimateText);
        Assert.Contains("the rate of your", vm.TaxEstimateDetail);
        Assert.Equal($"About {Format.Money(16950 - 14400)} more to pay", vm.TaxBalanceText);
        Assert.True(vm.TaxBalanceIsOwed);

        // Kept, and a hand rate is only used without an assessment.
        var again = await LoadAsync();
        again.TaxYear = Today.Year;
        Assert.Equal(16950m, again.TaxReferenceAmount);
        again.TaxReferenceAmount = null;
        again.TaxRateInput = 10m;
        Assert.Equal($"≈ {Format.Money(11300)}", again.TaxEstimateText);
        Assert.Equal($"About {Format.Money(14400 - 11300)} back", again.TaxBalanceText);
    }

    [Fact]
    public async Task ATaxForAYearWithoutIncomeSaysWhyThereIsNoEstimate()
    {
        var vm = await LoadAsync();
        vm.TaxYear = Today.Year;
        vm.TaxReferenceAmount = 17400m;
        Assert.False(vm.HasTaxEstimate);
        Assert.StartsWith($"Keystone has no income recorded for {Today.Year - 1}", vm.TaxEstimateDetail);
    }
}
