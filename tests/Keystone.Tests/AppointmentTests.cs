using Keystone.Data;
using Keystone.Models;
using Keystone.Services;
using Keystone.ViewModels;

namespace Keystone.Tests;

/// <summary>Formats times and dates, so it runs apart from the language tests.</summary>
[Collection(nameof(LanguageCollection))]
public sealed class AppointmentTests : IDisposable
{
    private const string CalendarId = "family";
    private static readonly DateTime Now = DateTime.Today.AddHours(10);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"keystone-test-{Guid.NewGuid():N}.db");
    private readonly KeystoneRepository _repository;

    public AppointmentTests()
    {
        _repository = new KeystoneRepository(_databasePath);
        _repository.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    /// <summary>A calendar kept in memory, like the Calendar app would keep it.</summary>
    private sealed class FakeCalendar : ICalendarStore
    {
        public Dictionary<string, CalendarEvent> Events { get; } = [];
        private int _next;

        public CalendarEvent Add(string title, DateTime start, DateTime end, DateTime? modified = null)
        {
            var e = new CalendarEvent { Key = $"ev{++_next}", Title = title, Start = start, End = end, LastModified = modified ?? DateTime.Now };
            Events[e.Key] = e;
            return e;
        }

        public IReadOnlyList<CalendarEvent> GetEvents(string calendarId, DateTime from, DateTime to) =>
            Events.Values.Where(e => e.End >= from && e.Start <= to).Select(Clone).ToList();

        public CalendarEvent? Find(string calendarId, string key) => Events.TryGetValue(key, out var e) ? Clone(e) : null;

        public string Save(string calendarId, CalendarEvent calendarEvent)
        {
            var saved = Clone(calendarEvent);
            if (saved.Key == "")
                saved.Key = $"ev{++_next}";
            saved.LastModified = DateTime.Now;
            Events[saved.Key] = saved;
            return saved.Key;
        }

        public void Delete(string calendarId, string key) => Events.Remove(key);

        private static CalendarEvent Clone(CalendarEvent e) => new()
        {
            Key = e.Key, Title = e.Title, Location = e.Location, Notes = e.Notes, Start = e.Start, End = e.End,
            IsAllDay = e.IsAllDay, LastModified = e.LastModified,
        };
    }

    private static Appointment Dentist() => new()
    {
        Id = 1, Title = "Dentist", Location = "Bahnhofstrasse 12", Start = Now.AddDays(2), End = Now.AddDays(2).AddHours(1),
        // Edited a little while ago, before any sync in the test.
        CreatedAt = DateTime.Now.AddMinutes(-5), UpdatedAt = DateTime.Now.AddMinutes(-5),
    };

    [Fact]
    public void NewAppointment_IsAddedToTheCalendar()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();

        var result = CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        var e = Assert.Single(calendar.Events.Values);
        Assert.Equal("Dentist", e.Title);
        Assert.Equal("Bahnhofstrasse 12", e.Location);
        Assert.Equal(e.Key, dentist.CalendarEventKey);
        Assert.Equal(CalendarId, dentist.CalendarId);
        Assert.Equal(1, result.Written);
        Assert.Contains(dentist, result.Changed);
    }

    [Fact]
    public void EventAddedInTheCalendar_BecomesAnAppointment()
    {
        var calendar = new FakeCalendar();
        calendar.Add("Parents' evening", Now.AddDays(3), Now.AddDays(3).AddHours(2));

        var result = CalendarSync.Run(calendar, CalendarId, [], Now);

        var added = Assert.Single(result.Added);
        Assert.Equal("Parents' evening", added.Title);
        Assert.True(added.IsLinked);
        Assert.Null(added.RemindMinutesBefore); // the calendar has its own alerts
    }

    [Fact]
    public void SecondSync_ChangesNothing()
    {
        var calendar = new FakeCalendar();
        calendar.Add("Haircut", Now.AddDays(1), Now.AddDays(1).AddHours(1));
        var appointments = new List<Appointment> { Dentist() };
        var first = CalendarSync.Run(calendar, CalendarId, appointments, Now);
        appointments.AddRange(first.Added);

        var second = CalendarSync.Run(calendar, CalendarId, appointments, Now);

        Assert.Empty(second.Added);
        Assert.Empty(second.Removed);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, calendar.Events.Count);
    }

    [Fact]
    public void ChangeInTheCalendar_IsBroughtIn()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();
        CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        var e = calendar.Events[dentist.CalendarEventKey!];
        e.Start = e.Start.AddHours(2);
        e.End = e.End.AddHours(2);
        e.LastModified = DateTime.Now.AddMinutes(1);
        var result = CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        Assert.Equal(Now.AddDays(2).AddHours(2), dentist.Start);
        Assert.Contains(dentist, result.Changed);
        Assert.Equal(0, result.Written);
    }

    [Fact]
    public void ChangeInKeystone_IsSentToTheCalendar()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();
        CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        dentist.Title = "Dentist (Anna)";
        dentist.UpdatedAt = DateTime.Now.AddMinutes(1);
        var result = CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        Assert.Equal("Dentist (Anna)", calendar.Events[dentist.CalendarEventKey!].Title);
        Assert.Equal(1, result.Written);
    }

    [Fact]
    public void ChangedOnBothSides_TheLaterChangeWins()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();
        CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        dentist.Title = "Changed in Keystone";
        dentist.UpdatedAt = DateTime.Now.AddMinutes(1);
        var e = calendar.Events[dentist.CalendarEventKey!];
        e.Title = "Changed in Calendar";
        e.LastModified = DateTime.Now.AddMinutes(2);
        CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        Assert.Equal("Changed in Calendar", dentist.Title);
    }

    [Fact]
    public void DeletedInTheCalendar_IsRemovedHere_UnlessChangedHereSince()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();
        var haircut = Dentist();
        haircut.Id = 2;
        haircut.Title = "Haircut";
        CalendarSync.Run(calendar, CalendarId, [dentist, haircut], Now);

        calendar.Events.Clear();
        haircut.UpdatedAt = DateTime.Now.AddMinutes(1);
        var result = CalendarSync.Run(calendar, CalendarId, [dentist, haircut], Now);

        Assert.Equal(dentist, Assert.Single(result.Removed));
        Assert.Equal("Haircut", Assert.Single(calendar.Events.Values).Title); // put back
    }

    [Fact]
    public void EventMovedFarAhead_IsFollowedNotDeleted()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();
        CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        var e = calendar.Events[dentist.CalendarEventKey!];
        e.Start = Now.AddYears(2);
        e.End = Now.AddYears(2).AddHours(1);
        e.LastModified = DateTime.Now.AddMinutes(1);
        var result = CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        Assert.Empty(result.Removed);
        Assert.Equal(Now.AddYears(2), dentist.Start);
    }

    [Fact]
    public void OldAppointments_AreLeftAlone()
    {
        var calendar = new FakeCalendar();
        var old = Dentist();
        old.Start = Now.AddDays(-90);
        old.End = old.Start.AddHours(1);

        var result = CalendarSync.Run(calendar, CalendarId, [old], Now);

        Assert.Empty(calendar.Events);
        Assert.Equal(0, result.Written);
    }

    [Fact]
    public void SwitchingCalendars_CopiesAppointmentsToTheNewOne()
    {
        var calendar = new FakeCalendar();
        var dentist = Dentist();
        CalendarSync.Run(calendar, CalendarId, [dentist], Now);

        var other = new FakeCalendar();
        CalendarSync.Run(other, "work", [dentist], Now);

        Assert.Single(other.Events);
        Assert.Equal("work", dentist.CalendarId);
    }

    [Fact]
    public void AllDayEvents_KeepTheirDays()
    {
        var calendar = new FakeCalendar();
        var holiday = new Appointment
        {
            Title = "Ski week", IsAllDay = true, Start = Now.Date.AddDays(5), End = Now.Date.AddDays(9), CreatedAt = Now, UpdatedAt = Now,
        };
        CalendarSync.Run(calendar, CalendarId, [holiday], Now);

        var e = Assert.Single(calendar.Events.Values);
        Assert.True(e.IsAllDay);
        Assert.True(CalendarSync.SameContent(holiday, e));
        Assert.True(holiday.IsOn(DateOnly.FromDateTime(Now.Date.AddDays(9))));
        Assert.False(holiday.IsOn(DateOnly.FromDateTime(Now.Date.AddDays(10))));
    }

    [Fact]
    public async Task Appointments_RoundTripAndBecomeSharedWhenTheirPersonIsRemoved()
    {
        var anna = await _repository.AddPersonAsync(new Person { Name = "Anna" });
        var dentist = Dentist();
        dentist.Id = 0;
        dentist.PersonId = anna.Id;
        dentist.RemindMinutesBefore = 60;
        await _repository.SaveAsync(dentist, isNew: true);
        await _repository.DeletePersonAsync(anna.Id);

        var saved = Assert.Single(await _repository.GetAllAsync<Appointment>());
        Assert.Equal("Dentist", saved.Title);
        Assert.Null(saved.PersonId);
        Assert.Equal(60, saved.RemindMinutesBefore);

        await _repository.SetCalendarSyncAsync(true, CalendarId);
        var settings = await _repository.GetSettingsAsync();
        Assert.True(settings.CalendarSyncEnabled);
        Assert.Equal(CalendarId, settings.CalendarId);
    }

    [Fact]
    public async Task SyncResult_IsSavedInOneGo()
    {
        var kept = Dentist();
        kept.Id = 0;
        var gone = Dentist();
        gone.Id = 0;
        gone.Title = "Cancelled";
        await _repository.SaveAsync(kept, isNew: true);
        await _repository.SaveAsync(gone, isNew: true);

        kept.Title = "Dentist, moved";
        var added = new Appointment { Title = "From Calendar", Start = Now, End = Now.AddHours(1), CreatedAt = Now, UpdatedAt = Now };
        await _repository.SaveAppointmentsAsync([added], [kept], [gone.Id]);

        var titles = (await _repository.GetAllAsync<Appointment>()).Select(a => a.Title).Order();
        Assert.Equal(["Dentist, moved", "From Calendar"], titles);
    }

    [Fact]
    public void Reminder_ComesBeforeTheStart()
    {
        var dentist = Dentist();
        dentist.Id = 7;
        dentist.RemindMinutesBefore = 60;
        var at = dentist.RemindAt(8)!.Value;

        var reminders = ReminderPlanner.Plan([], [], [], [], Now, 8,
            appointments: [new AppointmentReminder(dentist.Id, dentist.Title, at, "Friday, 10:00", dentist.Location)]);

        var reminder = Assert.Single(reminders);
        Assert.Equal(dentist.Start.AddHours(-1), reminder.At);
        Assert.Equal("Dentist", reminder.Title);
        Assert.Equal("Friday, 10:00 · Bahnhofstrasse 12", reminder.Body);
    }

    [Fact]
    public void Rows_SayWhenAndHowLong()
    {
        var today = new Appointment { Title = "Call", Start = Now.AddHours(2), End = Now.AddHours(2.5) };
        var row = new AppointmentRow(today, "Anna", Now);
        Assert.Equal("Today", row.WhenText);
        Assert.Equal($"{today.Start:HH:mm}–{today.End:HH:mm}", row.TimeText);
        Assert.True(row.IsToday);

        var allDay = new AppointmentRow(new Appointment { Title = "Trip", IsAllDay = true, Start = Now.Date.AddDays(1), End = Now.Date.AddDays(1) }, "", Now);
        Assert.Equal("Tomorrow", allDay.WhenText);
        Assert.Equal("All day", allDay.TimeText);
    }
}
