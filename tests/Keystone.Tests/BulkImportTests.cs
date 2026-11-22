using Keystone.Data;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

[Collection(nameof(LanguageCollection))]
public sealed class BulkImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"runway-bulk-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public BulkImportTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_folder);
    }

    private string File(string name)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, $"made-up content of {name}");
        return path;
    }

    [Fact]
    public async Task FilesSeveralDocumentsAtOnce()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));

        // Local AI is off, so the files are ready to fill in straight away.
        await vm.StartBulkDocumentsAsync([File("bill.pdf"), File("letter.pdf"), File("notice.pdf")]);
        Assert.True(vm.IsBulkPanelOpen);
        Assert.Equal(3, vm.BulkItems.Count);
        Assert.All(vm.BulkItems, i => Assert.Equal(BulkStatus.Ready, i.Status));
        Assert.Equal("bill", vm.BulkItems[0].Title);

        // The same file again isn't added twice; new ones join the list.
        await vm.StartBulkDocumentsAsync([Path.Combine(_folder, "bill.pdf"), File("extra.pdf")]);
        Assert.Equal(4, vm.BulkItems.Count);
        vm.RemoveBulkItemCommand.Execute(vm.BulkItems[3]);

        var bill = vm.BulkItems[0];
        bill.KindIndex = (int)DocumentKind.Bill;
        bill.Title = "Electricity Q4";
        bill.Sender = "IWB";
        bill.Person = vm.PersonOptions.First(o => o.Id == anna.Id);
        bill.Amount = 186.40m;
        bill.DueDate = new DateTime(2026, 12, 31);
        vm.BulkItems[1].KindIndex = (int)DocumentKind.Letter;
        vm.BulkItems[2].IsSelected = false;
        Assert.Equal("Save 2 documents", vm.BulkSaveText);

        await vm.SaveBulkCommand.ExecuteAsync(null);

        Assert.Null(vm.BulkError);
        var unticked = Assert.Single(vm.BulkItems); // stays in the list
        Assert.Equal("notice", unticked.Title);
        var documents = await _repository.GetDocumentsAsync();
        Assert.Equal(2, documents.Count);
        var saved = documents.Single(d => d.Title == "Electricity Q4");
        Assert.Equal(anna.Id, saved.PersonId);
        Assert.StartsWith("Anna/", saved.RelativePath);
        Assert.NotNull(saved.TransactionId); // added to expenses
        Assert.Contains(vm.Transactions, t => t.Description == "IWB: Electricity Q4" && t.Amount == 186.40m);
    }

    [Fact]
    public async Task AsksForMissingDetailsPerFile()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        await vm.StartBulkDocumentsAsync([File("a.pdf"), File("b.pdf")]);

        vm.BulkItems[0].Title = " ";
        vm.BulkItems[1].KindIndex = (int)DocumentKind.Bill; // a bill without an amount, still to add to expenses

        await vm.SaveBulkCommand.ExecuteAsync(null);

        Assert.NotNull(vm.BulkError);
        Assert.Equal(2, vm.BulkItems.Count);
        Assert.All(vm.BulkItems, i => Assert.NotNull(i.Error));
        Assert.Empty(await _repository.GetDocumentsAsync());
    }
}
