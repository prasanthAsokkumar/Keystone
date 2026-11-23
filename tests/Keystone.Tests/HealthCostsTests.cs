using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Health costs: premiums and cost-sharing bills (Leistungsabrechnungen) per person and calendar year.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class HealthCostsTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-health-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public HealthCostsTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private static HouseholdDocument Paper(string title, string sender, decimal? amount, DocumentKind kind = DocumentKind.Bill) =>
        new() { Title = title, Sender = sender, Amount = amount, Kind = kind, Date = Today };

    [Fact]
    public void CostSharingBillsAreRecognised()
    {
        var policies = new List<InsurancePolicy> { new() { Insurer = "Musterkasse", Kind = InsuranceKind.HealthBasic } };

        Assert.True(HealthCosts.IsCostSharing(Paper("Leistungsabrechnung", "Helsana Versicherungen AG", 86.40m), policies));
        // A bill from a known or your own health insurer that isn't for a premium.
        Assert.True(HealthCosts.IsCostSharing(Paper("Rechnung Arztbesuch", "Musterkasse", 120m), policies));
        Assert.True(HealthCosts.IsCostSharing(Paper("Abrechnung 4711", "Sanitas", 45m), policies));
        Assert.False(HealthCosts.IsCostSharing(Paper("Prämienrechnung Januar", "Helsana", 412m), policies));
        Assert.False(HealthCosts.IsCostSharing(Paper("Electricity Q3", "IWB", 190m), policies));
        Assert.False(HealthCosts.IsCostSharing(Paper("Leistungsabrechnung", "Helsana", null), policies)); // nothing to pay
        // "Pax" inside another word is not the insurer list matching.
        Assert.False(HealthCosts.IsCostSharing(Paper("Dinner", "Swicarest Bar", 80m), policies));
    }

    [Fact]
    public async Task TheYearsCostsArePerPersonWithTheMaximum()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna", BirthYear = Today.Year - 38 });
        var sofia = await _repository.AddPersonAsync(new Person { Name = "Sofia", BirthYear = Today.Year - 6 });
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Helsana", Kind = InsuranceKind.HealthBasic, PersonId = anna.Id, Premium = 400, PremiumRecurrence = Recurrence.Monthly,
            Deductible = 1500,
        });
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Helsana", Kind = InsuranceKind.HealthBasic, PersonId = sofia.Id, Premium = 110, PremiumRecurrence = Recurrence.Monthly,
            Deductible = 0,
        });
        foreach (var (person, amount) in new[] { (anna.Id, 400m), (sofia.Id, 110m) })
            await _repository.AddTransactionAsync(new Transaction
            {
                Date = new DateOnly(Today.Year, 1, 1), Description = "Helsana", Category = Categories.HealthInsurance, Amount = amount,
                Type = TransactionType.Expense, Recurrence = Recurrence.Monthly, PersonId = person,
            });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Leistungsabrechnung", Sender = "Helsana", Kind = DocumentKind.Bill, Amount = 380m, PersonId = anna.Id,
            Date = new DateOnly(Today.Year, 1, 20), PaidOn = new DateOnly(Today.Year, 2, 1), RelativePath = "a.pdf",
        });
        await _repository.AddDocumentAsync(new HouseholdDocument
        {
            Title = "Leistungsabrechnung", Sender = "Helsana", Kind = DocumentKind.Bill, Amount = 400m, PersonId = sofia.Id,
            Date = new DateOnly(Today.Year, 1, 25), RelativePath = "b.pdf",
        });
        var vm = new MainViewModel(_repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));

        var rows = vm.HealthCostRows.ToDictionary(r => r.PersonName);
        Assert.Equal(["Anna", "Sofia"], rows.Keys.Order());
        Assert.Equal((400m * 12, 380m), (rows["Anna"].Line.PremiumsYear, rows["Anna"].Line.CostSharing));
        // Adult: deductible 1'500 + 10% share up to 700.
        Assert.Equal($"{Format.Money(380)} of max. {Format.Money(2200)}", rows["Anna"].MaxText);
        // Child: deductible 0 + share up to 350; 400 billed is past it.
        Assert.Equal("Max. reached: no more cost sharing this year", rows["Sofia"].MaxText);
        Assert.Equal($"{Format.Money(400)} still to pay", rows["Sofia"].CostSharingDetail);
        Assert.Equal($"{Format.Money(400 * 12 + 110 * 12 + 780)} in all · {Format.Money(510 * 12)} premiums · {Format.Money(780)} cost sharing",
            vm.HealthSummary);
        Assert.Equal(2, vm.HealthStatementRows.Count);

        // Last year: nothing.
        vm.PreviousHealthYearCommand.Execute(null);
        Assert.Empty(vm.HealthCostRows);
        Assert.False(vm.HasHealthStatements);
    }
}
