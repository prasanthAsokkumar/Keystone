using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>One person's health costs in a calendar year (the Swiss deductible runs per calendar year).</summary>
/// <param name="PersonId">Null for shared premiums, e.g. one family premium for everyone.</param>
/// <param name="PremiumsPaid">Premiums due up to today.</param>
/// <param name="PremiumsYear">Premiums for the whole year.</param>
/// <param name="CostSharing">What the cost-sharing bills (Leistungsabrechnungen) of the year add up to.</param>
/// <param name="CostSharingOpen">Of that, not paid yet.</param>
/// <param name="MaxCostSharing">The most the insurer can bill this person in a year: deductible plus the 10% share's
/// cap (CHF 700, children CHF 350); null without a deductible on their basic insurance.</param>
public sealed record HealthCostLine(
    int? PersonId, decimal PremiumsPaid, decimal PremiumsYear, decimal CostSharing, decimal CostSharingOpen, decimal? MaxCostSharing)
{
    public decimal Total => PremiumsYear + CostSharing;
}

/// <summary>
/// What the household pays for health insurance: premiums, and the cost-sharing bills the health insurer sends after
/// doctor's or pharmacy bills (deductible "Franchise" and the 10% share "Selbstbehalt", on a "Leistungsabrechnung").
/// A guide, not advice.
/// </summary>
public static class HealthCosts
{
    /// <summary>The 10% share stops at CHF 700 a year for adults, CHF 350 for children (KVG art. 64).</summary>
    public const decimal MaxShareAdult = 700m, MaxShareChild = 350m;

    /// <summary>Basic insurance counts as a child's up to the year they turn 18.</summary>
    public const int ChildUntilAge = 18;

    private static readonly string[] CostSharingWords =
    [
        "leistungsabrechnung", "kostenbeteiligung", "selbstbehalt", "franchise", "rückforderung", "rueckforderung",
        "décompte de prestations", "decompte de prestations", "participation aux coûts", "benefit statement", "cost sharing",
    ];

    private static readonly string[] PremiumWords = ["prämie", "praemie", "premium", "prime", "police", "policy"];

    /// <summary>Health insurers by name, matched as whole words of the sender (CSS is too short to be sure).</summary>
    private static readonly string[] HealthInsurers =
    [
        "Helsana", "Swica", "Sanitas", "Visana", "Concordia", "KPT", "Atupri", "ÖKK", "OKK", "Sympany", "Assura",
        "Groupe Mutuel", "EGK", "Agrisano", "Aquilana", "Sumiswalder", "Philos", "Avenir", "Mutuel", "Easy Sana", "Vivao",
    ];

    private static bool HasWord(string text, string word) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}]){Regex.Escape(word)}(?![\p{{L}}])", RegexOptions.IgnoreCase);

    /// <summary>A health insurer: one the household has basic or supplementary health insurance with, or a known one.</summary>
    public static bool IsHealthInsurer(string sender, IEnumerable<InsurancePolicy> policies) =>
        sender.Trim() != "" &&
        (policies.Any(p => p.Kind is InsuranceKind.HealthBasic or InsuranceKind.HealthSupplementary && DuplicateFinder.SameSender(p.Insurer, sender)) ||
         HealthInsurers.Any(name => HasWord(sender, name)) ||
         sender.Contains("krankenkasse", StringComparison.CurrentCultureIgnoreCase) ||
         sender.Contains("krankenversicherung", StringComparison.CurrentCultureIgnoreCase) ||
         sender.Contains("caisse maladie", StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// A cost-sharing bill: it says so ("Leistungsabrechnung", "Kostenbeteiligung"…), or it is a bill from a health
    /// insurer that isn't for a premium.
    /// </summary>
    public static bool IsCostSharing(HouseholdDocument document, IEnumerable<InsurancePolicy> policies)
    {
        if (document.Amount is not > 0)
            return false;
        var text = $"{document.Title} {document.Notes}";
        var fromInsurer = IsHealthInsurer(document.Sender, policies);
        if (CostSharingWords.Any(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            return fromInsurer || document.Kind is DocumentKind.Bill or DocumentKind.Invoice or DocumentKind.Statement;
        return fromInsurer && document.IsPayable && !PremiumWords.Any(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>The year's health costs per person (and shared), in the order people are listed.</summary>
    public static IReadOnlyList<HealthCostLine> ForYear(
        int year, DateOnly today, IEnumerable<Transaction> entries, IEnumerable<HouseholdDocument> documents,
        IReadOnlyList<InsurancePolicy> policies, IReadOnlyList<Person> people)
    {
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        var premiums = Schedule.Expand(
            entries.Where(t => t.Type == TransactionType.Expense && t.Category == Categories.HealthInsurance), start, end);
        var bills = documents.Where(d => d.Date.Year == year && IsCostSharing(d, policies)).ToList();

        decimal? Max(int? personId)
        {
            var basic = policies.FirstOrDefault(p => p.Kind == InsuranceKind.HealthBasic && p.PersonId == personId && p.Deductible is not null &&
                                                     !p.HasEnded(start));
            if (basic?.Deductible is not { } deductible)
                return null;
            var birthYear = people.FirstOrDefault(p => p.Id == personId)?.BirthYear;
            var isChild = birthYear is { } born && year - born <= ChildUntilAge;
            return deductible + (isChild ? MaxShareChild : MaxShareAdult);
        }

        HealthCostLine Line(int? personId) => new(
            personId,
            premiums.Where(o => o.Source.PersonId == personId && o.Date <= today).Sum(o => o.Amount),
            premiums.Where(o => o.Source.PersonId == personId).Sum(o => o.Amount),
            bills.Where(d => d.PersonId == personId).Sum(d => d.Amount ?? 0),
            bills.Where(d => d.PersonId == personId && d.IsOpenBill).Sum(d => d.Amount ?? 0),
            personId is null ? null : Max(personId));

        return people.Select(p => (int?)p.Id).Append(null)
            .Select(Line)
            .Where(l => l.PremiumsYear > 0 || l.CostSharing > 0)
            .ToList();
    }

    /// <summary>The cost-sharing bills of a year, newest first.</summary>
    public static IReadOnlyList<HouseholdDocument> Statements(int year, IEnumerable<HouseholdDocument> documents, IEnumerable<InsurancePolicy> policies) =>
        documents.Where(d => d.Date.Year == year && IsCostSharing(d, policies.ToList()))
            .OrderByDescending(d => d.Date).ThenByDescending(d => d.Id)
            .ToList();
}
