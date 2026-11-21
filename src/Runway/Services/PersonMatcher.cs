using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Runway.Models;

namespace Runway.Services;

/// <summary>
/// Finds the household member a name on a document refers to. Letters write names in many ways
/// ("Herr Lukas Meier", "MEIER LUKAS", "Lukas M."), so names are compared word by word,
/// ignoring case and accents.
/// </summary>
public static class PersonMatcher
{
    /// <summary>The one member the name points to, or null when it fits nobody or several people.</summary>
    public static Person? Match(string? name, IEnumerable<Person> people)
    {
        var words = Words(name ?? "");
        if (words.Count == 0)
            return null;

        var names = people.Select(p => (Person: p, Words: Words(p.Name))).ToList();

        // Only words that belong to one member decide, usually first names; a shared family name doesn't.
        var named = words
            .Select(w => names.Where(n => n.Words.Contains(w)).Select(n => n.Person).ToList())
            .Where(owners => owners.Count == 1)
            .Select(owners => owners[0])
            .Distinct()
            .ToList();

        // Several members named means the document is for them together.
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>The words of a name, lower case without accents, leaving out initials.</summary>
    private static HashSet<string> Words(string name)
    {
        var plain = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(char.IsLetter(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return plain.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToHashSet();
    }
}
