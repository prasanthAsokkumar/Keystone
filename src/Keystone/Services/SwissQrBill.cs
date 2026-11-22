using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Keystone.Services;

/// <summary>
/// The payment part of a Swiss QR-bill, read from its QR code ("Swiss Payments Code", SPC). The data is
/// exact: no guessing of amounts or account numbers.
/// </summary>
/// <param name="Iban">The account to pay to, without spaces (an IBAN or a QR-IBAN).</param>
/// <param name="Amount">Null when the bill leaves the amount open, e.g. a donation slip.</param>
/// <param name="ReferenceType">QRR (QR reference), SCOR (creditor reference) or NON.</param>
/// <param name="InvoiceDate">From the structured bill information, when the invoicer gives it.</param>
/// <param name="DueDate">Invoice date plus the payment term from the bill information.</param>
public sealed record QrBill(
    string Iban, string Creditor, decimal? Amount, string Currency, string ReferenceType, string Reference,
    string Message, DateOnly? InvoiceDate = null, DateOnly? DueDate = null)
{
    public bool HasReference => Reference != "";

    /// <summary>"CH44 3199 9123 0008 8901 2" for display and e-banking.</summary>
    public string FormattedIban => FormatIban(Iban);

    /// <summary>The reference in the groups printed on the bill.</summary>
    public string FormattedReference => FormatReference(ReferenceType, Reference);

    public static string FormatIban(string iban) =>
        string.Join(" ", Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));

    /// <summary>A QR reference is printed as 2 + 5×5 digits; a creditor reference in groups of 4.</summary>
    public static string FormatReference(string type, string reference)
    {
        if (reference == "")
            return "";
        if (type == "QRR" && reference.Length == 27)
            return reference[..2] + " " + string.Join(" ", Enumerable.Range(0, 5).Select(i => reference.Substring(2 + i * 5, 5)));
        return string.Join(" ", Enumerable.Range(0, (reference.Length + 3) / 4)
            .Select(i => reference.Substring(i * 4, Math.Min(4, reference.Length - i * 4))));
    }
}

public static class SwissQrBillParser
{
    /// <summary>Reads the text of a QR-bill's QR code; null when it isn't one.</summary>
    public static QrBill? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        var lines = payload.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string Line(int i) => i < lines.Length ? lines[i].Trim() : "";

        if (Line(0) != "SPC" || !Line(1).StartsWith("02", StringComparison.Ordinal))
            return null;

        var iban = Line(3).Replace(" ", "").ToUpperInvariant();
        if (!Regex.IsMatch(iban, @"^(CH|LI)\d{2}[0-9A-Z]{17}$"))
            return null;

        // Creditor: address type, name, then the address (lines 6–10).
        var creditor = Line(5);
        decimal? amount = decimal.TryParse(Line(18), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
        var currency = Line(19) is { Length: 3 } c ? c : "CHF";
        var referenceType = Line(27) is "QRR" or "SCOR" ? Line(27) : "NON";
        var reference = Line(28).Replace(" ", "");
        var message = Line(29);
        var billInformation = Line(30) == "EPD" ? Line(31) : "";
        var (invoiceDate, dueDate) = ReadBillInformation(billInformation);

        return new QrBill(iban, creditor, amount, currency, referenceType, reference, message, invoiceDate, dueDate);
    }

    /// <summary>
    /// Swico's "S1" bill information: /11/ is the invoice date (YYMMDD) and /40/ the payment conditions
    /// ("discount:days;…"). The condition without discount gives the due date.
    /// </summary>
    internal static (DateOnly? InvoiceDate, DateOnly? DueDate) ReadBillInformation(string information)
    {
        if (!information.StartsWith("//S1/", StringComparison.Ordinal))
            return (null, null);

        var parts = information[5..].Split('/');
        DateOnly? invoiceDate = null;
        int? days = null;
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var (tag, content) = (parts[i], parts[i + 1]);
            if (tag == "11" && content.Length >= 6 &&
                DateOnly.TryParseExact(content[..6], "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                invoiceDate = date;
            if (tag == "40")
            {
                foreach (var condition in content.Split(';'))
                {
                    var pieces = condition.Split(':');
                    if (pieces.Length == 2 && decimal.TryParse(pieces[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var discount) &&
                        discount == 0 && int.TryParse(pieces[1], out var d))
                        days = d;
                }
            }
        }

        return (invoiceDate, invoiceDate is { } start && days is { } term ? start.AddDays(term) : null);
    }
}
