using System.Collections.Generic;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>How much an insurance matters for a Swiss household, as a guide.</summary>
public enum CoverNeed { Required, Recommended, IfItApplies, Optional }

/// <summary>
/// One kind of insurance common in Switzerland, as shown in the Insurance page's cover overview.
/// </summary>
/// <param name="Kind">The kind a new policy for it gets.</param>
/// <param name="CountsAs">Policy kinds that provide this cover (a combined household policy covers contents and liability).</param>
/// <param name="Need">How much it matters.</param>
/// <param name="NeedText">The label shown for the need, in English (translated when shown).</param>
/// <param name="Hint">One plain sentence on what it is for, in English (translated when shown).</param>
/// <param name="PerPerson">Each household member needs their own (basic health insurance).</param>
public sealed record CoverItem(
    InsuranceKind Kind, IReadOnlyList<InsuranceKind> CountsAs, CoverNeed Need, string NeedText, string Hint, bool PerPerson = false);

/// <summary>
/// The insurances most Swiss households have or should think about. A guide, not advice: what is required can
/// depend on the canton, the job and whether you rent or own.
/// </summary>
public static class SwissCover
{
    public static readonly IReadOnlyList<CoverItem> Items =
    [
        new(InsuranceKind.HealthBasic, [InsuranceKind.HealthBasic], CoverNeed.Required, "Required by law",
            "Everyone living in Switzerland needs it, within 3 months of moving here or of birth.", PerPerson: true),
        new(InsuranceKind.PrivateLiability, [InsuranceKind.PrivateLiability, InsuranceKind.HouseholdLiability], CoverNeed.Recommended, "Recommended",
            "Pays for damage you cause to others. It costs little, and most landlords ask for it."),
        new(InsuranceKind.HouseholdContents, [InsuranceKind.HouseholdContents, InsuranceKind.HouseholdLiability], CoverNeed.Recommended, "Recommended",
            "Covers your belongings against fire, water damage and theft. Required in a few cantons."),
        new(InsuranceKind.Car, [InsuranceKind.Car], CoverNeed.IfItApplies, "If you have a car",
            "Liability insurance is required by law for every car and motorbike; partial or full casco is up to you."),
        new(InsuranceKind.Building, [InsuranceKind.Building], CoverNeed.IfItApplies, "If you own a home",
            "Required in most cantons for home owners, often with the cantonal building insurance."),
        new(InsuranceKind.Accident, [InsuranceKind.Accident], CoverNeed.IfItApplies, "If you aren’t employed",
            "Employers insure you if you work at least 8 hours a week; otherwise add accident cover to your basic health insurance."),
        new(InsuranceKind.LegalProtection, [InsuranceKind.LegalProtection], CoverNeed.Optional, "Optional",
            "Pays for lawyers and court costs, e.g. in a dispute with a landlord, an employer or after a traffic accident."),
        new(InsuranceKind.HealthSupplementary, [InsuranceKind.HealthSupplementary], CoverNeed.Optional, "Optional",
            "Extras basic insurance doesn’t pay for, such as dental care, glasses or a private room in hospital."),
        new(InsuranceKind.Life, [InsuranceKind.Life], CoverNeed.Optional, "If others depend on you",
            "Pays out if you die, e.g. so a partner or children can keep the home or pay off a mortgage."),
        new(InsuranceKind.IncomeProtection, [InsuranceKind.IncomeProtection], CoverNeed.Optional, "Optional",
            "Tops up your income if illness keeps you from working for long. Check first what your employer and pension fund pay."),
        new(InsuranceKind.Travel, [InsuranceKind.Travel], CoverNeed.Optional, "Optional",
            "Cancellation costs and help abroad. Often already included with a credit card or a supplementary health insurance."),
    ];
}
