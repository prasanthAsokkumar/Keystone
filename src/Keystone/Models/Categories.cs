using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Localization;

namespace Keystone.Models;

/// <summary>Suggested categories. Users can still type their own.</summary>
public static class Categories
{
    public static IReadOnlyList<string> Expense { get; } =
    [
        "Rent",
        "Bills",
        "Health insurance",
        "Other insurance",
        "Groceries",
        "Transport",
        "Subscriptions",
        "Taxes",
        Pillar3a,
        PensionBuyIn,
        Childcare,
        MedicalCosts,
        Donations,
        "Other",
    ];

    public static IReadOnlyList<string> Income { get; } =
    [
        "Salary",
        "Freelance",
        "Investments",
        "Other",
    ];

    public const string Uncategorized = "Uncategorized";

    /// <summary>Payments into a 3a account; the Retirement page adds these up against the yearly limit.</summary>
    public const string Pillar3a = "Pillar 3a";

    // Categories the tax return helper adds up as deductions.

    /// <summary>Voluntary payments into the pension fund ("Einkauf").</summary>
    public const string PensionBuyIn = "Pension fund buy-in";

    public const string Childcare = "Childcare";

    /// <summary>Doctor, dentist and pharmacy costs paid yourself.</summary>
    public const string MedicalCosts = "Medical costs";

    public const string Donations = "Donations";

    public const string DebtRepayment = "Debt repayment";

    public const string HealthInsurance = "Health insurance";

    public const string OtherInsurance = "Other insurance";

    public const string Taxes = "Taxes";

    /// <summary>Premiums: listed on the Insurance page rather than with the other expenses.</summary>
    public static bool IsInsurance(string category) =>
        string.Equals(category, HealthInsurance, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(category, OtherInsurance, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tax payments: listed on the Taxes page rather than with the other expenses.</summary>
    public static bool IsTaxes(string category) => string.Equals(category, Taxes, StringComparison.OrdinalIgnoreCase);

    /// <summary>Built-in categories are stored in English and shown in the app's language.</summary>
    public static string Display(string category) =>
        IsBuiltIn(category) ? Loc.T(category) : category;

    /// <summary>The stored form of a category typed or picked in any language, e.g. "Miete" becomes "Rent".</summary>
    public static string Normalize(string category)
    {
        var trimmed = category.Trim();
        return All.FirstOrDefault(c => Loc.AllTranslations(c).Any(t => t.Equals(trimmed, StringComparison.CurrentCultureIgnoreCase)))
            ?? trimmed;
    }

    public static IReadOnlyList<string> DisplayOptions(bool isIncome) =>
        (isIncome ? Income : Expense).Select(Display).ToList();

    private static IEnumerable<string> All => Expense.Concat(Income).Append(Uncategorized).Append(DebtRepayment);

    private static bool IsBuiltIn(string category) => All.Contains(category);
}
