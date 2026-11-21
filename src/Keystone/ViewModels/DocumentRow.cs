using System;
using System.Globalization;
using Keystone.Models;
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
        { IsOverdue: true } => Loc.F("Overdue since {0}", Format.Date(Document.DueDate!.Value)),
        { IsOpen: true } => Document.DueDate is { } due ? Loc.F("Due {0}", Format.Date(due)) : Loc.T("To pay"),
        { IsPaid: true } => Loc.F("Paid {0}", Format.Date(Document.PaidOn!.Value)),
        _ => "",
    };

    public bool HasStatus => StatusText != "";

    public bool IsMissing => !FileExists;
}
