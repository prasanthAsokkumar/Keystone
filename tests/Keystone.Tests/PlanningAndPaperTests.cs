using Keystone.Data;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Contracts and dates, repeating tasks, the inbox, document text, QR-bills and planning.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class PlanningAndPaperTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-planning-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public PlanningAndPaperTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_folder);
    }

    private async Task<MainViewModel> LoadAsync(bool ownDocumentsFolder = true)
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        if (ownDocumentsFolder)
            await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    // ── Deadlines ──

    [Fact]
    public void ARentalContractWithThreeMonthsNotice()
    {
        var contract = new Contract { ContractEnd = new DateOnly(2027, 3, 31), NoticeMonths = 3, Cost = 2450, CostRecurrence = Recurrence.Monthly };
        Assert.Equal(new DateOnly(2026, 12, 31), contract.NextDeadline(new DateOnly(2026, 11, 22))!.Value.CancelBy);
        Assert.Equal(29_400m, contract.YearlyCost);
    }

    [Fact]
    public void RepeatingDatesMoveOn()
    {
        var vignette = new ImportantDate { Date = new DateOnly(2026, 1, 31), RepeatMonths = 12 };
        Assert.Equal(new DateOnly(2027, 1, 31), vignette.NextDate(new DateOnly(2026, 11, 22)));
        Assert.False(vignette.HasPassed(new DateOnly(2026, 11, 22)));

        var passport = new ImportantDate { Date = new DateOnly(2026, 3, 1) };
        Assert.True(passport.HasPassed(new DateOnly(2026, 11, 22)));
        Assert.Equal((180, 0), ImportantDate.Defaults(ImportantDateKind.Passport));
    }

    [Fact]
    public void WarrantiesLastTwoYears() =>
        Assert.Equal(new DateOnly(2028, 3, 14), new Warranty { PurchaseDate = new DateOnly(2026, 3, 15) }.ExpiresOn);

    [Fact]
    public async Task ContractsDatesAndWarrantiesAreReminded()
    {
        var vm = await LoadAsync();
        vm.NewContractCommand.Execute(null);
        vm.ContractProvider = "Swisscom";
        vm.ContractKindIndex = (int)ContractKind.Mobile;
        vm.ContractCost = 65;
        vm.ContractEnd = DateTime.Today.AddMonths(6);
        await vm.SaveContractCommand.ExecuteAsync(null);
        Assert.Null(vm.ContractError);

        vm.NewImportantDateCommand.Execute(null);
        vm.ImportantDateKindIndex = (int)ImportantDateKind.Passport;
        Assert.Equal("Passport", vm.ImportantDateTitle);
        Assert.Equal(180m, vm.ImportantDateRemindDays);
        vm.ImportantDateDate = DateTime.Today.AddMonths(8);
        await vm.SaveImportantDateCommand.ExecuteAsync(null);

        vm.NewWarrantyCommand.Execute(null);
        vm.WarrantyItem = "Washing machine";
        vm.WarrantyPurchaseDate = DateTime.Today.AddMonths(-20);
        await vm.SaveWarrantyCommand.ExecuteAsync(null);

        Assert.Single(vm.ContractRows);
        Assert.Single(vm.ImportantDateRows);
        Assert.Single(vm.WarrantyRows);
        var reminders = vm.PlanReminders(DateTime.Now);
        Assert.Contains(reminders, r => r.Id.StartsWith("contract-"));
        Assert.Contains(reminders, r => r.Id.StartsWith("date-") && r.Title.StartsWith("Coming up: Passport"));
        Assert.Contains(reminders, r => r.Id.StartsWith("warranty-"));
        Assert.Equal(3, vm.Search("swisscom").Count + vm.Search("passport").Count + vm.Search("washing").Count);
    }

    // ── Repeating tasks ──

    [Fact]
    public async Task ARepeatingTaskComesBack()
    {
        await _repository.AddTaskAsync(new TaskItem
        {
            Title = "Read the meters", DueDate = Today.AddMonths(-2), Repeat = TaskRepeat.Monthly, CreatedAt = DateTime.Now,
        });
        var vm = await LoadAsync();

        await vm.ToggleTaskCommand.ExecuteAsync(vm.TaskRows[0].Task);

        // Done late: the next one is the first monthly date from today on, not one already past.
        var next = Assert.Single(vm.TaskRows);
        Assert.Equal("Read the meters", next.Title);
        Assert.True(next.Task.DueDate >= Today);
        Assert.Equal(TaskRepeat.Monthly, next.Task.Repeat);
        Assert.Equal(2, (await _repository.GetTasksAsync()).Count);
    }

    // ── Documents ──

    [Fact]
    public void KeepsDocumentsAsLongAsSwissPracticeSays()
    {
        Assert.Equal(new DateOnly(2024, 12, 31), RetentionGuide.KeepUntil(new HouseholdDocument
        {
            Kind = DocumentKind.Bill, Amount = 100, Date = new DateOnly(2019, 3, 1), PaidOn = new DateOnly(2019, 3, 20),
        }));
        Assert.Equal(new DateOnly(2035, 12, 31), RetentionGuide.KeepUntil(new HouseholdDocument { Kind = DocumentKind.Notice, Date = new DateOnly(2025, 5, 1) }));
        Assert.Null(RetentionGuide.KeepUntil(new HouseholdDocument { Kind = DocumentKind.Payslip, Date = new DateOnly(2001, 1, 1) }));
        Assert.Null(RetentionGuide.KeepUntil(new HouseholdDocument { Kind = DocumentKind.Bill, Amount = 50, Date = new DateOnly(2010, 1, 1) })); // still to pay
    }

    [Fact]
    public async Task FindsWordsInsideDocuments()
    {
        var vm = await LoadAsync();
        var file = Path.Combine(_folder, "letter.txt");
        File.WriteAllText(file, "Sehr geehrte Frau Meier, Ihre Versichertennummer lautet 756.1234.5678.97.");
        await vm.StartNewDocumentAsync(file);
        vm.DocumentKindIndex = (int)DocumentKind.Letter;
        vm.DocumentTitle = "Welcome letter";
        await vm.SaveDocumentCommand.ExecuteAsync(null);

        for (var i = 0; i < 50 && vm.DocumentContent(vm.Documents[0].Id) is null; i++)
            await Task.Delay(20);

        vm.DocumentSearch = "756.1234";
        Assert.Single(vm.DocumentRows);
        var hit = Assert.Single(vm.Search("Versichertennummer"));
        Assert.Contains("Versichertennummer lautet", hit.Detail); // where it was found
    }

    [Fact]
    public async Task ReadsTheQrBillWhenFilingABill()
    {
        var vm = await LoadAsync();
        vm.ScanQrBill = _ => new QrBill("CH4431999123000889012", "Robert Schneider AG", 1949.75m, "CHF", "QRR",
            "210000000003139471430009017", "Auftrag vom 15.06.2020", new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1));
        var file = Path.Combine(_folder, "bill.pdf");
        File.WriteAllText(file, "x");

        await vm.StartNewDocumentAsync(file);

        Assert.Equal((int)DocumentKind.Bill, vm.DocumentKindIndex);
        Assert.Equal("Robert Schneider AG", vm.DocumentSender);
        Assert.Equal(1949.75m, vm.DocumentAmount);
        Assert.Equal(new DateTime(2026, 12, 1), vm.DocumentDueDate);
        Assert.Equal("CH44 3199 9123 0008 8901 2", vm.DocumentPaymentIban);
        Assert.Equal("21 00000 00003 13947 14300 09017", vm.DocumentPaymentReference);

        vm.DocumentCreateEntry = false;
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        var saved = Assert.Single(await _repository.GetDocumentsAsync());
        Assert.Equal("CH4431999123000889012", saved.PaymentIban);
    }

    [Fact]
    public async Task FilesScansFromTheInbox()
    {
        var vm = await LoadAsync();
        Directory.CreateDirectory(vm.InboxFolder);
        foreach (var name in new[] { "scan-1.pdf", "scan-2.jpg", ".DS_Store" })
        {
            var path = Path.Combine(vm.InboxFolder, name);
            File.WriteAllText(path, $"made-up scan {name}");
            File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-1));
        }
        File.WriteAllText(Path.Combine(vm.InboxFolder, "arriving.pdf"), "x"); // still being written

        vm.CheckInbox();
        Assert.Equal(2, vm.InboxFiles.Count);
        Assert.Equal("2 new scans in your inbox", vm.InboxText);

        vm.ScanQrBill = _ => null;
        await vm.FileInboxCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.BulkItems.Count);
        Assert.All(vm.BulkItems, i => Assert.True(i.FromInbox));

        await vm.SaveBulkCommand.ExecuteAsync(null);

        Assert.Equal(2, (await _repository.GetDocumentsAsync()).Count);
        Assert.False(File.Exists(Path.Combine(vm.InboxFolder, "scan-1.pdf"))); // filed, so gone from the inbox
        Assert.True(File.Exists(Path.Combine(vm.InboxFolder, "arriving.pdf")));
    }

    // ── Planning ──

    [Fact]
    public void ScenariosChangeEntriesFromADate()
    {
        var salary = new Transaction
        {
            Id = 1, Date = new DateOnly(2026, 1, 25), Description = "Salary", Amount = 5_000, Type = TransactionType.Income,
            Recurrence = Recurrence.Monthly,
        };
        var changes = new[]
        {
            new ScenarioChange(ScenarioChangeKind.ChangeEntry, new DateOnly(2027, 1, 1), EntryId: 1, Percent: 80),
            new ScenarioChange(ScenarioChangeKind.AddMonthly, new DateOnly(2027, 1, 5), Amount: 980, Description: "Daycare"),
            new ScenarioChange(ScenarioChangeKind.AddOnce, new DateOnly(2027, 6, 1), Amount: 25_000, Description: "Car"),
        };

        var result = Scenario.Apply([salary], changes);
        var december = Schedule.Expand(result, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));
        var february = Schedule.Expand(result, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28));

        Assert.Equal(5_000m, december.Sum(o => o.SignedAmount));
        Assert.Equal(4_000m - 980m, february.Sum(o => o.SignedAmount));
        Assert.Contains(result, t => t.Description == "Car" && t.Recurrence == Recurrence.OneTime);

        var saved = Scenario.Load(Scenario.Save(changes));
        Assert.Equal(changes, saved);
    }

    [Fact]
    public async Task WhatIfComparesWithoutTouchingRealEntries()
    {
        await _repository.SetStartingBalanceAsync(10_000);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddMonths(-1), Description = "Salary", Amount = 6_000, Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddMonths(-1), Description = "Rent", Amount = 5_500, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();

        vm.ScenarioKindIndex = 1; // stop an entry
        vm.ScenarioEntry = vm.ScenarioEntries.First(e => e.Transaction.Description == "Salary");
        await vm.AddScenarioChangeCommand.ExecuteAsync(null);

        Assert.Null(vm.Summary.ZeroCashDate);
        Assert.NotNull(vm.ScenarioSummary.ZeroCashDate); // without the salary, the money runs out
        Assert.Equal(2, (await _repository.GetTransactionsAsync()).Count);
        Assert.Contains(vm.ScenarioComparison, r => r.IsDifferent);

        // The changes are kept for next time.
        var again = await LoadAsync(ownDocumentsFolder: false);
        Assert.Single(again.ScenarioChangeRows);
    }

    [Fact]
    public async Task SavingsGoalsFollowTheirAccount()
    {
        var vm = await LoadAsync();
        vm.NewBankAccountCommand.Execute(null);
        vm.AccountName = "Holiday savings";
        vm.AccountKindIndex = (int)BankAccountKind.Savings;
        vm.AccountBalanceToday = 1_000;
        await vm.SaveBankAccountCommand.ExecuteAsync(null);

        vm.NewGoalCommand.Execute(null);
        vm.GoalName = "Japan";
        vm.GoalTarget = 7_000;
        vm.GoalDate = DateTime.Today.AddMonths(12);
        Assert.Equal("Holiday savings", vm.GoalAccount?.Account?.Name); // a savings account is suggested
        await vm.SaveGoalCommand.ExecuteAsync(null);

        var goal = Assert.Single(vm.GoalRows);
        Assert.Equal(1_000m, goal.Saved);
        Assert.Equal(500m, goal.MonthlyNeeded);
        Assert.True(goal.CanPlanTransfer);

        await vm.PlanGoalTransferCommand.ExecuteAsync(goal);
        var transfer = Assert.Single(vm.Transactions, t => t.IsTransfer);
        Assert.Equal(500m, transfer.Amount);
        Assert.Equal(vm.BankAccounts[1].Id, transfer.ToAccountId);
    }

    [Fact]
    public void PlansTheRetirementGap()
    {
        var anna = new Person { Id = 1, Name = "Anna", BirthYear = 1990 };
        PensionAccount Account(string name, PensionPillar pillar, decimal balance, decimal? pension = null) => new()
        {
            Name = name, Pillar = pillar, PersonId = 1,
            Statements = [new PensionStatement { Date = new DateOnly(2026, 1, 1), Balance = balance, ProjectedPension = pension }],
        };
        var accounts = new[]
        {
            Account("AHV", PensionPillar.Ahv, 0, 28_680),
            Account("Pensionskasse", PensionPillar.PensionFund, 100_000, 30_000),
            Account("VIAC", PensionPillar.Pillar3a, 20_000),
            Account("Frankly", PensionPillar.Pillar3a, 10_000),
        };

        var plan = RetirementPlanner.Plan(8_000, 80, [anna], accounts, _ => 7_000, 2026);

        var person = Assert.Single(plan.People);
        Assert.Equal(2055, person.RetirementYear);
        Assert.Equal((28_680m + 30_000m) / 12, person.PensionPerMonth);
        Assert.Equal(30_000m + 7_000m * 29, person.CapitalAtRetirement);
        Assert.Equal(6_400m, plan.NeedPerMonth);
        // Two 3a accounts: taken out at 63 and 64, before the pension fund starts at 65.
        Assert.Equal([63, 64], person.Withdrawals.Select(w => w.Age));
        Assert.Equal([2053, 2054], person.Withdrawals.Select(w => w.Year));
    }

    [Fact]
    public async Task NetWorthAddsUpAccountsSavingsAndDebts()
    {
        await _repository.SetStartingBalanceAsync(20_000);
        var viac = await _repository.SavePensionAccountAsync(new PensionAccount { Name = "VIAC", Pillar = PensionPillar.Pillar3a });
        await _repository.SavePensionStatementAsync(new PensionStatement { AccountId = viac.Id, Date = Today.AddMonths(-2), Balance = 15_000 });
        await _repository.AddDebtAsync(new Debt
        {
            Name = "Car loan", Balance = 8_000, StartDate = Today.AddDays(-1), AnnualInterestRate = 0, MonthlyPayment = 0,
            FirstPaymentDate = Today.AddYears(2),
        });
        var vm = await LoadAsync();

        Assert.Equal(27_000m, vm.NetWorthOn(Today));
        Assert.Equal(Format.Money(27_000), vm.NetWorthTodayText);
        Assert.Equal(24, vm.NetWorthHistory.Count);
    }
}
