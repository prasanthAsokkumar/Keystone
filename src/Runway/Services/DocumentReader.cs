using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Runway.Localization;

namespace Runway.Services;

/// <summary>What a dropped file contains, ready to hand to the model: text, or one image.</summary>
public sealed record DocumentContent(string FileName, string? Text, string? ImageBase64, bool WasShortened)
{
    public bool IsImage => ImageBase64 is not null;
}

/// <summary>Reads PDFs, text/CSV files and images on this Mac. Nothing is sent anywhere.</summary>
public static class DocumentReader
{
    /// <summary>Longer text is cut, so the model's context window isn't exceeded; the user is told.</summary>
    public const int MaxTextLength = 60_000;

    /// <summary>Big enough to read a letter; larger images make a local model much slower.</summary>
    private const int MaxImageSide = 1280;

    private static readonly string[] TextExtensions = [".csv", ".tsv", ".txt", ".md"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] ConvertedImageExtensions = [".heic", ".heif", ".tif", ".tiff", ".webp", ".gif", ".bmp"];

    public static string SupportedFilesText => Loc.T("PDF, CSV, text files and photos (PNG, JPG, HEIC)");

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".pdf" || TextExtensions.Contains(extension) || ImageExtensions.Contains(extension) ||
               ConvertedImageExtensions.Contains(extension);
    }

    /// <exception cref="NotSupportedException">The file type can't be read, or the PDF has no text.</exception>
    public static async Task<DocumentContent> ReadAsync(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".pdf")
            return Shorten(name, await Task.Run(() => ReadPdfText(path)));

        if (TextExtensions.Contains(extension))
            return Shorten(name, await File.ReadAllTextAsync(path));

        if (ImageExtensions.Contains(extension) || ConvertedImageExtensions.Contains(extension))
            return new DocumentContent(name, null, Convert.ToBase64String(await ReadImageAsync(path)), false);

        throw new NotSupportedException(Loc.F("Keystone can’t read {0} files. Try {1}.", extension, SupportedFilesText));
    }

    public static DocumentContent Shorten(string name, string text) =>
        text.Length > MaxTextLength
            ? new DocumentContent(name, text[..MaxTextLength], null, true)
            : new DocumentContent(name, text, null, false);

    private static string ReadPdfText(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
            // Page.Text runs words together; this keeps the spaces and line breaks.
            text.AppendLine($"--- Page {page.Number} ---").AppendLine(ContentOrderTextExtractor.GetText(page));

        if (pdf.GetPages().All(p => string.IsNullOrWhiteSpace(p.Text)))
            throw new NotSupportedException(
                Loc.T("This PDF is a scan with no text in it. Take a screenshot or photo of it and drop that instead."));
        return text.ToString();
    }

    /// <summary>
    /// Uses macOS's built-in sips to turn HEIC and other formats into a JPEG small enough for a
    /// local model. Elsewhere, PNG and JPEG are passed through as they are.
    /// </summary>
    private static async Task<byte[]> ReadImageAsync(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            if (ConvertedImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                throw new NotSupportedException(Loc.T("Convert the image to PNG or JPG first."));
            return await File.ReadAllBytesAsync(path);
        }

        var output = Path.Combine(Path.GetTempPath(), $"runway-import-{Guid.NewGuid():N}.jpg");
        try
        {
            var sips = Process.Start(new ProcessStartInfo("sips")
            {
                ArgumentList = { "-s", "format", "jpeg", "-Z", MaxImageSide.ToString(), path, "--out", output },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await sips.WaitForExitAsync();
            if (sips.ExitCode != 0 || !File.Exists(output))
                throw new NotSupportedException(Loc.T("Keystone couldn’t open this image."));
            return await File.ReadAllBytesAsync(output);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
