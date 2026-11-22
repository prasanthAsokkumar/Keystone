using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>One person's retirement, as far as their letters and savings show it.</summary>
/// <param name="RetirementYear">The year they turn 65; null without a birth year.</param>
/// <param name="PensionPerMonth">AHV and pension fund pensions as projected in the letters.</param>
/// <param name="CapitalAtRetirement">Pillar 3a, 3b and vested benefits by then, with this year's 3a payments continued.</param>
public sealed record PersonRetirement(
    int? PersonId, string Name, int? RetirementYear, decimal PensionPerMonth, decimal CapitalAtRetirement,
    IReadOnlyList<Pillar3aWithdrawal> Withdrawals);

/// <summary>When to take out one 3a account: one account per year keeps each withdrawal's tax lower.</summary>
public sealed record Pillar3aWithdrawal(string Account, int Year, int Age, decimal Amount);

public sealed record RetirementGap(
    decimal NeedPerMonth, decimal PensionPerMonth, decimal CapitalPerMonth, IReadOnlyList<PersonRetirement> People)
{
    /// <summary>What is missing each month (zero when covered).</summary>
    public decimal GapPerMonth => Math.Max(0, NeedPerMonth - PensionPerMonth - CapitalPerMonth);

    public decimal CoveredShare => NeedPerMonth <= 0 ? 1 : Math.Min(1, (PensionPerMonth + CapitalPerMonth) / NeedPerMonth);
}

/// <summary>
/// Compares what the household will need in retirement with the pensions and savings it can count on. Savings
/// are spread over <see cref="CapitalYears"/> years. No interest or inflation is assumed, so it errs on the safe side.
/// </summary>
public static class RetirementPlanner
{
    /// <summary>Years the savings are assumed to last, from 65 to 85.</summary>
    public const int CapitalYears = 20;

    public static RetirementGap Plan(
        decimal livingCostsPerMonth, int needPercent, IEnumerable<Person> people, IEnumerable<PensionAccount> accounts,
        Func<int?, decimal> pillar3aPerYear, int thisYear)
    {
        var accountList = accounts.ToList();
        var owners = people.Select(p => (int?)p.Id).Concat(accountList.Select(a => a.PersonId)).Distinct()
            .Where(id => accountList.Any(a => a.PersonId == id));
        var byId = people.ToDictionary(p => p.Id);

        var result = new List<PersonRetirement>();
        foreach (var id in owners)
        {
            var person = id is { } pid && byId.TryGetValue(pid, out var p) ? p : null; // shared accounts have no person
            int? retirementYear = person?.BirthYear is { } born ? born + SwissPension.RetirementAge : null;
            var yearsLeft = retirementYear is { } r ? Math.Max(0, r - thisYear) : 0;
            var mine = accountList.Where(a => a.PersonId == id && a.Statements.Count > 0).ToList();

            var pension = mine.Where(a => a.Pillar is PensionPillar.Ahv or PensionPillar.PensionFund)
                .Sum(a => a.Statements[^1].ProjectedPension
                          ?? (a.Statements[^1].ProjectedCapital is { } capital ? capital * SwissPension.MinimumConversionRate : 0)) / 12;

            var pillar3a = mine.Where(a => a.Pillar == PensionPillar.Pillar3a).ToList();
            var payments = pillar3aPerYear(id);
            var capitalNow = mine.Where(a => a.Pillar is PensionPillar.Pillar3a or PensionPillar.Pillar3b or PensionPillar.VestedBenefits)
                .Sum(a => a.Statements[^1].Balance);
            var capital = capitalNow + payments * yearsLeft;

            // One 3a account per year, from 60 on; the payments to come are split across the accounts.
            var withdrawals = new List<Pillar3aWithdrawal>();
            if (retirementYear is { } year && pillar3a.Count > 0)
            {
                var share = pillar3a.Count == 0 ? 0 : payments * yearsLeft / pillar3a.Count;
                // The years before 65, so no 3a withdrawal falls in the year the pension fund pays out.
                var lastAge = SwissPension.RetirementAge - 1;
                var firstAge = Math.Max(SwissPension.Pillar3aEarliestWithdrawalAge, lastAge - pillar3a.Count + 1);
                for (var i = 0; i < pillar3a.Count; i++)
                {
                    var age = Math.Min(lastAge, firstAge + i);
                    withdrawals.Add(new Pillar3aWithdrawal(pillar3a[i].Name, year - SwissPension.RetirementAge + age, age,
                        pillar3a[i].Statements[^1].Balance + share));
                }
            }

            result.Add(new PersonRetirement(id, person?.Name ?? "", retirementYear, Math.Round(pension, 2), capital, withdrawals));
        }

        var need = Math.Round(livingCostsPerMonth * needPercent / 100m, 2);
        return new RetirementGap(need, result.Sum(r => r.PensionPerMonth),
            Math.Round(result.Sum(r => r.CapitalAtRetirement) / (CapitalYears * 12), 2), result);
    }
}
