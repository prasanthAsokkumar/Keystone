using Keystone.Data;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Uses formatted texts, so it runs apart from the language tests.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class RetirementTests : IDisposable
{
    private static readonly int ThisYear = DateTime.Today.Year;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"runway-test-{Guid.NewGuid():N}.db");
    private readonly KeystoneRepository _repository;

    public RetirementTests()
    {
        _repository = new KeystoneRepository(_databasePath);
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Theory]
    [InlineData(2024, true, 7_056)]
    [InlineData(2025, true, 7_258)]
    [InlineData(2026, false, 36_288)]
    [InlineData(2030, true, 7_258)] // later years use the latest known limits
    [InlineData(2010, true, 6_883)]
    public void Pillar3aLimits(int year, bool hasPensionFund, decimal limit) =>
        Assert.Equal(limit, SwissPension.Pillar3aLimit(year, hasPensionFund));

    [Fact]
    public void ReadsAPensionFundCertificate()
    {
        var details = PensionStatementExtraction.Parse("""
            {"pillar":"pension_fund","provider":"Pensionskasse Basel-Stadt","date":"2026-01-01","balance":"123'456.70",
             "paid_in":14200,"projected_capital":512000,"projected_pension":34816,"buy_in":85000.5}
            """);

        Assert.Equal(PensionPillar.PensionFund, details.Pillar);
        Assert.Equal("Pensionskasse Basel-Stadt", details.Provider);
        Assert.Equal(new DateOnly(2026, 1, 1), details.Date);
        Assert.Equal(123_456.70m, details.Balance);
        Assert.Equal(34_816m, details.ProjectedPension);
        Assert.Equal(85_000.5m, details.BuyInPotential);
    }

    [Fact]
    public void ReadsSwissDates() =>
        Assert.Equal(new DateOnly(2026, 1, 1), PensionStatementExtraction.Parse(
            """{"pillar":"pillar_3a","provider":"VIAC","date":"01.01.2026","balance":1,"paid_in":0,"projected_capital":0,"projected_pension":0,"buy_in":0}""").Date);

    [Fact]
    public void MissingFiguresStayEmpty()
    {
        var details = PensionStatementExtraction.Parse(
            """{"pillar":"something","provider":"","date":"","balance":0,"paid_in":0,"projected_capital":0,"projected_pension":0,"buy_in":0}""");

        Assert.Null(details.Pillar);
        Assert.Null(details.Date);
        Assert.Null(details.Balance);
        Assert.Null(details.ProjectedCapital);
    }

    [Fact]
    public async Task AccountsAndStatements_RoundTripOldestFirst()
    {
        var account = await _repository.SavePensionAccountAsync(new PensionAccount { Name = "VIAC", Pillar = PensionPillar.Pillar3a });
        await _repository.SavePensionStatementAsync(new PensionStatement { AccountId = account.Id, Date = new DateOnly(2025, 12, 31), Balance = 30_000 });
        await _repository.SavePensionStatementAsync(new PensionStatement { AccountId = account.Id, Date = new DateOnly(2024, 12, 31), Balance = 21_000 });

        var saved = Assert.Single(await _repository.GetPensionAccountsAsync());
        Assert.Equal(PensionPillar.Pillar3a, saved.Pillar);
        Assert.Equal([21_000m, 30_000m], saved.Statements.Select(s => s.Balance));

        await _repository.DeletePensionAccountAsync(account.Id);
        Assert.Empty(await _repository.GetPensionAccountsAsync());
    }

    [Fact]
    public async Task SavingStatements_UpdatesTotalsAndPension()
    {
        var person = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();

        // The first letter creates the account.
        vm.NewStatementCommand.Execute(null);
        Assert.True(vm.IsNewStatementAccount);
        vm.NewPensionAccountName = "Pensionskasse Basel-Stadt";
        vm.NewPensionPillarIndex = (int)PensionPillar.PensionFund;
        vm.NewPensionPerson = vm.PersonOptions.First(o => o.Id == person.Id);
        vm.StatementDate = new DateTime(2025, 1, 1);
        vm.StatementBalance = 100_000;
        await vm.SaveStatementCommand.ExecuteAsync(null);
        Assert.Null(vm.StatementError);

        // A year later, the next letter for the same account.
        var account = Assert.Single(vm.PensionAccountRows).Account;
        vm.AddStatementToCommand.Execute(account);
        Assert.False(vm.IsNewStatementAccount);
        vm.StatementDate = new DateTime(2026, 1, 1);
        vm.StatementBalance = 112_000;
        vm.StatementProjectedCapital = 480_000;
        vm.StatementProjectedPension = 32_640;
        vm.StatementBuyIn = 85_000;
        await vm.SaveStatementCommand.ExecuteAsync(null);

        // AHV letters project a pension; they hold no savings.
        vm.NewStatementCommand.Execute(null);
        vm.StatementAccount = vm.StatementAccountOptions[^1];
        vm.NewPensionAccountName = "AHV";
        vm.NewPensionPillarIndex = (int)PensionPillar.Ahv;
        vm.StatementDate = new DateTime(2026, 3, 1);
        vm.StatementProjectedPension = 27_000;
        await vm.SaveStatementCommand.ExecuteAsync(null);
        Assert.Null(vm.StatementError);

        Assert.Equal(Format(112_000), vm.RetirementSavingsText);
        Assert.Contains(Format(12_000), vm.RetirementSavingsDetail);
        Assert.Equal($"{Format((32_640 + 27_000) / 12m)} / month", vm.ExpectedPensionText);
        Assert.Equal(Format(85_000), vm.BuyInText);
        var row = vm.PensionAccountRows.First(r => r.Account.Pillar == PensionPillar.PensionFund);
        Assert.Contains("+12.0%", row.ChangeText);

        var accounts = await _repository.GetPensionAccountsAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal(2, accounts.First(a => a.Pillar == PensionPillar.PensionFund).Statements.Count);
    }

    [Fact]
    public async Task Pillar3a_AddsUpThisYearsPaymentsAgainstTheLimit()
    {
        var person = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        await _repository.SavePensionAccountAsync(new PensionAccount { Name = "PK", Pillar = PensionPillar.PensionFund, PersonId = person.Id });
        await _repository.SavePensionAccountAsync(new PensionAccount { Name = "VIAC", Pillar = PensionPillar.Pillar3a, PersonId = person.Id });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(ThisYear, 1, 25), Description = "3a", Category = Categories.Pillar3a, Amount = 500,
            Type = TransactionType.Expense, Recurrence = Recurrence.Monthly, PersonId = person.Id,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(ThisYear - 1, 12, 20), Description = "Last year", Category = Categories.Pillar3a, Amount = 7_000,
            Type = TransactionType.Expense, PersonId = person.Id,
        });

        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();

        var row = Assert.Single(vm.Pillar3aRows);
        Assert.Equal(6_000m, row.Paid); // 12 monthly payments this year
        Assert.Equal(SwissPension.Pillar3aLimit(ThisYear, hasPensionFund: true), row.Limit);
        Assert.Equal(row.Limit - 6_000m, row.Left);

        vm.AddPillar3aPaymentCommand.Execute(row);
        Assert.True(vm.IsEntryPanelOpen);
        Assert.Equal(row.Left, vm.NewAmount);
        Assert.Equal(person.Id, vm.NewPerson.Id);
    }

    [Fact]
    public async Task ReadLetter_PicksTheMatchingAccount()
    {
        await _repository.SavePensionAccountAsync(new PensionAccount { Name = "VIAC 3a", Pillar = PensionPillar.Pillar3a });
        await _repository.SavePensionAccountAsync(new PensionAccount { Name = "Pensionskasse Basel-Stadt", Pillar = PensionPillar.PensionFund });
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();

        vm.NewStatementCommand.Execute(null);
        vm.ApplyStatementDetails(new PensionStatementDetails(
            PensionPillar.PensionFund, "Pensionskasse Basel-Stadt", new DateOnly(2026, 1, 1), 99_000, null, 400_000, 30_000, null));
        Assert.Equal("Pensionskasse Basel-Stadt", vm.StatementAccount?.Account?.Name);
        Assert.Equal(99_000m, vm.StatementBalance);
        Assert.Equal(400_000m, vm.StatementProjectedCapital);

        // An unknown provider prepares a new account.
        vm.NewStatementCommand.Execute(null);
        vm.ApplyStatementDetails(new PensionStatementDetails(
            PensionPillar.VestedBenefits, "Stiftung Auffangeinrichtung", null, 15_000, null, null, null, null));
        Assert.True(vm.IsNewStatementAccount);
        Assert.Equal("Stiftung Auffangeinrichtung", vm.NewPensionAccountName);
        Assert.Equal((int)PensionPillar.VestedBenefits, vm.NewPensionPillarIndex);
    }

    private static string Format(decimal value) => value.ToString("C0", System.Globalization.CultureInfo.CurrentCulture);
}
