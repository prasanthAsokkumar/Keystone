using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Keystone.Localization;

namespace Keystone.Services;

/// <summary>One booking on a bank statement. <see cref="Amount"/> is negative for money going out.</summary>
/// <param name="CounterpartyIban">The other side's account, when the bank gives it; recognises transfers between own accounts.</param>
public sealed record BankLine(DateOnly Date, decimal Amount, string Description, string Reference, string? CounterpartyIban = null);

/// <summary>A bank statement: its account, its bookings and, when given, the balance at its end.</summary>
public sealed record BankStatement(string? Iban, decimal? ClosingBalance, DateOnly? ClosingDate, IReadOnlyList<BankLine> Lines);

/// <summary>
/// Reads bank statements as Swiss banks export them: ISO 20022 camt.053 / camt.054 / camt.052 files, and
/// CSV exports, whose columns are recognised by their German, French or English names.
/// </summary>
public static class BankStatementParser
{
    public static readonly string[] Extensions = [".xml", ".camt", ".csv", ".txt", ".tsv"];

    public static string SupportedFilesText => "camt.053 (XML), CSV";

    /// <summary>A file that may be a bank statement, judged by its name and first bytes.</summary>
    public static bool LooksLikeStatement(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".xml" or ".camt")
            return Head(path).Contains("camt.05", StringComparison.Ordinal);
        if (extension is ".csv" or ".tsv" or ".txt")
        {
            try
            {
                return FindCsvLayout(ReadLines(path)) is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    /// <exception cref="InvalidOperationException">The file isn't a statement Keystone can read.</exception>
    public static BankStatement Parse(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".xml" or ".camt")
        {
            try
            {
                return ParseCamt(XDocument.Load(path));
            }
            catch (XmlException)
            {
                throw new InvalidOperationException(Loc.T("This XML file isn’t a camt bank statement."));
            }
        }

        return ParseCsv(ReadLines(path));
    }

    // ── camt (ISO 20022) ────────────────────────────────────────────────────

    public static BankStatement ParseCamt(XDocument document)
    {
        var root = document.Root ?? throw new InvalidOperationException(Loc.T("This XML file isn’t a camt bank statement."));
        // camt.053: Stmt, camt.054: Ntfctn, camt.052: Rpt. Namespaces differ by version, so names are matched locally.
        var reports = root.Descendants().Where(e => e.Name.LocalName is "Stmt" or "Ntfctn" or "Rpt").ToList();
        if (reports.Count == 0)
            throw new InvalidOperationException(Loc.T("This XML file isn’t a camt bank statement."));

        string? iban = null;
        decimal? closing = null;
        DateOnly? closingDate = null;
        var lines = new List<BankLine>();

        foreach (var report in reports)
        {
            iban ??= Path1(report, "Acct", "Id", "IBAN")?.Value;

            foreach (var balance in Children(report, "Bal"))
            {
                var code = Path1(balance, "Tp", "CdOrPrtry", "Cd")?.Value;
                if (code is "CLBD" && Amount(balance) is { } value)
                {
                    closing = value;
                    closingDate = ReadDate(Path1(balance, "Dt"));
                }
            }

            foreach (var entry in Children(report, "Ntry"))
            {
                var status = Path1(entry, "Sts", "Cd")?.Value ?? Path1(entry, "Sts")?.Value;
                if (status is "PDNG" or "INFO")
                    continue; // not booked yet
                if (Amount(entry) is not { } entryAmount)
                    continue;

                var date = ReadDate(Path1(entry, "BookgDt")) ?? ReadDate(Path1(entry, "ValDt"));
                if (date is null)
                    continue;

                var entryRef = Path1(entry, "AcctSvcrRef")?.Value ?? Path1(entry, "NtryRef")?.Value ?? "";
                var details = entry.Descendants().Where(e => e.Name.LocalName == "TxDtls").ToList();
                var entryText = Path1(entry, "AddtlNtryInf")?.Value?.Trim() ?? "";

                // A collective booking lists each payment with its own amount; those become separate lines.
                var split = details.Count > 1 && details.All(d => TxAmount(d, entryAmount) is not null);
                if (split)
                {
                    for (var i = 0; i < details.Count; i++)
                    {
                        var amount = TxAmount(details[i], entryAmount)!.Value;
                        lines.Add(new BankLine(date.Value, amount, Describe(details[i], entryText, amount),
                            Reference(details[i], $"{entryRef}#{i + 1}"), CounterIban(details[i], amount)));
                    }
                }
                else
                {
                    var detail = details.FirstOrDefault();
                    lines.Add(new BankLine(date.Value, entryAmount,
                        detail is null ? entryText : Describe(detail, entryText, entryAmount),
                        detail is null ? entryRef : Reference(detail, entryRef),
                        detail is null ? null : CounterIban(detail, entryAmount)));
                }
            }
        }

        return new BankStatement(Normalize(iban), closing, closingDate, lines);
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) =>
        parent.Elements().Where(e => e.Name.LocalName == name);

    /// <summary>The first element down a path of local names.</summary>
    private static XElement? Path1(XElement? start, params string[] names)
    {
        var current = start;
        foreach (var name in names)
        {
            current = current?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (current is null)
                return null;
        }
        return current;
    }

    /// <summary>The signed amount of an entry or balance: debits are negative.</summary>
    private static decimal? Amount(XElement element)
    {
        var amount = Path1(element, "Amt")?.Value;
        if (amount is null || !decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        return Path1(element, "CdtDbtInd")?.Value == "DBIT" ? -value : value;
    }

    private static decimal? TxAmount(XElement detail, decimal entryAmount)
    {
        var text = Path1(detail, "AmtDtls", "TxAmt", "Amt")?.Value ?? Path1(detail, "Amt")?.Value;
        if (text is null || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        var indicator = Path1(detail, "CdtDbtInd")?.Value;
        var negative = indicator is null ? entryAmount < 0 : indicator == "DBIT";
        return negative ? -value : value;
    }

    /// <summary>"Counterparty: payment reason", whichever parts the bank gives.</summary>
    private static string Describe(XElement detail, string entryText, decimal amount)
    {
        // Paying out: the creditor is the other side; receiving: the debtor.
        var party = amount < 0 ? "Cdtr" : "Dbtr";
        var parties = Path1(detail, "RltdPties");
        var name = (Path1(parties, party, "Nm") ?? Path1(parties, party, "Pty", "Nm"))?.Value?.Trim();
        var reason = string.Join(" ", detail.Descendants().Where(e => e.Name.LocalName == "Ustrd").Select(e => e.Value.Trim()))
            .Trim();
        if (reason == "")
            reason = Path1(detail, "AddtlTxInf")?.Value?.Trim() ?? "";

        var text = (name, reason) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{name}: {reason}",
            ({ Length: > 0 }, _) => name!,
            (_, { Length: > 0 }) => reason,
            _ => entryText,
        };
        return Clean(text == "" ? entryText : text);
    }

    private static string Reference(XElement detail, string fallback)
    {
        var refs = Path1(detail, "Refs");
        var value = new[] { "AcctSvcrRef", "TxId", "EndToEndId", "InstrId" }
            .Select(n => Path1(refs, n)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "NOTPROVIDED");
        return value ?? fallback;
    }

    private static string? CounterIban(XElement detail, decimal amount)
    {
        var parties = Path1(detail, "RltdPties");
        return Normalize(Path1(parties, amount < 0 ? "CdtrAcct" : "DbtrAcct", "Id", "IBAN")?.Value);
    }

    private static DateOnly? ReadDate(XElement? holder)
    {
        var text = Path1(holder, "Dt")?.Value ?? Path1(holder, "DtTm")?.Value ?? holder?.Value;
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateOnly.FromDateTime(date)
            : null;
    }

    // ── CSV ─────────────────────────────────────────────────────────────────

    private sealed record CsvLayout(int HeaderRow, char Separator, int Date, int Amount, int Debit, int Credit,
        int[] Text, int Balance, int Reference);

    private static readonly string[] DateNames = ["buchungsdatum", "booking date", "date de comptabilisation", "transaction date",
        "abschlussdatum", "datum", "date", "valuta", "value date", "valeur"];
    private static readonly string[] AmountNames = ["betrag", "amount", "montant", "umsatz"];
    private static readonly string[] DebitNames = ["belastung", "lastschrift", "debit", "débit", "soll", "ausgang"];
    private static readonly string[] CreditNames = ["gutschrift", "credit", "crédit", "haben", "eingang"];
    private static readonly string[] TextNames = ["buchungstext", "beschreibung", "description", "details", "text", "avisierungstext",
        "mitteilung", "zahlungsgrund", "verwendungszweck", "empfänger", "auftraggeber", "payee", "libellé", "libelle", "merchant"];
    private static readonly string[] BalanceNames = ["saldo", "balance", "solde", "kontostand"];
    private static readonly string[] ReferenceNames = ["referenz", "reference", "référence", "transaction id", "transaktions-nr", "id"];

    public static BankStatement ParseCsv(IReadOnlyList<string> lines)
    {
        var layout = FindCsvLayout(lines)
            ?? throw new InvalidOperationException(Loc.T("Couldn’t find the date and amount columns in this CSV file."));

        var bookings = new List<(BankLine Line, decimal? Balance, int Row)>();
        for (var row = layout.HeaderRow + 1; row < lines.Count; row++)
        {
            var cells = SplitCsv(lines[row], layout.Separator);
            string Cell(int i) => i >= 0 && i < cells.Count ? cells[i].Trim() : "";

            if (ParseDate(Cell(layout.Date)) is not { } date)
                continue;

            decimal amount;
            if (layout.Amount >= 0)
            {
                if (ParseAmount(Cell(layout.Amount)) is not { } value)
                    continue;
                amount = value;
            }
            else
            {
                var debit = ParseAmount(Cell(layout.Debit)) ?? 0;
                var credit = ParseAmount(Cell(layout.Credit)) ?? 0;
                amount = Math.Abs(credit) - Math.Abs(debit);
            }
            if (amount == 0)
                continue; // summary or empty lines

            var text = Clean(string.Join(" ", layout.Text.Select(Cell).Where(t => t != "").Distinct()));
            var reference = layout.Reference >= 0 && Cell(layout.Reference) != ""
                ? Cell(layout.Reference)
                : $"{date:yyyyMMdd}|{amount.ToString(CultureInfo.InvariantCulture)}|{text}";
            bookings.Add((new BankLine(date, amount, text, reference), ParseAmount(Cell(layout.Balance)), row));
        }

        // The latest booking's balance is the balance at the end of the statement.
        var last = bookings.Where(b => b.Balance is not null)
            .OrderByDescending(b => b.Line.Date)
            .ThenBy(b => FirstRowIsNewest(bookings) ? b.Row : -b.Row)
            .FirstOrDefault();

        var iban = lines.Take(layout.HeaderRow + 1).Select(l => IbanPattern.Match(l.Replace(" ", ""))).FirstOrDefault(m => m.Success)?.Value;
        return new BankStatement(Normalize(iban), last.Balance, last.Balance is null ? null : last.Line.Date,
            bookings.Select(b => b.Line).ToList());
    }

    private static bool FirstRowIsNewest(List<(BankLine Line, decimal? Balance, int Row)> bookings) =>
        bookings.Count > 1 && bookings[0].Line.Date >= bookings[^1].Line.Date;

    private static CsvLayout? FindCsvLayout(IReadOnlyList<string> lines)
    {
        for (var row = 0; row < Math.Min(lines.Count, 40); row++)
        {
            var separator = new[] { ';', ',', '\t' }.MaxBy(c => lines[row].Count(x => x == c));
            var headers = SplitCsv(lines[row], separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
            if (headers.Count < 2)
                continue;

            int Find(string[] names) =>
                names.Select(n => headers.FindIndex(h => h == n || h.StartsWith(n + " ", StringComparison.Ordinal) ||
                                                         h.StartsWith(n + "(", StringComparison.Ordinal) ||
                                                         h.Contains(n, StringComparison.Ordinal)))
                    .FirstOrDefault(i => i >= 0, -1);

            var date = Find(DateNames);
            var amount = Find(AmountNames);
            var debit = Find(DebitNames);
            var credit = Find(CreditNames);
            if (date < 0 || (amount < 0 && (debit < 0 || credit < 0)))
                continue;

            var used = new HashSet<int> { date, amount, debit, credit };
            var text = Enumerable.Range(0, headers.Count)
                .Where(i => !used.Contains(i) && TextNames.Any(n => headers[i].Contains(n, StringComparison.Ordinal)))
                .ToArray();
            var balance = Find(BalanceNames);
            var reference = headers.FindIndex(h => ReferenceNames.Contains(h));
            return new CsvLayout(row, separator, date, amount, debit, credit, text, balance, reference);
        }

        return null;
    }

    /// <summary>Splits one CSV line, keeping separators inside quotes.</summary>
    internal static List<string> SplitCsv(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == separator && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }
        cells.Add(cell.ToString());
        return cells;
    }

    private static readonly string[] DateFormats =
        ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy",
         "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss"];

    internal static DateOnly? ParseDate(string text)
    {
        text = text.Trim().Trim('"');
        return DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateOnly.FromDateTime(date)
            : null;
    }

    /// <summary>Reads "1'234.50", "-1234.50", "1.234,50", "CHF 12.00" and "(12.00)".</summary>
    internal static decimal? ParseAmount(string text)
    {
        text = text.Trim().Trim('"').Replace("CHF", "").Replace("'", "").Replace("’", "").Replace(" ", "").Replace(" ", "");
        if (text == "" || text == "-")
            return null;
        var negative = text.StartsWith('(') && text.EndsWith(')');
        text = text.Trim('(', ')');

        var lastComma = text.LastIndexOf(',');
        var lastDot = text.LastIndexOf('.');
        if (lastComma > lastDot)
            text = text.Replace(".", "").Replace(',', '.'); // 1.234,50
        else
            text = text.Replace(",", ""); // 1,234.50

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        return negative ? -value : value;
    }

    private static readonly Regex IbanPattern = new(@"\b(CH|LI)\d{2}[0-9A-Z]{12,17}\b", RegexOptions.Compiled);

    private static string? Normalize(string? iban) =>
        string.IsNullOrWhiteSpace(iban) ? null : string.Concat(iban.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // Swiss bank exports are UTF-8 or Windows-1252 (Latin-1 is close enough for umlauts).
        var text = IsUtf8(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        return text.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Head(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[2048];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
