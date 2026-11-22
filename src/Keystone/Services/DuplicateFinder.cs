using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>How sure Keystone is that a document was filed before.</summary>
public enum DuplicateMatch
{
    /// <summary>Byte for byte the same file.</summary>
    SameFile,

    /// <summary>The same QR-bill reference: the same bill, perhaps scanned again.</summary>
    SameBill,

    /// <summary>Same sender, amount and date: very likely the same paper.</summary>
    Similar,
}

public sealed record Duplicate(HouseholdDocument Document, DuplicateMatch Match);

/// <summary>Notices a document being filed a second time, so bills aren't counted or reminded twice.</summary>
public static class DuplicateFinder
{
    /// <summary>A fingerprint of the file's contents (SHA-256, lower-case hex).</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The document already filed that this one repeats, the surest match first; null when it is new.</summary>
    public static Duplicate? Find(
        IEnumerable<HouseholdDocument> filed, string hash, string sender, decimal? amount, DateOnly? date, string reference)
    {
        var documents = filed as IReadOnlyCollection<HouseholdDocument> ?? filed.ToList();
        if (hash != "" && documents.FirstOrDefault(d => d.FileHash == hash) is { } same)
            return new Duplicate(same, DuplicateMatch.SameFile);

        var cleanReference = Clean(reference);
        if (cleanReference.Length >= 6 && documents.FirstOrDefault(d => Clean(d.PaymentReference) == cleanReference) is { } bill)
            return new Duplicate(bill, DuplicateMatch.SameBill);

        if (amount is > 0 && date is { } day && sender.Trim() is { Length: > 0 } from &&
            documents.FirstOrDefault(d => d.Amount == amount && d.Date == day && SameSender(d.Sender, from)) is { } similar)
            return new Duplicate(similar, DuplicateMatch.Similar);

        return null;
    }

    /// <summary>"Helsana" and "Helsana Versicherungen AG" are the same sender.</summary>
    public static bool SameSender(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        if (a.Length == 0 || b.Length == 0)
            return false;
        return a.Contains(b, StringComparison.CurrentCultureIgnoreCase) || b.Contains(a, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string Clean(string reference) => new(reference.Where(char.IsLetterOrDigit).ToArray());
}
