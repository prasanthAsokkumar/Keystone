using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>
/// Suggests a category for a bank booking: first from how the household categorised the same payee before,
/// then from well-known Swiss shops, insurers, utilities and offices.
/// </summary>
public static class CategoryGuesser
{
    private static readonly (string Category, bool Income, string[] Words)[] Known =
    [
        ("Salary", true, ["lohn", "salaer", "salar", "salary", "gehalt", "lohnzahlung", "salaire"]),
        ("Groceries", false, ["migros", "coop", "denner", "lidl", "aldi", "volg", "spar ", "landi", "alnatura", "farmy"]),
        ("Transport", false, ["sbb", "cff", "bls", "postauto", "vbz", "bvb", "tpg", "mobility", "uber", "tankstelle",
            "shell", "avia", "socar", "parking", "parkhaus", "easypark", "twint parking"]),
        ("Subscriptions", false, ["swisscom", "sunrise", "salt ", "netflix", "spotify", "apple.com", "youtube", "disney",
            "wingo", "yallo", "upc", "digitec", "microsoft"]),
        ("Health insurance", false, ["helsana", "css ", "swica", "sanitas", "visana", "concordia", "assura", "groupe mutuel",
            "atupri", "kpt", "sympany", "egk", "oekk", "kolping", "krankenkasse"]),
        ("Other insurance", false, ["mobiliar", "axa", "zurich vers", "allianz", "generali", "baloise", "helvetia", "vaudoise",
            "smile.direct", "tcs", "versicherung"]),
        ("Taxes", false, ["steuerverwaltung", "steueramt", "steuer", "administration fiscale", "impots", "impôts"]),
        ("Rent", false, ["miete", "mietzins", "loyer"]),
        ("Bills", false, ["iwb", "ewz", "ekz", "bkw", "groupe e", "romande energie", "primeo", "energie", "strom", "serafe",
            "wasser", "gasversorgung"]),
        (Categories.Pillar3a, false, ["3a", "viac", "frankly", "finpension", "vorsorgekonto"]),
        (Categories.Childcare, false, ["kita", "krippe", "tagesstaette", "hort ", "creche"]),
        (Categories.MedicalCosts, false, ["apotheke", "pharmacie", "zahnarzt", "spital", "hopital", "dr. med", "arztpraxis"]),
        (Categories.Donations, false, ["spende", "unicef", "caritas", "rotes kreuz", "srk", "msf", "wwf", "glueckskette", "heks"]),
    ];

    public static string Guess(string description, bool isIncome, IEnumerable<Transaction> history)
    {
        var key = PayeeKey(description);
        if (key != "")
        {
            var earlier = history
                .Where(t => t.IsIncome == isIncome && !t.IsTransfer && t.Category != "" && PayeeKey(t.Description) == key)
                .OrderByDescending(t => t.Date)
                .FirstOrDefault();
            if (earlier is not null)
                return earlier.Category;
        }

        var text = " " + Plain(description) + " ";
        foreach (var (category, income, words) in Known)
        {
            if (income == isIncome && words.Any(w => text.Contains(w, StringComparison.Ordinal)))
                return category;
        }

        return "Other";
    }

    /// <summary>The payee's first two words, lower case without accents: "Migros Basel 1234" and "MIGROS BASEL" match.</summary>
    internal static string PayeeKey(string description)
    {
        var words = Plain(description).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Any(char.IsLetter))
            .Take(2);
        return string.Join(" ", words);
    }

    private static string Plain(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(c) || c is '.' ? c : ' ');
        }
        return builder.ToString();
    }
}
