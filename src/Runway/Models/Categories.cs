using System;
using System.Collections.Generic;
using System.Linq;
using Runway.Localization;

namespace Runway.Models;

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

    public const string DebtRepayment = "Debt repayment";

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
