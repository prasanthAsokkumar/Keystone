using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.Services;

/// <remarks>Stored by name in the saved scenario.</remarks>
public enum ScenarioChangeKind
{
    /// <summary>A repeating entry changes to a share of its amount, e.g. working 80%.</summary>
    ChangeEntry,

    /// <summary>A repeating entry stops, e.g. a job or a subscription ending.</summary>
    StopEntry,

    /// <summary>A new monthly income or expense, e.g. daycare.</summary>
    AddMonthly,

    /// <summary>A one-time income or expense, e.g. a new car.</summary>
    AddOnce,
}

/// <summary>One "what if" change, from a date on.</summary>
/// <param name="EntryId">For changing or stopping an entry: which one.</param>
/// <param name="Percent">For changing an entry: its new share, e.g. 80.</param>
/// <param name="Amount">For new amounts: positive, with <paramref name="IsIncome"/> deciding the direction.</param>
public sealed record ScenarioChange(
    ScenarioChangeKind Kind, DateOnly From, int? EntryId = null, decimal Percent = 100, decimal Amount = 0,
    bool IsIncome = false, string Description = "");

/// <summary>Applies what-if changes to the household's entries, leaving the real ones untouched.</summary>
public static class Scenario
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The entries as they would be with the changes. Ids of new entries are negative.</summary>
    public static List<Transaction> Apply(IReadOnlyList<Transaction> entries, IEnumerable<ScenarioChange> changes)
    {
        var result = entries.ToList();
        var newId = -1;

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case ScenarioChangeKind.ChangeEntry or ScenarioChangeKind.StopEntry
                    when result.FirstOrDefault(t => t.Id == change.EntryId) is { } entry:
                    result.Remove(entry);
                    // Until the change, the entry stays as it is…
                    if (entry.Date < change.From)
                        result.Add(Copy(entry, end: Min(entry.EndDate, change.From.AddDays(-1))));
                    // …and from then on runs at its new share.
                    if (change.Kind == ScenarioChangeKind.ChangeEntry && change.Percent > 0)
                    {
                        var start = FirstOccurrenceFrom(entry, change.From);
                        if (start is not null && (entry.EndDate is null || start <= entry.EndDate))
                            result.Add(Copy(entry, start, entry.EndDate, Math.Round(entry.Amount * change.Percent / 100m, 2), newId--));
                    }
                    break;

                case ScenarioChangeKind.AddMonthly or ScenarioChangeKind.AddOnce when change.Amount > 0:
                    result.Add(new Transaction
                    {
                        Id = newId--,
                        Date = change.From,
                        Description = change.Description,
                        Amount = change.Amount,
                        Type = change.IsIncome ? TransactionType.Income : TransactionType.Expense,
                        Recurrence = change.Kind == ScenarioChangeKind.AddMonthly ? Recurrence.Monthly : Recurrence.OneTime,
                    });
                    break;
            }
        }

        return result;
    }

    /// <summary>A repeating entry's first date on or after <paramref name="from"/>, keeping its day of the month.</summary>
    private static DateOnly? FirstOccurrenceFrom(Transaction entry, DateOnly from)
    {
        if (!entry.IsRecurring)
            return entry.Date >= from ? entry.Date : null;
        for (var i = 0; i < 1200; i++)
        {
            var date = entry.Date.AddMonths(i * entry.IntervalMonths);
            if (date >= from)
                return date;
        }
        return null;
    }

    private static DateOnly? Min(DateOnly? a, DateOnly b) => a is { } x && x < b ? x : b;

    private static Transaction Copy(Transaction entry, DateOnly? start = null, DateOnly? end = null, decimal? amount = null, int? id = null) => new()
    {
        Id = id ?? entry.Id,
        Date = start ?? entry.Date,
        EndDate = end,
        Description = entry.Description,
        Category = entry.Category,
        Amount = amount ?? entry.Amount,
        Type = entry.Type,
        Recurrence = entry.Recurrence,
        PersonId = entry.PersonId,
        AccountId = entry.AccountId,
        ToAccountId = entry.ToAccountId,
        IsDebtPayment = entry.IsDebtPayment,
    };

    public static string Describe(ScenarioChange change, IReadOnlyList<Transaction> entries)
    {
        var entry = entries.FirstOrDefault(t => t.Id == change.EntryId)?.Description ?? "";
        var from = change.From.ToDateTime(TimeOnly.MinValue);
        return change.Kind switch
        {
            ScenarioChangeKind.ChangeEntry => Loc.F("{0} at {1}% from {2:MMM yyyy}", entry, change.Percent, from),
            ScenarioChangeKind.StopEntry => Loc.F("{0} stops from {1:MMM yyyy}", entry, from),
            ScenarioChangeKind.AddMonthly => Loc.F(change.IsIncome ? "{0}: +{1} a month from {2:MMM yyyy}" : "{0}: −{1} a month from {2:MMM yyyy}",
                change.Description, change.Amount.ToString("N0", Loc.Instance.Culture), from),
            _ => Loc.F(change.IsIncome ? "{0}: +{1} once in {2:MMM yyyy}" : "{0}: −{1} once in {2:MMM yyyy}",
                change.Description, change.Amount.ToString("N0", Loc.Instance.Culture), from),
        };
    }

    public static string Save(IEnumerable<ScenarioChange> changes) => JsonSerializer.Serialize(changes, Json);

    public static List<ScenarioChange> Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ScenarioChange>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
