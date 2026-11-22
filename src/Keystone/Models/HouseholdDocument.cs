using System;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum DocumentKind { Bill, Invoice, Letter, Notice, Contract, Policy, Statement, Payslip, Other }

/// <summary>
/// A bill, letter or other paper kept for a household member. The file itself lives in the
/// documents folder (OneDrive); this row holds its details and where it is.
/// </summary>
public class HouseholdDocument
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public DocumentKind Kind { get; set; }

    /// <summary>Who sent it, e.g. the company or office.</summary>
    public string Sender { get; set; } = "";

    /// <summary>The household member it belongs to; null means the whole household.</summary>
    public int? PersonId { get; set; }

    /// <summary>The date printed on the document.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Amount to pay. Only used for bills and invoices.</summary>
    public decimal? Amount { get; set; }

    public DateOnly? DueDate { get; set; }

    public DateOnly? PaidOn { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>Path of the file relative to the documents folder, with '/' separators.</summary>
    public string RelativePath { get; set; } = "";

    public DateTime AddedAt { get; set; }

    /// <summary>From the bill's QR code: the account to pay to (IBAN or QR-IBAN).</summary>
    public string PaymentIban { get; set; } = "";

    /// <summary>From the bill's QR code: the payment reference to quote.</summary>
    public string PaymentReference { get; set; } = "";

    /// <summary>The expense entry created for this bill, if any.</summary>
    public int? TransactionId { get; set; }

    public bool IsPayable => Kind is DocumentKind.Bill or DocumentKind.Invoice;

    public bool IsPaid => PaidOn is not null;

    /// <summary>A bill or invoice with an amount that hasn't been paid yet.</summary>
    public bool IsOpenBill => IsPayable && !IsPaid && Amount is > 0;

    public bool IsOverdue(DateOnly today) => IsOpenBill && DueDate < today;

    /// <summary>The type's name in the app's language.</summary>
    public static string KindName(DocumentKind kind) => Loc.T(EnglishKindName(kind));

    private static string EnglishKindName(DocumentKind kind) => kind switch
    {
        DocumentKind.Bill => "Bill",
        DocumentKind.Invoice => "Invoice",
        DocumentKind.Letter => "Letter",
        DocumentKind.Notice => "Notice",
        DocumentKind.Contract => "Contract",
        DocumentKind.Policy => "Policy",
        DocumentKind.Statement => "Statement",
        DocumentKind.Payslip => "Payslip",
        _ => "Other",
    };

    /// <summary>
    /// Folder name for this kind of document, e.g. "Bills". Always English, so files stay put
    /// when the app's language changes.
    /// </summary>
    public static string FolderName(DocumentKind kind) => kind switch
    {
        DocumentKind.Other => "Other",
        DocumentKind.Policy => "Policies",
        _ => EnglishKindName(kind) + "s",
    };
}
