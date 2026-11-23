using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Sorting lists by their column headers, and dragging rows between pages or into an order of your own.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class SortingAndDragTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-sortdrag-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public SortingAndDragTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    private Task<Transaction> AddAsync(string description, decimal amount, string category = "Other", int daysAgo = 1,
        TransactionType type = TransactionType.Expense) =>
        _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddDays(-daysAgo), Description = description, Amount = amount, Category = category, Type = type,
        });

    // ── Sorting ──

    [Fact]
    public async Task ClickingAHeaderSortsAndClickingAgainReverses()
    {
        await AddAsync("Migros", 79.40m, daysAgo: 3);
        await AddAsync("Coop", 101.07m, daysAgo: 1);
        await AddAsync("Apotheke", 16.90m, daysAgo: 2);
        var vm = await LoadAsync();
        vm.IsExpensesTab = true;
        Assert.Equal(["Coop", "Apotheke", "Migros"], vm.EntryRows.Select(r => r.Description)); // newest first

        vm.SortListCommand.Execute("transactions:amount");
        Assert.Equal(["Coop", "Migros", "Apotheke"], vm.EntryRows.Select(r => r.Description)); // largest first
        Assert.Equal("-amount", vm.ListSorts["transactions"]);

        vm.SortListCommand.Execute("transactions:amount");
        Assert.Equal(["Apotheke", "Migros", "Coop"], vm.EntryRows.Select(r => r.Description));

        vm.SortListCommand.Execute("transactions:description");
        Assert.Equal(["Apotheke", "Coop", "Migros"], vm.EntryRows.Select(r => r.Description)); // A–Z first

        // Remembered after a restart.
        await Task.Delay(100);
        var again = await LoadAsync();
        again.IsExpensesTab = true;
        Assert.Equal(["Apotheke", "Coop", "Migros"], again.EntryRows.Select(r => r.Description));
    }

    [Fact]
    public async Task MissingValuesStayAtTheEndEitherWay()
    {
        foreach (var (title, amount) in new[] { ("Letter", (decimal?)null), ("Bill A", 50m), ("Bill B", 200m) })
            await _repository.AddDocumentAsync(new HouseholdDocument
            {
                Title = title, Kind = amount is null ? DocumentKind.Letter : DocumentKind.Bill, Amount = amount, Date = Today, AddedAt = DateTime.Now,
            });
        var vm = await LoadAsync();

        vm.SortListCommand.Execute("documents:amount");
        Assert.Equal(["Bill B", "Bill A", "Letter"], vm.DocumentRows.Select(r => r.Title));
        vm.SortListCommand.Execute("documents:amount");
        Assert.Equal(["Bill A", "Bill B", "Letter"], vm.DocumentRows.Select(r => r.Title));
    }

    // ── Dragging entries ──

    [Fact]
    public async Task AnExpenseDraggedOntoInsuranceBecomesAPremium()
    {
        var entry = await AddAsync("Helsana", 412.35m);
        var vm = await LoadAsync();
        vm.IsExpensesTab = true;
        var row = vm.EntryRows.Single();

        Assert.True(vm.CanDrop("insurance", null, row));
        Assert.False(vm.CanDrop("expenses", null, row)); // already there
        await vm.DropAsync("insurance", null, row);

        Assert.Empty(vm.EntryRows);
        // A one-time payment made yesterday is listed with the ended premiums ("Paid …").
        Assert.Equal(Categories.HealthInsurance, Assert.Single(vm.EndedInsuranceEntryRows).Transaction.Category);
        Assert.Equal(Categories.HealthInsurance, (await _repository.GetTransactionsAsync()).Single().Category);
        Assert.Equal("Moved “Helsana” to Insurance", vm.NoticeText);

        // Back with Undo, or by dragging it onto Expenses.
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal("Other", (await _repository.GetTransactionsAsync()).Single().Category);
        await vm.DropAsync("taxes", null, vm.EntryRows.Single());
        Assert.Single(vm.TaxEntryRows);
        await vm.DropAsync("expenses", null, vm.TaxEntryRows.Single());
        Assert.Equal(Categories.Uncategorized, vm.EntryRows.Single().Transaction.Category);
    }

    [Fact]
    public async Task EntriesMoveBetweenIncomeAndExpenses()
    {
        await AddAsync("Refund", 120m, category: "Rent");
        var vm = await LoadAsync();
        vm.IsExpensesTab = true;

        await vm.DropAsync("income", null, vm.EntryRows.Single());

        var moved = (await _repository.GetTransactionsAsync()).Single();
        Assert.Equal((TransactionType.Income, Categories.Uncategorized), (moved.Type, moved.Category));
        Assert.Empty(vm.EntryRows);
        vm.IsIncomeTab = true;
        Assert.Single(vm.EntryRows);
        Assert.False(vm.CanDrop("insurance", null, new EntryRow(new Transaction { Type = TransactionType.Transfer }, "")));
    }

    // ── Dragging documents ──

    [Fact]
    public async Task DocumentsGoWhereTheyAreDragged()
    {
        await _repository.AddDocumentAsync(new HouseholdDocument { Title = "Letter from the commune", Kind = DocumentKind.Letter, Date = Today, AddedAt = DateTime.Now });
        var vm = await LoadAsync();
        var row = vm.DocumentRows.Single();
        Assert.Empty(vm.TaxDocumentRows);

        await vm.DropAsync("taxes", null, row);
        Assert.Single(vm.TaxDocumentRows);
        Assert.Equal("Taxes", (await _repository.GetDocumentsAsync()).Single().Section);

        // Onto Documents: listed there only.
        await vm.DropAsync("documents", null, vm.TaxDocumentRows.Single());
        Assert.Empty(vm.TaxDocumentRows);
        Assert.Empty(vm.InsuranceDocumentRows);
        Assert.Single(vm.DocumentRows);
    }

    [Fact]
    public async Task ADocumentDroppedOnAPersonMovesToTheirFolder()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var vm = await LoadAsync();
        var file = Path.Combine(_folder, "dentist.pdf");
        await File.WriteAllTextAsync(file, "made-up dentist bill");
        await vm.StartNewDocumentAsync(file);
        vm.DocumentKindIndex = (int)DocumentKind.Letter;
        vm.DocumentTitle = "Dentist";
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        var row = vm.DocumentRows.Single();
        Assert.StartsWith("Household/", row.Document.RelativePath);

        var annaOption = vm.PersonOptions.Single(o => o.Id == anna.Id);
        Assert.True(vm.CanDrop("person", annaOption, row));
        await vm.DropAsync("person", annaOption, row);

        var saved = (await _repository.GetDocumentsAsync()).Single();
        Assert.Equal(anna.Id, saved.PersonId);
        Assert.StartsWith("Anna/", saved.RelativePath);
        Assert.True(File.Exists(Path.Combine(vm.DocumentsFolder, saved.RelativePath)));
        Assert.Equal("“Dentist” is now Anna’s", vm.NoticeText);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null((await _repository.GetDocumentsAsync()).Single().PersonId);
        Assert.True(File.Exists(Path.Combine(vm.DocumentsFolder, vm.Documents.Single().RelativePath)));
    }

    // ── Your own order ──

    [Fact]
    public async Task TasksCanBePutInYourOwnOrder()
    {
        foreach (var title in new[] { "Renew passport", "Book dentist", "Sort out the cellar" })
            await _repository.AddTaskAsync(new TaskItem { Title = title, CreatedAt = DateTime.Now });
        var vm = await LoadAsync();
        var before = vm.TaskRows.Select(r => r.Title).ToList();

        // The last one dragged onto the first goes to the top.
        await vm.DropAsync("reorder:tasks", vm.TaskRows[0], vm.TaskRows[2]);

        var expected = new[] { before[2], before[0], before[1] };
        Assert.Equal(expected, vm.TaskRows.Select(r => r.Title));
        Assert.Equal(MainViewModel.ManualSort, vm.ListSorts["tasks"]);

        // Kept after a restart, and a header click still sorts.
        await Task.Delay(100);
        var again = await LoadAsync();
        Assert.Equal(expected, again.TaskRows.Select(r => r.Title));
        again.SortListCommand.Execute("tasks:title");
        Assert.Equal(["Book dentist", "Renew passport", "Sort out the cellar"], again.TaskRows.Select(r => r.Title));
        Assert.False(again.CanDrop("reorder:tasks", again.TaskRows[0], again.TaskRows[0]));
    }

    [Fact]
    public async Task PoliciesAndDebtsCanBeReordered()
    {
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = "Helsana", Premium = 400 });
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = "Mobiliar", Premium = 640 });
        var vm = await LoadAsync();
        var first = vm.InsuranceRows[0].Policy.Insurer;

        await vm.DropAsync("reorder:policies", vm.InsuranceRows[0], vm.InsuranceRows[1]);

        Assert.NotEqual(first, vm.InsuranceRows[0].Policy.Insurer);
        Assert.Equal(1, (await _repository.GetInsurancePoliciesAsync()).Single(p => p.Insurer != first).SortOrder);
        Assert.False(vm.CanDrop("reorder:policies", vm.InsuranceRows[0], new TaskRow(new TaskItem(), "", Today)));
    }
}
