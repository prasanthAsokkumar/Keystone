using Keystone.Data;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Insurance, reminders, taxes, search, the app lock and backups.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class HouseholdFeaturesTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-features-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public HouseholdFeaturesTests()
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

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        return vm;
    }

    // ── Insurance ──

    [Fact]
    public void BasicHealthInsuranceMustBeCancelledBy30November()
    {
        var policy = new InsurancePolicy
        {
            Kind = InsuranceKind.HealthBasic, ContractEnd = new DateOnly(2026, 12, 31), NoticeMonths = 1,
        };
        Assert.Equal((new DateOnly(2026, 12, 31), new DateOnly(2026, 11, 30)), policy.NextDeadline(new DateOnly(2026, 10, 1)));
        // Once the deadline has passed, the next chance is a year later.
        Assert.Equal((new DateOnly(2027, 12, 31), new DateOnly(2027, 11, 30)), policy.NextDeadline(new DateOnly(2026, 12, 2)));
    }

    [Fact]
    public void ThreeMonthsNoticeEndsTheMonthBefore()
    {
        var policy = new InsurancePolicy { ContractEnd = new DateOnly(2027, 3, 31), NoticeMonths = 3 };
        Assert.Equal(new DateOnly(2026, 12, 31), policy.NextDeadline(new DateOnly(2026, 11, 21))!.Value.CancelBy);
    }

    [Fact]
    public void APolicyThatDoesNotRenewEnds()
    {
        var policy = new InsurancePolicy { ContractEnd = new DateOnly(2026, 6, 30), RenewsAutomatically = false };
        Assert.True(policy.HasEnded(new DateOnly(2026, 7, 1)));
        Assert.Null(policy.NextDeadline(new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public async Task SavingAPolicyCanAddItsPremiumToExpenses()
    {
        var vm = await LoadAsync();
        vm.NewInsuranceCommand.Execute(null);
        vm.PolicyInsurer = "Helsana";
        vm.PolicyKindIndex = (int)InsuranceKind.HealthBasic;
        Assert.Equal(1m, vm.PolicyNoticeMonths); // the usual notice for basic insurance
        vm.PolicyPremium = 412.35m;
        vm.PolicyRecurrenceIndex = 0; // monthly
        vm.PolicyAddToExpenses = true;
        await vm.SaveInsuranceCommand.ExecuteAsync(null);

        Assert.Null(vm.PolicyError);
        var row = Assert.Single(vm.InsuranceRows);
        Assert.Equal(412.35m * 12, row.Policy.YearlyPremium);
        Assert.True(row.HasCancelBy);
        var premium = Assert.Single(vm.Transactions);
        Assert.Equal(Recurrence.Monthly, premium.Recurrence);
        Assert.Equal("Health insurance", premium.Category);
        Assert.Single(await _repository.GetInsurancePoliciesAsync());
    }

    // ── Reminders ──

    [Fact]
    public void PlansRemindersAtTheChosenHour()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0);
        var reminders = ReminderPlanner.Plan(
            [new BillReminder(1, "Premium", "Helsana", 412.35m, new DateOnly(2026, 12, 1))],
            [new TaskReminder(2, "Renew passport", new DateOnly(2026, 11, 25)), new TaskReminder(3, "Old", new DateOnly(2026, 11, 1))],
            [new DeadlineReminder(4, "Mobiliar", "Car", new DateOnly(2026, 12, 31))],
            [new Pillar3aReminder("Anna", 1_258m, 2026), new Pillar3aReminder("Lukas", 0m, 2026)],
            now, hour: 8);

        Assert.Equal(new DateTime(2026, 11, 25, 8, 0, 0), reminders[0].At); // the task
        Assert.Contains(reminders, r => r.Id == "bill-1-before" && r.At == new DateTime(2026, 11, 30, 8, 0, 0));
        Assert.Contains(reminders, r => r.Id == "bill-1-due");
        Assert.Contains(reminders, r => r.At == new DateTime(2026, 12, 1, 8, 0, 0) && r.Id.StartsWith("insurance-4"));
        Assert.Contains(reminders, r => r.At == new DateTime(2026, 12, 24, 8, 0, 0) && r.Id.StartsWith("insurance-4"));
        Assert.Equal(2, reminders.Count(r => r.Id.StartsWith("pillar3a-2026-Anna")));
        Assert.DoesNotContain(reminders, r => r.Id.Contains("Lukas")); // nothing left to pay in
        Assert.DoesNotContain(reminders, r => r.Id == "task-3"); // in the past
        Assert.Equal(reminders.OrderBy(r => r.At), reminders);
    }

    // ── Taxes ──

    [Fact]
    public async Task GathersTheTaxYear()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        // With a pension fund, the lower 3a limit applies.
        await _repository.SavePensionAccountAsync(new PensionAccount { Name = "PK", Pillar = PensionPillar.PensionFund, PersonId = anna.Id });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(Today.Year - 1, 3, 1), Description = "Shared groceries", Category = "Groceries", Amount = 100,
            Type = TransactionType.Expense,
        });
        var year = Today.Year - 1;
        async Task Add(string category, decimal amount, TransactionType type, Recurrence recurrence = Recurrence.OneTime) =>
            await _repository.AddTransactionAsync(new Transaction
            {
                Date = new DateOnly(year, 1, 25), Description = category, Category = category, Amount = amount,
                Type = type, Recurrence = recurrence, EndDate = new DateOnly(year, 12, 31), PersonId = anna.Id,
            });
        await Add("Salary", 4_000, TransactionType.Income, Recurrence.Monthly);
        await Add(Categories.Pillar3a, 700, TransactionType.Expense, Recurrence.Monthly); // 8,400: over the limit
        await Add(Categories.Childcare, 980, TransactionType.Expense, Recurrence.Monthly);
        await Add(Categories.Donations, 250, TransactionType.Expense);
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = $"Lohnausweis {year}", Kind = DocumentKind.Payslip, PersonId = anna.Id, Date = new DateOnly(year + 1, 1, 20),
            RelativePath = "x.pdf",
        });

        var vm = await LoadAsync();
        vm.TaxYear = year;

        var income = vm.TaxSections[0];
        Assert.Equal("Salary · Anna", Assert.Single(income.Lines).Label);
        Assert.Equal(Format.Money(48_000), income.Lines[0].AmountText);

        var deductions = vm.TaxSections[1].Lines;
        var pillar3a = deductions.Single(l => l.Label == "Pillar 3a · Anna");
        Assert.Equal(Format.Money(SwissPension.Pillar3aLimit(year, true)), pillar3a.AmountText);
        Assert.True(pillar3a.IsWarning);
        Assert.Equal(Format.Money(11_760), deductions.Single(l => l.Label == "Childcare costs").AmountText);
        Assert.Contains(deductions, l => l.Label == "Donations");

        var certificate = vm.TaxChecklist.Single(i => i.Label == "Salary certificate · Anna");
        Assert.True(certificate.IsFound);
        Assert.Contains("[x] Salary certificate · Anna", vm.TaxSummaryText());
    }

    // ── Search ──

    [Fact]
    public async Task SearchFindsEverythingAndOpensIt()
    {
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today, Description = "Helsana premium", Amount = 412, Type = TransactionType.Expense,
        });
        await _repository.AddNoteAsync(new Note { Title = "Helsana or CSS?", Body = "Compare", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = "Helsana", Kind = InsuranceKind.HealthBasic });
        var vm = await LoadAsync();

        vm.GlobalSearch = "h";
        Assert.Empty(vm.SearchResults); // too short

        vm.GlobalSearch = "helsana";
        Assert.Equal(["Entry", "Note", "Insurance"], vm.SearchResults.Select(r => r.Kind));

        vm.OpenSearchResultCommand.Execute(vm.SearchResults.Single(r => r.Kind == "Note"));
        Assert.Equal(AppPage.Notes, vm.CurrentPage);
        Assert.Equal("Helsana or CSS?", vm.SelectedNoteItem?.Title);
        Assert.Equal("", vm.GlobalSearch);
    }

    // ── App lock ──

    [Fact]
    public async Task LockNeedsTheUserToTurnOnAndToUnlock()
    {
        var vm = await LoadAsync();
        var answer = false;
        vm.Authenticate = _ => Task.FromResult(answer);
        if (!vm.CanLock)
            return; // no Touch ID or password check on this machine

        vm.IsLockEnabled = true; // the check fails, so it stays off
        await Task.Delay(50);
        Assert.False(vm.IsLockEnabled);

        answer = true;
        vm.IsLockEnabled = true;
        await Task.Delay(50);
        Assert.True(vm.IsLockEnabled);
        Assert.True((await _repository.GetSettingsAsync()).LockEnabled);

        vm.LockDelayIndex = 2; // 5 minutes
        var left = DateTime.Now;
        vm.OnLeftApp(left);
        vm.OnReturnedToApp(left.AddMinutes(2));
        Assert.False(vm.IsLocked);

        answer = false;
        vm.OnLeftApp(left);
        vm.OnReturnedToApp(left.AddMinutes(6));
        Assert.True(vm.IsLocked);

        answer = true;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.False(vm.IsLocked);
    }

    /// <summary>Turns the lock on with "as soon as I switch away", the setting the Touch ID prompt used to upset.</summary>
    private async Task<MainViewModel> LockedImmediatelyAsync(Func<MainViewModel, Task<bool>> touchId)
    {
        var vm = await LoadAsync();
        vm.Authenticate = _ => touchId(vm);
        vm.IsLockEnabled = true;
        await Task.Delay(50);
        vm.LockDelayIndex = 0; // as soon as I switch away
        vm.LockNowCommand.Execute(null);
        return vm;
    }

    [Fact]
    public async Task TheTouchIdPromptDoesNotLockAgain()
    {
        var prompts = 0;
        // Like macOS: the prompt takes focus away while it is shown, and gives it back afterwards.
        var vm = await LockedImmediatelyAsync(model =>
        {
            prompts++;
            model.OnLeftApp(DateTime.Now);
            return Task.FromResult(true);
        });
        if (!vm.CanLock)
            return;
        prompts = 0;

        await vm.UnlockCommand.ExecuteAsync(null);
        vm.OnReturnedToApp(DateTime.Now);

        Assert.False(vm.IsLocked);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task ACancelledPromptWaitsForTheUser()
    {
        var prompts = 0;
        var vm = await LockedImmediatelyAsync(model =>
        {
            prompts++;
            model.OnLeftApp(DateTime.Now);
            return Task.FromResult(prompts == 1); // turning the lock on succeeds, unlocking is cancelled
        });
        if (!vm.CanLock)
            return;

        await vm.UnlockCommand.ExecuteAsync(null);
        vm.OnReturnedToApp(DateTime.Now); // back from the cancelled prompt
        await Task.Delay(50);

        Assert.True(vm.IsLocked);
        Assert.Equal(2, prompts); // no new prompt until the user clicks Unlock
        Assert.Equal("Not unlocked. Try again.", vm.LockMessage);
    }

    [Fact]
    public async Task FilePickersDoNotLock()
    {
        var vm = await LockedImmediatelyAsync(_ => Task.FromResult(true));
        if (!vm.CanLock)
            return;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.False(vm.IsLocked);

        using (vm.OwnWindow())
            vm.OnLeftApp(DateTime.Now); // the picker takes focus
        vm.OnReturnedToApp(DateTime.Now);
        Assert.False(vm.IsLocked);

        // Really switching to another app still locks right away.
        vm.OnLeftApp(DateTime.Now);
        Assert.True(vm.IsLocked);
        Assert.False(vm.CanAcceptDrop);
    }

    // ── Backups ──

    [Fact]
    public void KeepsTheLatestThirtyBackups()
    {
        var backups = Path.Combine(_folder, "Backups");
        var start = new DateTime(2026, 10, 1, 9, 0, 0);
        for (var i = 0; i < 32; i++)
            DatabaseBackup.Create(_repository.DatabasePath, backups, start.AddDays(i));

        var list = DatabaseBackup.List(backups);
        Assert.Equal(DatabaseBackup.Keep, list.Count);
        Assert.Equal(start.AddDays(31), list[0].CreatedAt);
        Assert.False(DatabaseBackup.IsDue(backups, start.AddDays(31).AddHours(5)));
        Assert.True(DatabaseBackup.IsDue(backups, start.AddDays(32).AddHours(1)));
        Assert.True(DatabaseBackup.IsKeystoneDatabase(list[0].Path));
    }

    [Fact]
    public async Task RestoringABackupBringsTheDataBackAndKeepsTheCurrentState()
    {
        await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var vm = await LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents")); // its own backups folder
        await vm.BackUpNowCommand.ExecuteAsync(null);
        var backup = DatabaseBackup.List(vm.BackupFolder).First();

        await _repository.AddPersonAsync(new Person { Name = "Lukas" });
        vm.ChooseBackupToRestore(backup.Path);
        Assert.True(vm.HasRestoreCandidate);
        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Null(vm.BackupError);
        Assert.Equal(["Anna"], vm.People.Select(p => p.Name));
        // The state before restoring was backed up too.
        Assert.True(DatabaseBackup.List(vm.BackupFolder).Count >= 2);
    }

    [Fact]
    public void RefusesFilesThatAreNotBackups()
    {
        var path = Path.Combine(_folder, "not-a-backup.db");
        File.WriteAllText(path, "hello");
        Assert.False(DatabaseBackup.IsKeystoneDatabase(path));
    }
}
