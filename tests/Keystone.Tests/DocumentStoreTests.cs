using Keystone.Models;
using Keystone.Services;

namespace Keystone.Tests;

public sealed class DocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"runway-docs-{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly DocumentStore _store;

    public DocumentStoreTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "scan.PDF");
        File.WriteAllText(_source, "pdf");
        _store = new DocumentStore(Path.Combine(_root, "KeystoneApp"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static HouseholdDocument Bill(string title = "Health insurance premium") => new()
    {
        Title = title, Kind = DocumentKind.Bill, Date = new DateOnly(2026, 11, 15),
    };

    [Fact]
    public void PlannedPath_FilesByPersonYearAndType()
    {
        Assert.Equal("Lukas/2026/Bills/2026-11-15 Health insurance premium.pdf",
            DocumentStore.PlannedPath(Bill(), "Lukas", ".PDF"));
        Assert.Equal("Household/2026/Notices/2026-11-15 Rent increase.pdf",
            DocumentStore.PlannedPath(new HouseholdDocument
            {
                Title = "Rent increase", Kind = DocumentKind.Notice, Date = new DateOnly(2026, 11, 15),
            }, null, ".pdf"));
    }

    [Theory]
    [InlineData("Tax 2025: final/ok?", "Tax 2025- final-ok-")]
    [InlineData("  ..  ", "Untitled")]
    [InlineData("A   lot   of  spaces", "A lot of spaces")]
    public void SafeName_RemovesCharactersFileSystemsReject(string name, string expected) =>
        Assert.Equal(expected, DocumentStore.SafeName(name));

    [Fact]
    public void Add_CopiesFileAndNumbersDuplicates()
    {
        var first = _store.Add(_source, Bill(), "Lukas");
        var second = _store.Add(_source, Bill(), "Lukas");

        Assert.Equal("Lukas/2026/Bills/2026-11-15 Health insurance premium.pdf", first);
        Assert.Equal("Lukas/2026/Bills/2026-11-15 Health insurance premium (2).pdf", second);
        Assert.True(File.Exists(_store.FullPath(first)));
        Assert.True(File.Exists(_source)); // The original is left alone.
    }

    [Fact]
    public void Refile_MovesFileWhenDetailsChangeAndRemovesEmptyFolders()
    {
        var document = Bill();
        document.RelativePath = _store.Add(_source, document, "Lukas");

        document.Kind = DocumentKind.Letter;
        document.Title = "Premium change";
        var moved = _store.Refile(document, "Sofia");

        Assert.Equal("Sofia/2026/Letters/2026-11-15 Premium change.pdf", moved);
        Assert.True(File.Exists(_store.FullPath(moved)));
        Assert.False(Directory.Exists(Path.Combine(_store.RootFolder, "Lukas")));
    }

    [Fact]
    public void Refile_LeavesNumberedFileWhereItIs()
    {
        _store.Add(_source, Bill(), "Lukas");
        var document = Bill();
        document.RelativePath = _store.Add(_source, document, "Lukas");

        Assert.Equal(document.RelativePath, _store.Refile(document, "Lukas"));
    }

    [Fact]
    public void Delete_MovesFileToDeletedFolder()
    {
        var document = Bill();
        document.RelativePath = _store.Add(_source, document, null);

        _store.Delete(document);

        Assert.False(_store.Exists(document));
        Assert.True(File.Exists(Path.Combine(_store.RootFolder, "Deleted", "2026-11-15 Health insurance premium.pdf")));
    }

    [Fact]
    public void DocumentDetails_ParsesModelAnswer()
    {
        const string json = """
            {"type":"bill","title":"Health insurance premium 2027","sender":"Helsana","date":"2026-11-15",
             "amount":"1'272.35","due_date":"2026-12-01"}
            """;

        Assert.Equal(
            new DocumentDetails(DocumentKind.Bill, "Health insurance premium 2027", "Helsana", new DateOnly(2026, 11, 15),
                1272.35m, new DateOnly(2026, 12, 1)),
            DocumentDetailsExtraction.Parse(json));
    }

    [Fact]
    public void DocumentDetails_LeavesUnknownValuesEmpty()
    {
        var details = DocumentDetailsExtraction.Parse(
            """{"type":"memo","title":"","sender":"","date":"soon","amount":0,"due_date":""}""");

        Assert.Equal(new DocumentDetails(null, "", "", null, null, null), details);
    }

    [Fact]
    public void OpenBill_IsOverdueAfterDueDateUntilPaid()
    {
        var bill = Bill();
        bill.Amount = 100;
        bill.DueDate = new DateOnly(2026, 12, 1);

        Assert.False(bill.IsOverdue(new DateOnly(2026, 12, 1)));
        Assert.True(bill.IsOverdue(new DateOnly(2026, 12, 2)));
        bill.PaidOn = new DateOnly(2026, 12, 3);
        Assert.False(bill.IsOverdue(new DateOnly(2026, 12, 4)));
    }
}
