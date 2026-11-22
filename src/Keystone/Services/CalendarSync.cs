using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>A calendar in the Calendar app that appointments can be kept in.</summary>
public sealed record CalendarInfo(string Id, string Title, string Account)
{
    public override string ToString() => Account == "" || Account == Title ? Title : $"{Title} ({Account})";
}

/// <summary>An event as the calendar has it. Times are local; all-day events run from midnight to midnight of their last day.</summary>
public sealed class CalendarEvent
{
    /// <summary>
    /// Identifies the event. An occurrence of a repeating event has its own key, so each one can be an
    /// appointment of its own.
    /// </summary>
    public string Key { get; set; } = "";

    public string Title { get; set; } = "";

    public string Location { get; set; } = "";

    public string Notes { get; set; } = "";

    public DateTime Start { get; set; }

    public DateTime End { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>When it was last changed in the calendar, if the calendar knows.</summary>
    public DateTime? LastModified { get; set; }
}

/// <summary>Where events are read from and written to: the Mac's Calendar app, or a fake in tests.</summary>
public interface ICalendarStore
{
    /// <summary>Events in the calendar that overlap the time from <paramref name="from"/> to <paramref name="to"/>.</summary>
    IReadOnlyList<CalendarEvent> GetEvents(string calendarId, DateTime from, DateTime to);

    /// <summary>The event with this key wherever it is in time, or null if it is gone.</summary>
    CalendarEvent? Find(string calendarId, string key);

    /// <summary>Adds the event (empty key) or saves the one with its key; returns its key.</summary>
    string Save(string calendarId, CalendarEvent calendarEvent);

    void Delete(string calendarId, string key);
}

/// <summary>What a sync changed in Keystone, for saving.</summary>
public sealed class CalendarSyncResult
{
    /// <summary>Events new in the calendar, as new appointments.</summary>
    public List<Appointment> Added { get; } = [];

    /// <summary>Appointments whose fields or calendar link changed.</summary>
    public List<Appointment> Changed { get; } = [];

    /// <summary>Appointments whose event was deleted in the calendar.</summary>
    public List<Appointment> Removed { get; } = [];

    /// <summary>Events added to or changed in the calendar.</summary>
    public int Written { get; set; }

    public List<string> Errors { get; } = [];

    public bool ChangedKeystone => Added.Count + Changed.Count + Removed.Count > 0;
}

/// <summary>
/// Keeps appointments and one calendar in step, both ways. Whichever side changed since the last sync wins;
/// when both did, the later change wins. Only a span around today is compared, so old history is left alone.
/// </summary>
public static class CalendarSync
{
    /// <summary>How far back events are compared and new appointments sent.</summary>
    public static readonly TimeSpan Past = TimeSpan.FromDays(30);

    /// <summary>How far ahead events are compared.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(366);

    public static CalendarSyncResult Run(ICalendarStore store, string calendarId, IReadOnlyList<Appointment> appointments, DateTime now)
    {
        var result = new CalendarSyncResult();
        var from = now.Date - Past;
        var to = now.Date + Ahead;

        var events = new Dictionary<string, CalendarEvent>();
        foreach (var calendarEvent in store.GetEvents(calendarId, from, to))
            events.TryAdd(calendarEvent.Key, calendarEvent);

        foreach (var appointment in appointments)
        {
            try
            {
                SyncOne(store, calendarId, appointment, events, from, to, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{appointment.Title}: {ex.Message}");
            }
        }

        // What is left is new in the calendar.
        foreach (var calendarEvent in events.Values)
        {
            var appointment = new Appointment
            {
                CreatedAt = DateTime.Now,
                CalendarId = calendarId,
                CalendarEventKey = calendarEvent.Key,
            };
            CopyFrom(calendarEvent, appointment);
            appointment.UpdatedAt = DateTime.Now;
            appointment.SyncedAt = DateTime.Now;
            result.Added.Add(appointment);
        }

        return result;
    }

    private static void SyncOne(ICalendarStore store, string calendarId, Appointment appointment,
        Dictionary<string, CalendarEvent> events, DateTime from, DateTime to, CalendarSyncResult result)
    {
        // Linked to another calendar: the calendar was switched, so this one gets a copy.
        if (appointment.IsLinked && appointment.CalendarId != calendarId)
            appointment.Unlink();

        if (!appointment.IsLinked)
        {
            if (appointment.EndsAt < from)
                return; // long past: not worth sending
            Push(store, calendarId, appointment, result);
            return;
        }

        var key = appointment.CalendarEventKey!;
        if (!events.Remove(key, out var calendarEvent))
        {
            if (appointment.EndsAt < from || appointment.Start > to)
                return; // outside the span compared
            calendarEvent = store.Find(calendarId, key);
            if (calendarEvent is null)
            {
                // Deleted in the calendar. Unless it changed here since, delete it here too.
                if (IsChangedHere(appointment))
                {
                    appointment.CalendarEventKey = null;
                    Push(store, calendarId, appointment, result);
                }
                else
                    result.Removed.Add(appointment);
                return;
            }
        }

        if (SameContent(appointment, calendarEvent))
        {
            if (appointment.SyncedAt is null || IsChangedHere(appointment))
            {
                appointment.SyncedAt = DateTime.Now;
                result.Changed.Add(appointment);
            }
            return;
        }

        var changedThere = calendarEvent.LastModified is not { } modified || appointment.SyncedAt is not { } synced || modified > synced;
        var keepOurs = IsChangedHere(appointment) &&
                       (!changedThere || calendarEvent.LastModified is not { } there || appointment.UpdatedAt >= there);
        if (keepOurs)
        {
            Push(store, calendarId, appointment, result, calendarEvent.Key);
        }
        else
        {
            CopyFrom(calendarEvent, appointment);
            appointment.SyncedAt = DateTime.Now;
            result.Changed.Add(appointment);
        }
    }

    private static bool IsChangedHere(Appointment appointment) =>
        appointment.SyncedAt is not { } synced || appointment.UpdatedAt > synced;

    private static void Push(ICalendarStore store, string calendarId, Appointment appointment, CalendarSyncResult result, string key = "")
    {
        var calendarEvent = new CalendarEvent
        {
            Key = key,
            Title = appointment.Title,
            Location = appointment.Location,
            Notes = appointment.Notes,
            Start = appointment.IsAllDay ? appointment.Start.Date : appointment.Start,
            End = appointment.IsAllDay ? appointment.End.Date : appointment.End,
            IsAllDay = appointment.IsAllDay,
        };
        appointment.CalendarEventKey = store.Save(calendarId, calendarEvent);
        appointment.CalendarId = calendarId;
        appointment.SyncedAt = DateTime.Now;
        result.Written++;
        result.Changed.Add(appointment);
    }

    private static void CopyFrom(CalendarEvent calendarEvent, Appointment appointment)
    {
        appointment.Title = calendarEvent.Title.Trim() is { Length: > 0 } title ? title : "–";
        appointment.Location = calendarEvent.Location.Trim();
        appointment.Notes = calendarEvent.Notes.Trim();
        appointment.IsAllDay = calendarEvent.IsAllDay;
        appointment.Start = calendarEvent.IsAllDay ? calendarEvent.Start.Date : calendarEvent.Start;
        appointment.End = calendarEvent.IsAllDay ? calendarEvent.End.Date : calendarEvent.End;
        if (appointment.End < appointment.Start)
            appointment.End = appointment.Start;
        appointment.CalendarEventKey = calendarEvent.Key;
    }

    /// <summary>Times are compared to the minute: calendars may keep seconds Keystone doesn't.</summary>
    public static bool SameContent(Appointment appointment, CalendarEvent calendarEvent)
    {
        static long Minutes(DateTime time) => time.Ticks / TimeSpan.TicksPerMinute;

        if (appointment.Title != (calendarEvent.Title.Trim() is { Length: > 0 } title ? title : "–") ||
            appointment.Location != calendarEvent.Location.Trim() ||
            appointment.Notes != calendarEvent.Notes.Trim() ||
            appointment.IsAllDay != calendarEvent.IsAllDay)
            return false;

        return appointment.IsAllDay
            ? appointment.Start.Date == calendarEvent.Start.Date && appointment.End.Date == calendarEvent.End.Date
            : Minutes(appointment.Start) == Minutes(calendarEvent.Start) && Minutes(appointment.End) == Minutes(calendarEvent.End);
    }
}
