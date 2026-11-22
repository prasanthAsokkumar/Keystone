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

/// <summary>A reminder as shown in Settings.</summary>
public sealed record ReminderRow(string WhenText, string Title, string Body);

/// <summary>macOS notifications for bills, tasks, insurance deadlines and pillar 3a.</summary>
public partial class MainViewModel
{
    private static readonly int[] ReminderHours = [6, 7, 8, 9, 10, 12, 14, 17, 19, 20];

    private CancellationTokenSource? _reminderDelay;
    private bool _askedForNotifications;

    public IReadOnlyList<string> ReminderHourNames => ReminderHours.Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString("t", Loc.Instance.Culture)).ToList();

    [ObservableProperty]
    public partial bool RemindersEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int ReminderHourIndex { get; set; } = 2;

    private int ReminderHour => ReminderHours[Math.Clamp(ReminderHourIndex, 0, ReminderHours.Length - 1)];

    [ObservableProperty]
    public partial string ReminderPermissionText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsNotificationsDenied { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpcomingReminders))]
    public partial IReadOnlyList<ReminderRow> UpcomingReminders { get; set; } = [];

    public bool HasUpcomingReminders => UpcomingReminders.Count > 0;

    public bool CanSendNotifications => MacNotifications.IsSupported;

    private void ApplyReminderSettings(bool enabled, int hour)
    {
        RemindersEnabled = enabled;
        ReminderHourIndex = Array.IndexOf(ReminderHours, hour) is var i and >= 0 ? i : 2;
    }

    partial void OnRemindersEnabledChanged(bool value)
    {
        if (_isLoading)
            return;
        _ = SaveReminderSettingsAsync();
        ScheduleReminders();
    }

    partial void OnReminderHourIndexChanged(int value)
    {
        if (_isLoading)
            return;
        _ = SaveReminderSettingsAsync();
        ScheduleReminders();
    }

    private async Task SaveReminderSettingsAsync()
    {
        try
        {
            await repository.SetRemindersAsync(RemindersEnabled, ReminderHour);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    /// <summary>Everything that is reminded of, from now on.</summary>
    public IReadOnlyList<Reminder> PlanReminders(DateTime now) => ReminderPlanner.Plan(
        Documents.Where(d => d.IsOpenBill && d.DueDate is not null)
            .Select(d => new BillReminder(d.Id, d.Title, d.Sender, d.Amount ?? 0, d.DueDate!.Value)),
        _tasks.Where(t => !t.IsDone && t.DueDate is not null).Select(t => new TaskReminder(t.Id, t.Title, t.DueDate!.Value)),
        _policies.Where(p => !p.HasEnded(Today))
            .Select(p => (Policy: p, Deadline: p.NextDeadline(Today)))
            .Where(x => x.Deadline is not null)
            .Select(x => new DeadlineReminder(x.Policy.Id, x.Policy.Insurer, InsurancePolicy.KindName(x.Policy.Kind), x.Deadline!.Value.CancelBy,
                x.Policy.PersonId is null ? "" : PersonName(x.Policy.PersonId))),
        Pillar3aRowsForReminders().Select(r => new Pillar3aReminder(r.PersonName, r.Left, Today.Year)),
        now, ReminderHour,
        _contracts.Where(c => !c.HasEnded(Today))
            .Select(c => (Contract: c, Deadline: c.NextDeadline(Today)))
            .Where(x => x.Deadline is not null)
            .Select(x => new DeadlineReminder(x.Contract.Id, x.Contract.Provider, Contract.KindName(x.Contract.Kind), x.Deadline!.Value.CancelBy,
                x.Contract.PersonId is null ? "" : PersonName(x.Contract.PersonId))),
        _dates.Where(d => !d.HasPassed(Today))
            .Select(d => new DateReminder(d.Id, d.Title, d.NextDate(Today), d.RemindDaysBefore, d.PersonId is null ? "" : PersonName(d.PersonId))),
        _warranties.Where(w => !w.HasExpired(Today)).Select(w => new WarrantyReminder(w.Id, w.Item, w.ExpiresOn)),
        AppointmentReminders(ReminderHour));

    /// <summary>Re-plans reminders shortly after a change; many changes in a row plan only once.</summary>
    private void ScheduleReminders()
    {
        if (_isLoading)
            return;
        _reminderDelay?.Cancel();
        var delay = _reminderDelay = new CancellationTokenSource();
        _ = ScheduleRemindersLaterAsync(delay.Token);
    }

    private async Task ScheduleRemindersLaterAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(1500, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ApplyRemindersAsync();
    }

    private async Task ApplyRemindersAsync()
    {
        var plan = PlanReminders(DateTime.Now);
        UpcomingReminders = plan.Take(5)
            .Select(r => new ReminderRow(FormatReminderTime(r.At), r.Title, r.Body))
            .ToList();

        if (!MacNotifications.IsSupported)
        {
            ReminderPermissionText = Loc.T("Notifications work in the installed app.");
            return;
        }

        var permission = await MacNotifications.GetPermissionAsync();
        if (RemindersEnabled && permission == NotificationPermission.NotAsked && !_askedForNotifications)
        {
            _askedForNotifications = true;
            await MacNotifications.RequestPermissionAsync();
            permission = await MacNotifications.GetPermissionAsync();
        }

        IsNotificationsDenied = permission == NotificationPermission.Denied;
        ReminderPermissionText = permission switch
        {
            NotificationPermission.Allowed => Loc.T("Notifications are allowed."),
            NotificationPermission.Denied => Loc.T("Notifications are turned off for Keystone in System Settings."),
            _ => Loc.T("macOS will ask whether Keystone may send notifications."),
        };

        MacNotifications.Replace(RemindersEnabled && permission == NotificationPermission.Allowed ? plan : []);
    }

    private static string FormatReminderTime(DateTime at) =>
        at.Date == DateTime.Today ? Loc.F("Today, {0:t}", at)
        : at.Date == DateTime.Today.AddDays(1) ? Loc.F("Tomorrow, {0:t}", at)
        : $"{Format.Date(DateOnly.FromDateTime(at))}, {at:t}";

    [RelayCommand]
    private async Task SendTestReminderAsync()
    {
        if (!MacNotifications.IsSupported)
            return;
        if (await MacNotifications.GetPermissionAsync() != NotificationPermission.Allowed)
            await MacNotifications.RequestPermissionAsync();
        MacNotifications.ShowNow(Loc.T("Keystone reminders work"), Loc.T("You’ll be reminded of bills, tasks and deadlines like this."));
        await ApplyRemindersAsync();
    }

    [RelayCommand]
    private void OpenNotificationSettings() => MacNotifications.OpenSystemSettings();
}
