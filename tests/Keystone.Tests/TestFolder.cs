namespace Keystone.Tests;

/// <summary>Removes a test's temporary folder, even while background work is still finishing a save in it.</summary>
public static class TestFolder
{
    public static void Delete(string folder)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}
