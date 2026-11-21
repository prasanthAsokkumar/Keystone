using Runway.Data;
using Runway.Services;
using Runway.ViewModels;

namespace Runway.Tests;

/// <summary>The app was renamed to Keystone; its OneDrive folder moves from RunwayApp to KeystoneApp.</summary>
/// <remarks>Changes the OneDrive location for the whole process, so it runs apart from other tests.</remarks>
[Collection(nameof(LanguageCollection))]
public sealed class DocumentsFolderTests : IDisposable
{
    private readonly string _pras = Path.Combine(Path.GetTempPath(), $"runway-folder-{Guid.NewGuid():N}", "PRAS");

    private string NewRoot => Path.Combine(_pras, "KeystoneApp");

    private string OldRoot => Path.Combine(_pras, "RunwayApp");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_pras)))
            Directory.Delete(Path.GetDirectoryName(_pras)!, recursive: true);
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

    [Fact]
    public async Task LoadingTheAppMovesTheFolderAndDocumentsStillOpen()
    {
        var oneDrive = Path.GetDirectoryName(_pras)!;
        var previous = Environment.GetEnvironmentVariable(DocumentStore.OneDrivePathVariable);
        Environment.SetEnvironmentVariable(DocumentStore.OneDrivePathVariable, oneDrive);
        try
        {
            OldFile("Anna/2026/Bills/2026-11-15 Premium.pdf");
            var repository = new RunwayRepository(Path.Combine(oneDrive, "test.db"));
            repository.Migrate();
            await repository.AddDocumentAsync(new Runway.Models.HouseholdDocument
            {
                Title = "Premium", RelativePath = "Anna/2026/Bills/2026-11-15 Premium.pdf", Date = new DateOnly(2026, 11, 15),
            });

            var vm = new MainViewModel(repository);
            await vm.LoadAsync();

            Assert.Equal(NewRoot, vm.DocumentsFolder);
            Assert.True(vm.IsDefaultDocumentsFolder);
            Assert.False(Assert.Single(vm.DocumentRows).IsMissing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DocumentStore.OneDrivePathVariable, previous);
        }
    }
}
