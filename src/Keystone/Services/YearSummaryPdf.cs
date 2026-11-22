using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Keystone.Services;

/// <summary>What the year summary shows, already formatted in the app's language.</summary>
public sealed record YearSummary(
    string Title,
    string Subtitle,
    IReadOnlyList<string> Columns,
    IReadOnlyList<YearSummaryRow> Months,
    YearSummaryRow Total,
    string CategoriesTitle,
    IReadOnlyList<(string Name, string Amount, double Share)> Categories,
    string Footer);

public sealed record YearSummaryRow(string Label, string Income, string Spending, string Net, bool IsPlanned = false, bool NetIsNegative = false);

/// <summary>A one-page PDF of a financial year: months, totals and spending by category, e.g. for a tax advisor.</summary>
public static class YearSummaryPdf
{
    private const float Width = 595, Height = 842, Margin = 48; // A4 in points

    private static readonly SKColor Ink = new(0x1F, 0x29, 0x37);
    private static readonly SKColor Muted = new(0x6B, 0x72, 0x80);
    private static readonly SKColor Line = new(0xE5, 0xE7, 0xEB);
    private static readonly SKColor Brand = new(0x05, 0x96, 0x69);
    private static readonly SKColor Negative = new(0xDC, 0x26, 0x26);

    public static void Write(string path, YearSummary summary)
    {
        using var stream = new SKFileWStream(path);
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = summary.Title,
            Creator = "Keystone",
            Creation = DateTime.Now,
        });
        var canvas = document.BeginPage(Width, Height);
        Draw(canvas, summary);
        document.EndPage();
        document.Close();
    }

    private static void Draw(SKCanvas canvas, YearSummary summary)
    {
        var regular = SKTypeface.FromFamilyName("Helvetica Neue") ?? SKTypeface.Default;
        var bold = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold) ?? regular;
        using var paint = new SKPaint { IsAntialias = true, Color = Ink };
        using var title = new SKFont(bold, 22);
        using var heading = new SKFont(bold, 12);
        using var text = new SKFont(regular, 10.5f);
        using var strong = new SKFont(bold, 10.5f);
        using var small = new SKFont(regular, 9);

        var y = Margin + 22;
        canvas.DrawText(summary.Title, Margin, y, SKTextAlign.Left, title, paint);
        y += 18;
        paint.Color = Muted;
        canvas.DrawText(summary.Subtitle, Margin, y, SKTextAlign.Left, text, paint);
        y += 30;

        // Month table: label left, three amounts right-aligned.
        float[] right = [Width - Margin - 220, Width - Margin - 110, Width - Margin];
        paint.Color = Muted;
        canvas.DrawText(summary.Columns[0], Margin, y, SKTextAlign.Left, small, paint);
        for (var i = 0; i < 3; i++)
            canvas.DrawText(summary.Columns[i + 1], right[i], y, SKTextAlign.Right, small, paint);
        y += 8;
        Rule(canvas, y);
        y += 16;

        foreach (var month in summary.Months)
        {
            Row(canvas, paint, month, y, right, text, month.IsPlanned ? Muted : Ink);
            y += 19;
        }
        Rule(canvas, y - 11);
        y += 4;
        Row(canvas, paint, summary.Total, y, right, strong, Ink);
        y += 40;

        if (summary.Categories.Count > 0)
        {
            paint.Color = Ink;
            canvas.DrawText(summary.CategoriesTitle, Margin, y, SKTextAlign.Left, heading, paint);
            y += 18;
            const float barStart = Margin + 170, barWidth = 200;
            foreach (var (name, amount, share) in summary.Categories)
            {
                if (y > Height - Margin - 30)
                    break; // one page; the smallest categories are left out
                paint.Color = Ink;
                canvas.DrawText(Fit(name, text, 160), Margin, y, SKTextAlign.Left, text, paint);
                paint.Color = Line;
                canvas.DrawRoundRect(new SKRoundRect(new SKRect(barStart, y - 8, barStart + barWidth, y - 2), 3), paint);
                paint.Color = Brand;
                var filled = (float)Math.Clamp(share, 0, 1) * barWidth;
                if (filled > 0.5f)
                    canvas.DrawRoundRect(new SKRoundRect(new SKRect(barStart, y - 8, barStart + filled, y - 2), 3), paint);
                paint.Color = Ink;
                canvas.DrawText(amount, Width - Margin, y, SKTextAlign.Right, text, paint);
                y += 18;
            }
        }

        paint.Color = Muted;
        canvas.DrawText(summary.Footer, Margin, Height - Margin + 10, SKTextAlign.Left, small, paint);
    }

    private static void Row(SKCanvas canvas, SKPaint paint, YearSummaryRow row, float y, float[] right, SKFont font, SKColor color)
    {
        paint.Color = color;
        canvas.DrawText(row.Label, Margin, y, SKTextAlign.Left, font, paint);
        canvas.DrawText(row.Income, right[0], y, SKTextAlign.Right, font, paint);
        canvas.DrawText(row.Spending, right[1], y, SKTextAlign.Right, font, paint);
        paint.Color = row.NetIsNegative ? Negative : color;
        canvas.DrawText(row.Net, right[2], y, SKTextAlign.Right, font, paint);
    }

    private static void Rule(SKCanvas canvas, float y)
    {
        using var line = new SKPaint { Color = Line, StrokeWidth = 0.75f, IsAntialias = true };
        canvas.DrawLine(Margin, y, Width - Margin, y, line);
    }

    /// <summary>Shortens text with an ellipsis so it fits the width.</summary>
    private static string Fit(string value, SKFont font, float width)
    {
        if (font.MeasureText(value) <= width)
            return value;
        while (value.Length > 1 && font.MeasureText(value + "…") > width)
            value = value[..^1];
        return value + "…";
    }
}
