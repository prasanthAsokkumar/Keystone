using Keystone.Data;

namespace Keystone.Tests;

/// <summary>The app was renamed from Runway; its database moves to a Keystone folder.</summary>
public sealed class DatabaseLocationTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), $"keystone-appdata-{Guid.NewGuid():N}");

    private string NewPath => Path.Combine(_appData, "Keystone", KeystoneDbContext.DatabaseFileName);

    // Debug and Release builds use different database names (development data apart from real data).
    private string OldPath => Path.Combine(_appData, "Runway", KeystoneDbContext.LegacyDatabaseFileName);

    public void Dispose()
    {
        if (Directory.Exists(_appData))
            Directory.Delete(_appData, recursive: true);
    }

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void MovesTheOldDatabaseWithItsJournal()
    {
        Write(OldPath, "db");
        Write(OldPath + "-wal", "wal");

        Assert.Equal(NewPath, KeystoneDbContext.ResolveDatabasePath(_appData));

        Assert.Equal("db", File.ReadAllText(NewPath));
        Assert.Equal("wal", File.ReadAllText(NewPath + "-wal"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(OldPath))); // the empty old folder is removed
    }

    [Fact]
    public void LeavesAnExistingKeystoneDatabaseAlone()
    {
        Write(OldPath, "old");
        Write(NewPath, "new");

        Assert.Equal(NewPath, KeystoneDbContext.ResolveDatabasePath(_appData));
        Assert.Equal("new", File.ReadAllText(NewPath));
        Assert.True(File.Exists(OldPath));
    }

    [Fact]
    public void FreshInstallUsesTheKeystoneFolder() =>
        Assert.Equal(NewPath, KeystoneDbContext.ResolveDatabasePath(_appData));

    [Fact]
    public async Task MovedDatabaseStillOpens()
    {
        var old = new KeystoneRepository(OldPath);
        old.Migrate();
        await old.AddPersonAsync(new Keystone.Models.Person { Name = "Anna" });
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var repository = new KeystoneRepository(KeystoneDbContext.ResolveDatabasePath(_appData));
        repository.Migrate();

        Assert.Equal("Anna", Assert.Single(await repository.GetPeopleAsync()).Name);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
