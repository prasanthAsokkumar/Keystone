using System;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum ContractKind { Mobile, Internet, TvAndStreaming, Gym, Leasing, Rent, Subscription, Energy, Other }

/// <summary>A contract other than insurance (phone, internet, gym, leasing…), with its cost and notice deadline.</summary>
public class Contract
{
    public int Id { get; set; }

    /// <summary>The provider, e.g. "Swisscom".</summary>
    public string Provider { get; set; } = "";

    public ContractKind Kind { get; set; }

    public int? PersonId { get; set; }

    public string CustomerNumber { get; set; } = "";

    public decimal Cost { get; set; }

    public Recurrence CostRecurrence { get; set; } = Recurrence.Monthly;

    /// <summary>The last day of the current minimum term; null for contracts that can be cancelled any time.</summary>
    public DateOnly? ContractEnd { get; set; }

    public bool RenewsAutomatically { get; set; } = true;

    public int NoticeMonths { get; set; } = 2;

    public string Notes { get; set; } = "";

    public int? DocumentId { get; set; }

    public decimal YearlyCost => CostRecurrence switch
    {
        Recurrence.Monthly => Cost * 12,
        Recurrence.Quarterly => Cost * 4,
        _ => Cost,
    };

    public (DateOnly End, DateOnly CancelBy)? NextDeadline(DateOnly today) =>
        NoticeDeadline.Next(ContractEnd, RenewsAutomatically, NoticeMonths, today);

    public bool HasEnded(DateOnly today) => !RenewsAutomatically && ContractEnd < today;

    /// <summary>Usual notice periods: rent 3 months, most others 2 months.</summary>
    public static int DefaultNoticeMonths(ContractKind kind) => kind switch
    {
        ContractKind.Rent or ContractKind.Leasing => 3,
        ContractKind.Gym or ContractKind.Subscription => 1,
        _ => 2,
    };

    public static string KindName(ContractKind kind) => Loc.T(kind switch
    {
        ContractKind.Mobile => "Mobile phone",
        ContractKind.Internet => "Internet",
        ContractKind.TvAndStreaming => "TV and streaming",
        ContractKind.Gym => "Gym",
        ContractKind.Leasing => "Leasing",
        ContractKind.Rent => "Rental contract",
        ContractKind.Subscription => "Subscription",
        ContractKind.Energy => "Energy",
        _ => "Other",
    });

    public static string CategoryFor(ContractKind kind) => kind switch
    {
        ContractKind.Rent => "Rent",
        ContractKind.Leasing => "Transport",
        ContractKind.Energy => "Bills",
        _ => "Subscriptions",
    };
}

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum ImportantDateKind { Passport, IdentityCard, DrivingLicence, CarInspection, TyreChange, Vignette, Other }

/// <summary>A date to keep in mind: a document expiring, a car inspection, the yearly vignette…</summary>
public class ImportantDate
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public ImportantDateKind Kind { get; set; }

    public int? PersonId { get; set; }

    /// <summary>When it expires or is due.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Months until it comes round again, e.g. 12 for the vignette; 0 for once.</summary>
    public int RepeatMonths { get; set; }

    /// <summary>How long before the date to be reminded.</summary>
    public int RemindDaysBefore { get; set; } = 30;

    public string Notes { get; set; } = "";

    /// <summary>The next time it is due: repeating dates move on once they have passed.</summary>
    public DateOnly NextDate(DateOnly today)
    {
        var date = Date;
        if (RepeatMonths > 0)
        {
            for (var i = 1; date < today; i++)
                date = Date.AddMonths(RepeatMonths * i);
        }
        return date;
    }

    public bool HasPassed(DateOnly today) => RepeatMonths <= 0 && Date < today;

    /// <summary>Sensible defaults: passports need months to renew, a vignette is due every January.</summary>
    public static (int RemindDaysBefore, int RepeatMonths) Defaults(ImportantDateKind kind) => kind switch
    {
        ImportantDateKind.Passport or ImportantDateKind.IdentityCard => (180, 0),
        ImportantDateKind.DrivingLicence => (60, 0),
        ImportantDateKind.CarInspection => (45, 0),
        ImportantDateKind.TyreChange => (14, 6),
        ImportantDateKind.Vignette => (30, 12),
        _ => (14, 0),
    };

    public static string KindName(ImportantDateKind kind) => Loc.T(kind switch
    {
        ImportantDateKind.Passport => "Passport",
        ImportantDateKind.IdentityCard => "Identity card",
        ImportantDateKind.DrivingLicence => "Driving licence",
        ImportantDateKind.CarInspection => "Car inspection (MFK)",
        ImportantDateKind.TyreChange => "Tyre change",
        ImportantDateKind.Vignette => "Motorway vignette",
        _ => "Other",
    });
}

/// <summary>Something bought with a warranty, and the receipt that proves it.</summary>
public class Warranty
{
    /// <summary>Swiss law gives two years on new goods.</summary>
    public const int DefaultMonths = 24;

    public int Id { get; set; }

    public string Item { get; set; } = "";

    public string Store { get; set; } = "";

    public int? PersonId { get; set; }

    public DateOnly PurchaseDate { get; set; }

    public decimal? Price { get; set; }

    public int Months { get; set; } = DefaultMonths;

    /// <summary>The receipt, filed in Documents.</summary>
    public int? DocumentId { get; set; }

    public string Notes { get; set; } = "";

    public DateOnly ExpiresOn => PurchaseDate.AddMonths(Months).AddDays(-1);

    public bool HasExpired(DateOnly today) => ExpiresOn < today;
}

/// <summary>Money set aside for something: a holiday, a car, an emergency fund.</summary>
public class SavingsGoal
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Target { get; set; }

    public DateOnly? TargetDate { get; set; }

    /// <summary>The account the money is kept in; its balance is the progress. Null to track by hand.</summary>
    public int? AccountId { get; set; }

    /// <summary>Saved so far, for a goal without an account.</summary>
    public decimal Saved { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A household document's text, kept for searching inside it.</summary>
public class DocumentText
{
    public int DocumentId { get; set; }

    public string Text { get; set; } = "";
}
