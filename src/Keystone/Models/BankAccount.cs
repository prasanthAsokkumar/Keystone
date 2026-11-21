using System;
using System.Linq;
using Keystone.Localization;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum BankAccountKind { Current, Savings, CreditCard, Cash }

/// <summary>A bank account, card or cash box. Entries belong to one; together they make the household's cash.</summary>
public class BankAccount
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public BankAccountKind Kind { get; set; }

    /// <summary>Used to recognise the account in imported bank statements. Stored without spaces.</summary>
    public string Iban { get; set; } = "";

    /// <summary>Balance before the account's first entry.</summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>How far the account may go below zero: an overdraft, or a credit card's limit.</summary>
    public decimal OverdraftLimit { get; set; }

    public static string KindName(BankAccountKind kind) => Loc.T(kind switch
    {
        BankAccountKind.Savings => "Savings account",
        BankAccountKind.CreditCard => "Credit card",
        BankAccountKind.Cash => "Cash on hand",
        _ => "Current account",
    });

    /// <summary>IBANs are compared without spaces and in capitals.</summary>
    public static string NormalizeIban(string? iban) =>
        string.Concat((iban ?? "").Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>"CH93 0076 2011 6238 5295 7" for display.</summary>
    public static string FormatIban(string iban) =>
        string.Join(" ", Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));
}
