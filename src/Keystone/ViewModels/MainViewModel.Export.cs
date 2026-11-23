using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>
/// Exports for keeping records or handing to a tax advisor: the entries as listed, every booking of a
/// financial year (CSV for Excel or Numbers), and a one-page PDF summary of the year.
/// </summary>
public partial class MainViewModel
{
    /// <summary>"Keystone Expenses 2026-11-23.csv": the export holds the open tab.</summary>
    public string EntriesExportFileName =>
        $"Keystone {Loc.T(TransactionsTab switch { 0 => "Income", 2 => "Transfers", _ => "Expenses" })} {Today:yyyy-MM-dd}.csv";

    public string YearExportFileName => $"Keystone {Report.Label.Replace('/', '-')}";

    private string PersonNameOf(int? personId) =>
        personId is { } id ? People.FirstOrDefault(p => p.Id == id)?.Name ?? "" : Loc.T("Shared");

    private string AccountNameOf(int? accountId) =>
        BankAccounts.FirstOrDefault(a => a.Id == accountId)?.Name ?? "";

    private static string TypeName(Transaction t) =>
        Loc.T(t.IsTransfer ? "Transfer" : t.IsIncome ? "Income" : "Expense");

    /// <summary>The entries shown on the Transactions page: the open tab, with the current filters and sort.</summary>
    public string BuildEntriesCsv() => Csv.Build(
        [Loc.T("Date"), Loc.T("Description"), Loc.T("Category"), Loc.T("Type"), Loc.T("Amount"),
         Loc.T("Repeats"), Loc.T("Ends on"), Loc.T("Person"), Loc.T("Account"), Loc.T("To account")],
        EntryRows.Select(r => r.Transaction).Select(t => new[]
        {
            Csv.Date(t.Date),
            t.Description,
            t.IsTransfer ? "" : Categories.Display(t.Category),
            TypeName(t),
            Csv.Amount(t.IsTransfer ? t.Amount : t.SignedAmount),
            t.IsRecurring ? t.RecurrenceName : Loc.T("One-time"),
            t.EndDate is { } end ? Csv.Date(end) : "",
            PersonNameOf(t.PersonId),
            AccountNameOf(AccountIdOf(t)),
            t.IsTransfer ? AccountNameOf(t.ToAccountId) : "",
        }));

    /// <summary>
    /// Every booking in the financial year shown on the Year page, one line each: repeating entries once per
    /// time they happen (with any single-time change), debt payments included, planned ones marked.
    /// </summary>
    public string BuildYearCsv()
    {
        var occurrences = Schedule.Expand(_entries, Report.Start, Report.End);
        return Csv.Build(
            [Loc.T("Date"), Loc.T("Description"), Loc.T("Category"), Loc.T("Type"), Loc.T("Amount"),
             Loc.T("Person"), Loc.T("Account"), Loc.T("Planned")],
            occurrences.Select(o =>
            {
                var t = o.Source;
                return new[]
                {
                    Csv.Date(o.Date),
                    t.Description,
                    t.IsTransfer ? "" : Categories.Display(t.Category),
                    TypeName(t),
                    Csv.Amount(t.IsTransfer ? o.Amount : o.SignedAmount),
                    t.IsDebtPayment ? "" : PersonNameOf(t.PersonId),
                    AccountNameOf(AccountIdOf(t)),
                    o.Date > Today ? Loc.T("Yes") : "",
                };
            }));
    }

    public YearSummary BuildYearSummary()
    {
        var report = Report;
        YearSummaryRow Row(string label, decimal income, decimal spending, bool planned = false) =>
            new(label, Format.Money(income), Format.Money(spending), Format.SignedMoney(income - spending), planned, income < spending);

        var subtitle = report.RangeText;
        if (report.PlannedExpenses > 0)
            subtitle += " · " + Loc.T("Months after today are planned, not yet booked");

        return new YearSummary(
            Loc.F("Income and spending {0}", report.Label),
            subtitle,
            [Loc.T("Month"), Loc.T("Income"), Loc.T("Spending"), Loc.T("Net")],
            report.Months.Select(m => Row(Format.Month(m.Month), m.Income, m.Expenses, m.IsPlanned)).ToList(),
            Row(Loc.T("Total"), report.TotalIncome, report.TotalExpenses),
            Loc.T("Spending by category"),
            report.ExpensesByCategory.Select(c => (Categories.Display(c.Category), Format.Money(c.Amount), c.Share)).ToList(),
            Loc.F("Made with Keystone on {0}. Transfers between your own accounts and borrowed money are left out.", Format.Date(Today)));
    }

    public Task ExportEntriesCsvAsync(string path) => ExportAsync(path, () => File.WriteAllText(path, BuildEntriesCsv(), Csv.Encoding));

    public Task ExportYearCsvAsync(string path) => ExportAsync(path, () => File.WriteAllText(path, BuildYearCsv(), Csv.Encoding));

    public Task ExportYearPdfAsync(string path)
    {
        var summary = BuildYearSummary();
        return ExportAsync(path, () => YearSummaryPdf.Write(path, summary));
    }

    private async Task ExportAsync(string path, Action write)
    {
        try
        {
            await Task.Run(write);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.F("Could not export: {0}", ex.Message));
            return;
        }

        ShowNotice(Loc.F("Saved {0} to {1}", Path.GetFileName(path), Path.GetFileName(Path.GetDirectoryName(path)) ?? ""));
    }
}
