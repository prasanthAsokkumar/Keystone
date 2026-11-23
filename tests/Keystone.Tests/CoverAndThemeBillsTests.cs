using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>The Insurance page shows which common Swiss insurances the household has; Insurance and Taxes show their open bills.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class CoverAndThemeBillsTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-cover-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public CoverAndThemeBillsTests()
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

    private Task<InsurancePolicy> PolicyAsync(string insurer, InsuranceKind kind, decimal premium, int? personId = null) =>
        _repository.SaveInsurancePolicyAsync(new InsurancePolicy { Insurer = insurer, Kind = kind, Premium = premium, PersonId = personId });

    [Fact]
    public async Task TheCoverOverviewShowsWhatIsThereAndWhatIsMissing()
    {
        var lukas = await _repository.AddPersonAsync(new Person { Name = "Lukas" });
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        await PolicyAsync("Helsana", InsuranceKind.HealthBasic, 4800, lukas.Id);
        await PolicyAsync("Mobiliar", InsuranceKind.HouseholdLiability, 640);
        await PolicyAsync("AXA", InsuranceKind.Car, 1100);
        var vm = await LoadAsync();

        CoverRow Row(InsuranceKind kind) => vm.CoverRows.Single(r => r.Kind == kind);

        // Basic health insurance is per person: Anna has none recorded.
        Assert.Equal("Helsana · missing for Anna", Row(InsuranceKind.HealthBasic).Status);
        Assert.True(Row(InsuranceKind.HealthBasic).IsGap);
        // One household policy covers both contents and liability.
        Assert.True(Row(InsuranceKind.HouseholdContents).IsCovered);
        Assert.True(Row(InsuranceKind.PrivateLiability).IsCovered);
        Assert.Equal($"AXA · {Format.Money(1100)} a year", Row(InsuranceKind.Car).Status);
        Assert.Equal("Not added yet", Row(InsuranceKind.LegalProtection).Status);
        Assert.False(Row(InsuranceKind.LegalProtection).IsGap); // optional

        // Renting: building insurance doesn't apply. Hidden, and remembered.
        await vm.HideCoverCommand.ExecuteAsync(Row(InsuranceKind.Building));
        Assert.DoesNotContain(vm.CoverRows, r => r.Kind == InsuranceKind.Building);
        Assert.Equal("Doesn’t apply: Building", vm.HiddenCoverText);
        var again = await LoadAsync();
        Assert.Equal([InsuranceKind.Building], again.HiddenCoverRows.Select(r => r.Kind));
        await again.UnhideCoverCommand.ExecuteAsync(again.HiddenCoverRows[0]);
        Assert.Contains(again.CoverRows, r => r.Kind == InsuranceKind.Building);

        // "+" opens the form for that kind.
        vm.AddCoverCommand.Execute(Row(InsuranceKind.LegalProtection));
        Assert.True(vm.IsInsurancePanelOpen);
        Assert.Equal((int)InsuranceKind.LegalProtection, vm.PolicyKindIndex);
    }

    [Fact]
    public async Task OldPoliciesKeepTheirKind()
    {
        // Kinds are stored by name, so policies saved before the new kinds were added read back the same.
        await PolicyAsync("Mobiliar", InsuranceKind.HouseholdLiability, 640);
        await PolicyAsync("Generali", InsuranceKind.Building, 900);
        var vm = await LoadAsync();
        Assert.Equal([InsuranceKind.HouseholdLiability, InsuranceKind.Building], vm.InsuranceRows.Select(r => r.Policy.Kind).Order());
    }

    private async Task FileBillAsync(MainViewModel vm, string title, string sender, decimal amount)
    {
        var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(path, $"made-up bill {Guid.NewGuid()}");
        await vm.StartNewDocumentAsync(path);
        vm.DocumentKindIndex = (int)DocumentKind.Bill;
        vm.DocumentTitle = title;
        vm.DocumentSender = sender;
        vm.DocumentAmount = amount;
        vm.DocumentDueDate = Today.AddDays(20).ToDateTime(TimeOnly.MinValue);
        vm.DocumentCreateEntry = false;
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Null(vm.DocumentError);
    }

    [Fact]
    public async Task InsuranceAndTaxesShowTheirOpenBills()
    {
        await PolicyAsync("Helsana", InsuranceKind.HealthBasic, 4800);
        var vm = await LoadAsync();
        await FileBillAsync(vm, "Kostenbeteiligung Arztbesuch", "Helsana", 86.40m);
        await FileBillAsync(vm, "Steuerrechnung 2026", "Steuerverwaltung Basel-Stadt", 4500m);
        await FileBillAsync(vm, "Electricity Q4", "IWB", 190m);
        await FileBillAsync(vm, "Rechnung 2027", "Die Mobiliar", 312m); // a known insurer, no policy with them yet
        await FileBillAsync(vm, "Dinner", "Paxi Restaurant", 80m); // "Pax" inside another word isn't the insurer

        Assert.Equal(["Kostenbeteiligung Arztbesuch", "Rechnung 2027"], vm.InsuranceBillRows.Select(r => r.Title));
        Assert.Equal($"2 bills · {Format.Money(398.40m)}", vm.InsuranceBillsText);
        Assert.Equal(["Steuerrechnung 2026"], vm.TaxBillRows.Select(r => r.Title));
        // Open bills are listed once, above the papers.
        Assert.DoesNotContain(vm.InsuranceDocumentRows, r => r.Title == "Kostenbeteiligung Arztbesuch");

        // Paid: it leaves the bills and joins the papers.
        await vm.MarkDocumentPaidCommand.ExecuteAsync(vm.TaxBillRows[0].Document);
        Assert.False(vm.HasTaxBillRows);
        Assert.Contains(vm.TaxDocumentRows, r => r.Title == "Steuerrechnung 2026");
    }

    [Fact]
    public async Task ThePolicyDocumentChoiceSaysWhoseEachPaperIs()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var vm = await LoadAsync();
        await FileBillAsync(vm, "Prämie 2027", "Helsana", 412m);
        vm.Documents.Single().PersonId = anna.Id;

        vm.NewInsuranceCommand.Execute(null);
        // A premium bill from an insurer is offered too, not only papers filed as policies.
        var option = vm.PolicyDocumentOptions.Single(o => o.Document is not null);
        Assert.Equal("Helsana: Prämie 2027", option.Title);
        Assert.Equal($"Anna · {Format.Date(vm.Documents.Single().Date)}", option.Detail);
        Assert.False(vm.PolicyDocumentOptions[0].HasDetail); // "None"
    }

    [Fact]
    public async Task EndedPoliciesAndPremiumsAreListedApart()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Sanitas", Kind = InsuranceKind.HealthBasic, Premium = 350, PremiumRecurrence = Recurrence.Monthly,
            ContractEnd = today.AddMonths(-2), RenewsAutomatically = false,
        });
        await PolicyAsync("Helsana", InsuranceKind.HealthBasic, 412);
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = today.AddYears(-2), EndDate = today.AddMonths(-2), Description = "Sanitas", Category = Categories.HealthInsurance,
            Amount = 350, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        await _repository.AddTransactionAsync(new Transaction
        {
            Date = today.AddMonths(-2).AddDays(1), Description = "Helsana", Category = Categories.HealthInsurance,
            Amount = 412, Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();

        Assert.Equal(["Helsana"], vm.InsuranceRows.Select(r => r.Insurer));
        Assert.Equal(["Sanitas"], vm.EndedInsuranceRows.Select(r => r.Insurer));
        Assert.Equal("Show 1 ended policy", vm.EndedPoliciesText);

        var current = Assert.Single(vm.InsuranceEntryRows);
        Assert.Equal(("Helsana", $"Since {Format.Date(today.AddMonths(-2).AddDays(1))}"), (current.Description, current.PhaseText));
        var ended = Assert.Single(vm.EndedInsuranceEntryRows);
        Assert.Equal($"Ended {Format.Date(today.AddMonths(-2))}", ended.PhaseText);
        vm.ToggleEndedPremiumsCommand.Execute(null);
        Assert.Equal("Hide ended premiums", vm.EndedPremiumsText);
    }
}
