using System;
using System.Globalization;
using Keystone.Models;
using Keystone.Services;
using Keystone.Localization;

namespace Keystone.ViewModels;

/// <summary>One row of the Documents table.</summary>
public sealed record DocumentRow(HouseholdDocument Document, string PersonName, bool FileExists, DateOnly Today)
{
    public string Title => Document.Title;

    public string KindName => HouseholdDocument.KindName(Document.Kind);

    /// <summary>Type and sender, shown under the title.</summary>
    public string DetailText => string.IsNullOrWhiteSpace(Document.Sender) ? KindName : $"{KindName} · {Document.Sender}";

    public string DateText => Format.Date(Document.Date);

    public bool IsShared => Document.PersonId is null;

    public bool HasAmount => Document.IsPayable && Document.Amount is > 0;

    public string AmountText => HasAmount ? Document.Amount!.Value.ToString("#,##0.00", CultureInfo.CurrentCulture) : "";

    public bool IsOverdue => Document.IsOverdue(Today);

    public bool IsOpen => Document.IsOpenBill && !IsOverdue;

    public bool IsPaid => Document.IsPayable && Document.IsPaid;

    public bool CanMarkPaid => Document.IsOpenBill;

    public string StatusText => this switch
    {
        { IsOverdue: true } when Document.HasInstalments =>
            Loc.F("Instalment {0}/{1} overdue", Document.NextInstalmentNumber, Document.Instalments.Count),
        { IsOpen: true } when Document.HasInstalments =>
            Loc.F("Instalment {0}/{1} · {2}", Document.NextInstalmentNumber, Document.Instalments.Count, Format.Date(Document.NextDueDate!.Value)),
        { IsOverdue: true } => Loc.F("Overdue since {0}", Format.Date(Document.DueDate!.Value)),
        { IsOpen: true } => Document.DueDate is { } due ? Loc.F("Due {0}", Format.Date(due)) : Loc.T("To pay"),
        { IsPaid: true } => Loc.F("Paid {0}", Format.Date(Document.PaidOn!.Value)),
        _ when RetentionGuide.CanBeThrownAway(Document, Today) => Loc.T("Past keeping time"),
        _ => "",
    };

    public bool HasStatus => StatusText != "";

    /// <summary>For a bill paid in parts: the next part and what is left, e.g. "CHF 500 due 3 Feb 2027 · CHF 1'000 left of CHF 1'500".</summary>
    public string? InstalmentTip => Document is { HasInstalments: true, IsOpenBill: true, NextDueDate: { } due }
        ? Loc.F("{0} due {1} · {2} left of {3}", Format.Money(Document.NextAmount), Format.Date(due),
            Format.Money(Document.AmountLeft), Format.Money(Document.Amount ?? 0))
        : null;

    public string MarkPaidTip => Document.HasInstalments ? Loc.T("Mark the next instalment as paid today") : Loc.T("Mark as paid today");

    public bool IsMissing => !FileExists;
}
