using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Keystone.Models;

namespace Keystone.Data;

/// <summary>Data access for the app. Each call uses its own short-lived context.</summary>
public sealed class KeystoneRepository(string databasePath)
{
    /// <summary>Id of the migration matching the schema that EnsureCreated produced in early builds.</summary>
    private const string InitialMigrationId = "20261009154851_InitialCreate";

    private KeystoneDbContext CreateContext() => new(databasePath);

    /// <summary>Creates the database if needed and applies pending schema migrations.</summary>
    public void Migrate()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var db = CreateContext();
        BaselineLegacyDatabase(db);
        db.Database.Migrate();
    }

    /// <summary>
    /// Databases created before migrations were introduced already have the initial tables
    /// but no migration history. Record the initial migration so it isn't applied twice.
    /// </summary>
    private static void BaselineLegacyDatabase(KeystoneDbContext db)
    {
        if (!TableExists(db, "Transactions") || TableExists(db, "__EFMigrationsHistory"))
            return;

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSql(
            $"""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({InitialMigrationId}, {ProductInfo.GetVersion()})""");
    }

    private static bool TableExists(KeystoneDbContext db, string table) =>
        db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {table}")
            .AsEnumerable()
            .Single() > 0;

    public async Task<List<Transaction>> GetTransactionsAsync()
    {
        await using var db = CreateContext();
        return await db.Transactions
            .AsNoTracking()
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .ToListAsync();
    }

    public async Task<Transaction> AddTransactionAsync(Transaction transaction)
    {
        await using var db = CreateContext();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }

    /// <summary>Saves every field of an existing entry.</summary>
    public async Task UpdateTransactionAsync(Transaction transaction)
    {
        await using var db = CreateContext();
        db.Transactions.Update(transaction);
        await db.SaveChangesAsync();
    }

    public async Task DeleteTransactionAsync(int id)
    {
        await using var db = CreateContext();
        await db.Transactions.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    public async Task SetEndDateAsync(int id, DateOnly? endDate)
    {
        await using var db = CreateContext();
        await db.Transactions
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.EndDate, endDate));
    }

    public async Task<List<Person>> GetPeopleAsync()
    {
        await using var db = CreateContext();
        return await db.People.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Person> AddPersonAsync(Person person)
    {
        await using var db = CreateContext();
        db.People.Add(person);
        await db.SaveChangesAsync();
        return person;
    }

    /// <summary>Deletes the person. Their entries stay, as shared household entries.</summary>
    public async Task DeletePersonAsync(int id)
    {
        await using var db = CreateContext();
        await db.People.Where(p => p.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Newest first.</summary>
    public async Task<List<HouseholdDocument>> GetDocumentsAsync()
    {
        await using var db = CreateContext();
        var documents = await db.Documents.AsNoTracking().ToListAsync();
        return documents.OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).ToList();
    }

    public async Task<HouseholdDocument> AddDocumentAsync(HouseholdDocument document)
    {
        await using var db = CreateContext();
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    public async Task UpdateDocumentAsync(HouseholdDocument document)
    {
        await using var db = CreateContext();
        db.Documents.Update(document);
        await db.SaveChangesAsync();
    }

    public async Task DeleteDocumentAsync(int id)
    {
        await using var db = CreateContext();
        await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync();
    }

    public async Task<List<Note>> GetNotesAsync()
    {
        await using var db = CreateContext();
        return await db.Notes.AsNoTracking().ToListAsync();
    }

    public async Task<Note> AddNoteAsync(Note note)
    {
        await using var db = CreateContext();
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        return note;
    }

    public async Task UpdateNoteAsync(Note note)
    {
        await using var db = CreateContext();
        db.Notes.Update(note);
        await db.SaveChangesAsync();
    }

    public async Task DeleteNoteAsync(int id)
    {
        await using var db = CreateContext();
        await db.Notes.Where(n => n.Id == id).ExecuteDeleteAsync();
    }

    public async Task<List<TaskItem>> GetTasksAsync()
    {
        await using var db = CreateContext();
        return await db.Tasks.AsNoTracking().ToListAsync();
    }

    public async Task<TaskItem> AddTaskAsync(TaskItem task)
    {
        await using var db = CreateContext();
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    public async Task UpdateTaskAsync(TaskItem task)
    {
        await using var db = CreateContext();
        db.Tasks.Update(task);
        await db.SaveChangesAsync();
    }

    public async Task DeleteTaskAsync(int id)
    {
        await using var db = CreateContext();
        await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Accounts with their statements, oldest statement first.</summary>
    public async Task<List<PensionAccount>> GetPensionAccountsAsync()
    {
        await using var db = CreateContext();
        var accounts = await db.PensionAccounts.AsNoTracking().Include(a => a.Statements).ToListAsync();
        foreach (var account in accounts)
            account.Statements = account.Statements.OrderBy(s => s.Date).ThenBy(s => s.Id).ToList();
        return accounts;
    }

    /// <summary>Saves the account's own fields; statements are saved separately.</summary>
    public async Task<PensionAccount> SavePensionAccountAsync(PensionAccount account)
    {
        await using var db = CreateContext();
        var row = new PensionAccount
        {
            Id = account.Id, Name = account.Name, Pillar = account.Pillar, PersonId = account.PersonId, Notes = account.Notes,
        };
        if (row.Id == 0)
            db.PensionAccounts.Add(row);
        else
            db.PensionAccounts.Update(row);
        await db.SaveChangesAsync();
        account.Id = row.Id;
        return account;
    }

    public async Task DeletePensionAccountAsync(int id)
    {
        await using var db = CreateContext();
        await db.PensionStatements.Where(s => s.AccountId == id).ExecuteDeleteAsync();
        await db.PensionAccounts.Where(a => a.Id == id).ExecuteDeleteAsync();
    }

    public async Task<PensionStatement> SavePensionStatementAsync(PensionStatement statement)
    {
        await using var db = CreateContext();
        if (statement.Id == 0)
            db.PensionStatements.Add(statement);
        else
            db.PensionStatements.Update(statement);
        await db.SaveChangesAsync();
        return statement;
    }

    public async Task DeletePensionStatementAsync(int id)
    {
        await using var db = CreateContext();
        await db.PensionStatements.Where(s => s.Id == id).ExecuteDeleteAsync();
    }

    public Task SetLanguageAsync(string language) => UpdateSettingsAsync(s => s.Language = language);

    public Task SetDocumentsFolderAsync(string folder) => UpdateSettingsAsync(s => s.DocumentsFolder = folder);

    public async Task<List<Debt>> GetDebtsAsync()
    {
        await using var db = CreateContext();
        return await db.Debts
            .AsNoTracking()
            .Include(d => d.ExtraPayments)
            .OrderBy(d => d.Id)
            .ToListAsync();
    }

    public async Task<Debt> AddDebtAsync(Debt debt)
    {
        await using var db = CreateContext();
        db.Debts.Add(debt);
        await db.SaveChangesAsync();
        return debt;
    }

    /// <summary>Deletes the debt and its extra payments.</summary>
    public async Task DeleteDebtAsync(int id)
    {
        await using var db = CreateContext();
        await db.Debts.Where(d => d.Id == id).ExecuteDeleteAsync();
    }

    public async Task<DebtPayment> AddDebtPaymentAsync(DebtPayment payment)
    {
        await using var db = CreateContext();
        db.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        await using var db = CreateContext();
        return await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == AppSettings.SingletonId)
            ?? new AppSettings();
    }

    public Task SetStartingBalanceAsync(decimal startingBalance) =>
        UpdateSettingsAsync(s => s.StartingBalance = startingBalance);

    public Task SetFinancialYearStartMonthAsync(int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        return UpdateSettingsAsync(s => s.FinancialYearStartMonth = month);
    }

    public Task SetAiImportEnabledAsync(bool enabled) => UpdateSettingsAsync(s => s.AiImportEnabled = enabled);

    public Task SetAiSettingsAsync(string endpoint, string model) =>
        UpdateSettingsAsync(s =>
        {
            s.AiEndpoint = endpoint;
            s.AiModel = model;
        });

    private async Task UpdateSettingsAsync(Action<AppSettings> update)
    {
        await using var db = CreateContext();
        var settings = await db.Settings.FindAsync(AppSettings.SingletonId);
        if (settings is null)
        {
            settings = new AppSettings();
            db.Settings.Add(settings);
        }

        update(settings);
        await db.SaveChangesAsync();
    }
}
