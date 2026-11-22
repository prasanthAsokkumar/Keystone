using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Keystone.Services.Mac;

public enum CalendarPermission
{
    /// <summary>Not running as a Mac app bundle, so macOS won't give access (e.g. during development).</summary>
    Unsupported,
    NotAsked,
    Denied,
    Allowed,
}

/// <summary>
/// The Mac's calendars through EventKit: whatever the Calendar app shows, including iCloud, Google and
/// Exchange calendars. All calls are made on the UI thread.
/// </summary>
public static unsafe class MacCalendar
{
    private const long EntityTypeEvent = 0;
    private const long SpanThisEvent = 0;

    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _access;
    private static IntPtr _accessBlock;
    private static IntPtr _store;
    private static bool? _supported;

    /// <summary>EventKit needs a real app bundle that says why it wants the calendars, or macOS ends the app.</summary>
    public static bool IsSupported
    {
        get
        {
            _supported ??= OperatingSystem.IsMacOS() && ObjC.BundleIdentifier() is { Length: > 0 } &&
                           ObjC.LoadFramework("EventKit");
            return _supported.Value;
        }
    }

    public static CalendarPermission Permission
    {
        get
        {
            if (!IsSupported)
                return CalendarPermission.Unsupported;
            // 0 not determined, 1 restricted, 2 denied, 3 full access (or "authorized" before macOS 14), 4 write only
            var status = (long)ObjC.SendWithLong(ObjC.Class("EKEventStore"), ObjC.Sel("authorizationStatusForEntityType:"), EntityTypeEvent);
            return status switch
            {
                3 => CalendarPermission.Allowed,
                0 => CalendarPermission.NotAsked,
                _ => CalendarPermission.Denied,
            };
        }
    }

    /// <summary>Asks macOS for access to the calendars; the system shows its prompt only the first time.</summary>
    public static Task<bool> RequestAccessAsync()
    {
        if (!IsSupported)
            return Task.FromResult(false);

        lock (Gate)
        {
            if (_access is { Task.IsCompleted: false } running)
                return running.Task;
            _access = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_accessBlock == IntPtr.Zero)
                _accessBlock = ObjC.GlobalBlock((IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&AccessReply, "v@?B@");

            var store = Store();
            var fullAccess = ObjC.Sel("requestFullAccessToEventsWithCompletion:");
            if (ObjC.SendReturnBool(store, ObjC.Sel("respondsToSelector:"), fullAccess) != 0)
                ObjC.Send(store, fullAccess, _accessBlock); // macOS 14 and later
            else
                ObjC.SendLong(store, ObjC.Sel("requestAccessToEntityType:completion:"), EntityTypeEvent, _accessBlock);
            return _access.Task;
        }
    }

    [UnmanagedCallersOnly]
    private static void AccessReply(IntPtr block, byte granted, IntPtr error) => _access?.TrySetResult(granted != 0);

    /// <summary>Opens Privacy &amp; Security → Calendars in System Settings.</summary>
    public static void OpenSystemSettings() =>
        System.Diagnostics.Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Calendars");

    /// <summary>Opens the Calendar app.</summary>
    public static void OpenCalendarApp() => System.Diagnostics.Process.Start("open", "-a Calendar");

    private static IntPtr Store()
    {
        if (_store == IntPtr.Zero)
            _store = ObjC.New("EKEventStore");
        return _store;
    }

    /// <summary>A fresh store after access was granted, so it sees the calendars.</summary>
    public static void Reset()
    {
        if (_store != IntPtr.Zero)
            ObjC.Send(_store, ObjC.Sel("reset"));
    }

    /// <summary>Calendars that events can be added to.</summary>
    public static IReadOnlyList<CalendarInfo> WritableCalendars()
    {
        var calendars = new List<CalendarInfo>();
        if (Permission != CalendarPermission.Allowed)
            return calendars;

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var all = ObjC.SendWithLong(Store(), ObjC.Sel("calendarsForEntityType:"), EntityTypeEvent);
            foreach (var calendar in Items(all))
            {
                if (ObjC.SendReturnBool(calendar, ObjC.Sel("allowsContentModifications")) == 0)
                    continue;
                calendars.Add(Info(calendar));
            }
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }

        calendars.Sort((a, b) => string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase));
        return calendars;
    }

    /// <summary>Adds a calendar next to the default one (usually in iCloud), so it shows on the iPhone too.</summary>
    public static CalendarInfo CreateCalendar(string title)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var store = Store();
            var calendar = ObjC.SendWithLong(ObjC.Class("EKCalendar"), ObjC.Sel("calendarForEntityType:eventStore:"), EntityTypeEvent, store);
            ObjC.Send(calendar, ObjC.Sel("setTitle:"), ObjC.NSString(title));
            var defaultCalendar = ObjC.Send(store, ObjC.Sel("defaultCalendarForNewEvents"));
            var source = defaultCalendar != IntPtr.Zero
                ? ObjC.Send(defaultCalendar, ObjC.Sel("source"))
                : FirstLocalSource(store);
            if (source == IntPtr.Zero)
                throw new InvalidOperationException("There is no calendar account to add it to.");
            ObjC.Send(calendar, ObjC.Sel("setSource:"), source);
            if (ObjC.SendCommit(store, ObjC.Sel("saveCalendar:commit:error:"), calendar, 1, out var error) == 0)
                throw new InvalidOperationException(ErrorText(error));
            return Info(calendar);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr FirstLocalSource(IntPtr store)
    {
        foreach (var source in Items(ObjC.Send(store, ObjC.Sel("sources"))))
            if (ObjC.SendReturnLong(source, ObjC.Sel("sourceType")) == 0) // EKSourceTypeLocal
                return source;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Reading and writing events. Everything EventKit hands out lives until the session ends, so a whole
    /// sync runs in one session, without awaiting in between.
    /// </summary>
    public static Session Open() => new();

    public sealed class Session : ICalendarStore, IDisposable
    {
        private readonly IntPtr _pool = ObjC.objc_autoreleasePoolPush();
        private readonly Dictionary<string, IntPtr> _seen = [];

        public IReadOnlyList<CalendarEvent> GetEvents(string calendarId, DateTime from, DateTime to)
        {
            var calendar = Calendar(calendarId);
            var store = Store();
            var predicate = ObjC.Send(store, ObjC.Sel("predicateForEventsWithStartDate:endDate:calendars:"),
                NSDate(from), NSDate(to), ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObject:"), calendar));
            var events = new List<CalendarEvent>();
            foreach (var item in Items(ObjC.Send(store, ObjC.Sel("eventsMatchingPredicate:"), predicate)))
            {
                var calendarEvent = Read(item);
                if (calendarEvent.Key == "")
                    continue;
                _seen[calendarEvent.Key] = item;
                events.Add(calendarEvent);
            }
            return events;
        }

        public CalendarEvent? Find(string calendarId, string key)
        {
            var item = Lookup(calendarId, key);
            return item == IntPtr.Zero ? null : Read(item);
        }

        public string Save(string calendarId, CalendarEvent calendarEvent)
        {
            var store = Store();
            var item = calendarEvent.Key == "" ? IntPtr.Zero : Lookup(calendarId, calendarEvent.Key);
            if (item == IntPtr.Zero)
            {
                item = ObjC.Send(ObjC.Class("EKEvent"), ObjC.Sel("eventWithEventStore:"), store);
                ObjC.Send(item, ObjC.Sel("setCalendar:"), Calendar(calendarId));
            }

            ObjC.Send(item, ObjC.Sel("setTitle:"), ObjC.NSString(calendarEvent.Title));
            ObjC.Send(item, ObjC.Sel("setLocation:"), calendarEvent.Location == "" ? IntPtr.Zero : ObjC.NSString(calendarEvent.Location));
            ObjC.Send(item, ObjC.Sel("setNotes:"), calendarEvent.Notes == "" ? IntPtr.Zero : ObjC.NSString(calendarEvent.Notes));
            ObjC.SendBool(item, ObjC.Sel("setAllDay:"), calendarEvent.IsAllDay ? (byte)1 : (byte)0);
            if (calendarEvent.IsAllDay)
            {
                ObjC.Send(item, ObjC.Sel("setStartDate:"), NSDate(calendarEvent.Start.Date));
                ObjC.Send(item, ObjC.Sel("setEndDate:"), NSDate(calendarEvent.End.Date.AddDays(1).AddSeconds(-1)));
            }
            else
            {
                ObjC.Send(item, ObjC.Sel("setStartDate:"), NSDate(calendarEvent.Start));
                ObjC.Send(item, ObjC.Sel("setEndDate:"), NSDate(calendarEvent.End));
            }

            if (ObjC.SendSpanCommit(store, ObjC.Sel("saveEvent:span:commit:error:"), item, SpanThisEvent, 1, out var error) == 0)
                throw new InvalidOperationException(ErrorText(error));
            var key = KeyOf(item);
            _seen[key] = item;
            return key;
        }

        public void Delete(string calendarId, string key)
        {
            var item = Lookup(calendarId, key);
            if (item == IntPtr.Zero)
                return; // already gone
            if (ObjC.SendSpanCommit(Store(), ObjC.Sel("removeEvent:span:commit:error:"), item, SpanThisEvent, 1, out var error) == 0)
                throw new InvalidOperationException(ErrorText(error));
            _seen.Remove(key);
        }

        /// <summary>An occurrence of a repeating event is only found by reading its day.</summary>
        private IntPtr Lookup(string calendarId, string key)
        {
            if (_seen.TryGetValue(key, out var seen))
                return seen;
            if (key.Contains('|'))
                return IntPtr.Zero;
            var item = ObjC.Send(Store(), ObjC.Sel("eventWithIdentifier:"), ObjC.NSString(key));
            if (item == IntPtr.Zero)
                return IntPtr.Zero;
            // Moved to another calendar: gone from this one.
            var calendar = ObjC.Send(item, ObjC.Sel("calendar"));
            return ObjC.ToManagedString(ObjC.Send(calendar, ObjC.Sel("calendarIdentifier"))) == calendarId ? item : IntPtr.Zero;
        }

        private static IntPtr Calendar(string calendarId)
        {
            var calendar = ObjC.Send(Store(), ObjC.Sel("calendarWithIdentifier:"), ObjC.NSString(calendarId));
            if (calendar == IntPtr.Zero)
                throw new InvalidOperationException("The calendar is no longer there.");
            return calendar;
        }

        public void Dispose() => ObjC.objc_autoreleasePoolPop(_pool);
    }

    // ── Reading EventKit objects ────────────────────────────────────────────

    private static CalendarInfo Info(IntPtr calendar)
    {
        var source = ObjC.Send(calendar, ObjC.Sel("source"));
        return new CalendarInfo(
            ObjC.ToManagedString(ObjC.Send(calendar, ObjC.Sel("calendarIdentifier"))) ?? "",
            ObjC.ToManagedString(ObjC.Send(calendar, ObjC.Sel("title"))) ?? "",
            source == IntPtr.Zero ? "" : ObjC.ToManagedString(ObjC.Send(source, ObjC.Sel("title"))) ?? "");
    }

    private static CalendarEvent Read(IntPtr item)
    {
        var isAllDay = ObjC.SendReturnBool(item, ObjC.Sel("isAllDay")) != 0;
        var start = FromNSDate(ObjC.Send(item, ObjC.Sel("startDate"))) ?? DateTime.Now;
        var end = FromNSDate(ObjC.Send(item, ObjC.Sel("endDate"))) ?? start;
        if (isAllDay)
        {
            // Calendars end all-day events at 23:59:59 or at the next midnight.
            var last = end > start ? end.AddSeconds(-1).Date : start.Date;
            (start, end) = (start.Date, last < start.Date ? start.Date : last);
        }

        return new CalendarEvent
        {
            Key = KeyOf(item),
            Title = ObjC.ToManagedString(ObjC.Send(item, ObjC.Sel("title"))) ?? "",
            Location = ObjC.ToManagedString(ObjC.Send(item, ObjC.Sel("location"))) ?? "",
            Notes = ObjC.ToManagedString(ObjC.Send(item, ObjC.Sel("notes"))) ?? "",
            Start = start,
            End = end,
            IsAllDay = isAllDay,
            LastModified = FromNSDate(ObjC.Send(item, ObjC.Sel("lastModifiedDate"))),
        };
    }

    /// <summary>
    /// The event's identifier; for an occurrence of a repeating event, with the date it was first planned for,
    /// which stays the same when that one occurrence is moved.
    /// </summary>
    private static string KeyOf(IntPtr item)
    {
        var id = ObjC.ToManagedString(ObjC.Send(item, ObjC.Sel("eventIdentifier"))) ?? "";
        if (id == "")
            return "";
        var repeats = ObjC.SendReturnBool(item, ObjC.Sel("hasRecurrenceRules")) != 0 ||
                      ObjC.SendReturnBool(item, ObjC.Sel("isDetached")) != 0;
        if (!repeats)
            return id;
        var occurrence = ObjC.Send(item, ObjC.Sel("occurrenceDate"));
        return occurrence == IntPtr.Zero
            ? id
            : $"{id}|{(long)ObjC.SendReturnDouble(occurrence, ObjC.Sel("timeIntervalSince1970"))}";
    }

    private static IEnumerable<IntPtr> Items(IntPtr array)
    {
        if (array == IntPtr.Zero)
            yield break;
        var count = ObjC.SendReturnLong(array, ObjC.Sel("count"));
        for (long i = 0; i < count; i++)
            yield return ObjC.SendWithLong(array, ObjC.Sel("objectAtIndex:"), i);
    }

    private static IntPtr NSDate(DateTime local)
    {
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeMilliseconds() / 1000.0;
        return ObjC.SendDouble(ObjC.Class("NSDate"), ObjC.Sel("dateWithTimeIntervalSince1970:"), seconds);
    }

    private static DateTime? FromNSDate(IntPtr date)
    {
        if (date == IntPtr.Zero)
            return null;
        var seconds = ObjC.SendReturnDouble(date, ObjC.Sel("timeIntervalSince1970"));
        var local = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)).LocalDateTime;
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    }

    private static string ErrorText(IntPtr error) =>
        error == IntPtr.Zero
            ? "The calendar didn't accept the change."
            : ObjC.ToManagedString(ObjC.Send(error, ObjC.Sel("localizedDescription"))) ?? "The calendar didn't accept the change.";
}
