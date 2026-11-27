using Keystone.Data;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>
/// Earlier versions kept documents in OneDrive/&lt;a folder&gt;/KeystoneApp (RunwayApp before the rename); such a folder
/// stays in use. New households get ~/Documents/Keystone.
/// </summary>
/// <remarks>Changes the home folder for the whole process, so it runs apart from other tests.</remarks>
[Collection(nameof(LanguageCollection))]
public sealed class DocumentsFolderTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"keystone-folder-{Guid.NewGuid():N}");

    private string Household => Path.Combine(_home, "OneDrive", "Meier");

    private string NewRoot => Path.Combine(Household, "KeystoneApp");

    private string OldRoot => Path.Combine(Household, "RunwayApp");

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private void OldFile(string relative)
    {
        var path = Path.Combine(OldRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void RenamesTheOldFolder()
    {
        OldFile("Anna/2026/Bills/2026-11-15 Premium.pdf");

        var (folder, problem) = DocumentStore.MoveLegacyFolder(NewRoot);

        Assert.Null(problem);
        Assert.Equal(NewRoot, folder);
        Assert.False(Directory.Exists(OldRoot));
        Assert.True(File.Exists(Path.Combine(NewRoot, "Anna/2026/Bills/2026-11-15 Premium.pdf")));
    }

    [Fact]
    public void ReplacesAnEmptyNewFolder()
    {
        OldFile("a.pdf");
        Directory.CreateDirectory(NewRoot);

        Assert.Equal((NewRoot, null), DocumentStore.MoveLegacyFolder(NewRoot));
        Assert.True(File.Exists(Path.Combine(NewRoot, "a.pdf")));
    }

    [Fact]
    public void KeepsTheOldFolderWhenBothHoldFiles()
    {
        OldFile("a.pdf");
        Directory.CreateDirectory(NewRoot);
        File.WriteAllText(Path.Combine(NewRoot, "b.pdf"), "x");

        var (folder, problem) = DocumentStore.MoveLegacyFolder(NewRoot);

        Assert.Equal(OldRoot, folder);
        Assert.NotNull(problem);
        Assert.True(File.Exists(Path.Combine(OldRoot, "a.pdf")));
    }

    [Fact]
    public void NothingToDoWithoutAnOldFolder() =>
        Assert.Equal((NewRoot, null), DocumentStore.MoveLegacyFolder(NewRoot));

    /// <summary>Loads the app with <see cref="_home"/> as the home folder.</summary>
    private async Task<MainViewModel> LoadAsync(bool withDocument)
    {
        var previous = Environment.GetEnvironmentVariable(DocumentStore.HomeVariable);
        Environment.SetEnvironmentVariable(DocumentStore.HomeVariable, _home);
        try
        {
            Directory.CreateDirectory(_home);
            var repository = new KeystoneRepository(Path.Combine(_home, "test.db"));
            repository.Migrate();
            if (withDocument)
                await repository.AddDocumentAsync(new Keystone.Models.HouseholdDocument
                {
                    Title = "Premium", RelativePath = "Anna/2026/Bills/2026-11-15 Premium.pdf", Date = new DateOnly(2026, 11, 15),
                });
            var vm = new MainViewModel(repository);
            await vm.LoadAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return vm;
        }
        finally
        {
            Environment.SetEnvironmentVariable(DocumentStore.HomeVariable, previous);
        }
    }

    [Fact]
    public async Task ARunwayFolderIsRenamedAndKeptAsTheChosenFolder()
    {
        OldFile("Anna/2026/Bills/2026-11-15 Premium.pdf");

        var vm = await LoadAsync(withDocument: true);

        Assert.Equal(NewRoot, vm.DocumentsFolder);
        Assert.False(vm.IsDefaultDocumentsFolder);
        Assert.False(Assert.Single(vm.DocumentRows).IsMissing);
    }

    [Fact]
    public async Task AnEarlierKeystoneFolderInOneDriveStaysInUse()
    {
        var file = Path.Combine(NewRoot, "Anna/2026/Bills/2026-11-15 Premium.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "earlier default folder");

        var vm = await LoadAsync(withDocument: true);

        Assert.Equal(NewRoot, vm.DocumentsFolder);
        Assert.False(Assert.Single(vm.DocumentRows).IsMissing);
    }

    [Fact]
    public async Task ANewHouseholdUsesDocumentsKeystone()
    {
        var vm = await LoadAsync(withDocument: false);

        Assert.Equal(Path.Combine(_home, "Documents", "Keystone"), vm.DocumentsFolder);
        Assert.True(vm.IsDefaultDocumentsFolder);
    }
}
