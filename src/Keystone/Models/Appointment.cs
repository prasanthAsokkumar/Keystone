using System;

namespace Keystone.Models;

/// <summary>
/// An appointment: the doctor, a meeting at school, a viewing. It can be kept in step with a calendar in the
/// Mac's Calendar app.
/// </summary>
public class Appointment
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string Location { get; set; } = "";

    public string Notes { get; set; } = "";

    /// <summary>Local time. For an all-day appointment, midnight of its first day.</summary>
    public DateTime Start { get; set; }

    /// <summary>Local time. For an all-day appointment, midnight of its last day.</summary>
    public DateTime End { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>Who it is for; null means the household.</summary>
    public int? PersonId { get; set; }

    /// <summary>Minutes before the start to be reminded; null for no reminder from Keystone.</summary>
    public int? RemindMinutesBefore { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When it was last changed in Keystone.</summary>
    public DateTime UpdatedAt { get; set; }

    // ── Calendar link ───────────────────────────────────────────────────────

    /// <summary>The calendar it is kept in step with; null when it isn't linked.</summary>
    public string? CalendarId { get; set; }

    /// <summary>Identifies the event in that calendar.</summary>
    public string? CalendarEventKey { get; set; }

    /// <summary>When Keystone and the calendar last agreed on it.</summary>
    public DateTime? SyncedAt { get; set; }

    public bool IsLinked => CalendarEventKey is not null;

    /// <summary>The first moment after it: the end, or the day after the last day for an all-day one.</summary>
    public DateTime EndsAt => IsAllDay ? End.Date.AddDays(1) : End;

    public bool HasPassed(DateTime now) => EndsAt <= now;

    public bool IsOn(DateOnly day)
    {
        var date = day.ToDateTime(TimeOnly.MinValue);
        return Start < date.AddDays(1) && EndsAt > date;
    }

    /// <summary>When to be reminded; an all-day appointment counts from <paramref name="allDayHour"/> on its day.</summary>
    public DateTime? RemindAt(int allDayHour) => RemindMinutesBefore is { } minutes
        ? (IsAllDay ? Start.Date.AddHours(allDayHour) : Start).AddMinutes(-minutes)
        : null;

    public void Unlink()
    {
        CalendarId = null;
        CalendarEventKey = null;
        SyncedAt = null;
    }
}
