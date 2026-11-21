using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Keystone.Services;

/// <summary>
/// Copies of Keystone's database, kept next to the documents (in OneDrive by default), so the data
/// survives a lost or broken Mac.
/// </summary>
public static class DatabaseBackup
{
    public const string FolderName = "Backups";

    /// <summary>How many copies are kept; older ones are removed.</summary>
    public const int Keep = 30;

    /// <summary>A new copy is made when the newest one is older than this.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // Development builds keep their own backups apart, so they never show up for restoring real data.
#if DEBUG
    private const string Prefix = "keystone-dev-";
#else
    private const string Prefix = "keystone-";
#endif
    private const string DateFormat = "yyyy-MM-dd-HHmm";

    public sealed record Backup(string Path, DateTime CreatedAt);

    /// <summary>Copies the database into <paramref name="folder"/> and removes copies beyond <see cref="Keep"/>.</summary>
    public static Backup Create(string databasePath, string folder, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{Prefix}{now.ToString(DateFormat, CultureInfo.InvariantCulture)}.db");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{Prefix}{now.ToString(DateFormat, CultureInfo.InvariantCulture)}-{n}.db");

        // SQLite's own backup copies a consistent snapshot, even while the app is writing.
        using (var source = Open(databasePath, SqliteOpenMode.ReadOnly))
        using (var destination = Open(target, SqliteOpenMode.ReadWriteCreate))
            source.BackupDatabase(destination);

        Prune(folder);
        return new Backup(target, now);
    }

    /// <summary>Backups in the folder, newest first.</summary>
    public static IReadOnlyList<Backup> List(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        return Directory.EnumerateFiles(folder, $"{Prefix}*.db")
            .Select(path => TryParseDate(Path.GetFileNameWithoutExtension(path)) is { } date ? new Backup(path, date) : null)
            .OfType<Backup>()
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Path, StringComparer.Ordinal)
            .ToList();
    }

    public static bool IsDue(string folder, DateTime now) =>
        List(folder).FirstOrDefault() is not { } latest || now - latest.CreatedAt >= Interval;

    /// <summary>
    /// Replaces the database's content with a backup's. The current data is backed up first, into the same
    /// folder, so a restore can be undone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file isn't a Keystone backup.</exception>
    public static void Restore(string backupPath, string databasePath, string folder, DateTime now)
    {
        if (!IsKeystoneDatabase(backupPath))
            throw new InvalidOperationException("This file isn't a Keystone backup.");

        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
            Create(databasePath, folder, now);

        using var source = Open(backupPath, SqliteOpenMode.ReadOnly);
        using var destination = Open(databasePath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>A SQLite file with Keystone's tables.</summary>
    public static bool IsKeystoneDatabase(string path)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Transactions', 'Settings')";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 2;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void Prune(string folder)
    {
        foreach (var old in List(folder).Skip(Keep))
            File.Delete(old.Path);
    }

    private static DateTime? TryParseDate(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length < Prefix.Length + DateFormat.Length)
            return null;
        return DateTime.TryParseExact(name.Substring(Prefix.Length, DateFormat.Length), DateFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    // Without pooling, so files aren't held open after a backup or restore.
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
