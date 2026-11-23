using Keystone.Data;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Filing the same paper twice, and papers that also belong under Insurance or Contracts &amp; dates.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class DuplicatesAndFollowUpTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-dupes-{Guid.NewGuid():N}");
    private readonly KeystoneRepository _repository;

    public DuplicatesAndFollowUpTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
        _repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_folder);
    }

    private async Task<MainViewModel> LoadAsync()
    {
        var vm = new MainViewModel(_repository) { ScanQrBill = _ => null };
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        return vm;
    }

    private string File(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static async Task FileAsync(MainViewModel vm, string path, string title, DocumentKind kind = DocumentKind.Letter,
        string sender = "", decimal? amount = null)
    {
        await vm.StartNewDocumentAsync(path);
        vm.DocumentKindIndex = (int)kind;
        vm.DocumentTitle = title;
        vm.DocumentSender = sender;
        vm.DocumentAmount = amount;
        await vm.SaveDocumentCommand.ExecuteAsync(null);
    }

    private async Task<HouseholdDocument> AddDocumentAsync(HouseholdDocument document)
    {
        await _repository.AddDocumentAsync(document);
        return document;
    }

    // ── Duplicates ──

    [Fact]
    public void TheSameFileOrTheSameBillIsFound()
    {
        var filed = new[]
        {
            new HouseholdDocument { Title = "Dentist", FileHash = "abc", Sender = "Zahnarzt Muster", Amount = 240, Date = new DateOnly(2026, 11, 2) },
            new HouseholdDocument { Title = "Electricity", PaymentReference = "21 00000 00003 13947 14300 09017", Date = new DateOnly(2026, 10, 1) },
        };

        Assert.Equal(DuplicateMatch.SameFile, DuplicateFinder.Find(filed, "abc", "", null, null, "")!.Match);
        Assert.Equal("Electricity", DuplicateFinder.Find(filed, "zzz", "IWB", 99, null, "210000000003139471430009017")!.Document.Title);
        // A scan of the same paper: another file, but same sender (written longer), amount and date.
        Assert.Equal(DuplicateMatch.Similar,
            DuplicateFinder.Find(filed, "zzz", "Zahnarzt Muster AG", 240, new DateOnly(2026, 11, 2), "")!.Match);
        Assert.Null(DuplicateFinder.Find(filed, "zzz", "Zahnarzt Muster", 240, new DateOnly(2026, 11, 3), ""));
        Assert.Null(DuplicateFinder.Find(filed, "", "", null, null, "1234"));
    }

    [Fact]
    public async Task FilingTheSameFileAgainWarnsAndCanBeSkipped()
    {
        var vm = await LoadAsync();
        var letter = File("letter.pdf", "made-up letter from the Steuerverwaltung");
        await FileAsync(vm, letter, "Tax assessment 2025");
        Assert.NotEqual("", Assert.Single(vm.Documents).FileHash);

        await vm.StartNewDocumentAsync(letter);

        Assert.True(vm.HasDocumentDuplicate);
        Assert.Equal($"You already filed this file on {Format.Date(Today)} as “Tax assessment 2025”.", vm.DocumentDuplicateText);
        Assert.Equal("File anyway", vm.DocumentSubmitText);
        vm.SkipDuplicateDocumentCommand.Execute(null);
        Assert.False(vm.IsDocumentPanelOpen);
        Assert.Single(await _repository.GetDocumentsAsync());

        // "Show it" opens the one already filed.
        await vm.StartNewDocumentAsync(letter);
        vm.ShowDuplicateDocumentCommand.Execute(null);
        Assert.Equal("Tax assessment 2025", vm.EditingDocument!.Title);
        Assert.False(vm.HasDocumentDuplicate);
    }

    [Fact]
    public async Task ASecondScanOfABillIsntAddedToExpensesAgain()
    {
        var vm = await LoadAsync();
        await FileAsync(vm, File("scan-1.pdf", "first scan"), "Dentist", DocumentKind.Bill, "Zahnarzt Muster", 240);
        Assert.Single(vm.Transactions);

        await vm.StartNewDocumentAsync(File("scan-2.pdf", "second scan, other pixels"));
        vm.DocumentKindIndex = (int)DocumentKind.Bill;
        vm.DocumentSender = "Zahnarzt Muster";
        vm.DocumentAmount = 240;
        vm.DocumentDate = DateTime.Today;
        Assert.False(vm.HasDocumentDuplicate); // only checked once the reader has filled the form…

        // …which the QR code and the AI do; here the same check runs as they would.
        var check = typeof(MainViewModel).GetMethod("RefreshDocumentDuplicate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        check.Invoke(vm, null);
        Assert.True(vm.HasDocumentDuplicate);
        Assert.False(vm.DocumentCreateEntry);
    }

    [Fact]
    public async Task BulkImportUnticksFilesAlreadyFiledAndClearsThemFromTheInbox()
    {
        var vm = await LoadAsync();
        var filedBefore = File("insurance.pdf", "made-up policy");
        await FileAsync(vm, filedBefore, "Household insurance");

        Directory.CreateDirectory(vm.InboxFolder);
        var again = Path.Combine(vm.InboxFolder, "scan-again.pdf");
        System.IO.File.Copy(filedBefore, again);
        var fresh = Path.Combine(vm.InboxFolder, "scan-new.pdf");
        System.IO.File.WriteAllText(fresh, "made-up new letter");
        var twice = File("twice.pdf", "made-up new letter"); // same as the fresh scan

        await vm.StartBulkDocumentsAsync([again, fresh], fromInbox: true);
        await vm.StartBulkDocumentsAsync([twice]);

        var copy = vm.BulkItems.Single(i => i.FileName == "scan-again.pdf");
        Assert.False(copy.IsSelected);
        Assert.StartsWith("You already filed this file", copy.DuplicateText);
        Assert.True(vm.BulkItems.Single(i => i.FileName == "scan-new.pdf").IsSelected);
        Assert.Equal("This file is in the list twice.", vm.BulkItems.Single(i => i.FileName == "twice.pdf").DuplicateText);
        Assert.Equal("Save 1 document", vm.BulkSaveText);

        await vm.SaveBulkCommand.ExecuteAsync(null);

        Assert.Equal(2, (await _repository.GetDocumentsAsync()).Count);
        Assert.False(System.IO.File.Exists(again)); // moved to Deleted, not offered again
        Assert.True(System.IO.File.Exists(Path.Combine(vm.DocumentsFolder, "Deleted", "scan-again.pdf")));
        Assert.Contains("taken out of the inbox", vm.NoticeText);
        Assert.Equal("twice.pdf", Assert.Single(vm.BulkItems).FileName); // not from the inbox: stays for a decision
    }

    [Fact]
    public async Task OldDocumentsGetTheirFingerprintInTheBackground()
    {
        var vm = await LoadAsync();
        await FileAsync(vm, File("old.pdf", "made-up old letter"), "Old letter");
        var document = Assert.Single(await _repository.GetDocumentsAsync());
        await _repository.SetDocumentHashAsync(document.Id, "");

        var again = await LoadAsync();
        for (var i = 0; i < 100 && (await _repository.GetDocumentsAsync()).Single().FileHash == ""; i++)
            await Task.Delay(20);

        Assert.Equal(DuplicateFinder.Hash(Path.Combine(again.DocumentsFolder, document.RelativePath)),
            (await _repository.GetDocumentsAsync()).Single().FileHash);
    }

    [Fact]
    public void EntriesFromADocumentReadTwiceAreMarked()
    {
        var existing = new[]
        {
            new Transaction { Date = new DateOnly(2026, 1, 1), Description = "Helsana premium", Amount = 412.35m,
                Type = TransactionType.Expense, Recurrence = Recurrence.Monthly },
            new Transaction { Date = new DateOnly(2026, 11, 20), Description = "Pharmacy", Amount = 38.20m, Type = TransactionType.Expense },
        };
        ExtractedEntry Entry(string description, decimal amount, DateOnly date, Recurrence recurrence = Recurrence.OneTime) =>
            new(description, amount, false, date, recurrence, "Other");

        Assert.True(ImportItem.IsAlreadyRecorded(Entry("Health insurance", 412.35m, new DateOnly(2027, 1, 1), Recurrence.Monthly), existing));
        Assert.True(ImportItem.IsAlreadyRecorded(Entry("Health insurance December", 412.35m, new DateOnly(2026, 12, 2)), existing));
        Assert.True(ImportItem.IsAlreadyRecorded(Entry("Muster Apotheke", 38.20m, new DateOnly(2026, 11, 21)), existing));
        Assert.False(ImportItem.IsAlreadyRecorded(Entry("Muster Apotheke", 38.20m, new DateOnly(2026, 12, 20)), existing));
        Assert.False(ImportItem.IsAlreadyRecorded(Entry("Gym", 79m, new DateOnly(2026, 12, 1), Recurrence.Monthly), existing));
    }

    // ── Insurance and Contracts & dates ──

    [Fact]
    public void TheAiSaysWhatElseADocumentIs()
    {
        const string answer = """
            {"type":"policy","title":"Health insurance policy 2027","sender":"Helsana","date":"2026-11-01","amount":0,"due_date":"",
             "record":"insurance","record_kind":"health_basic","reference":"123.456.789","cost":412.35,"frequency":"monthly",
             "end_date":"2027-12-31","notice_months":0,"item":"","person":""}
            """;

        var details = DocumentDetailsExtraction.Parse(answer);

        Assert.Equal(new FollowUp(FollowUpKind.Insurance, "HealthBasic", "123.456.789", 412.35m, Recurrence.Monthly,
            new DateOnly(2027, 12, 31), null, ""), details.FollowUp);
        Assert.Null(DocumentDetailsExtraction.Parse(answer.Replace("\"insurance\"", "\"none\"")).FollowUp);
        Assert.Equal("DrivingLicence",
            DocumentDetailsExtraction.Parse(answer.Replace("\"insurance\"", "\"date\"").Replace("health_basic", "driving_licence")).FollowUp!.Subkind);
    }

    [Fact]
    public async Task AFiledPolicyIsSavedUnderInsuranceWithItsPremium()
    {
        var vm = await LoadAsync();
        await vm.StartNewDocumentAsync(File("police.pdf", "made-up policy"));
        vm.DocumentKindIndex = (int)DocumentKind.Letter;
        vm.DocumentTitle = "Household insurance policy";
        vm.DocumentSender = "Die Mobiliar";

        // What the local AI read from the paper.
        vm.SetDocumentFollowUpForTest(new FollowUp(FollowUpKind.Insurance, "HouseholdLiability", "H-55.123", 640m, Recurrence.Yearly,
            Today.AddMonths(2)));
        Assert.True(vm.HasDocumentFollowUp);
        Assert.StartsWith("Saved under Insurance", vm.DocumentFollowUpNote);
        await vm.SaveDocumentCommand.ExecuteAsync(null);

        // No form to fill in: the policy is there, with its premium as a yearly expense from the next term on.
        Assert.Equal(AppPage.Insurance, vm.CurrentPage);
        Assert.False(vm.IsInsurancePanelOpen);
        var policy = Assert.Single(await _repository.GetInsurancePoliciesAsync());
        Assert.Equal(("Die Mobiliar", InsuranceKind.HouseholdLiability, 640m, vm.Documents.Single().Id),
            (policy.Insurer, policy.Kind, policy.Premium, policy.DocumentId!.Value));
        var premium = vm.Transactions.Single();
        var start = MainViewModel.PremiumStart(Recurrence.Yearly, Today.AddMonths(2), Today);
        Assert.Equal((640m, Recurrence.Yearly, Categories.OtherInsurance, start), (premium.Amount, premium.Recurrence, premium.Category, premium.Date));
        Assert.Equal($"Added Die Mobiliar to Insurance · premium {Format.Money(640m)} / year in your expenses from {Format.Date(start)}", vm.NoticeText);
        // The letter is now one of the insurance papers.
        Assert.Contains(vm.InsuranceDocumentRows, r => r.Title == "Household insurance policy");

        // Undo takes it all back; the paper stays filed.
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Empty(await _repository.GetInsurancePoliciesAsync());
        Assert.Empty(vm.Transactions);
        Assert.Single(vm.Documents);

        // Unticked, the document is just filed.
        await vm.StartNewDocumentAsync(File("contract.pdf", "made-up contract"));
        vm.DocumentKindIndex = (int)DocumentKind.Contract;
        vm.DocumentTitle = "Mobile contract";
        vm.DocumentAddFollowUp = false;
        await vm.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Equal(AppPage.Documents, vm.CurrentPage);
        Assert.False(vm.IsContractPanelOpen);
    }

    [Fact]
    public async Task ANewPremiumReplacesTheOldOneFromTheNewTerm()
    {
        var termEnd = Today.AddDays(40);
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Helsana", Kind = InsuranceKind.HealthBasic, PolicyNumber = "123.456.789", Premium = 380,
            PremiumRecurrence = Recurrence.Monthly, ContractEnd = termEnd, NoticeMonths = 1,
        });
        // The premium was entered by hand, under another name.
        var old = await _repository.AddTransactionAsync(new Transaction
        {
            Date = Today.AddMonths(-10), Description = "Krankenkasse Lukas", Category = Categories.HealthInsurance, Amount = 380,
            Type = TransactionType.Expense, Recurrence = Recurrence.Monthly,
        });
        var vm = await LoadAsync();
        var letter = await AddDocumentAsync(new HouseholdDocument { Title = "Prämien nächstes Jahr", Sender = "Helsana Versicherungen AG", Date = Today });
        var newTermEnd = termEnd.AddYears(1);

        var (text, undo) = await vm.SavePolicyFromDocumentAsync(letter,
            new FollowUp(FollowUpKind.Insurance, "HealthBasic", "123 456 789", 412.35m, Recurrence.Monthly, newTermEnd));

        // From the new term's first day the new premium counts, the old one until the day before.
        var newStart = termEnd.AddDays(1);
        Assert.Equal(newStart.AddDays(-1), vm.Transactions.Single(t => t.Id == old.Id).EndDate);
        var added = vm.Transactions.Single(t => t.Id != old.Id);
        Assert.Equal((412.35m, newStart, "Krankenkasse Lukas"), (added.Amount, added.Date, added.Description));
        Assert.StartsWith("Updated Helsana in Insurance · new premium", text);

        // Filing the same letter again changes nothing more.
        var (again, _) = await vm.SavePolicyFromDocumentAsync(letter,
            new FollowUp(FollowUpKind.Insurance, "HealthBasic", "123 456 789", 412.35m, Recurrence.Monthly, newTermEnd));
        Assert.Equal("Updated Helsana in Insurance · premium already in your expenses", again);
        Assert.Equal(2, vm.Transactions.Count);

        // On the Insurance page the old premium says until when, the new one from when.
        var rows = vm.InsuranceEntryRows.ToDictionary(r => r.Transaction.Id);
        Assert.Equal($"Until {Format.Date(newStart.AddDays(-1))}", rows[old.Id].PhaseText);
        Assert.True(rows[old.Id].IsEnding);
        Assert.Equal($"From {Format.Date(newStart)}", rows[added.Id].PhaseText);
        Assert.Equal([old.Id, added.Id], vm.InsuranceEntryRows.Select(r => r.Transaction.Id)); // current first
        // Only what is paid now counts a month: not the old and the new premium together.
        Assert.Equal($"{Format.Money(380)} a month", vm.InsuranceEntriesTotal);

        await undo();
        Assert.Null(vm.Transactions.Single(t => t.Id == old.Id).EndDate);
    }

    [Fact]
    public void APremiumCountsFromTheNextPayment()
    {
        var today = new DateOnly(2026, 11, 23);
        // A new term that hasn't begun: from its first day.
        Assert.Equal(new DateOnly(2027, 1, 1), MainViewModel.PremiumStart(Recurrence.Monthly, new DateOnly(2027, 12, 31), today));
        // The current term: the next payment after today.
        Assert.Equal(new DateOnly(2026, 12, 1), MainViewModel.PremiumStart(Recurrence.Monthly, new DateOnly(2026, 12, 31), today));
        Assert.Equal(new DateOnly(2027, 1, 1), MainViewModel.PremiumStart(Recurrence.Yearly, new DateOnly(2026, 12, 31), today));
        Assert.Equal(new DateOnly(2027, 1, 1), MainViewModel.PremiumStart(Recurrence.Quarterly, new DateOnly(2026, 12, 31), today));
        // No term end known: from next month.
        Assert.Equal(new DateOnly(2026, 12, 1), MainViewModel.PremiumStart(Recurrence.Yearly, null, today));
    }

    [Fact]
    public async Task ANewPolicyLetterUpdatesThePolicyYouHave()
    {
        await _repository.SaveInsurancePolicyAsync(new InsurancePolicy
        {
            Insurer = "Helsana", Kind = InsuranceKind.HealthBasic, PolicyNumber = "123.456.789", Premium = 380,
            PremiumRecurrence = Recurrence.Monthly, ContractEnd = new DateOnly(2026, 12, 31), NoticeMonths = 1,
        });
        var vm = await LoadAsync();
        var document = await AddDocumentAsync(new HouseholdDocument { Title = "Policy 2027", Sender = "Helsana Versicherungen AG", Date = Today });
        var followUp = new FollowUp(FollowUpKind.Insurance, "HealthBasic", "123 456 789", 412.35m, Recurrence.Monthly, new DateOnly(2027, 12, 31));

        Assert.StartsWith("Also update “Helsana” in Insurance", vm.FollowUpText(document, followUp));
        Assert.Equal("Helsana", await vm.ApplyFollowUpAsync(document, followUp));

        var policy = Assert.Single(await _repository.GetInsurancePoliciesAsync());
        Assert.Equal((412.35m, new DateOnly(2027, 12, 31), 1, document.Id), (policy.Premium, policy.ContractEnd!.Value, policy.NoticeMonths, policy.DocumentId!.Value));
        Assert.Equal("Helsana", policy.Insurer);
    }

    [Fact]
    public async Task ContractsDatesAndWarrantiesAreAddedFromPapers()
    {
        var lukas = await _repository.AddPersonAsync(new Person { Name = "Lukas" });
        var vm = await LoadAsync();

        var mobile = await AddDocumentAsync(new HouseholdDocument { Title = "Mobile subscription", Sender = "Swisscom", Date = Today });
        Assert.Equal("Swisscom", await vm.ApplyFollowUpAsync(mobile,
            new FollowUp(FollowUpKind.Contract, "Mobile", "K-778", 65m, Recurrence.Monthly, Today.AddMonths(14), 2)));
        var contract = Assert.Single(await _repository.GetAllAsync<Contract>());
        Assert.Equal((ContractKind.Mobile, 65m, 2, mobile.Id), (contract.Kind, contract.Cost, contract.NoticeMonths, contract.DocumentId!.Value));

        var passport = await AddDocumentAsync(new HouseholdDocument { Title = "Passport", PersonId = lukas.Id, Date = Today });
        Assert.Null(await vm.ApplyFollowUpAsync(passport, new FollowUp(FollowUpKind.Date, "Passport"))); // no date: nothing to remind of
        Assert.Equal("Passport", await vm.ApplyFollowUpAsync(passport, new FollowUp(FollowUpKind.Date, "Passport", EndDate: new DateOnly(2031, 5, 3))));
        // A new passport later replaces the old date rather than adding a second one.
        await vm.ApplyFollowUpAsync(passport, new FollowUp(FollowUpKind.Date, "Passport", EndDate: new DateOnly(2036, 5, 3)));
        var date = Assert.Single(await _repository.GetAllAsync<ImportantDate>());
        Assert.Equal((new DateOnly(2036, 5, 3), lukas.Id, 180), (date.Date, date.PersonId!.Value, date.RemindDaysBefore));

        var receipt = await AddDocumentAsync(new HouseholdDocument { Title = "Dishwasher", Sender = "Fust", Date = new DateOnly(2026, 11, 20), Amount = 899 });
        Assert.Equal("Bosch Serie 4", await vm.ApplyFollowUpAsync(receipt, new FollowUp(FollowUpKind.Warranty, Item: "Bosch Serie 4")));
        var warranty = Assert.Single(await _repository.GetAllAsync<Warranty>());
        Assert.Equal((899m, new DateOnly(2028, 11, 19), "Fust"), (warranty.Price!.Value, warranty.ExpiresOn, warranty.Store));
    }

    // ── Saving from several places at once ──

    [Fact]
    public async Task SavesFromTheBackgroundDontLockEachOtherOut()
    {
        var saves = Enumerable.Range(1, 40).Select(async i =>
        {
            await Task.Yield();
            var document = new HouseholdDocument { Title = $"Letter {i}", Date = Today, AddedAt = DateTime.Now };
            await _repository.AddDocumentAsync(document);
            await _repository.SaveDocumentTextAsync(document.Id, $"text {i}");
            await _repository.SetDocumentHashAsync(document.Id, $"hash {i}");
        });

        await Task.WhenAll(saves.Select(s => Task.Run(() => s)));

        Assert.Equal(40, (await _repository.GetDocumentsAsync()).Count);
        Assert.Equal(40, (await _repository.GetDocumentTextsAsync()).Count);
    }
}
