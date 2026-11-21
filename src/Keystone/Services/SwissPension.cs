using System;
using System.Collections.Generic;
using System.Linq;

namespace Keystone.Services;

/// <summary>Swiss retirement provision rules the app uses.</summary>
public static class SwissPension
{
    /// <summary>Reference age for AHV and pension funds; women born from 1964 on also retire at 65 (AHV 21).</summary>
    public const int RetirementAge = 65;

    /// <summary>Pillar 3a money can be drawn at the earliest five years before the reference age.</summary>
    public const int Pillar3aEarliestWithdrawalAge = 60;

    /// <summary>Legal minimum share of the mandatory BVG savings paid out yearly as pension.</summary>
    public const decimal MinimumConversionRate = 0.068m;

    /// <summary>Maximum tax-deductible pillar 3a payments per person and year (CHF), set by the Federal Council.</summary>
    private static readonly SortedDictionary<int, (decimal WithPensionFund, decimal WithoutPensionFund)> Pillar3aLimits = new()
    {
        [2021] = (6_883m, 34_416m),
        [2022] = (6_883m, 34_416m),
        [2023] = (7_056m, 35_280m),
        [2024] = (7_056m, 35_280m),
        [2025] = (7_258m, 36_288m),
        [2026] = (7_258m, 36_288m),
    };

    /// <summary>The last year whose limits are known; later years use these until updated.</summary>
    public static int LatestKnownLimitYear => Pillar3aLimits.Keys.Last();

    /// <summary>
    /// The most that can be paid into pillar 3a in <paramref name="year"/>. Without a pension fund
    /// (e.g. self-employed) it is 20% of net earned income up to the returned maximum.
    /// </summary>
    public static decimal Pillar3aLimit(int year, bool hasPensionFund)
    {
        var known = Pillar3aLimits.Keys.Where(y => y <= year).DefaultIfEmpty(Pillar3aLimits.Keys.First()).Max();
        var limits = Pillar3aLimits[known];
        return hasPensionFund ? limits.WithPensionFund : limits.WithoutPensionFund;
    }
}
