using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;
using Keystone.Services.Mac;

namespace Keystone.ViewModels;

/// <summary>The Appointments page, and keeping appointments in step with the Mac's Calendar app.</summary>
public partial class MainViewModel
{
    /// <summary>Appointments within this many days show on the Overview.</summary>
    private const int AppointmentSoonDays = 7;

    /// <summary>While Keystone is open, the calendar is checked this often for changes made elsewhere.</summary>
    private static readonly TimeSpan CalendarSyncInterval = TimeSpan.FromMinutes(10);

    private static readonly int?[] AppointmentReminderMinutes = [null, 0, 15, 30, 60, 120, 1440, 2880];

    /// <summary>The usual reminder for a new appointment: an hour before.</summary>
    private const int DefaultAppointmentReminderIndex = 4;

    private readonly List<Appointment> _appointments = [];

    public IReadOnlyList<string> AppointmentStatusFilters => Translate("Upcoming", "Past", "All appointments");

    public IReadOnlyList<string> AppointmentReminderNames => Translate(
        "No reminder", "When it starts", "15 minutes before", "30 minutes before", "1 hour before", "2 hours before",
        "1 day before", "2 days before");

    [ObservableProperty]
    public partial string AppointmentSearch { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption AppointmentPersonFilter { get; set; } = PersonOption.Everyone;

    [ObservableProperty]
    public partial int AppointmentStatusFilterIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppointmentRows), nameof(AppointmentsEmptyText))]
    public partial IReadOnlyList<AppointmentRow> AppointmentRows { get; set; } = [];

    public bool HasAppointmentRows => AppointmentRows.Count > 0;

    public string AppointmentsEmptyText => _appointments.Count == 0
        ? Loc.T("No appointments yet. Add one, or turn on calendar sync in Settings to bring in the ones from your calendar.")
        : AppointmentStatusFilterIndex == 0 && AppointmentSearch == "" && AppointmentPersonFilter is { IsEveryone: true }
            ? Loc.T("Nothing coming up.")
            : Loc.T("No appointments match these filters.");

    [ObservableProperty]
    public partial string AppointmentCountText { get; set; } = "";

    [ObservableProperty]
    public partial string NextAppointmentText { get; set; } = "";

    [ObservableProperty]
    public partial string NextAppointmentDetail { get; set; } = "";

    /// <summary>Shown on the Overview, e.g. "3 appointments this week".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppointmentsSoon))]
    public partial string AppointmentsSoonText { get; set; } = "";

    public bool HasAppointmentsSoon => AppointmentsSoonText != "";

    partial void OnAppointmentSearchChanged(string value) => RefreshAppointmentRows();

    partial void OnAppointmentPersonFilterChanged(PersonOption value) => RefreshAppointmentRows();

    partial void OnAppointmentStatusFilterIndexChanged(int value) => RefreshAppointmentRows();

    [RelayCommand]
    private void ShowAppointments()
    {
        AppointmentSearch = "";
        AppointmentPersonFilter = PersonOption.Everyone;
        AppointmentStatusFilterIndex = 0;
        CurrentPage = AppPage.Appointments;
    }

    private async Task LoadAppointmentsAsync(AppSettings settings)
    {
        _appointments.Clear();
        _appointments.AddRange(await repository.GetAllAsync<Appointment>());
        _calendarId = settings.CalendarId;
        IsCalendarSyncEnabled = settings.CalendarSyncEnabled;
        RefreshAppointmentRows();
    }

    private void RefreshAppointmentRows()
    {
        var now = DateTime.Now;
        var search = AppointmentSearch.Trim();
        var person = AppointmentPersonFilter ?? PersonOption.Everyone;
        var shown = _appointments
            .Where(a => person.IsEveryone || a.PersonId == person.Id)
            .Where(a => AppointmentStatusFilterIndex switch
            {
                0 => !a.HasPassed(now),
                1 => a.HasPassed(now),
                _ => true,
            })
            .Where(a => search == "" ||
                        a.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        a.Location.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        a.Notes.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        // Upcoming soonest first; past ones most recent first.
        shown = AppointmentStatusFilterIndex == 1
            ? shown.OrderByDescending(a => a.Start)
            : shown.OrderBy(a => a.HasPassed(now)).ThenBy(a => a.HasPassed(now) ? -a.Start.Ticks : a.Start.Ticks);
        AppointmentRows = Sorted("appointments", shown.Select(a => new AppointmentRow(a, PersonName(a.PersonId), now)), null,
            new SortKey<AppointmentRow>("title", r => r.Appointment.Title),
            new SortKey<AppointmentRow>("person", r => r.PersonName),
            new SortKey<AppointmentRow>("date", r => r.Appointment.Start));

        var upcoming = _appointments.Where(a => !a.HasPassed(now)).OrderBy(a => a.Start).ToList();
        AppointmentCountText = Loc.F("{0} coming up", upcoming.Count);
        if (upcoming.FirstOrDefault() is { } next)
        {
            var row = new AppointmentRow(next, PersonName(next.PersonId), now);
            NextAppointmentText = next.Title;
            NextAppointmentDetail = string.Join(" · ", new[] { row.WhenText, row.TimeText, next.Location }.Where(t => t != ""));
        }
        else
        {
            NextAppointmentText = "–";
            NextAppointmentDetail = Loc.T("Nothing planned");
        }

        var soon = upcoming.Count(a => a.Start < now.Date.AddDays(AppointmentSoonDays + 1));
        var today = upcoming.Count(a => a.IsOn(Today));
        AppointmentsSoonText = soon == 0
            ? ""
            : today > 0
                ? Loc.Plural(today, "{0} appointment today", "{0} appointments today")
                : Loc.Plural(soon, "{0} appointment this week", "{0} appointments this week");

        ScheduleReminders();
    }

    /// <summary>Reminders for appointments that ask for one.</summary>
    private IEnumerable<AppointmentReminder> AppointmentReminders(int allDayHour) =>
        _appointments
            .Where(a => a.RemindAt(allDayHour) is not null)
            .Select(a =>
            {
                var row = new AppointmentRow(a, "", DateTime.Now);
                var when = $"{row.WhenText}, {row.TimeText}";
                return new AppointmentReminder(a.Id, a.Title, a.RemindAt(allDayHour)!.Value, when, a.Location);
            });

    // ── Appointment panel ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsAppointmentPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppointmentFormTitle), nameof(IsEditingAppointment), nameof(AppointmentLinkText))]
    public partial Appointment? EditingAppointment { get; set; }

    public bool IsEditingAppointment => EditingAppointment is not null;

    public string AppointmentFormTitle => Loc.T(EditingAppointment is null ? "New appointment" : "Edit appointment");

    /// <summary>Whether, and where, the appointment is in the calendar.</summary>
    public string AppointmentLinkText => EditingAppointment is { IsLinked: true } && IsCalendarSyncEnabled && SelectedCalendar is { } calendar
        ? Loc.F("Also in your calendar “{0}”.", calendar.Title)
        : IsCalendarSyncReady && SelectedCalendar is { } target
            ? Loc.F("Will be added to your calendar “{0}”.", target.Title)
            : "";

    [ObservableProperty]
    public partial string AppointmentTitle { get; set; } = "";

    [ObservableProperty]
    public partial PersonOption AppointmentPerson { get; set; } = PersonOption.Shared;

    [ObservableProperty]
    public partial DateTime? AppointmentDate { get; set; }

    [ObservableProperty]
    public partial bool AppointmentAllDay { get; set; }

    [ObservableProperty]
    public partial TimeSpan? AppointmentStartTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan? AppointmentEndTime { get; set; }

    /// <summary>The last day of an all-day appointment; empty for one day.</summary>
    [ObservableProperty]
    public partial DateTime? AppointmentEndDate { get; set; }

    [ObservableProperty]
    public partial string AppointmentLocation { get; set; } = "";

    [ObservableProperty]
    public partial string AppointmentNotes { get; set; } = "";

    /// <summary>Index into <see cref="AppointmentReminderNames"/>.</summary>
    [ObservableProperty]
    public partial int AppointmentReminderIndex { get; set; } = DefaultAppointmentReminderIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppointmentError))]
    public partial string? AppointmentError { get; set; }

    public bool HasAppointmentError => !string.IsNullOrEmpty(AppointmentError);

    /// <summary>Moving the start keeps the length: a one-hour appointment stays one hour.</summary>
    partial void OnAppointmentStartTimeChanged(TimeSpan? oldValue, TimeSpan? newValue)
    {
        if (newValue is not { } start)
            return;
        var length = oldValue is { } before && AppointmentEndTime is { } end && end > before ? end - before : TimeSpan.FromHours(1);
        var newEnd = start + length;
        AppointmentEndTime = newEnd < TimeSpan.FromDays(1) ? newEnd : new TimeSpan(23, 59, 0);
    }

    [RelayCommand]
    private void NewAppointment()
    {
        CloseOtherPanels();
        EditingAppointment = null;
        AppointmentTitle = "";
        AppointmentPerson = AppointmentPersonFilter is { IsEveryone: false } filter ? filter : PersonOption.Shared;
        AppointmentDate = DateTime.Today.AddDays(1);
        AppointmentAllDay = false;
        AppointmentStartTime = null;
        AppointmentStartTime = new TimeSpan(9, 0, 0);
        AppointmentEndDate = null;
        AppointmentLocation = "";
        AppointmentNotes = "";
        AppointmentReminderIndex = DefaultAppointmentReminderIndex;
        AppointmentError = null;
        IsAppointmentPanelOpen = true;
    }

    [RelayCommand]
    private void EditAppointment(Appointment appointment)
    {
        CloseOtherPanels();
        EditingAppointment = appointment;
        AppointmentTitle = appointment.Title;
        AppointmentPerson = PersonOptions.FirstOrDefault(o => o.Id == appointment.PersonId) ?? PersonOption.Shared;
        AppointmentDate = appointment.Start.Date;
        AppointmentAllDay = appointment.IsAllDay;
        AppointmentStartTime = null;
        AppointmentStartTime = appointment.IsAllDay ? new TimeSpan(9, 0, 0) : appointment.Start.TimeOfDay;
        AppointmentEndTime = appointment.IsAllDay ? new TimeSpan(10, 0, 0) : appointment.End.TimeOfDay;
        AppointmentEndDate = appointment.IsAllDay && appointment.End.Date > appointment.Start.Date ? appointment.End.Date : null;
        AppointmentLocation = appointment.Location;
        AppointmentNotes = appointment.Notes;
        AppointmentReminderIndex = Math.Max(0, Array.IndexOf(AppointmentReminderMinutes, appointment.RemindMinutesBefore));
        AppointmentError = null;
        IsAppointmentPanelOpen = true;
    }

    [RelayCommand]
    private void ClearAppointmentEndDate() => AppointmentEndDate = null;

    [RelayCommand]
    private async Task SaveAppointmentAsync()
    {
        AppointmentError = null;
        if (string.IsNullOrWhiteSpace(AppointmentTitle) || AppointmentDate is not { } date)
        {
            AppointmentError = Loc.T("Enter what it is and the date.");
            return;
        }

        DateTime start, end;
        if (AppointmentAllDay)
        {
            start = date.Date;
            end = AppointmentEndDate?.Date ?? date.Date;
            if (end < start)
            {
                AppointmentError = Loc.T("The last day can’t be before the first.");
                return;
            }
        }
        else
        {
            if (AppointmentStartTime is not { } from || AppointmentEndTime is not { } until)
            {
                AppointmentError = Loc.T("Enter when it starts and ends.");
                return;
            }
            start = date.Date + from;
            end = date.Date + until;
            if (end < start)
            {
                AppointmentError = Loc.T("It can’t end before it starts.");
                return;
            }
        }

        await WaitForCalendarSyncAsync();
        var existing = EditingAppointment;
        var appointment = existing ?? new Appointment { CreatedAt = DateTime.Now };
        var before = existing is null ? null : Copy(existing);
        appointment.Title = AppointmentTitle.Trim();
        appointment.PersonId = AppointmentPerson.Id;
        appointment.IsAllDay = AppointmentAllDay;
        appointment.Start = start;
        appointment.End = end;
        appointment.Location = AppointmentLocation.Trim();
        appointment.Notes = AppointmentNotes.Trim();
        appointment.RemindMinutesBefore = AppointmentReminderMinutes[Math.Clamp(AppointmentReminderIndex, 0, AppointmentReminderMinutes.Length - 1)];
        appointment.UpdatedAt = DateTime.Now;
        try
        {
            await repository.SaveAsync(appointment, existing is null);
        }
        catch (Exception ex)
        {
            if (before is not null)
                Restore(appointment, before);
            AppointmentError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        if (existing is null)
            _appointments.Add(appointment);
        CloseAppointmentPanel();
        RefreshAppointmentRows();
        await SyncCalendarAsync();
    }

    [RelayCommand]
    private async Task DeleteAppointmentAsync(Appointment appointment)
    {
        await WaitForCalendarSyncAsync();
        try
        {
            await repository.DeleteAsync<Appointment>(a => a.Id == appointment.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }

        _appointments.Remove(appointment);
        if (EditingAppointment == appointment)
            CloseAppointmentPanel();
        RefreshAppointmentRows();

        // Deleted here, so deleted in the calendar too.
        if (appointment is { IsLinked: true, CalendarId: { } calendarId } && IsCalendarSyncReady && calendarId == _calendarId)
        {
            try
            {
                using var session = MacCalendar.Open();
                session.Delete(calendarId, appointment.CalendarEventKey!);
            }
            catch (Exception ex)
            {
                CalendarSyncError = Loc.F("Deleted here, but not in your calendar: {0}", ex.Message);
            }
        }
    }

    private void CloseAppointmentPanel()
    {
        IsAppointmentPanelOpen = false;
        EditingAppointment = null;
        AppointmentError = null;
    }

    private static Appointment Copy(Appointment a) => new()
    {
        Id = a.Id, Title = a.Title, Location = a.Location, Notes = a.Notes, Start = a.Start, End = a.End, IsAllDay = a.IsAllDay,
        PersonId = a.PersonId, RemindMinutesBefore = a.RemindMinutesBefore, CreatedAt = a.CreatedAt, UpdatedAt = a.UpdatedAt,
        CalendarId = a.CalendarId, CalendarEventKey = a.CalendarEventKey, SyncedAt = a.SyncedAt,
    };

    private static void Restore(Appointment target, Appointment from)
    {
        (target.Title, target.Location, target.Notes, target.Start, target.End, target.IsAllDay) =
            (from.Title, from.Location, from.Notes, from.Start, from.End, from.IsAllDay);
        (target.PersonId, target.RemindMinutesBefore, target.UpdatedAt) = (from.PersonId, from.RemindMinutesBefore, from.UpdatedAt);
    }

    // ── Calendar sync ───────────────────────────────────────────────────────

    /// <summary>The chosen calendar's id, kept even while that calendar can't be listed.</summary>
    private string _calendarId = "";

    private Task? _calendarSync;
    private bool _syncAgain;
    private bool _applyingCalendars;
    private CancellationTokenSource? _calendarSyncDelay;
    private bool _calendarTimerStarted;

    public bool CanUseCalendar => MacCalendar.IsSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppointmentLinkText), nameof(CalendarSyncSummary), nameof(ShowsCalendarChoice))]
    public partial bool IsCalendarSyncEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalendarOptions))]
    public partial IReadOnlyList<CalendarInfo> CalendarOptions { get; set; } = [];

    public bool HasCalendarOptions => CalendarOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppointmentLinkText), nameof(CalendarSyncSummary))]
    public partial CalendarInfo? SelectedCalendar { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCalendarChoice))]
    public partial bool IsCalendarAllowed { get; set; }

    public bool ShowsCalendarChoice => IsCalendarSyncEnabled && IsCalendarAllowed;

    [ObservableProperty]
    public partial bool IsCalendarDenied { get; set; }

    [ObservableProperty]
    public partial string CalendarPermissionText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCalendarSyncing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarSyncSummary))]
    public partial DateTime? LastCalendarSync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalendarSyncError), nameof(CalendarSyncSummary))]
    public partial string? CalendarSyncError { get; set; }

    public bool HasCalendarSyncError => !string.IsNullOrEmpty(CalendarSyncError);

    /// <summary>Sync is on, allowed, and has a calendar to sync with.</summary>
    private bool IsCalendarSyncReady =>
        IsCalendarSyncEnabled && _calendarId != "" && MacCalendar.Permission == CalendarPermission.Allowed;

    /// <summary>For the Appointments page, e.g. "In step with “Family” · 14:05".</summary>
    public string CalendarSyncSummary =>
        !IsCalendarSyncEnabled ? Loc.T("Not synced with your calendar")
        : SelectedCalendar is null ? Loc.T("Choose a calendar in Settings")
        : LastCalendarSync is { } at ? Loc.F("In step with “{0}” · {1:t}", SelectedCalendar.Title, at)
        : Loc.F("Syncs with “{0}”", SelectedCalendar.Title);

    partial void OnIsCalendarSyncEnabledChanged(bool value)
    {
        if (_isLoading)
            return;
        _ = TurnCalendarSyncAsync(value);
    }

    partial void OnSelectedCalendarChanged(CalendarInfo? value)
    {
        if (_isLoading || _applyingCalendars || value is null || value.Id == _calendarId)
            return;
        _calendarId = value.Id;
        LastCalendarSync = null;
        _ = SaveCalendarSettingsAndSyncAsync();
    }

    private async Task SaveCalendarSettingsAndSyncAsync()
    {
        await SaveCalendarSettingsAsync();
        await SyncCalendarAsync();
    }

    private async Task TurnCalendarSyncAsync(bool on)
    {
        if (on && MacCalendar.Permission == CalendarPermission.NotAsked)
        {
            using (OwnWindow()) // the macOS prompt takes focus; that isn't leaving Keystone
                await MacCalendar.RequestAccessAsync();
            MacCalendar.Reset();
        }

        RefreshCalendarState();
        await SaveCalendarSettingsAsync();
        if (on)
            await SyncCalendarAsync();
    }

    private async Task SaveCalendarSettingsAsync()
    {
        try
        {
            await repository.SetCalendarSyncAsync(IsCalendarSyncEnabled, _calendarId);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    /// <summary>Starts syncing once Keystone has loaded, and keeps checking while it is open.</summary>
    private async Task StartCalendarSyncAsync()
    {
        RefreshCalendarState();
        await SyncCalendarAsync();
        if (_calendarTimerStarted)
            return;
        _calendarTimerStarted = true;
        while (true)
        {
            await Task.Delay(CalendarSyncInterval);
            if (!IsLocked)
                await SyncCalendarAsync();
        }
    }

    /// <summary>The permission, the calendars to choose from and the chosen one.</summary>
    private void RefreshCalendarState()
    {
        var permission = MacCalendar.Permission;
        IsCalendarAllowed = permission == CalendarPermission.Allowed;
        IsCalendarDenied = permission == CalendarPermission.Denied && IsCalendarSyncEnabled;
        CalendarPermissionText = permission switch
        {
            CalendarPermission.Unsupported => Loc.T("Calendar sync works in the installed app."),
            CalendarPermission.NotAsked => Loc.T("macOS will ask whether Keystone may use your calendars."),
            CalendarPermission.Denied => Loc.T("Keystone isn’t allowed to use your calendars. Allow it in System Settings → Privacy & Security → Calendars."),
            _ when _calendarId == "" => Loc.T("Choose the calendar to keep your appointments in."),
            _ => "",
        };

        _applyingCalendars = true;
        try
        {
            CalendarOptions = IsCalendarAllowed ? MacCalendar.WritableCalendars() : [];
            SelectedCalendar = CalendarOptions.FirstOrDefault(c => c.Id == _calendarId);
        }
        catch (Exception ex)
        {
            CalendarSyncError = Loc.F("Could not read your calendars: {0}", ex.Message);
        }
        finally
        {
            _applyingCalendars = false;
        }

        if (IsCalendarAllowed && _calendarId != "" && SelectedCalendar is null && CalendarOptions.Count > 0)
            CalendarPermissionText = Loc.T("The calendar Keystone used is gone. Choose another one.");
        OnPropertyChanged(nameof(AppointmentLinkText));
        OnPropertyChanged(nameof(CalendarSyncSummary));
    }

    /// <summary>A calendar of its own keeps household appointments apart, and shows on the iPhone through iCloud.</summary>
    [RelayCommand]
    private async Task CreateKeystoneCalendarAsync()
    {
        CalendarSyncError = null;
        try
        {
            var calendar = MacCalendar.CreateCalendar(Loc.T("Keystone"));
            _calendarId = calendar.Id;
        }
        catch (Exception ex)
        {
            CalendarSyncError = Loc.F("Could not add the calendar: {0}", ex.Message);
            return;
        }

        LastCalendarSync = null;
        RefreshCalendarState();
        await SaveCalendarSettingsAndSyncAsync();
    }

    [RelayCommand]
    private Task SyncCalendarNowAsync()
    {
        RefreshCalendarState();
        return SyncCalendarAsync();
    }

    [RelayCommand]
    private void OpenCalendarSettings() => MacCalendar.OpenSystemSettings();

    [RelayCommand]
    private void OpenCalendarApp() => MacCalendar.OpenCalendarApp();

    [RelayCommand]
    private void ShowCalendarSettings() => CurrentPage = AppPage.Settings;

    /// <summary>Syncs shortly, e.g. when coming back to Keystone; many requests in a row sync once.</summary>
    private void ScheduleCalendarSync()
    {
        if (_isLoading || !IsCalendarSyncReady)
            return;
        _calendarSyncDelay?.Cancel();
        var delay = _calendarSyncDelay = new CancellationTokenSource();
        _ = SyncCalendarLaterAsync(delay.Token);
    }

    private async Task SyncCalendarLaterAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(1000, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SyncCalendarAsync();
    }

    private async Task WaitForCalendarSyncAsync()
    {
        if (_calendarSync is { IsCompleted: false } running)
            await running;
    }

    /// <summary>Syncs now. A request while a sync runs is done right after it.</summary>
    private async Task SyncCalendarAsync()
    {
        if (!IsCalendarSyncReady)
            return;
        if (_calendarSync is { IsCompleted: false } running)
        {
            _syncAgain = true;
            await running;
            return;
        }

        _calendarSync = RunCalendarSyncAsync();
        await _calendarSync;
    }

    private async Task RunCalendarSyncAsync()
    {
        IsCalendarSyncing = true;
        try
        {
            do
            {
                _syncAgain = false;
                var calendarId = _calendarId;
                CalendarSyncResult result;
                // EventKit objects live in the session; nothing is awaited inside it.
                using (var session = MacCalendar.Open())
                    result = CalendarSync.Run(session, calendarId, _appointments.ToList(), DateTime.Now);

                if (result.ChangedKeystone)
                {
                    await repository.SaveAppointmentsAsync(result.Added, result.Changed.Distinct().ToList(), result.Removed.Select(a => a.Id));
                    foreach (var removed in result.Removed)
                    {
                        _appointments.Remove(removed);
                        if (EditingAppointment == removed)
                            CloseAppointmentPanel();
                    }
                    _appointments.AddRange(result.Added);
                    RefreshAppointmentRows();
                }

                LastCalendarSync = DateTime.Now;
                CalendarSyncError = result.Errors.Count == 0
                    ? null
                    : Loc.F("Some appointments could not be synced: {0}", string.Join("; ", result.Errors.Take(3)));
            }
            while (_syncAgain && IsCalendarSyncReady);
        }
        catch (Exception ex)
        {
            CalendarSyncError = Loc.F("Could not sync with your calendar: {0}", ex.Message);
        }
        finally
        {
            IsCalendarSyncing = false;
        }
    }
}

/// <summary>One row of the Appointments list.</summary>
public sealed record AppointmentRow(Appointment Appointment, string PersonName, DateTime Now)
{
    public string Title => Appointment.Title;

    public string DayText => Appointment.Start.Day.ToString(Loc.Instance.Culture);

    public string MonthText => Appointment.Start.ToString("MMM", Loc.Instance.Culture).TrimEnd('.').ToUpper(Loc.Instance.Culture);

    public string Location => Appointment.Location;

    public bool HasLocation => Appointment.Location != "";

    public bool IsShared => Appointment.PersonId is null;

    public bool IsLinked => Appointment.IsLinked;

    public bool IsPast => Appointment.HasPassed(Now);

    public bool IsNow => !IsPast && Appointment.Start <= Now;

    public bool IsToday => !IsPast && Appointment.IsOn(DateOnly.FromDateTime(Now));

    public bool HasReminder => Appointment.RemindMinutesBefore is not null;

    /// <summary>"Today", "Tomorrow", "Friday", or the date.</summary>
    public string WhenText
    {
        get
        {
            var day = Appointment.Start.Date;
            var today = Now.Date;
            if (IsNow && day < today)
                return Loc.T("Now");
            if (day == today)
                return Loc.T("Today");
            if (day == today.AddDays(1))
                return Loc.T("Tomorrow");
            if (day == today.AddDays(-1))
                return Loc.T("Yesterday");
            if (day > today && day < today.AddDays(7))
                return day.ToString("dddd", Loc.Instance.Culture);
            return day.ToString(Loc.Instance.Language == "de" ? "ddd, d. MMM yyyy" : "ddd, d MMM yyyy", Loc.Instance.Culture);
        }
    }

    /// <summary>"09:30–10:15", "All day" or "All day, until 14 Mar".</summary>
    public string TimeText
    {
        get
        {
            var a = Appointment;
            if (a.IsAllDay)
                return a.End.Date > a.Start.Date
                    ? Loc.F("All day, until {0}", a.End.ToString(Loc.Instance.Language == "de" ? "d. MMM" : "d MMM", Loc.Instance.Culture))
                    : Loc.T("All day");
            var culture = Loc.Instance.Culture;
            if (a.End <= a.Start)
                return a.Start.ToString("t", culture);
            return a.End.Date > a.Start.Date
                ? $"{a.Start.ToString("t", culture)} – {Format.Date(DateOnly.FromDateTime(a.End))} {a.End.ToString("t", culture)}"
                : $"{a.Start.ToString("t", culture)}–{a.End.ToString("t", culture)}";
        }
    }

    public string DetailText => string.Join(" · ", new[] { WhenText, TimeText, Location }.Where(t => t != ""));
}
