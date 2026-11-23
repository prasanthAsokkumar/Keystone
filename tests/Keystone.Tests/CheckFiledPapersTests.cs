using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Insurance papers filed before policies were saved from them are read again on request.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class CheckFiledPapersTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-checkpapers-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public CheckFiledPapersTests()
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

    /// <summary>Files a paper the old way: just the document, nothing saved from it.</summary>
    private async Task<HouseholdDocument> FileAsync(MainViewModel vm, string title, string sender, DocumentKind kind, DateOnly date)
    {
        var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(path, $"made-up {title} {Guid.NewGuid()}");
        await vm.StartNewDocumentAsync(path);
        vm.DocumentKindIndex = (int)kind;
        vm.DocumentTitle = title;
        vm.DocumentSender = sender;
        vm.DocumentDate = date.ToDateTime(TimeOnly.MinValue);
        vm.DocumentAddFollowUp = false;
        vm.DocumentCreateEntry = false;
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Null(vm.DocumentError);
        return vm.Documents.Single(d => d.Title == title);
    }

    [Fact]
    public async Task OldPapersAddTheirPoliciesAndTheLatestPremiumCounts()
    {
        var vm = await LoadAsync();
        var termEnd = Today.AddMonths(1);
        var policyPaper = await FileAsync(vm, "Police Hausrat", "Die Mobiliar", DocumentKind.Policy, Today.AddYears(-2));
        await FileAsync(vm, "Prämie 2025", "Helsana", DocumentKind.Letter, Today.AddYears(-1));
        var newLetter = await FileAsync(vm, "Prämie 2026", "Helsana", DocumentKind.Letter, Today.AddDays(-5));
        await FileAsync(vm, "Electricity Q3", "IWB", DocumentKind.Bill, Today);

        // What the local AI reads from each paper.
        var reads = new List<string>();
        vm.ReadPaperForTest = (document, _) =>
        {
            reads.Add(document.Title);
            return Task.FromResult<FollowUp?>(document.Title switch
            {
                "Police Hausrat" => new FollowUp(FollowUpKind.Insurance, "HouseholdLiability", "H-1", 640m, Recurrence.Yearly, termEnd),
                "Prämie 2025" => new FollowUp(FollowUpKind.Insurance, "HealthBasic", "K-9", 380m, Recurrence.Monthly, termEnd.AddYears(-1)),
                "Prämie 2026" => new FollowUp(FollowUpKind.Insurance, "HealthBasic", "K-9", 412.35m, Recurrence.Monthly, termEnd),
                _ => null,
            });
        };

        await vm.CheckFiledPapersCommand.ExecuteAsync(null);

        // Only insurance papers are read, oldest first.
        Assert.Equal(["Police Hausrat", "Prämie 2025", "Prämie 2026"], reads);
        var policies = await _repository.GetInsurancePoliciesAsync();
        Assert.Equal(2, policies.Count);
        var helsana = policies.Single(p => p.Insurer == "Helsana");
        Assert.Equal((412.35m, newLetter.Id), (helsana.Premium, helsana.DocumentId!.Value));
        Assert.Equal(policyPaper.Id, policies.Single(p => p.Insurer == "Die Mobiliar").DocumentId);
        // One running premium per policy: the latest one.
        Assert.Equal([412.35m, 640m], vm.Transactions.Where(t => t.EndDate is null).Select(t => t.Amount).Order());
        Assert.Equal("Checked 3 papers · added Die Mobiliar, Helsana", vm.NoticeText);

        // A second check reads nothing again, so the old letter can't bring back the old premium.
        reads.Clear();
        await vm.CheckFiledPapersCommand.ExecuteAsync(null);
        Assert.Empty(reads);
        Assert.Equal(412.35m, (await _repository.GetInsurancePoliciesAsync()).Single(p => p.Insurer == "Helsana").Premium);
    }

    [Fact]
    public async Task UndoTakesBackTheWholeCheck()
    {
        var vm = await LoadAsync();
        await FileAsync(vm, "Police Rechtsschutz", "Protekta", DocumentKind.Policy, Today.AddMonths(-3));
        vm.ReadPaperForTest = (_, _) => Task.FromResult<FollowUp?>(
            new FollowUp(FollowUpKind.Insurance, "LegalProtection", "R-7", 290m, Recurrence.Yearly, Today.AddMonths(9)));

        await vm.CheckFiledPapersCommand.ExecuteAsync(null);
        Assert.Single(await _repository.GetInsurancePoliciesAsync());
        Assert.Single(vm.Transactions);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Empty(await _repository.GetInsurancePoliciesAsync());
        Assert.Empty(vm.Transactions);
        // Undone, the paper can be checked again.
        Assert.Single(vm.PapersToCheck());
    }
}
