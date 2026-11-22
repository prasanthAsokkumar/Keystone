using System;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>
/// How long to keep a document, following common Swiss practice: tax-relevant papers 10 years, bills and
/// receipts 5 years (claims for regular payments expire after 5 years), salary and pension papers until
/// retirement, contracts and policies while they run. A guide, not legal advice.
/// </summary>
public static class RetentionGuide
{
    /// <summary>The first day the document may be thrown away; null to keep it (e.g. while a contract runs).</summary>
    public static DateOnly? KeepUntil(HouseholdDocument document)
    {
        var endOfYear = new DateOnly(document.Date.Year, 12, 31);
        return document.Kind switch
        {
            DocumentKind.Bill or DocumentKind.Invoice => (document.PaidOn ?? document.Date) is var paid && document.IsOpenBill
                ? null // still to pay
                : new DateOnly(paid.Year, 12, 31).AddYears(5),
            DocumentKind.Statement or DocumentKind.Notice => endOfYear.AddYears(10),
            DocumentKind.Letter => endOfYear.AddYears(2),
            _ => null, // contracts, policies, payslips and others: keep while they matter
        };
    }

    public static bool CanBeThrownAway(HouseholdDocument document, DateOnly today) => KeepUntil(document) is { } until && until < today;

    /// <summary>Why the document is kept as long as it is.</summary>
    public static string Reason(HouseholdDocument document) => Loc.T(document.Kind switch
    {
        DocumentKind.Bill or DocumentKind.Invoice => "Bills and receipts: 5 years after paying",
        DocumentKind.Statement or DocumentKind.Notice => "Tax-relevant papers: 10 years",
        DocumentKind.Letter => "Letters: 2 years, unless they matter longer",
        DocumentKind.Payslip => "Salary papers: until retirement, to check your AHV account",
        DocumentKind.Contract or DocumentKind.Policy => "Contracts and policies: while they run, and 10 years after",
        _ => "Keep as long as it matters",
    });
}
