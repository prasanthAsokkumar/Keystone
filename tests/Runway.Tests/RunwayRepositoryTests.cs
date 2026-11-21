using Runway.Data;
using Runway.Models;

namespace Runway.Tests;

public sealed class RunwayRepositoryTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"runway-test-{Guid.NewGuid():N}.db");
    private readonly RunwayRepository _repository;

    public RunwayRepositoryTests()
    {
        _repository = new RunwayRepository(_databasePath);
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Transactions_RoundTripNewestFirst()
    {
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 9, 1), Description = "Salary", Category = "Work",
            Amount = 4_200.50m, Type = TransactionType.Income,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 10, 3), Description = "Rent", Amount = 1_500m, Type = TransactionType.Expense,
        });

        var transactions = await _repository.GetTransactionsAsync();

        Assert.Equal(["Rent", "Salary"], transactions.Select(t => t.Description));
        var salary = transactions[1];
        Assert.Equal(4_200.50m, salary.Amount);
        Assert.Equal(TransactionType.Income, salary.Type);
        Assert.Equal("Work", salary.Category);
        Assert.Equal(new DateOnly(2026, 9, 1), salary.Date);
    }

    [Fact]
    public async Task DeleteTransaction_RemovesIt()
    {
        var added = await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 10, 1), Description = "Coffee", Amount = 4m, Type = TransactionType.Expense,
        });

        await _repository.DeleteTransactionAsync(added.Id);

        Assert.Empty(await _repository.GetTransactionsAsync());
    }

    [Fact]
    public async Task StartingBalance_DefaultsToZeroAndCanBeUpdated()
    {
        Assert.Equal(0m, (await _repository.GetSettingsAsync()).StartingBalance);

        await _repository.SetStartingBalanceAsync(25_000m);
        await _repository.SetStartingBalanceAsync(30_000m);

        Assert.Equal(30_000m, (await _repository.GetSettingsAsync()).StartingBalance);
    }

    [Fact]
    public async Task Monthly_RoundTripsAndCanBeStopped()
    {
        var added = await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 1, 1), Description = "Rent", Category = "Rent", Amount = 1_500m,
            Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });

        await _repository.SetEndDateAsync(added.Id, new DateOnly(2026, 10, 9));

        var stored = Assert.Single(await _repository.GetTransactionsAsync());
        Assert.Equal(Recurrence.Monthly, stored.Recurrence);
        Assert.Equal(new DateOnly(2026, 10, 9), stored.EndDate);
    }

    [Fact]
    public async Task FinancialYearStartMonth_DefaultsToJanuaryAndCanBeUpdated()
    {
        Assert.Equal(1, (await _repository.GetSettingsAsync()).FinancialYearStartMonth);

        await _repository.SetFinancialYearStartMonthAsync(4);

        Assert.Equal(4, (await _repository.GetSettingsAsync()).FinancialYearStartMonth);
    }

    [Fact]
    public async Task Documents_RoundTripAndUnlinkWhenEntryOrPersonIsDeleted()
    {
        var person = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var entry = await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 12, 1), Description = "Helsana", Amount = 412.35m, Type = TransactionType.Expense,
        });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Premium", Kind = DocumentKind.Bill, Sender = "Helsana", PersonId = person.Id,
            Date = new DateOnly(2026, 11, 15), Amount = 412.35m, DueDate = new DateOnly(2026, 12, 1),
            RelativePath = "Anna/2026/Bills/x.pdf", TransactionId = entry.Id,
        });

        var saved = Assert.Single(await _repository.GetDocumentsAsync());
        Assert.Equal((DocumentKind.Bill, 412.35m, person.Id, entry.Id), (saved.Kind, saved.Amount!.Value, saved.PersonId, saved.TransactionId));

        await _repository.DeleteTransactionAsync(entry.Id);
        await _repository.DeletePersonAsync(person.Id);

        var kept = Assert.Single(await _repository.GetDocumentsAsync());
        Assert.Null(kept.TransactionId);
        Assert.Null(kept.PersonId);
    }

    [Fact]
    public async Task DocumentImport_IsOffByDefaultAndKeepsItsSettings()
    {
        var defaults = await _repository.GetSettingsAsync();
        Assert.False(defaults.AiImportEnabled);
        Assert.Equal(AppSettings.DefaultAiModel, defaults.AiModel);

        await _repository.SetAiImportEnabledAsync(true);
        await _repository.SetAiSettingsAsync("http://localhost:11500", "qwen2.5vl:32b");

        var saved = await _repository.GetSettingsAsync();
        Assert.True(saved.AiImportEnabled);
        Assert.Equal(("http://localhost:11500", "qwen2.5vl:32b"), (saved.AiEndpoint, saved.AiModel));
    }

    [Fact]
    public async Task Debts_RoundTripWithExtraPaymentsAndDeleteCascades()
    {
        var debt = await _repository.AddDebtAsync(new Debt
        {
            Name = "Car loan", Balance = 12_000m, StartDate = new DateOnly(2026, 10, 9), AnnualInterestRate = 3.9m,
            MonthlyPayment = 450m, FirstPaymentDate = new DateOnly(2026, 11, 1),
        });
        await _repository.AddDebtPaymentAsync(new DebtPayment { DebtId = debt.Id, Date = new DateOnly(2026, 10, 9), Amount = 1_000m });

        var stored = Assert.Single(await _repository.GetDebtsAsync());
        Assert.Equal(3.9m, stored.AnnualInterestRate);
        Assert.Equal(1_000m, Assert.Single(stored.ExtraPayments).Amount);

        await _repository.DeleteDebtAsync(debt.Id);

        Assert.Empty(await _repository.GetDebtsAsync());
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DebtPayments";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task UpdateTransaction_SavesAllFields()
    {
        var added = await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 10, 1), Description = "Gym", Amount = 60m, Type = TransactionType.Expense,
        });
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });

        await _repository.UpdateTransactionAsync(new Transaction
        {
            Id = added.Id, Date = new DateOnly(2026, 11, 1), Description = "Gym membership", Category = "Health",
            Amount = 75m, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
            EndDate = new DateOnly(2027, 6, 30), PersonId = anna.Id,
        });

        var saved = Assert.Single(await _repository.GetTransactionsAsync());
        Assert.Equal(("Gym membership", "Health", 75m), (saved.Description, saved.Category, saved.Amount));
        Assert.Equal((Recurrence.Monthly, new DateOnly(2027, 6, 30)), (saved.Recurrence, saved.EndDate));
        Assert.Equal(anna.Id, saved.PersonId);
    }

    [Fact]
    public async Task DeletePerson_KeepsTheirEntriesAsShared()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        await _repository.AddPersonAsync(new Person { Name = "Ben" });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = new DateOnly(2026, 10, 25), Description = "Salary", Amount = 5_000m,
            Type = TransactionType.Income, PersonId = anna.Id,
        });

        await _repository.DeletePersonAsync(anna.Id);

        Assert.Equal(["Ben"], (await _repository.GetPeopleAsync()).Select(p => p.Name));
        var salary = Assert.Single(await _repository.GetTransactionsAsync());
        Assert.Null(salary.PersonId);
    }
}


public sealed class LegacyDatabaseMigrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"runway-legacy-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task DatabaseCreatedBeforeMigrations_IsUpgradedAndKeepsData()
    {
        // Schema exactly as EnsureCreated produced it before migrations were introduced.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Settings" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY, "StartingBalance" TEXT NOT NULL);
                CREATE TABLE "Transactions" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY AUTOINCREMENT,
                    "Date" TEXT NOT NULL, "Description" TEXT NOT NULL, "Category" TEXT NOT NULL,
                    "Amount" TEXT NOT NULL, "Type" TEXT NOT NULL);
                CREATE INDEX "IX_Transactions_Date" ON "Transactions" ("Date");
                INSERT INTO "Settings" VALUES (1, '1234.5');
                INSERT INTO "Transactions" ("Date", "Description", "Category", "Amount", "Type")
                    VALUES ('2026-10-01', 'Rent', 'Housing', '1500.0', 'Expense');
                """;
            command.ExecuteNonQuery();
        }

        var repository = new RunwayRepository(_databasePath);
        repository.Migrate();
        repository.Migrate(); // idempotent

        var transaction = Assert.Single(await repository.GetTransactionsAsync());
        Assert.Equal("Rent", transaction.Description);
        Assert.Equal(Recurrence.OneTime, transaction.Recurrence);
        Assert.Null(transaction.EndDate);

        var settings = await repository.GetSettingsAsync();
        Assert.Equal(1234.5m, settings.StartingBalance);
        Assert.Equal(1, settings.FinancialYearStartMonth);
    }
}
