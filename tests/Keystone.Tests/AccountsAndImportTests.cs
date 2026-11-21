using Keystone.Data;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

[Collection(nameof(LanguageCollection))]
public sealed class AccountsAndImportTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-accounts-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public AccountsAndImportTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task TheOldStartingBalanceBecomesTheMainAccount()
    {
        await _repository.SetStartingBalanceAsync(2_500m);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddDays(-3), Description = "Groceries", Amount = 100, Type = TransactionType.Expense,
        });

        var vm = await LoadAsync();

        var main = Assert.Single(vm.BankAccounts);
        Assert.Equal(2_500m, main.OpeningBalance);
        Assert.Equal(2_400m, vm.AccountBalance(main, Today));
        Assert.Equal(2_400m, vm.Summary.CurrentBalance);
        Assert.False(vm.HasSeveralAccounts);

        // Loading again doesn't make a second one.
        Assert.Single(await _repository.GetBankAccountsAsync());
    }

    [Fact]
    public async Task TransfersMoveMoneyWithoutChangingTheTotal()
    {
        await _repository.SetStartingBalanceAsync(5_000m);
        var vm = await LoadAsync();
        var main = vm.BankAccounts[0];

        vm.NewBankAccountCommand.Execute(null);
        vm.AccountName = "Savings";
        vm.AccountKindIndex = (int)BankAccountKind.Savings;
        vm.AccountIban = "ch56 0483 5012 3456 7800 9";
        vm.AccountBalanceToday = 10_000m;
        await vm.SaveBankAccountCommand.ExecuteAsync(null);
        var savings = vm.BankAccounts[1];
        Assert.Equal("CH5604835012345678009", savings.Iban);
        Assert.Equal(15_000m, vm.Summary.CurrentBalance);

        vm.NewEntryCommand.Execute(null);
        vm.NewIsTransfer = true;
        vm.NewAccount = vm.AccountOptions[0];
        vm.NewToAccount = vm.AccountOptions[1];
        vm.NewDescription = "To savings";
        vm.NewAmount = 1_000m;
        vm.NewDate = DateTime.Today.AddDays(-1);
        await vm.SaveTransactionCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);

        Assert.Equal(4_000m, vm.AccountBalance(main, Today));
        Assert.Equal(11_000m, vm.AccountBalance(savings, Today));
        Assert.Equal(15_000m, vm.Summary.CurrentBalance); // the household has as much as before
        Assert.Equal(0m, vm.Summary.YearAheadExpenses); // a transfer isn't spending
        Assert.Contains(vm.EntryRows, r => r.IsTransfer && r.DetailText.Contains("→ Savings"));

        // An account with entries can't be removed, nor can the main account.
        await vm.DeleteBankAccountCommand.ExecuteAsync(savings);
        Assert.Equal(2, vm.BankAccounts.Count);
        await vm.DeleteBankAccountCommand.ExecuteAsync(main);
        Assert.Equal(2, vm.BankAccounts.Count);
    }

    [Fact]
    public async Task TransfersNeedTwoDifferentAccounts()
    {
        var vm = await LoadAsync();
        vm.NewEntryCommand.Execute(null);
        vm.NewIsTransfer = true;
        vm.NewToAccount = vm.NewAccount;
        vm.NewDescription = "Nowhere";
        vm.NewAmount = 10;
        await vm.SaveTransactionCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task MatchingTheBankSetsOneAccount()
    {
        var vm = await LoadAsync();
        vm.BankBalance = 3_210.55m;
        await vm.MatchBankBalanceCommand.ExecuteAsync(null);
        Assert.Equal(3_210.55m, vm.AccountBalance(vm.BankAccounts[0], Today));
    }

    [Fact]
    public async Task OverdraftShowsHowFarItWouldStretch()
    {
        await _repository.SetStartingBalanceAsync(1_000m);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddDays(1), Description = "Rent", Amount = 1_500, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();
        Assert.False(vm.HasOverdraftRunway);

        vm.EditBankAccountCommand.Execute(vm.BankAccounts[0]);
        vm.AccountOverdraft = 2_000m;
        await vm.SaveBankAccountCommand.ExecuteAsync(null);

        Assert.True(vm.HasOverdraft);
        Assert.StartsWith("With your overdraft of", vm.OverdraftRunwayText);
        Assert.NotNull(vm.Summary.ZeroCashDate); // the runway itself still counts to zero
    }

    private string WriteCamt(string iban, params (string Date, decimal Amount, string Name, string Reference, string? CounterIban)[] bookings)
    {
        var entries = string.Join("\n", bookings.Select(b => $"""
            <Ntry><Amt Ccy="CHF">{Math.Abs(b.Amount):0.00}</Amt><CdtDbtInd>{(b.Amount < 0 ? "DBIT" : "CRDT")}</CdtDbtInd><Sts>BOOK</Sts>
              <BookgDt><Dt>{b.Date}</Dt></BookgDt><AcctSvcrRef>{b.Reference}</AcctSvcrRef>
              <NtryDtls><TxDtls><RltdPties>
                <{(b.Amount < 0 ? "Cdtr" : "Dbtr")}><Nm>{b.Name}</Nm></{(b.Amount < 0 ? "Cdtr" : "Dbtr")}>
                {(b.CounterIban is null ? "" : $"<CdtrAcct><Id><IBAN>{b.CounterIban}</IBAN></Id></CdtrAcct>")}
              </RltdPties></TxDtls></NtryDtls></Ntry>
            """));
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.04"><BkToCstmrStmt><Stmt>
              <Acct><Id><IBAN>{iban}</IBAN></Id></Acct>
              <Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy="CHF">4321.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>{Today.AddDays(-1):yyyy-MM-dd}</Dt></Dt></Bal>
              {entries}
            </Stmt></BkToCstmrStmt></Document>
            """;
        var path = Path.Combine(_folder, $"statement-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public async Task ImportsABankStatementIntoTheRightAccount()
    {
        // The rent is already entered as a repeating entry; its booking must not be added twice.
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddMonths(-2), Description = "Rent", Category = "Rent", Amount = 2_450, Type = TransactionType.Expense,
            Recurrence = Recurrence.Monthly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddMonths(-1), Description = "MIGROS BASEL", Category = "Groceries", Amount = 40, Type = TransactionType.Expense,
        });
        var vm = await LoadAsync();
        vm.NewBankAccountCommand.Execute(null);
        vm.AccountName = "Savings";
        vm.AccountIban = "CH5604835012345678009";
        await vm.SaveBankAccountCommand.ExecuteAsync(null);
        vm.EditBankAccountCommand.Execute(vm.BankAccounts[0]);
        vm.AccountIban = "CH9300762011623852957";
        await vm.SaveBankAccountCommand.ExecuteAsync(null);

        var rentDay = Schedule(Today.AddMonths(-2), Today);
        var path = WriteCamt("CH93 0076 2011 6238 5295 7",
            (rentDay.ToString("yyyy-MM-dd"), -2_450m, "Immobilien AG", "R-1", null),
            (Today.AddDays(-2).ToString("yyyy-MM-dd"), -87.40m, "MIGROS BASEL M-PARC", "R-2", null),
            (Today.AddDays(-1).ToString("yyyy-MM-dd"), -1_000m, "Lukas Meier", "R-3", "CH56 0483 5012 3456 7800 9"));

        await vm.ImportBankStatementAsync(path);

        Assert.True(vm.IsBankImport);
        Assert.Equal(vm.BankAccounts[0], vm.ImportAccount?.Account); // recognised by its IBAN
        Assert.Equal(3, vm.ImportItems.Count);
        Assert.True(vm.ImportItems[0].IsDuplicate); // the repeating rent
        Assert.False(vm.ImportItems[0].IsSelected);
        Assert.Equal("Groceries", vm.ImportItems[1].Category); // learned from the earlier MIGROS entry
        Assert.True(vm.ImportItems[2].IsTransfer); // to the savings account
        Assert.Equal(vm.BankAccounts[1], vm.ImportItems[2].CounterAccount?.Account);
        Assert.True(vm.HasImportBalance);

        await vm.AddImportedCommand.ExecuteAsync(null);
        Assert.Null(vm.ImportError);

        var saved = await _repository.GetTransactionsAsync();
        var transfer = Assert.Single(saved, t => t.IsTransfer);
        Assert.Equal(vm.BankAccounts[0].Id, transfer.AccountId);
        Assert.Equal(vm.BankAccounts[1].Id, transfer.ToAccountId);
        Assert.Contains(saved, t => t.ExternalId == "R-2" && t.Amount == 87.40m);
        // The account now shows the statement's closing balance on its date.
        Assert.Equal(4_321m, vm.AccountBalance(vm.BankAccounts[0], Today.AddDays(-1)));

        // Importing the same statement again finds everything already there.
        await vm.ImportBankStatementAsync(path);
        Assert.All(vm.ImportItems, i => Assert.True(i.IsDuplicate));
    }

    /// <summary>The rent's monthly date that falls in the last month.</summary>
    private static DateOnly Schedule(DateOnly start, DateOnly today) =>
        Keystone.Services.Schedule.Expand(
            [new Transaction { Date = start, Recurrence = Recurrence.Monthly, Amount = 1, Type = TransactionType.Expense }],
            today.AddMonths(-1), today).Last().Date;
}
