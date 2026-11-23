using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Keystone.Localization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Keystone.Services;

/// <summary>
/// Reads bank statements that only come as PDF, such as Revolut's. Each booking is one line with its date,
/// description, amount and the balance after it, e.g. "Nov 1, 2026 Migros 79.40 CHF 210.05 CHF". Whether money
/// went out or came in follows from how the balance changed, so nothing is guessed. Only the CHF part of a
/// statement is read; Keystone counts in francs.
/// </summary>
public static partial class PdfStatementParser
{
    /// <summary>The text of a PDF that has a text layer; empty for a scan or a file that can't be opened.</summary>
    public static string ReadText(string path)
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages())
                text.AppendLine(ContentOrderTextExtractor.GetText(page));
            return text.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>A Revolut statement: says so, and lists account transactions.</summary>
    public static bool IsStatement(string text) =>
        text.Contains("Revolut", StringComparison.Ordinal) &&
        text.Contains("Account transactions from", StringComparison.Ordinal);

    [GeneratedRegex(@"^([A-Z]{3}) Statement$")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^(?<date>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \d{1,2}, \d{4}) (?<description>.+?) (?<amount>-?[\d,]+\.\d{2}) CHF (?<balance>-?[\d,]+\.\d{2}) CHF$")]
    private static partial Regex Booking();

    [GeneratedRegex(@"(-?[\d,]+\.\d{2}) CHF")]
    private static partial Regex Money();

    [GeneratedRegex(@"Account transactions from (?<from>\w+ \d{1,2}, \d{4}) to (?<to>\w+ \d{1,2}, \d{4})")]
    private static partial Regex Period();

    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{10,30}$")]
    private static partial Regex Iban();

    /// <exception cref="InvalidOperationException">The text isn't a statement Keystone can read.</exception>
    public static BankStatement Parse(string text)
    {
        if (!IsStatement(text))
            throw new InvalidOperationException(
                Loc.T("Keystone reads PDF statements from Revolut. For other banks, download the statement as camt.053 (XML) or CSV from e-banking."));

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string? currency = null;
        string? iban = null;
        decimal? opening = null, closing = null;
        DateOnly? end = null;
        var bookings = new List<BankLine>();
        BankLine? last = null;
        decimal? balance = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (SectionHeader().Match(line) is { Success: true } header)
            {
                currency = header.Groups[1].Value;
                continue;
            }
            if (currency != "CHF")
                continue;

            if (iban is null && Iban().IsMatch(line))
                iban = line;
            if (Period().Match(line) is { Success: true } period && ParseDate(period.Groups["to"].Value) is { } to)
                end = to;

            // The balance summary: opening balance, money out, money in, closing balance.
            if (opening is null && line.StartsWith("Where your transactions are remitted", StringComparison.Ordinal) &&
                i + 1 < lines.Count && Money().Matches(lines[i + 1]) is { Count: 4 } summary)
            {
                opening = ParseAmount(summary[0].Groups[1].Value);
                closing = ParseAmount(summary[3].Groups[1].Value);
                balance = opening;
                continue;
            }

            if (Booking().Match(line) is { Success: true } booking && ParseDate(booking.Groups["date"].Value) is { } date)
            {
                var amount = ParseAmount(booking.Groups["amount"].Value);
                var after = ParseAmount(booking.Groups["balance"].Value);
                var description = booking.Groups["description"].Value.Trim();
                var signed = balance is { } before
                    ? before + amount == after ? amount : before - amount == after ? -amount : Guess(description, amount)
                    : Guess(description, amount);
                balance = after;
                last = new BankLine(date, signed, description,
                    // Revolut gives no booking number; date, amount and balance after it identify a booking.
                    $"revolut-{date:yyyyMMdd}-{signed.ToString(CultureInfo.InvariantCulture)}-{after.ToString(CultureInfo.InvariantCulture)}");
                bookings.Add(last);
                continue;
            }

            // A plain note under a booking, e.g. "Purchase of ETH", makes its description clearer.
            if (last is not null && IsNote(line))
            {
                bookings[^1] = last = last with { Description = $"{last.Description} · {line}" };
            }
            else
            {
                last = null;
            }
        }

        return new BankStatement(iban, closing, end, bookings);
    }

    /// <summary>Lines under a booking that aren't details: no "To:", "Card:", fees or foreign amounts.</summary>
    private static bool IsNote(string line) =>
        !line.Contains(':') && !char.IsDigit(line[0]) && !line.StartsWith("Date Description", StringComparison.Ordinal) &&
        !line.StartsWith("---", StringComparison.Ordinal) && !line.StartsWith("Page ", StringComparison.Ordinal) &&
        line is not ("IBAN" or "BIC") && !line.Contains("Revolut Bank", StringComparison.Ordinal) && line.Length < 60;

    /// <summary>Only when the balance doesn't tell: top-ups and money received come in, the rest goes out.</summary>
    private static decimal Guess(string description, decimal amount) =>
        description.Contains("top-up", StringComparison.OrdinalIgnoreCase) ||
        description.StartsWith("From ", StringComparison.Ordinal) ||
        description.StartsWith("Refund", StringComparison.OrdinalIgnoreCase)
            ? amount
            : -amount;

    private static decimal ParseAmount(string text) =>
        decimal.Parse(text.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text, ["MMM d, yyyy", "MMMM d, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
