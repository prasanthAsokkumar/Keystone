using System;
using System.Collections.Generic;

namespace Keystone.Services;

/// <summary>
/// Splits a bill that is too big to pay at once into parts, e.g. a tax bill paid over six months.
/// Parts are whole 5 centimes (the smallest Swiss coin); the last part takes what is left, so they add up exactly.
/// </summary>
public static class InstalmentPlan
{
    public const int MaxParts = 36;

    public sealed record Part(DateOnly DueDate, decimal Amount);

    /// <param name="total">What the parts must add up to.</param>
    /// <param name="count">How many parts (2 to <see cref="MaxParts"/>).</param>
    /// <param name="first">When the first part is due; the others follow every <paramref name="everyMonths"/> months.</param>
    public static IReadOnlyList<Part> Split(decimal total, int count, DateOnly first, int everyMonths = 1)
    {
        count = Math.Clamp(count, 1, MaxParts);
        var parts = new List<Part>(count);
        var each = Math.Floor(total / count * 20) / 20;
        for (var i = 0; i < count; i++)
            parts.Add(new Part(DueDate(first, i, everyMonths), i == count - 1 ? total - each * (count - 1) : each));
        return parts;
    }

    /// <summary>
    /// The date of part <paramref name="index"/> (from 0), counted from the first one each time, so a plan starting on
    /// 31 January is due on the 28th in February but on the 31st again in March.
    /// </summary>
    public static DateOnly DueDate(DateOnly first, int index, int everyMonths) =>
        first.AddMonths(index * Math.Clamp(everyMonths, 1, 12));
}
