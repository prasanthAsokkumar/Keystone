using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Changing one time of a repeating entry, editing debts and borrowing more, undoing deletes, and exports.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class ChangesUndoExportTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-changes-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public ChangesUndoExportTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_folder);
    }

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    private static Transaction Salary(DateOnly start) => new()
    {
        Date = start, Description = "Salary", Category = "Salary", Amount = 5000,
        Type = TransactionType.Income, Recurrence = Recurrence.Monthly,
    };

    // ── Changing one time ──

    [Fact]
    public void AChangedOrSkippedTimeReplacesOnlyThatOccurrence()
    {
        var rent = new Transaction
        {
            Date = new DateOnly(2026, 1, 1), Description = "Rent", Amount = 2000,
            Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
            Changes =
            [
                new EntryChange { Date = new DateOnly(2026, 2, 1), Amount = 2500 },
                new EntryChange { Date = new DateOnly(2026, 3, 1), Amount = null },
            ],
        };

        var occurrences = Schedule.Expand([rent], new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30));

        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 4, 1)], occurrences.Select(o => o.Date));
        Assert.Equal([-2000m, -2500m, -2000m], occurrences.Select(o => o.SignedAmount));
        // The regular dates still include the skipped one, so it can be changed back.
        Assert.Equal(4, Schedule.RegularDates(rent, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)).Count());
    }

    [Fact]
    public async Task A13thSalaryChangesTheBalanceAndTheYear()
    {
        var start = Today.AddMonths(-3);
        await _repository.AddTransactionAsync(Salary(start));
        var vm = await LoadAsync();
        var before = vm.Summary.CurrentBalance;

        vm.EditTransactionCommand.Execute(vm.Transactions.Single());
        Assert.True(vm.CanChangeOneTime);
        var paidLastMonth = vm.ChangeDateOptions.Last(o => o.Date <= Today);
        vm.ChangeDate = paidLastMonth;
        Assert.Equal(5000m, vm.ChangeAmount);
        vm.ChangeAmount = 10000;
        vm.ChangeNote = "13th salary";
        await vm.SaveEntryChangeCommand.ExecuteAsync(null);

        Assert.Equal(before + 5000, vm.Summary.CurrentBalance);
        Assert.Single(vm.EntryChangeRows);

        // Kept after a restart.
        var again = await LoadAsync();
        Assert.Equal(before + 5000, again.Summary.CurrentBalance);
        Assert.Equal("13th salary", again.Transactions.Single().Changes.Single().Note);

        // Skipping it instead takes the whole month out.
        again.EditTransactionCommand.Execute(again.Transactions.Single());
        again.ChangeDate = again.ChangeDateOptions.Single(o => o.Date == paidLastMonth.Date);
        again.ChangeSkip = true;
        await again.SaveEntryChangeCommand.ExecuteAsync(null);
        Assert.Equal(before - 5000, again.Summary.CurrentBalance);

        // And back to the usual.
        await again.RemoveEntryChangeCommand.ExecuteAsync(again.EntryChangeRows.Single());
        Assert.Equal(before, again.Summary.CurrentBalance);
        Assert.Empty((await _repository.GetTransactionsAsync()).Single().Changes);
    }

    [Fact]
    public async Task EditingAnEntryKeepsChangesThatStillFit()
    {
        var start = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-2);
        var salary = Salary(start);
        salary.Changes = [new EntryChange { Date = start.AddMonths(1), Amount = 6000 }];
        await _repository.AddTransactionAsync(salary);
        var vm = await LoadAsync();

        // A new description keeps the change…
        vm.EditTransactionCommand.Execute(vm.Transactions.Single());
        vm.NewDescription = "Salary Anna";
        await vm.SaveTransactionCommand.ExecuteAsync(null);
        Assert.Single((await _repository.GetTransactionsAsync()).Single().Changes);

        // …moving the payday to the 25th drops it, because that date no longer comes up.
        vm.EditTransactionCommand.Execute(vm.Transactions.Single());
        vm.NewDate = start.AddDays(24).ToDateTime(TimeOnly.MinValue);
        await vm.SaveTransactionCommand.ExecuteAsync(null);
        Assert.Empty((await _repository.GetTransactionsAsync()).Single().Changes);
        Assert.Empty(vm.Transactions.Single().Changes);
    }

    // ── Debts ──

    private static async Task AddDebtAsync(MainViewModel vm, string name, decimal balance, decimal payment)
    {
        vm.NewDebtCommand.Execute(null);
        vm.NewDebtName = name;
        vm.NewDebtBalance = balance;
        vm.NewDebtIsMonthly = true;
        vm.NewDebtRate = 0;
        vm.NewDebtPayment = payment;
        vm.NewDebtDate = DateTime.Today.AddMonths(1);
        await vm.AddDebtCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ADebtCanBeCorrected()
    {
        var vm = await LoadAsync();
        await AddDebtAsync(vm, "Car loan", 12_000, 500);

        vm.Debts.Single().EditCommand.Execute(null);
        Assert.True(vm.IsEditingDebt);
        Assert.Equal("Edit debt", vm.DebtFormTitle);
        Assert.Equal(12_000m, vm.NewDebtBalance);
        Assert.Equal(500m, vm.NewDebtPayment);
        vm.NewDebtBalance = 11_000;
        vm.NewDebtPayment = 550;
        vm.NewDebtName = "Car loan (bank)";
        await vm.AddDebtCommand.ExecuteAsync(null);

        Assert.False(vm.IsDebtPanelOpen);
        Assert.False(vm.IsEditingDebt);
        var debt = (await _repository.GetDebtsAsync()).Single();
        Assert.Equal(("Car loan (bank)", 11_000m, 550m, Today), (debt.Name, debt.Balance, debt.MonthlyPayment, debt.StartDate));
        Assert.Equal(11_000m, vm.Debts.Single().Outstanding);
        Assert.Equal(20, vm.Debts.Single().Plan!.Entries.Count);
    }

    [Fact]
    public async Task BorrowingMoreGrowsTheDebtAndTheCash()
    {
        var vm = await LoadAsync();
        await AddDebtAsync(vm, "Loan from Lukas", 1000, 100);
        var cash = vm.Summary.CurrentBalance;
        var item = vm.Debts.Single();

        item.BorrowAmount = 500;
        await item.BorrowMoreCommand.ExecuteAsync(null);

        Assert.Equal(1500m, item.Outstanding);
        Assert.Equal(15, item.Plan!.PaymentsAfter(Today));
        Assert.Equal(cash + 500, vm.Summary.CurrentBalance);
        // Borrowed money isn't earnings.
        Assert.Equal(0m, vm.Report.TotalIncome);

        // A borrowing recorded by mistake can be removed in the debt form.
        item.EditCommand.Execute(null);
        var row = Assert.Single(vm.DebtPaymentRows);
        Assert.StartsWith("Borrowed", row.Text);
        await vm.RemoveDebtPaymentCommand.ExecuteAsync(row);
        Assert.Equal(1000m, vm.Debts.Single().Outstanding);
        Assert.Empty((await _repository.GetDebtsAsync()).Single().ExtraPayments);
    }

    [Fact]
    public void BorrowingAfterPayoffStartsPaymentsAgain()
    {
        var start = new DateOnly(2026, 1, 1);
        var debt = new Debt
        {
            Balance = 200, StartDate = start, MonthlyPayment = 100, FirstPaymentDate = start.AddMonths(1),
            ExtraPayments = [new DebtPayment { Date = start.AddMonths(5).AddDays(14), Amount = -300 }],
        };

        var plan = DebtPlan.Build(debt);

        Assert.True(plan.PaysOff);
        Assert.Equal(500m, plan.Entries.Where(e => e.Payment > 0).Sum(e => e.Payment));
        Assert.Equal(new DateOnly(2026, 9, 1), plan.PayoffDate);
        Assert.Contains(plan.ToTransactions(), t => t.IsBorrowing && t.Amount == 300);
    }

    // ── Undo ──

    [Fact]
    public async Task ADeletedEntryComesBackWithUndo()
    {
        var salary = Salary(Today.AddMonths(-2));
        salary.Changes = [new EntryChange { Date = salary.Date.AddMonths(1), Amount = 7000, Note = "Bonus" }];
        await _repository.AddTransactionAsync(salary);
        var vm = await LoadAsync();
        var balance = vm.Summary.CurrentBalance;

        await vm.DeleteTransactionCommand.ExecuteAsync(vm.Transactions.Single());
        Assert.Empty(vm.Transactions);
        Assert.Empty(await _repository.GetTransactionsAsync());
        Assert.True(vm.HasNotice);
        Assert.True(vm.CanUndo);
        Assert.Equal("Deleted “Salary”", vm.NoticeText);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.False(vm.HasNotice);
        var restored = Assert.Single(await _repository.GetTransactionsAsync());
        Assert.Equal(salary.Id, restored.Id);
        Assert.Equal("Bonus", restored.Changes.Single().Note);
        Assert.Equal(balance, vm.Summary.CurrentBalance);
    }

    [Fact]
    public async Task ADeletedDebtComesBackWithItsPayments()
    {
        var vm = await LoadAsync();
        await AddDebtAsync(vm, "Car loan", 5000, 250);
        await AddDebtAsync(vm, "Laptop", 1200, 100);
        var car = vm.Debts[0];
        car.ExtraAmount = 1000;
        await car.RecordExtraPaymentCommand.ExecuteAsync(null);

        await car.DeleteCommand.ExecuteAsync(null);
        Assert.Single(await _repository.GetDebtsAsync());

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal(["Car loan", "Laptop"], vm.Debts.Select(d => d.Name));
        Assert.Equal(4000m, vm.Debts[0].Outstanding);
        Assert.Single((await _repository.GetDebtsAsync()).Single(d => d.Name == "Car loan").ExtraPayments);
    }

    [Fact]
    public async Task TheNoticeGoesAwayByItself()
    {
        var duration = MainViewModel.NoticeDuration;
        MainViewModel.NoticeDuration = TimeSpan.FromMilliseconds(50);
        try
        {
            var vm = await LoadAsync();
            vm.ShowNotice("Saved", () => Task.CompletedTask);
            for (var i = 0; i < 100 && vm.HasNotice; i++)
                await Task.Delay(20);
            Assert.False(vm.HasNotice);
            Assert.False(vm.CanUndo);
        }
        finally
        {
            MainViewModel.NoticeDuration = duration;
        }
    }

    // ── Export ──

    [Fact]
    public void CsvFieldsAreQuotedWhenNeeded()
    {
        Assert.Equal("Rent", Csv.Field("Rent"));
        Assert.Equal("\"Migros; Coop\"", Csv.Field("Migros; Coop"));
        Assert.Equal("\"The \"\"best\"\" pizza\"", Csv.Field("The \"best\" pizza"));
        Assert.Equal("1234.50", Csv.Amount(1234.5m));
        Assert.Equal("2026-01-03", Csv.Date(new DateOnly(2026, 1, 3)));
    }

    [Fact]
    public async Task EntriesAndTheYearExportAsCsvAndPdf()
    {
        var start = new DateOnly(Today.Year, 1, 25);
        var salary = Salary(start);
        salary.Changes = [new EntryChange { Date = new DateOnly(Today.Year, 12, 25), Amount = 10_000 }];
        await _repository.AddTransactionAsync(salary);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(Today.Year, 3, 4), Description = "Migros; weekly shop", Category = "Groceries",
            Amount = 123.45m, Type = TransactionType.Expense,
        });
        var vm = await LoadAsync();
        vm.CurrentYearCommand.Execute(null);

        var entries = vm.BuildEntriesCsv().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date;Description;Category;Type;Amount;Repeats;Ends on;Person;Account;To account", entries[0]);
        Assert.Contains(entries, l => l.StartsWith($"{Today.Year}-03-04;\"Migros; weekly shop\";Groceries;Expense;-123.45;One-time;"));
        Assert.Equal(3, entries.Length);

        var year = vm.BuildYearCsv().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 12 + 1, year.Length); // header, 12 salaries, 1 shop
        Assert.Contains(year, l => l.StartsWith($"{Today.Year}-12-25;Salary;Salary;Income;10000.00;"));

        var csvPath = Path.Combine(_folder, "year.csv");
        await vm.ExportYearCsvAsync(csvPath);
        var bytes = await File.ReadAllBytesAsync(csvPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // so Excel reads umlauts
        Assert.Equal("Saved year.csv to " + Path.GetFileName(_folder), vm.NoticeText);

        var summary = vm.BuildYearSummary();
        Assert.Equal(12, summary.Months.Count);
        Assert.Equal(Format.Money(65_000m), summary.Total.Income);

        var pdfPath = Path.Combine(_folder, "year.pdf");
        await vm.ExportYearPdfAsync(pdfPath);
        var pdf = await File.ReadAllBytesAsync(pdfPath);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 1000);
    }

    [Fact]
    public async Task AFailedExportSaysWhy()
    {
        var vm = await LoadAsync();
        await vm.ExportEntriesCsvAsync(Path.Combine(_folder, "missing", "entries.csv"));
        Assert.StartsWith("Could not export:", vm.NoticeText);
    }
}
