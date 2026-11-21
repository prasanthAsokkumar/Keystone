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
        });

        modelBuilder.Entity<Person>().Property(p => p.Name).HasMaxLength(100);

        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.AiEndpoint).HasMaxLength(200).HasDefaultValue(AppSettings.DefaultAiEndpoint);
            entity.Property(s => s.AiModel).HasMaxLength(100).HasDefaultValue(AppSettings.DefaultAiModel);
            entity.Property(s => s.DocumentsFolder).HasMaxLength(500);
            entity.Property(s => s.Language).HasMaxLength(8);
        });

        modelBuilder.Entity<HouseholdDocument>(entity =>
        {
            entity.ToTable("Documents");
            entity.Property(d => d.Title).HasMaxLength(200);
            entity.Property(d => d.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(d => d.Sender).HasMaxLength(200);
            entity.Property(d => d.Notes).HasMaxLength(2000);
            entity.Property(d => d.RelativePath).HasMaxLength(500);
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

        modelBuilder.Entity<Debt>(entity =>
        {
            entity.Property(d => d.Name).HasMaxLength(200);
            entity.HasMany(d => d.ExtraPayments).WithOne().HasForeignKey(p => p.DebtId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DebtPayment>().ToTable("DebtPayments");
    }
}
