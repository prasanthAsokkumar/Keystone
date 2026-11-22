using Keystone.Services;
using Keystone.Services.Mac;
using SkiaSharp;

namespace Keystone.Tests;

/// <summary>
/// Scans from phone apps (e.g. OneDrive) are PDFs holding only pictures of the pages. Their text is read with
/// macOS text recognition. These tests draw a made-up letter as a picture, like a scan, and read it back.
/// </summary>
[Collection(nameof(LanguageCollection))]
public sealed class ScanTextTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"keystone-scan-{Guid.NewGuid():N}");

    public ScanTextTests()
    {
        Keystone.Localization.Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => TestFolder.Delete(_folder);

    private static readonly string[] Letter =
    [
        "Muster Apotheke Basel",
        "Rechnung 48213",
        "Zahlbar bis 30.12.2026",
        "Total CHF 87.40",
    ];

    /// <summary>A photo of a page: black text on white, no text layer anywhere.</summary>
    private static SKImage DrawPage()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1240, 1754));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Helvetica"), 44);
        for (var i = 0; i < Letter.Length; i++)
            canvas.DrawText(Letter[i], 120, 200 + i * 90, SKTextAlign.Left, font, paint);
        return surface.Snapshot();
    }

    private string WriteScanPdf()
    {
        var path = Path.Combine(_folder, "scan.pdf");
        using var image = DrawPage();
        using (var stream = new SKFileWStream(path))
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(595, 842);
            canvas.DrawImage(image, new SKRect(0, 0, 595, 842));
            document.EndPage();
            document.Close();
        }
        return path;
    }

    [Fact]
    public async Task AScannedPdfIsReadLikeLiveText()
    {
        if (!TextRecognizer.IsSupported)
            return; // macOS only

        var content = await DocumentReader.ReadAsync(WriteScanPdf());

        Assert.Contains("--- Page 1 ---", content.Text);
        foreach (var line in Letter)
            Assert.Contains(line, content.Text);
    }

    [Fact]
    public async Task ThePhotoOfALetterCanBeSearched()
    {
        if (!TextRecognizer.IsSupported)
            return;
        var path = Path.Combine(_folder, "letter.png");
        using (var image = DrawPage())
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(path))
            data.SaveTo(file);

        var text = await DocumentReader.ReadTextAsync(path);

        Assert.Contains("Rechnung 48213", text);
        Assert.Contains("Total CHF 87.40", text);
    }

    [Fact]
    public async Task AnEmptyScanSaysSoPlainly()
    {
        var path = Path.Combine(_folder, "blank.pdf");
        using (var stream = new SKFileWStream(path))
        using (var document = SKDocument.CreatePdf(stream))
        {
            document.BeginPage(595, 842).Clear(SKColors.White);
            document.EndPage();
            document.Close();
        }

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => DocumentReader.ReadAsync(path));
        Assert.Contains("couldn’t read any text", error.Message);
        Assert.Equal("", await DocumentReader.ReadTextAsync(path));
    }
}
