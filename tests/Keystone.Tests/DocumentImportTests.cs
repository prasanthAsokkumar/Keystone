using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

public class DocumentImportTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    [Fact]
    public void Parse_ReadsEntriesFromModelAnswer()
    {
        const string json = """
            {"entries":[
              {"description":"Helsana health insurance","amount":2400.5,"type":"expense","date":"2027-01-01","frequency":"yearly","category":"Health insurance"},
              {"description":"Salary","amount":"7'250.00","type":"income","date":"2026-11-25","frequency":"monthly","category":"Salary"}
            ]}
            """;

        var entries = EntryExtraction.Parse(json, Today);

        Assert.Equal(
            [
                new ExtractedEntry("Helsana health insurance", 2400.5m, false, new DateOnly(2027, 1, 1), Recurrence.Yearly, "Health insurance"),
                new ExtractedEntry("Salary", 7250m, true, new DateOnly(2026, 11, 25), Recurrence.Monthly, "Salary"),
            ],
            entries);
    }

    [Fact]
    public void Parse_SkipsUnusableEntriesAndDefaultsBadValues()
    {
        const string json = """
            {"entries":[
              {"description":"","amount":10,"type":"expense","date":"2026-11-01","frequency":"once","category":""},
              {"description":"Total","amount":0,"type":"expense","date":"2026-11-01","frequency":"once","category":""},
              {"description":"Refund","amount":-35.456,"type":"expense","date":"20.11.2026","frequency":"weekly","category":"Other"}
            ]}
            """;

        var entry = Assert.Single(EntryExtraction.Parse(json, Today));

        Assert.Equal(new ExtractedEntry("Refund", 35.46m, false, Today, Recurrence.OneTime, "Other"), entry);
    }

    [Fact]
    public void Parse_RejectsAnswerThatIsNotJson() =>
        Assert.Throws<InvalidOperationException>(() => EntryExtraction.Parse("Sorry, I can't help.", Today));

    [Fact]
    public void Shorten_CutsLongTextAndSaysSo()
    {
        var shortened = DocumentReader.Shorten("statement.csv", new string('x', DocumentReader.MaxTextLength + 5));
        var kept = DocumentReader.Shorten("bill.txt", "Total 120.00");

        Assert.True(shortened.WasShortened);
        Assert.Equal(DocumentReader.MaxTextLength, shortened.Text!.Length);
        Assert.False(kept.WasShortened);
    }

    [Fact]
    public async Task ReadAsync_ReadsCsvAsText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runway-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "Date;Text;Amount\n2026-11-03;Coop;-54.20");
        try
        {
            var document = await DocumentReader.ReadAsync(path);

            Assert.False(document.IsImage);
            Assert.Contains("Coop", document.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("bill.PDF", true)]
    [InlineData("photo.heic", true)]
    [InlineData("statement.csv", true)]
    [InlineData("archive.zip", false)]
    public void IsSupported_ChecksExtension(string file, bool expected) =>
        Assert.Equal(expected, DocumentReader.IsSupported(file));

    [Fact]
    public void ImportItem_FlagsEntriesAlreadyRecorded()
    {
        Transaction[] existing =
        [
            new() { Description = "Coop", Amount = 54.20m, Type = TransactionType.Expense, Date = new DateOnly(2026, 11, 3) },
            new() { Description = "Rent", Amount = 2100m, Type = TransactionType.Expense, Date = new DateOnly(2025, 1, 1), Recurrence = Recurrence.Monthly },
        ];

        var sameDay = new ExtractedEntry("COOP Basel", 54.20m, false, new DateOnly(2026, 11, 3), Recurrence.OneTime, "");
        var sameBill = new ExtractedEntry("rent", 2100m, false, new DateOnly(2026, 12, 1), Recurrence.Monthly, "");
        var refund = new ExtractedEntry("Coop", 54.20m, true, new DateOnly(2026, 11, 3), Recurrence.OneTime, "");

        Assert.True(ImportItem.IsAlreadyRecorded(sameDay, existing));
        Assert.True(ImportItem.IsAlreadyRecorded(sameBill, existing));
        Assert.False(ImportItem.IsAlreadyRecorded(refund, existing));
        Assert.False(new ImportItem(sameDay, isDuplicate: true).IsSelected);
    }

    [Theory]
    [InlineData(2_000, 4_096)]
    [InlineData(12_000, 8_192)]
    [InlineData(40_000, 16_384)]
    [InlineData(DocumentReader.MaxTextLength, 32_768)]
    public void ContextSize_GrowsWithDocumentButStaysSmallForLetters(int characters, int expected) =>
        Assert.Equal(expected, LocalAiClient.ContextSizeFor(new DocumentContent("a.pdf", new string('x', characters), null, false)));

    [Fact]
    public void ContextSize_FitsAnImage() =>
        Assert.Equal(8_192, LocalAiClient.ContextSizeFor(new DocumentContent("a.jpg", null, "AAAA", false)));
}
