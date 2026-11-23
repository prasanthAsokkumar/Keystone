using System;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration and the order (that of the form's list) can change.</remarks>
public enum InsuranceKind
{
    HealthBasic,
    HealthSupplementary,
    Accident,
    /// <summary>Household contents and private liability in one policy, as Swiss insurers often sell them.</summary>
    HouseholdLiability,
    HouseholdContents,
    PrivateLiability,
    Building,
    Car,
    LegalProtection,
    Life,
    IncomeProtection,
    Travel,
    Pet,
    Other,
}

/// <summary>An insurance policy: what it costs, when its term ends and by when it must be cancelled.</summary>
public class InsurancePolicy
{
    public int Id { get; set; }

    /// <summary>Where the row was put by dragging; 0 for rows never moved by hand.</summary>
    public int SortOrder { get; set; }

    public string Insurer { get; set; } = "";

    public InsuranceKind Kind { get; set; }

    /// <summary>The insured household member; null for the whole household.</summary>
    public int? PersonId { get; set; }

    public string PolicyNumber { get; set; } = "";

    /// <summary>The premium per <see cref="PremiumRecurrence"/>.</summary>
    public decimal Premium { get; set; }

    public Recurrence PremiumRecurrence { get; set; } = Recurrence.Yearly;

    /// <summary>The last day of the current term, e.g. 31 December.</summary>
    public DateOnly? ContractEnd { get; set; }

    /// <summary>The term renews by itself for another year unless cancelled.</summary>
    public bool RenewsAutomatically { get; set; } = true;

    /// <summary>Months before the end of the term that a cancellation must arrive.</summary>
    public int NoticeMonths { get; set; } = 3;

    public string Notes { get; set; } = "";

    /// <summary>
    /// Basic health insurance: the yearly deductible ("Franchise", CHF 300 to 2'500 for adults, 0 to 600 for
    /// children). With it, the Insurance page says how much cost sharing can still come this year.
    /// </summary>
    public decimal? Deductible { get; set; }

    /// <summary>The policy document, filed in Documents.</summary>
    public int? DocumentId { get; set; }

    public decimal YearlyPremium => PremiumRecurrence switch
    {
        Recurrence.Monthly => Premium * 12,
        Recurrence.Quarterly => Premium * 4,
        _ => Premium,
    };

    /// <summary>
    /// The next term end that can still be cancelled in time, and the last day to cancel for it. For a
    /// policy that renews by itself, a missed deadline moves on to the next year's.
    /// </summary>
    public (DateOnly End, DateOnly CancelBy)? NextDeadline(DateOnly today) =>
        NoticeDeadline.Next(ContractEnd, RenewsAutomatically, NoticeMonths, today);

    public bool HasEnded(DateOnly today) => !RenewsAutomatically && ContractEnd < today;

    /// <summary>Typical Swiss notice periods: basic health insurance by 30 November for 1 January, others 3 months.</summary>
    public static int DefaultNoticeMonths(InsuranceKind kind) => kind == InsuranceKind.HealthBasic ? 1 : 3;

    public static string KindName(InsuranceKind kind) => Loc.T(kind switch
    {
        InsuranceKind.HealthBasic => "Health insurance (basic)",
        InsuranceKind.HealthSupplementary => "Health insurance (supplementary)",
        InsuranceKind.Accident => "Accident",
        InsuranceKind.HouseholdLiability => "Household contents and liability",
        InsuranceKind.HouseholdContents => "Household contents",
        InsuranceKind.PrivateLiability => "Private liability",
        InsuranceKind.Car => "Car and motorbike",
        InsuranceKind.Life => "Life",
        InsuranceKind.IncomeProtection => "Income protection",
        InsuranceKind.LegalProtection => "Legal protection",
        InsuranceKind.Travel => "Travel",
        InsuranceKind.Building => "Building",
        InsuranceKind.Pet => "Pet",
        _ => "Other",
    });

    /// <summary>The expense category premiums of this kind belong in.</summary>
    public static string CategoryFor(InsuranceKind kind) =>
        kind is InsuranceKind.HealthBasic or InsuranceKind.HealthSupplementary ? "Health insurance" : "Other insurance";
}
