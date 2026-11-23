using Keystone.Data;
using Keystone.Localization;
using Keystone.Services;
using Keystone.ViewModels;
using SkiaSharp;

namespace Keystone.Tests;

/// <summary>Revolut statements come only as PDF; their bookings are read exactly, without the AI.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class PdfStatementTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-pdfstatement-{Guid.NewGuid():N}");

    public PdfStatementTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => TestFolder.Delete(_folder);

    /// <summary>A made-up statement in Revolut's layout: a USD part with nothing in it, then the CHF part.</summary>
    private static readonly string[] Statement =
    [
        "USD Statement",
        "Revolut Bank UAB",
        "Where your transactions are remitted",
        "$0.00 $0.00 $0.00 $0.00",
        "CHF Statement",
        "Revolut Bank UAB",
        "Balance summary",
        "Where your transactions are remitted",
        "500.00 CHF 186.30 CHF 250.00 CHF 563.70 CHF",
        "Account transactions from November 1, 2026 to November 23, 2026",
        "Date Description Money out Money in Balance",
        "Nov 1, 2026 Apple Pay top-up by *1234 250.00 CHF 750.00 CHF",
        "From: *1234",
        "Nov 2, 2026 Migros 79.40 CHF 670.60 CHF",
        "To: Migros Basel",
        "Card: 400000******0000",
        "Nov 3, 2026 To Anna Meier 105.10 CHF 565.50 CHF",
        "Fee: 2.16 CHF 102.94 CHF",
        "40,000.00 LKR",
        "Nov 4, 2026 Transfer to Revolut Digital Assets Europe Ltd 1.80 CHF 563.70 CHF",
        "Purchase of ETH",
        "IBAN",
        "BIC",
        "LT000000000000000000",
        "REVOLT21",
    ];

    [Fact]
    public void BookingsAreReadWithTheirDirectionFromTheBalance()
    {
        var statement = PdfStatementParser.Parse(string.Join("\n", Statement));

        Assert.Equal([250.00m, -79.40m, -105.10m, -1.80m], statement.Lines.Select(l => l.Amount));
        Assert.Equal("Migros", statement.Lines[1].Description);
        Assert.Equal("Transfer to Revolut Digital Assets Europe Ltd · Purchase of ETH", statement.Lines[3].Description);
        Assert.Equal(new DateOnly(2026, 11, 2), statement.Lines[1].Date);
        Assert.Equal((563.70m, new DateOnly(2026, 11, 23), "LT000000000000000000"),
            (statement.ClosingBalance!.Value, statement.ClosingDate!.Value, statement.Iban));
        // Each booking has its own reference, so importing the statement again finds them all.
        Assert.Equal(4, statement.Lines.Select(l => l.Reference).Distinct().Count());
    }

    [Fact]
    public void OtherBanksPdfsAreExplained()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PdfStatementParser.Parse("Kontoauszug\nUBS Switzerland AG"));
        Assert.StartsWith("Keystone reads PDF statements from Revolut.", error.Message);
        Assert.False(PdfStatementParser.IsStatement("A letter mentioning Revolut"));
    }

    [Fact]
    public async Task ADroppedRevolutPdfIsImportedLikeABankStatement()
    {
        // A real PDF with a text layer, as Revolut's are.
        var path = Path.Combine(_folder, "account-statement.pdf");
        using (var stream = new SKFileWStream(path))
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(595, 842);
            using var paint = new SKPaint { Color = SKColors.Black };
            using var font = new SKFont(SKTypeface.FromFamilyName("Helvetica"), 8);
            for (var i = 0; i < Statement.Length; i++)
                canvas.DrawText(Statement[i], 30, 30 + i * 14, SKTextAlign.Left, font, paint);
            document.EndPage();
            document.Close();
        }
        Assert.True(MainViewModel.IsBankStatement(path));

        var repository = new KeystoneRepository(Path.Combine(_folder, "test.db"));
        repository.Migrate();
        var vm = new MainViewModel(repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));

        await vm.ImportBankStatementAsync(path);

        Assert.Null(vm.ImportError);
        Assert.Equal(4, vm.ImportItems.Count);
        Assert.True(vm.ImportItems[0].IsIncome);
        await vm.AddImportedCommand.ExecuteAsync(null);
        Assert.Equal(4, (await repository.GetTransactionsAsync()).Count);

        // The same statement again: every booking is recognised and left unticked.
        await vm.ImportBankStatementAsync(path);
        Assert.All(vm.ImportItems, i => Assert.True(i.IsDuplicate));
    }

    [Fact]
    public async Task OnlyTheExpensesOrOnlyTheIncomeCanBeTicked()
    {
        var path = Path.Combine(_folder, "statement.pdf");
        WritePdf(path);
        var repository = new KeystoneRepository(Path.Combine(_folder, "select.db"));
        repository.Migrate();
        var vm = new MainViewModel(repository);
        await vm.LoadAsync();
        await vm.ChangeDocumentsFolderAsync(Path.Combine(_folder, "Documents"));
        await vm.ImportBankStatementAsync(path);

        Assert.Equal("Expenses only (3)", vm.ImportExpensesOnlyText);
        Assert.Equal("Income only (1)", vm.ImportIncomeOnlyText);

        vm.SelectImportedCommand.Execute("expenses");
        Assert.Equal([false, true, true, true], vm.ImportItems.Select(i => i.IsSelected));
        Assert.Equal($"3 of 4 ticked · {Format.Money(186.30m)} out · {Format.Money(0)} in", vm.ImportSelectionText);

        vm.SelectImportedCommand.Execute("income");
        Assert.Equal([true, false, false, false], vm.ImportItems.Select(i => i.IsSelected));

        vm.SelectImportedCommand.Execute("none");
        Assert.False(vm.CanAddImported);

        vm.SelectImportedCommand.Execute("expenses");
        await vm.AddImportedCommand.ExecuteAsync(null);
        var saved = await repository.GetTransactionsAsync();
        Assert.Equal(3, saved.Count);
        Assert.All(saved, t => Assert.Equal(Keystone.Models.TransactionType.Expense, t.Type));

        // Importing again: "All" leaves the bookings already added unticked.
        await vm.ImportBankStatementAsync(path);
        vm.SelectImportedCommand.Execute("all");
        Assert.Equal([true, false, false, false], vm.ImportItems.Select(i => i.IsSelected));
    }

    private static void WritePdf(string path)
    {
        using var stream = new SKFileWStream(path);
        using var document = SKDocument.CreatePdf(stream);
        var canvas = document.BeginPage(595, 842);
        using var paint = new SKPaint { Color = SKColors.Black };
        using var font = new SKFont(SKTypeface.FromFamilyName("Helvetica"), 8);
        for (var i = 0; i < Statement.Length; i++)
            canvas.DrawText(Statement[i], 30, 30 + i * 14, SKTextAlign.Left, font, paint);
        document.EndPage();
        document.Close();
    }

    [Fact]
    public void LongDocumentsLeaveTheModelRoomToAnswer()
    {
        var statement = new DocumentContent("statement.pdf", new string('x', 15_600), null, false);
        Assert.Equal(16_384, LocalAiClient.ContextSizeFor(statement));
        Assert.Equal(4_096, LocalAiClient.ContextSizeFor(new DocumentContent("letter.pdf", new string('x', 2_000), null, false)));
    }
}
