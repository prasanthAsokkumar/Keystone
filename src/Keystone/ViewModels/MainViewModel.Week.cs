using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>One thing on a day of the week strip: "14:00 Dentist", "Rent", "Euthyrox runs out".</summary>
public sealed record WeekItem(AttentionKind Kind, string Text)
{
    public bool IsMoney => Kind is AttentionKind.Payment or AttentionKind.Bill;
    public bool IsTask => Kind == AttentionKind.Task;
    public bool IsAppointment => Kind == AttentionKind.Appointment;
    public bool IsDeadline => Kind is AttentionKind.Deadline or AttentionKind.Date or AttentionKind.Warranty;
    public bool IsHealth => Kind is AttentionKind.Medicine or AttentionKind.SickLeave;
}

/// <summary>A day of the week strip, with its first few things and how many more.</summary>
public sealed record WeekDay(DateOnly Date, bool IsToday, IReadOnlyList<WeekItem> Items, int More)
{
    public string DayName => IsToday ? Loc.T("Today") : Date.ToString("ddd", CultureInfo.CurrentCulture);

    public string DayNumber => Date.Day.ToString(CultureInfo.CurrentCulture);

    public bool IsEmpty => Items.Count == 0;

    public bool HasMore => More > 0;

    public string MoreText => Loc.F("+{0} more", More);
}

/// <summary>A kind of "Needs your attention" row the owner can switch off.</summary>
public sealed partial class AttentionFilter(string key, string title, bool isShown, Action<AttentionFilter> changed) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsShown { get; set; } = isShown;

    partial void OnIsShownChanged(bool value) => changed(this);
}

/// <summary>
/// The Overview's week at a glance (today and the next six days), the choice of what "Needs your attention" shows,
/// and the headline "Money lasts" figure.
/// </summary>
public partial class MainViewModel
{
    private const int WeekItemsPerDay = 3;

    [ObservableProperty]
    public partial IReadOnlyList<WeekDay> WeekDays { get; set; } = [];

    /// <summary>"Rent, 2 appointments and a notice deadline this week".</summary>
    [ObservableProperty]
    public partial string WeekSummary { get; set; } = "";

    internal void RefreshWeek(DateTime now, DateOnly today)
    {
        var end = today.AddDays(6);
        var items = new List<(DateOnly Date, int Order, WeekItem Item)>();
        void Add(DateOnly date, int order, AttentionKind kind, string text)
        {
            // Overdue things show on today, where they still need doing.
            var on = date < today ? today : date;
            if (on <= end && ShowsAttention(kind))
                items.Add((on, order, new WeekItem(kind, text)));
        }

        foreach (var a in Appointments.All.Where(a => !a.HasPassed(now)))
        {
            var day = DateOnly.FromDateTime(a.Start);
            if (day >= today)
                Add(day, a.IsAllDay ? 0 : 1, AttentionKind.Appointment,
                    a.IsAllDay ? a.Title : $"{a.Start.ToString("HH:mm", CultureInfo.CurrentCulture)} {a.Title}");
        }
        foreach (var t in Tasks.All.Where(t => !t.IsDone && t.DueDate is not null))
            Add(t.DueDate!.Value, 2, AttentionKind.Task, t.Title);
        foreach (var d in Documents.Where(d => d.IsOpenBill && d.NextDueDate is not null))
            Add(d.NextDueDate!.Value, 3, AttentionKind.Bill, d.Sender == "" ? d.Title : d.Sender);
        foreach (var o in Schedule.Expand(_entries.Where(t => !t.IsTransfer && !t.IsIncome), today, end))
            Add(o.Date, 4, AttentionKind.Payment, o.Source.Description);
        foreach (var o in Schedule.Expand(_entries.Where(t => !t.IsTransfer && t.IsIncome && !t.IsBorrowing), today, end))
            Add(o.Date, 4, AttentionKind.Payment, $"+ {o.Source.Description}");
        foreach (var p in _policies.Where(p => !p.HasEnded(today)))
            if (p.NextDeadline(today) is { } d)
                Add(d.CancelBy, 5, AttentionKind.Deadline, Loc.F("Cancel {0}", p.Insurer));
        foreach (var c in Deadlines.Contracts.Where(c => !c.HasEnded(today)))
            if (c.NextDeadline(today) is { } d)
                Add(d.CancelBy, 5, AttentionKind.Deadline, Loc.F("Cancel {0}", c.Provider));
        foreach (var date in Deadlines.Dates.Where(d => !d.HasPassed(today)))
            Add(date.NextDate(today), 5, AttentionKind.Date, date.Title);
        foreach (var m in Medicines.All.Where(m => m.IsTaken(today) && m.RunsOutOn is not null && m.RunsOutOn >= today))
            Add(m.RunsOutOn!.Value, 6, AttentionKind.Medicine, Loc.F("{0} runs out", m.Name));

        WeekDays = Enumerable.Range(0, 7).Select(i => today.AddDays(i)).Select(day =>
        {
            var list = items.Where(x => x.Date == day).OrderBy(x => x.Order).Select(x => x.Item).ToList();
            return new WeekDay(day, day == today, list.Take(WeekItemsPerDay).ToList(), Math.Max(0, list.Count - WeekItemsPerDay));
        }).ToList();

        var counts = new List<string>();
        void Count(int n, string one, string many)
        {
            if (n > 0)
                counts.Add(Loc.Plural(n, one, many));
        }
        Count(items.Count(x => x.Item.IsAppointment), "{0} appointment", "{0} appointments");
        Count(items.Count(x => x.Item.IsTask), "{0} task", "{0} tasks");
        Count(items.Count(x => x.Item.IsMoney), "{0} payment or bill", "{0} payments or bills");
        Count(items.Count(x => x.Item.IsDeadline), "{0} deadline or date", "{0} deadlines or dates");
        Count(items.Count(x => x.Item.IsHealth), "{0} medicine running out", "{0} medicines running out");
        WeekSummary = counts.Count == 0 ? Loc.T("A quiet week: nothing planned.") : string.Join(" · ", counts);
    }

    // ── What "Needs your attention" shows ───────────────────────────────────

    private HashSet<string> _hiddenAttention = [];

    [ObservableProperty]
    public partial IReadOnlyList<AttentionFilter> AttentionFilters { get; set; } = [];

    private static string AttentionKey(AttentionKind kind) => kind switch
    {
        AttentionKind.Bill or AttentionKind.Payment => "bills",
        AttentionKind.Task => "tasks",
        AttentionKind.Appointment => "appointments",
        AttentionKind.Deadline or AttentionKind.Date or AttentionKind.Warranty => "deadlines",
        AttentionKind.Medicine or AttentionKind.SickLeave => "health",
        _ => "inbox",
    };

    internal bool ShowsAttention(AttentionKind kind) => !_hiddenAttention.Contains(AttentionKey(kind));

    private void LoadHiddenAttention(string keys)
    {
        _hiddenAttention = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        RefreshAttentionFilters();
        OnPropertyChanged(nameof(HiddenAttentionText));
    }

    private void RefreshAttentionFilters() => AttentionFilters = new (string Key, string Title)[]
    {
        ("bills", Loc.T("Bills and payments")),
        ("tasks", Loc.T("Tasks")),
        ("appointments", Loc.T("Appointments")),
        ("deadlines", Loc.T("Notice deadlines, dates and warranties")),
        ("health", Loc.T("Medicines and sick days")),
        ("inbox", Loc.T("Scans in the inbox")),
    }.Select(f => new AttentionFilter(f.Key, f.Title, !_hiddenAttention.Contains(f.Key), AttentionFilterChanged)).ToList();

    private async void AttentionFilterChanged(AttentionFilter filter)
    {
        if (filter.IsShown)
            _hiddenAttention.Remove(filter.Key);
        else
            _hiddenAttention.Add(filter.Key);
        OnPropertyChanged(nameof(HiddenAttentionText));
        RefreshDashboard();
        try
        {
            await repository.SetHiddenAttentionAsync(string.Join(",", _hiddenAttention.Order()));
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
        }
    }

    /// <summary>"2 kinds hidden" under the attention title, so a quiet list isn't mistaken for nothing to do.</summary>
    public string HiddenAttentionText => _hiddenAttention.Count == 0 ? "" : " · " + Loc.Plural(_hiddenAttention.Count, "{0} kind hidden", "{0} kinds hidden");

    // ── Money lasts ──────────────────────────────────────────────────────────

    /// <summary>"until Mar 2028", "Out of cash" or "Indefinitely": the runway as a date rather than months.</summary>
    public string MoneyLastsText => Summary switch
    {
        { CurrentBalance: <= 0 } => Loc.T("Out of cash"),
        { ZeroCashDate: { } date } => Loc.F("until {0}", date.ToString("MMM yyyy", CultureInfo.CurrentCulture)),
        _ => Loc.T("Indefinitely"),
    };

    /// <summary>"14.2 months from today" or "Your income covers your spending".</summary>
    public string MoneyLastsDetail => Summary.ZeroCashDate is not null && Summary.CurrentBalance > 0
        ? Loc.F("{0} from today", RunwayText)
        : RunwayDetailText;
}
