using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Keystone.Models;

namespace Keystone.Data;

public class KeystoneDbContext(string databasePath) : DbContext
{
    /// <summary>Set this environment variable to use a different database file.</summary>
    public const string DatabasePathVariable = "KEYSTONE_DB_PATH";

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<AppSettings> Settings => Set<AppSettings>();

    public DbSet<Debt> Debts => Set<Debt>();

    public DbSet<Person> People => Set<Person>();

    public DbSet<HouseholdDocument> Documents => Set<HouseholdDocument>();

    public DbSet<Note> Notes => Set<Note>();

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<PensionAccount> PensionAccounts => Set<PensionAccount>();

    public DbSet<PensionStatement> PensionStatements => Set<PensionStatement>();

    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();

    public DbSet<InsurancePolicy> InsurancePolicies => Set<InsurancePolicy>();

    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<ImportantDate> ImportantDates => Set<ImportantDate>();

    public DbSet<Warranty> Warranties => Set<Warranty>();

    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();

    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    /// <summary>
    /// ~/Library/Application Support/Keystone/keystone.db. A database from when the app was called Runway
    /// is moved there first.
    /// </summary>
    public static string DefaultDatabasePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(DatabasePathVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return ResolveDatabasePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>Debug builds (development) keep their own database so they never touch real data.</summary>
#if DEBUG
    public const string DatabaseFileName = "keystone-dev.db";
    private const string LegacyDatabaseFileName = "runway-dev.db";
#else
    public const string DatabaseFileName = "keystone.db";
    private const string LegacyDatabaseFileName = "runway.db";
#endif

    /// <summary>The app's folder from before it was renamed to Keystone.</summary>
    private const string LegacyFolderName = "Runway";

    /// <summary>
    /// The database in <paramref name="appDataFolder"/>/Keystone. If there is none yet but there is one from
    /// when the app was called Runway, that one is moved over, with its journal files. If it can't be moved,
    /// the old one keeps being used, so data is never split or lost.
    /// </summary>
    public static string ResolveDatabasePath(string appDataFolder)
    {
        var path = Path.Combine(appDataFolder, "Keystone", DatabaseFileName);
        var legacy = Path.Combine(appDataFolder, LegacyFolderName, LegacyDatabaseFileName);
        if (File.Exists(path) || !File.Exists(legacy))
            return path;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // SQLite keeps recent changes in -wal and -shm files next to the database; they belong together.
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(legacy + suffix))
                    File.Move(legacy + suffix, path + suffix);
            File.Move(legacy, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put back what was moved, then keep using the old database.
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(path + suffix) && !File.Exists(legacy + suffix))
                    File.Move(path + suffix, legacy + suffix);
            return legacy;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(legacy)!).Any())
                Directory.Delete(Path.GetDirectoryName(legacy)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind does no harm.
        }

        return path;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        optionsBuilder.UseSqlite(connectionString);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(t => t.Type).HasConversion<string>().HasMaxLength(16);
            entity.Property(t => t.Recurrence).HasConversion<string>().HasMaxLength(16);
            entity.Property(t => t.Description).HasMaxLength(200);
            entity.Property(t => t.Category).HasMaxLength(100);
            entity.HasIndex(t => t.Date);
            // Removing a person keeps their entries as shared ones.
            entity.HasOne<Person>().WithMany().HasForeignKey(t => t.PersonId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<BankAccount>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<BankAccount>().WithMany().HasForeignKey(t => t.ToAccountId).OnDelete(DeleteBehavior.SetNull);
            entity.Property(t => t.ExternalId).HasMaxLength(200);
            entity.HasIndex(t => t.ExternalId);
        });

        modelBuilder.Entity<Person>().Property(p => p.Name).HasMaxLength(100);

        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.AiEndpoint).HasMaxLength(200).HasDefaultValue(AppSettings.DefaultAiEndpoint);
            entity.Property(s => s.AiModel).HasMaxLength(100).HasDefaultValue(AppSettings.DefaultAiModel);
            entity.Property(s => s.DocumentsFolder).HasMaxLength(500);
            entity.Property(s => s.Language).HasMaxLength(8);
            entity.Property(s => s.RemindersEnabled).HasDefaultValue(true);
            entity.Property(s => s.ReminderHour).HasDefaultValue(8);
            entity.Property(s => s.LockAfterMinutes).HasDefaultValue(5);
            entity.Property(s => s.RetirementNeedPercent).HasDefaultValue(80);
            entity.Property(s => s.CalendarId).HasMaxLength(300);
        });

        modelBuilder.Entity<HouseholdDocument>(entity =>
        {
            entity.ToTable("Documents");
            entity.Property(d => d.Title).HasMaxLength(200);
            entity.Property(d => d.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(d => d.Sender).HasMaxLength(200);
            entity.Property(d => d.Notes).HasMaxLength(2000);
            entity.Property(d => d.RelativePath).HasMaxLength(500);
            entity.Property(d => d.PaymentIban).HasMaxLength(40);
            entity.Property(d => d.PaymentReference).HasMaxLength(40);
            entity.HasIndex(d => d.Date);
            // Removing a person keeps their documents as household ones; deleting an entry unlinks its bill.
            entity.HasOne<Person>().WithMany().HasForeignKey(d => d.PersonId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Transaction>().WithMany().HasForeignKey(d => d.TransactionId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Note>(entity =>
        {
            entity.Property(n => n.Title).HasMaxLength(200);
            entity.Property(n => n.Project).HasMaxLength(100);
            entity.HasIndex(n => n.UpdatedAt);
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.ToTable("Tasks");
            entity.Property(t => t.Title).HasMaxLength(200);
            entity.Property(t => t.Repeat).HasConversion<string>().HasMaxLength(16).HasDefaultValue(TaskRepeat.Never);
            entity.Property(t => t.Notes).HasMaxLength(2000);
            entity.HasIndex(t => t.DueDate);
            // Removing a person keeps their tasks as household ones.
            entity.HasOne<Person>().WithMany().HasForeignKey(t => t.PersonId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PensionAccount>(entity =>
        {
            entity.Property(a => a.Name).HasMaxLength(200);
            entity.Property(a => a.Pillar).HasConversion<string>().HasMaxLength(16);
            entity.Property(a => a.Notes).HasMaxLength(2000);
            entity.HasMany(a => a.Statements).WithOne().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            // Removing a person keeps their accounts as household ones.
            entity.HasOne<Person>().WithMany().HasForeignKey(a => a.PersonId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PensionStatement>(entity =>
        {
            entity.HasIndex(s => s.Date);
            // Deleting the letter from Documents keeps the figures.
            entity.HasOne<HouseholdDocument>().WithMany().HasForeignKey(s => s.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BankAccount>(entity =>
        {
            entity.Property(a => a.Name).HasMaxLength(100);
            entity.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(a => a.Iban).HasMaxLength(40);
        });

        modelBuilder.Entity<InsurancePolicy>(entity =>
        {
            entity.ToTable("Insurance");
            entity.Property(p => p.Insurer).HasMaxLength(200);
            entity.Property(p => p.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(p => p.PremiumRecurrence).HasConversion<string>().HasMaxLength(16);
            entity.Property(p => p.PolicyNumber).HasMaxLength(100);
            entity.Property(p => p.Notes).HasMaxLength(2000);
            entity.HasOne<Person>().WithMany().HasForeignKey(p => p.PersonId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<HouseholdDocument>().WithMany().HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Contract>(entity =>
        {
            entity.Property(c => c.Provider).HasMaxLength(200);
            entity.Property(c => c.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(c => c.CostRecurrence).HasConversion<string>().HasMaxLength(16);
            entity.Property(c => c.CustomerNumber).HasMaxLength(100);
            entity.Property(c => c.Notes).HasMaxLength(2000);
            entity.HasOne<Person>().WithMany().HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<HouseholdDocument>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ImportantDate>(entity =>
        {
            entity.Property(d => d.Title).HasMaxLength(200);
            entity.Property(d => d.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(d => d.Notes).HasMaxLength(2000);
            entity.HasOne<Person>().WithMany().HasForeignKey(d => d.PersonId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Warranty>(entity =>
        {
            entity.Property(w => w.Item).HasMaxLength(200);
            entity.Property(w => w.Store).HasMaxLength(200);
            entity.Property(w => w.Notes).HasMaxLength(2000);
            entity.HasOne<Person>().WithMany().HasForeignKey(w => w.PersonId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<HouseholdDocument>().WithMany().HasForeignKey(w => w.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SavingsGoal>(entity =>
        {
            entity.Property(g => g.Name).HasMaxLength(200);
            entity.HasOne<BankAccount>().WithMany().HasForeignKey(g => g.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.Property(a => a.Title).HasMaxLength(200);
            entity.Property(a => a.Location).HasMaxLength(300);
            entity.Property(a => a.Notes).HasMaxLength(4000);
            entity.Property(a => a.CalendarId).HasMaxLength(300);
            entity.Property(a => a.CalendarEventKey).HasMaxLength(300);
            entity.HasIndex(a => a.Start);
            // Removing a person keeps their appointments as household ones.
            entity.HasOne<Person>().WithMany().HasForeignKey(a => a.PersonId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DocumentText>(entity =>
        {
            entity.HasKey(t => t.DocumentId);
            entity.HasOne<HouseholdDocument>().WithOne().HasForeignKey<DocumentText>(t => t.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Debt>(entity =>
        {
            entity.Property(d => d.Name).HasMaxLength(200);
            entity.HasMany(d => d.ExtraPayments).WithOne().HasForeignKey(p => p.DebtId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DebtPayment>().ToTable("DebtPayments");
    }
}
