using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Keystone.Data;

/// <summary>
/// Lets only one save reach the database at a time. Keystone saves from the background too (e.g. the text of a
/// document just filed, while the next one in a bulk import is being saved), and two SQLite transactions writing
/// at once can lock each other out ("database is locked"). Saves are short, so waiting a moment costs nothing.
/// </summary>
public sealed class OneWriterAtATime : SaveChangesInterceptor
{
    public static readonly OneWriterAtATime Instance = new();

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Gate.Wait();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Gate.Release();
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Gate.Release();
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Gate.Release();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Gate.Release();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Gate.Release();

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Gate.Release();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Makes SQLite wait up to 10 seconds for a lock held by another connection, instead of failing straight away
/// with "database is locked" (e.g. a save committing while a background read of a document's text is running).
/// </summary>
public sealed class WaitForLocks : DbConnectionInterceptor
{
    public static readonly WaitForLocks Instance = new();

    private const string Pragma = "PRAGMA busy_timeout = 10000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
