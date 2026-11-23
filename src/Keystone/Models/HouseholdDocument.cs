using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum DocumentKind { Bill, Invoice, Letter, Notice, Contract, Policy, Statement, Payslip, Other }

/// <summary>One part of a bill paid in instalments.</summary>
public class BillInstalment
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    public DateOnly DueDate { get; set; }

    public decimal Amount { get; set; }

    public DateOnly? PaidOn { get; set; }

    /// <summary>Its one-time expense entry, if the bill's payments are in the expenses.</summary>
    public int? TransactionId { get; set; }

    public bool IsPaid => PaidOn is not null;
}

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

    /// <summary>SHA-256 of the file as it was added, to notice the same file being filed again. Empty for old ones until read.</summary>
    public string FileHash { get; set; } = "";

    /// <summary>
    /// Where it was put by dragging: "Insurance" or "Taxes" to show it there, "None" for Documents only, and ""
    /// to let Keystone decide from the type, sender and title.
    /// </summary>
    public string Section { get; set; } = "";

    /// <summary>The expense entry created for this bill, if any.</summary>
    public int? TransactionId { get; set; }

    /// <summary>
    /// When the bill is paid in parts (agreed with the tax office or the sender): each part with its own date and
    /// amount. Empty for a bill paid at once. The bill counts as paid once every part is.
    /// </summary>
    public List<BillInstalment> Instalments { get; set; } = [];

    public bool IsPayable => Kind is DocumentKind.Bill or DocumentKind.Invoice;

    public bool IsPaid => PaidOn is not null;

    /// <summary>A bill or invoice with an amount that hasn't been paid yet.</summary>
    public bool IsOpenBill => IsPayable && !IsPaid && Amount is > 0;

    public bool HasInstalments => Instalments.Count > 0;

    /// <summary>The parts in the order they are due.</summary>
    public IReadOnlyList<BillInstalment> InstalmentsInOrder => Instalments.OrderBy(i => i.DueDate).ThenBy(i => i.Id).ToList();

    /// <summary>The first part not paid yet; null without a plan or once all are paid.</summary>
    public BillInstalment? NextInstalment => InstalmentsInOrder.FirstOrDefault(i => !i.IsPaid);

    /// <summary>Which part is next, counting from 1 (0 without one).</summary>
    public int NextInstalmentNumber => NextInstalment is { } next ? InstalmentsInOrder.ToList().IndexOf(next) + 1 : 0;

    /// <summary>When the next payment is due: the next part's date, or the bill's own.</summary>
    public DateOnly? NextDueDate => HasInstalments ? NextInstalment?.DueDate : DueDate;

    /// <summary>What the next payment is: the next part, or the whole bill.</summary>
    public decimal NextAmount => NextInstalment?.Amount ?? Amount ?? 0;

    /// <summary>What is still to pay: the parts not paid yet, or the whole bill while it's open.</summary>
    public decimal AmountLeft => !IsOpenBill ? 0 : HasInstalments ? Instalments.Where(i => !i.IsPaid).Sum(i => i.Amount) : Amount ?? 0;

    public bool IsOverdue(DateOnly today) => IsOpenBill && NextDueDate < today;

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
