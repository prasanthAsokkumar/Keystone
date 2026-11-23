using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>A bill too big to pay at once is paid in parts, each with its date, amount, reminder and expense entry.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class InstalmentTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-instalments-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public InstalmentTests()
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

    /// <summary>Files a made-up tax bill of CHF 4'500, due in a month, in the expenses.</summary>
    private async Task<HouseholdDocument> FileTaxBillAsync(MainViewModel vm, bool inParts)
    {
        var path = Path.Combine(_folder, "tax-bill.pdf");
        await File.WriteAllTextAsync(path, $"made-up tax bill {Guid.NewGuid()}");
        await vm.StartNewDocumentAsync(path);
        vm.DocumentKindIndex = (int)DocumentKind.Bill;
        vm.DocumentTitle = "Steuerrechnung 2026";
        vm.DocumentSender = "Steuerverwaltung Basel-Stadt";
        vm.DocumentAmount = 4500m;
        vm.DocumentDueDate = Today.AddMonths(1).ToDateTime(TimeOnly.MinValue);
        vm.DocumentCreateEntry = true;
        if (inParts)
        {
            vm.DocumentPaysInInstalments = true;
            vm.InstalmentCount = 3;
        }
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Null(vm.DocumentError);
        return vm.Documents.Single();
    }

    [Fact]
    public void PartsAreWholeFiveCentimesAndAddUpExactly()
    {
        var parts = InstalmentPlan.Split(1000m, 3, new DateOnly(2027, 1, 31));

        Assert.Equal([333.30m, 333.30m, 333.40m], parts.Select(p => p.Amount));
        // Counted from the first date each time, so the end of the month isn't lost after February.
        Assert.Equal([new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31)], parts.Select(p => p.DueDate));
        Assert.Equal(new DateOnly(2027, 7, 31), InstalmentPlan.Split(600m, 3, new DateOnly(2027, 1, 31), everyMonths: 3)[2].DueDate);
    }

    [Fact]
    public async Task ABillInPartsHasAnEntryAndAReminderForEachPart()
    {
        var vm = await LoadAsync();
        var bill = await FileTaxBillAsync(vm, inParts: true);

        Assert.Equal([1500m, 1500m, 1500m], bill.InstalmentsInOrder.Select(i => i.Amount));
        Assert.Equal(Today.AddMonths(1), bill.NextDueDate);
        Assert.Null(bill.TransactionId);

        // One one-time expense per part, on its date; a tax bill's go under Taxes.
        var entries = vm.Transactions.Where(t => t.Description.StartsWith("Steuerverwaltung")).OrderBy(t => t.Date).ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(Categories.Taxes, e.Category));
        Assert.Equal(bill.InstalmentsInOrder.Select(i => i.DueDate), entries.Select(e => e.Date));
        Assert.EndsWith("(1/3)", entries[0].Description);

        var row = vm.DocumentRows.Single();
        Assert.Equal($"Instalment 1/3 · {Format.Date(Today.AddMonths(1))}", row.StatusText);
        Assert.Contains(Format.Money(4500m), vm.OpenBillsText);

        var reminder = vm.PlanReminders(DateTime.Now).First(r => r.Id.StartsWith($"bill-{bill.Id}"));
        Assert.Contains("instalment 1 of 3", reminder.Body);
        Assert.Contains(Format.Money(1500m), reminder.Body);

        // Saved and read back.
        var again = await LoadAsync();
        Assert.Equal(3, again.Documents.Single().Instalments.Count);
    }

    [Fact]
    public async Task PayingPartsOneByOnePaysTheBillWithTheLast()
    {
        var vm = await LoadAsync();
        var bill = await FileTaxBillAsync(vm, inParts: true);

        await vm.MarkDocumentPaidCommand.ExecuteAsync(bill);
        Assert.True(bill.IsOpenBill);
        Assert.Equal(2, bill.NextInstalmentNumber);
        Assert.Equal(3000m, bill.AmountLeft);
        Assert.Equal($"Paid instalment 1 of 3 · {Format.Money(3000m)} left", vm.NoticeText);

        await vm.MarkDocumentPaidCommand.ExecuteAsync(bill);
        await vm.MarkDocumentPaidCommand.ExecuteAsync(bill);
        Assert.True(bill.IsPaid);
        Assert.Equal(0m, bill.AmountLeft);
        Assert.DoesNotContain(vm.PlanReminders(DateTime.Now), r => r.Id.StartsWith($"bill-{bill.Id}"));

        // Undo takes back only the last part.
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.False(bill.IsPaid);
        Assert.Equal(3, bill.NextInstalmentNumber);
    }

    [Fact]
    public async Task SplittingABillLaterReplacesItsSingleEntryAndKeepsItsCategory()
    {
        var vm = await LoadAsync();
        var bill = await FileTaxBillAsync(vm, inParts: false);
        var single = vm.Transactions.Single(t => t.Id == bill.TransactionId);

        vm.EditDocumentCommand.Execute(bill);
        Assert.False(vm.CanCreateDocumentEntry); // already in the expenses
        vm.DocumentPaysInInstalments = true;
        vm.InstalmentCount = 4;
        vm.InstalmentEveryMonths = 2;
        // The third part is made bigger; the bill then costs more (interest), which is shown.
        vm.InstalmentRows[2].Amount = 1225m;
        Assert.Equal($"{Format.Money(100m)} more than the bill, e.g. interest or fees.", vm.InstalmentDifferenceText);
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Null(vm.DocumentError);

        bill = vm.Documents.Single();
        Assert.DoesNotContain(vm.Transactions, t => t.Id == single.Id);
        var entries = vm.Transactions.Where(t => t.Description.Contains("Steuerrechnung")).OrderBy(t => t.Date).ToList();
        Assert.Equal([1125m, 1125m, 1225m, 1125m], entries.Select(e => e.Amount));
        Assert.All(entries, e => Assert.Equal(single.Category, e.Category));
        Assert.Equal(Today.AddMonths(1).AddMonths(6), entries[3].Date);

        // Taking the plan away again: one entry for the whole bill.
        vm.EditDocumentCommand.Execute(bill);
        vm.DocumentPaysInInstalments = false;
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        bill = vm.Documents.Single();
        Assert.False(bill.HasInstalments);
        Assert.Equal(4500m, vm.Transactions.Single(t => t.Description.Contains("Steuerrechnung")).Amount);
        Assert.Empty((await _repository.GetDocumentsAsync()).Single().Instalments);
    }

    [Fact]
    public async Task ChangingTheNumberKeepsPaidPartsAndSharesOutTheRest()
    {
        var vm = await LoadAsync();
        var bill = await FileTaxBillAsync(vm, inParts: true);
        await vm.MarkDocumentPaidCommand.ExecuteAsync(bill);

        vm.EditDocumentCommand.Execute(bill);
        vm.InstalmentCount = 5;
        Assert.Equal([1500m, 750m, 750m, 750m, 750m], vm.InstalmentRows.Select(r => r.Amount!.Value));
        Assert.True(vm.InstalmentRows[0].IsPaid);
        await vm.SaveDocumentCommand.ExecuteAsync(null);

        bill = vm.Documents.Single();
        Assert.Equal(2, bill.NextInstalmentNumber);
        Assert.Equal(5, vm.Transactions.Count(t => t.Description.Contains("Steuerrechnung")));
    }
}
