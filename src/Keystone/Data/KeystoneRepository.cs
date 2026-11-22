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

    /// <summary>The database file, e.g. for backups.</summary>
    public string DatabasePath => databasePath;

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
            .Include(t => t.Changes)
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

    /// <summary>Links documents to an entry again, e.g. when its deletion is undone.</summary>
    public async Task LinkDocumentsAsync(IReadOnlyCollection<int> documentIds, int transactionId)
    {
        await using var db = CreateContext();
        await db.Documents
            .Where(d => documentIds.Contains(d.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.TransactionId, transactionId));
    }

    /// <summary>Keeps only the given single-time changes of an entry, e.g. after its dates were edited.</summary>
    public async Task KeepEntryChangesAsync(int transactionId, IReadOnlyCollection<int> keepIds)
    {
        await using var db = CreateContext();
        await db.Set<EntryChange>()
            .Where(c => c.TransactionId == transactionId && !keepIds.Contains(c.Id))
            .ExecuteDeleteAsync();
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

    public async Task<List<InsurancePolicy>> GetInsurancePoliciesAsync()
    {
        await using var db = CreateContext();
        return await db.InsurancePolicies.AsNoTracking().ToListAsync();
    }

    public async Task<InsurancePolicy> SaveInsurancePolicyAsync(InsurancePolicy policy)
    {
        await using var db = CreateContext();
        if (policy.Id == 0)
            db.InsurancePolicies.Add(policy);
        else
            db.InsurancePolicies.Update(policy);
        await db.SaveChangesAsync();
        return policy;
    }

    public async Task DeleteInsurancePolicyAsync(int id)
    {
        await using var db = CreateContext();
        await db.InsurancePolicies.Where(p => p.Id == id).ExecuteDeleteAsync();
    }

    public async Task<List<BankAccount>> GetBankAccountsAsync()
    {
        await using var db = CreateContext();
        return await db.BankAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    public async Task<BankAccount> SaveBankAccountAsync(BankAccount account)
    {
        await using var db = CreateContext();
        if (account.Id == 0)
            db.BankAccounts.Add(account);
        else
            db.BankAccounts.Update(account);
        await db.SaveChangesAsync();
        return account;
    }

    public async Task DeleteBankAccountAsync(int id)
    {
        await using var db = CreateContext();
        await db.BankAccounts.Where(a => a.Id == id).ExecuteDeleteAsync();
    }

    // ── Simple records: contracts, important dates, warranties, savings goals ──

    public async Task<List<T>> GetAllAsync<T>() where T : class
    {
        await using var db = CreateContext();
        return await db.Set<T>().AsNoTracking().ToListAsync();
    }

    /// <summary>Adds a new record or saves every field of an existing one.</summary>
    public async Task<T> SaveAsync<T>(T entity, bool isNew) where T : class
    {
        await using var db = CreateContext();
        if (isNew)
            db.Set<T>().Add(entity);
        else
            db.Set<T>().Update(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> match) where T : class
    {
        await using var db = CreateContext();
        await db.Set<T>().Where(match).ExecuteDeleteAsync();
    }

    public async Task<Dictionary<int, string>> GetDocumentTextsAsync()
    {
        await using var db = CreateContext();
        return await db.DocumentTexts.AsNoTracking().ToDictionaryAsync(t => t.DocumentId, t => t.Text);
    }

    public async Task SetDocumentHashAsync(int documentId, string hash)
    {
        await using var db = CreateContext();
        await db.Documents.Where(d => d.Id == documentId).ExecuteUpdateAsync(s => s.SetProperty(d => d.FileHash, hash));
    }

    public async Task SaveDocumentTextAsync(int documentId, string text)
    {
        await using var db = CreateContext();
        var row = await db.DocumentTexts.FindAsync(documentId);
        if (row is null)
            db.DocumentTexts.Add(new DocumentText { DocumentId = documentId, Text = text });
        else
            row.Text = text;
        await db.SaveChangesAsync();
    }

    public async Task UpdatePersonAsync(Person person)
    {
        await using var db = CreateContext();
        db.People.Update(person);
        await db.SaveChangesAsync();
    }

    public Task SetScenarioAsync(string json) => UpdateSettingsAsync(s => s.ScenarioJson = json);

    public Task SetCalendarSyncAsync(bool enabled, string calendarId) =>
        UpdateSettingsAsync(s => (s.CalendarSyncEnabled, s.CalendarId) = (enabled, calendarId));

    /// <summary>Saves what a calendar sync changed, in one go.</summary>
    public async Task SaveAppointmentsAsync(IEnumerable<Appointment> added, IEnumerable<Appointment> changed, IEnumerable<int> removedIds)
    {
        await using var db = CreateContext();
        db.Appointments.AddRange(added);
        db.Appointments.UpdateRange(changed.Where(a => a.Id != 0));
        var removed = removedIds.ToList();
        if (removed.Count > 0)
            await db.Appointments.Where(a => removed.Contains(a.Id)).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
    }

    public Task SetRetirementNeedAsync(int percent) => UpdateSettingsAsync(s => s.RetirementNeedPercent = percent);

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

    /// <summary>Saves a debt's terms; its extra payments are saved separately.</summary>
    public async Task UpdateDebtAsync(Debt debt)
    {
        await using var db = CreateContext();
        await db.Debts.Where(d => d.Id == debt.Id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Name, debt.Name)
            .SetProperty(d => d.Balance, debt.Balance)
            .SetProperty(d => d.AnnualInterestRate, debt.AnnualInterestRate)
            .SetProperty(d => d.MonthlyPayment, debt.MonthlyPayment)
            .SetProperty(d => d.FirstPaymentDate, debt.FirstPaymentDate)
            .SetProperty(d => d.IsInterestOnly, debt.IsInterestOnly));
    }

    public async Task DeleteDebtPaymentAsync(int id)
    {
        await using var db = CreateContext();
        await db.Set<DebtPayment>().Where(p => p.Id == id).ExecuteDeleteAsync();
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

    public Task SetLockAsync(bool enabled, int afterMinutes) =>
        UpdateSettingsAsync(s => (s.LockEnabled, s.LockAfterMinutes) = (enabled, afterMinutes));

    public Task SetRemindersAsync(bool enabled, int hour) =>
        UpdateSettingsAsync(s => (s.RemindersEnabled, s.ReminderHour) = (enabled, hour));

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
