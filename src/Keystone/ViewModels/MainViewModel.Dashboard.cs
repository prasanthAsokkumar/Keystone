using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>What kind of thing an Overview line is about; decides its icon and where "Open" goes.</summary>
public enum AttentionKind { Bill, Task, Deadline, Date, Appointment, Inbox, Warranty, Payment, Medicine, SickLeave }

/// <summary>One line of "Needs your attention": what, how urgent, and where to deal with it.</summary>
public sealed record AttentionRow(AttentionKind Kind, string Text, string Detail, bool IsUrgent, Action Open)
{
    public bool HasDetail => Detail != "";

    public bool IsBill => Kind is AttentionKind.Bill or AttentionKind.Payment;
    public bool IsTask => Kind == AttentionKind.Task;
    public bool IsDeadline => Kind is AttentionKind.Deadline or AttentionKind.Date or AttentionKind.Warranty;
    public bool IsAppointment => Kind == AttentionKind.Appointment;
    public bool IsInbox => Kind == AttentionKind.Inbox;
    public bool IsMedicine => Kind == AttentionKind.Medicine;
    public bool IsSickLeave => Kind == AttentionKind.SickLeave;
}

/// <summary>One line of "Coming up": a payment, appointment, bill or deadline on a day in the next weeks.</summary>
public sealed record ComingUpRow(DateOnly Date, AttentionKind Kind, string Title, string Detail, string AmountText, bool IsIncome, string TimeText = "")
{
    public string DayText => Date.Day.ToString(CultureInfo.CurrentCulture);

    public string MonthText => Date.ToString("MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);

    public bool HasAmount => AmountText != "";

    public bool IsPayment => Kind == AttentionKind.Payment;
    public bool IsBill => Kind == AttentionKind.Bill;
    public bool IsDeadline => Kind is AttentionKind.Deadline or AttentionKind.Date;
    public bool IsAppointment => Kind == AttentionKind.Appointment;

    /// <summary>"14:00 · Praxis Spalen" for an appointment, the category and person for a payment.</summary>
    public string DetailLine => TimeText == "" ? Detail : Detail == "" ? TimeText : $"{TimeText} · {Detail}";
}

/// <summary>
/// The Overview: a greeting, what needs attention now (overdue bills, tasks, notice deadlines, today's appointments,
/// scans in the inbox), the money at a glance, and what is coming up in the next weeks across all pages.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Bills and tasks this close count as needing attention; deadlines and dates use their own lead time.</summary>
    public const int AttentionDays = 7;

    /// <summary>How far ahead "Coming up" looks.</summary>
    public const int ComingUpDays = 21;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention), nameof(AttentionSummary))]
    public partial IReadOnlyList<AttentionRow> AttentionRows { get; set; } = [];

    public bool HasAttention => AttentionRows.Count > 0;

    /// <summary>"3 things need your attention" or "All clear".</summary>
    public string AttentionSummary => AttentionRows.Count == 0
        ? Loc.T("All clear: nothing needs your attention right now.")
        : Loc.Plural(AttentionRows.Count, "{0} thing needs your attention", "{0} things need your attention");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComingUp))]
    public partial IReadOnlyList<ComingUpRow> ComingUpRows { get; set; } = [];

    public bool HasComingUp => ComingUpRows.Count > 0;

    /// <summary>The first lines of "Coming up"; the rest on request, so the Overview stays short.</summary>
    public const int ComingUpShown = 8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleComingUpRows), nameof(ComingUpMoreText))]
    public partial bool ShowAllComingUp { get; set; }

    public IReadOnlyList<ComingUpRow> VisibleComingUpRows => ShowAllComingUp ? ComingUpRows : ComingUpRows.Take(ComingUpShown).ToList();

    public bool HasMoreComingUp => ComingUpRows.Count > ComingUpShown;

    public string ComingUpMoreText => ShowAllComingUp
        ? Loc.T("Show fewer")
        : Loc.F("Show all {0}", ComingUpRows.Count);

    [RelayCommand]
    private void ToggleComingUp() => ShowAllComingUp = !ShowAllComingUp;

    partial void OnComingUpRowsChanged(IReadOnlyList<ComingUpRow> value)
    {
        OnPropertyChanged(nameof(VisibleComingUpRows));
        OnPropertyChanged(nameof(HasMoreComingUp));
        OnPropertyChanged(nameof(ComingUpMoreText));
    }

    /// <summary>"Good morning", "Good afternoon" or "Good evening", by the time of day.</summary>
    public string Greeting => DateTime.Now.Hour switch
    {
        < 12 => Loc.T("Good morning"),
        < 18 => Loc.T("Good afternoon"),
        _ => Loc.T("Good evening"),
    };

    [ObservableProperty]
    public partial string MonthInText { get; set; } = "";

    [ObservableProperty]
    public partial string MonthOutText { get; set; } = "";

    [ObservableProperty]
    public partial string MonthNetText { get; set; } = "";

    [ObservableProperty]
    public partial bool MonthNetIsNegative { get; set; }

    /// <summary>"CHF 3'120 still to come" for the rest of this month.</summary>
    [ObservableProperty]
    public partial string MonthDetailText { get; set; } = "";

    public string MonthTitle => Today.ToString("MMMM", CultureInfo.CurrentCulture);

    [RelayCommand]
    private void OpenAttention(AttentionRow row) => row.Open();

    // The Overview is fresh whenever it is shown (the day may have changed) and when scans arrive.
    partial void OnCurrentPageChanged(AppPage value)
    {
        NavigationChanged();
        if (value == AppPage.Overview)
            RefreshDashboard();
    }

    partial void OnInboxFilesChanged(IReadOnlyList<string> value) => RefreshDashboard();

    /// <summary>Rebuilds the Overview's lists. Cheap; called when money, papers, tasks or dates change.</summary>
    internal void RefreshDashboard()
    {
        var now = DateTime.Now;
        var today = Today;
        string Person(int? id) => id is null ? "" : PersonName(id);
        var rows = new List<(int Order, AttentionRow Row)>();

        // Bills: overdue first, then due within a week.
        var open = Documents.Where(d => d.IsOpenBill && d.NextDueDate is not null).ToList();
        var overdue = open.Where(d => d.NextDueDate < today).ToList();
        if (overdue.Count > 0)
            rows.Add((0, new AttentionRow(AttentionKind.Bill,
                Loc.Plural(overdue.Count, "{0} bill overdue · {1}", "{0} bills overdue · {1}", Format.Money(overdue.Sum(d => d.NextAmount))),
                string.Join(", ", overdue.Select(d => d.Sender == "" ? d.Title : d.Sender).Distinct().Take(3)), true, ShowOpenBills)));
        var dueSoon = open.Where(d => d.NextDueDate >= today && d.NextDueDate <= today.AddDays(AttentionDays)).ToList();
        if (dueSoon.Count > 0)
            rows.Add((3, new AttentionRow(AttentionKind.Bill,
                Loc.Plural(dueSoon.Count, "{0} bill due this week · {1}", "{0} bills due this week · {1}", Format.Money(dueSoon.Sum(d => d.NextAmount))),
                string.Join(", ", dueSoon.Select(d => d.Sender == "" ? d.Title : d.Sender).Distinct().Take(3)), false, ShowOpenBills)));

        // Tasks: overdue or due today.
        var tasks = _tasks.Where(t => !t.IsDone && t.DueDate <= today).ToList();
        if (tasks.Count > 0)
        {
            var late = tasks.Count(t => t.DueDate < today);
            rows.Add((late > 0 ? 1 : 4, new AttentionRow(AttentionKind.Task,
                Loc.Plural(tasks.Count, "{0} task to do today", "{0} tasks to do today") + (late > 0 ? Loc.F(" · {0} overdue", late) : ""),
                string.Join(", ", tasks.OrderBy(t => t.DueDate).Select(t => t.Title).Take(3)), late > 0, ShowTasksDue)));
        }

        // Appointments today.
        foreach (var a in _appointments.Where(a => !a.HasPassed(now) && DateOnly.FromDateTime(a.Start) == today).OrderBy(a => a.Start))
            rows.Add((2, new AttentionRow(AttentionKind.Appointment,
                a.IsAllDay ? Loc.F("Today: {0}", a.Title) : Loc.F("Today {0}: {1}", a.Start.ToString("HH:mm", CultureInfo.CurrentCulture), a.Title),
                string.Join(" · ", new[] { a.Location, Person(a.PersonId) }.Where(s => s != "")), false, () => CurrentPage = AppPage.Appointments)));

        // Notice deadlines within the warning time: cancel now or it renews.
        foreach (var p in _policies.Where(p => !p.HasEnded(today)))
            if (p.NextDeadline(today) is { } d && d.CancelBy.DayNumber - today.DayNumber <= InsuranceDeadlineWarningDays)
                rows.Add((5, new AttentionRow(AttentionKind.Deadline,
                    Loc.F("Cancel {0} by {1}", p.Insurer, Format.Date(d.CancelBy)),
                    $"{InsurancePolicy.KindName(p.Kind)} · {DaysLeftText(d.CancelBy)}", d.CancelBy.DayNumber - today.DayNumber <= 14,
                    () => CurrentPage = AppPage.Insurance)));
        foreach (var c in _contracts.Where(c => !c.HasEnded(today)))
            if (c.NextDeadline(today) is { } d && d.CancelBy.DayNumber - today.DayNumber <= InsuranceDeadlineWarningDays)
                rows.Add((5, new AttentionRow(AttentionKind.Deadline,
                    Loc.F("Cancel {0} by {1}", c.Provider, Format.Date(d.CancelBy)),
                    $"{Contract.KindName(c.Kind)} · {DaysLeftText(d.CancelBy)}", d.CancelBy.DayNumber - today.DayNumber <= 14,
                    () => { CurrentPage = AppPage.Contracts; DeadlinesTab = 0; })));

        // Important dates (passport, MFK…) inside their own lead time.
        foreach (var date in _dates.Where(d => !d.HasPassed(today)))
        {
            var next = date.NextDate(today);
            if (next.DayNumber - today.DayNumber <= date.RemindDaysBefore)
                rows.Add((6, new AttentionRow(AttentionKind.Date, Loc.F("{0} on {1}", date.Title, Format.Date(next)),
                    string.Join(" · ", new[] { Person(date.PersonId), DaysLeftText(next) }.Where(s => s != "")), next.DayNumber - today.DayNumber <= 7,
                    () => { CurrentPage = AppPage.Contracts; DeadlinesTab = 1; })));
        }

        // Warranties ending within a month: last chance to claim.
        foreach (var w in _warranties.Where(w => !w.HasExpired(today) && w.ExpiresOn.DayNumber - today.DayNumber <= 30))
            rows.Add((7, new AttentionRow(AttentionKind.Warranty, Loc.F("Warranty for {0} ends {1}", w.Item, Format.Date(w.ExpiresOn)),
                DaysLeftText(w.ExpiresOn), false, () => { CurrentPage = AppPage.Contracts; DeadlinesTab = 2; })));

        // Someone off sick: what's still to do (tell work, the certificate).
        foreach (var leave in _sickLeaves.Where(l => l.IsOngoing(today) && l.PersonId is not null))
        {
            var day = CertificateDayOf(leave);
            var todo = new[]
            {
                leave.Informed ? "" : Loc.T("tell work"),
                leave.NeedsCertificate(today, day) ? Loc.T("certificate needed") : "",
            }.Where(s => s != "").ToList();
            rows.Add((1, new AttentionRow(AttentionKind.SickLeave,
                Loc.F("{0} is off sick · day {1}", PersonName(leave.PersonId), leave.Days(today)),
                todo.Count > 0 ? Loc.F("Still to do: {0}", string.Join(", ", todo)) : Loc.T("Work is told; get well soon"),
                todo.Count > 0, () => ShowMember(leave.PersonId))));
        }

        // Medicines running out or prescriptions ending.
        foreach (var m in _medicines.Where(m => m.PersonId is not null))
            if (MedicineAlert(m) is { } alert)
                rows.Add((4, new AttentionRow(AttentionKind.Medicine, $"{m.Name} {m.Strength}".Trim() + " · " + alert,
                    PersonName(m.PersonId), m.RunsOutOn is { } runsOut && runsOut.DayNumber - today.DayNumber <= 2,
                    () => ShowMember(m.PersonId))));

        if (HasInboxFiles)
            rows.Add((8, new AttentionRow(AttentionKind.Inbox, InboxText, Loc.T("File them under Documents"), false,
                () => FileInboxCommand.Execute(null))));

        AttentionRows = rows.Where(r => ShowsAttention(r.Row.Kind)).OrderBy(r => r.Order).ThenByDescending(r => r.Row.IsUrgent).Select(r => r.Row).ToList();

        RefreshComingUp(now, today);
        RefreshWeek(now, today);
        RefreshMonth(today);
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(MonthTitle));
    }

    /// <summary>Payments, appointments, bills without an expense entry, and deadlines of the next weeks, by date.</summary>
    private void RefreshComingUp(DateTime now, DateOnly today)
    {
        var until = today.AddDays(ComingUpDays);
        var list = new List<ComingUpRow>();
        var names = People.ToDictionary(p => p.Id, p => p.Name);
        string Person(int? id) => id is { } key && names.TryGetValue(key, out var name) ? name : "";

        foreach (var o in Schedule.Expand(_entries.Where(t => !t.IsTransfer), today.AddDays(1), until))
        {
            var t = o.Source;
            var detail = t.IsDebtPayment ? Loc.T("Debt") : string.Join(" · ", new[] { Categories.Display(t.Category), Person(t.PersonId) }.Where(s => s != ""));
            list.Add(new ComingUpRow(o.Date, AttentionKind.Payment, t.Description, detail, Format.SignedMoney(o.SignedAmount), t.IsIncome));
        }

        foreach (var a in _appointments.Where(a => !a.HasPassed(now) && DateOnly.FromDateTime(a.Start) > today && DateOnly.FromDateTime(a.Start) <= until))
            list.Add(new ComingUpRow(DateOnly.FromDateTime(a.Start), AttentionKind.Appointment, a.Title,
                string.Join(" · ", new[] { a.Location, Person(a.PersonId) }.Where(s => s != "")), "", false,
                a.IsAllDay ? Loc.T("All day") : a.Start.ToString("HH:mm", CultureInfo.CurrentCulture)));

        // Bills already in the expenses come as payments; the others on their due date.
        foreach (var d in Documents.Where(d => d.IsOpenBill && d.TransactionId is null && !d.Instalments.Any(i => i.TransactionId is not null) &&
                                               d.NextDueDate > today && d.NextDueDate <= until))
            list.Add(new ComingUpRow(d.NextDueDate!.Value, AttentionKind.Bill, d.Sender == "" ? d.Title : $"{d.Sender}: {d.Title}",
                Loc.T("Bill to pay"), Format.SignedMoney(-d.NextAmount), false));

        foreach (var p in _policies.Where(p => !p.HasEnded(today)))
            if (p.NextDeadline(today) is { } d && d.CancelBy > today && d.CancelBy <= until)
                list.Add(new ComingUpRow(d.CancelBy, AttentionKind.Deadline, Loc.F("Last day to cancel {0}", p.Insurer), InsurancePolicy.KindName(p.Kind), "", false));
        foreach (var c in _contracts.Where(c => !c.HasEnded(today)))
            if (c.NextDeadline(today) is { } d && d.CancelBy > today && d.CancelBy <= until)
                list.Add(new ComingUpRow(d.CancelBy, AttentionKind.Deadline, Loc.F("Last day to cancel {0}", c.Provider), Contract.KindName(c.Kind), "", false));
        foreach (var date in _dates.Where(d => !d.HasPassed(today)))
            if (date.NextDate(today) is var next && next > today && next <= until)
                list.Add(new ComingUpRow(next, AttentionKind.Date, date.Title, Person(date.PersonId), "", false));

        ComingUpRows = list.OrderBy(r => r.Date).ThenBy(r => r.TimeText).ToList();
    }

    /// <summary>This calendar month's money in and out: what has come and gone, plus what is still expected.</summary>
    private void RefreshMonth(DateOnly today)
    {
        var start = new DateOnly(today.Year, today.Month, 1);
        var occurrences = Schedule.Expand(_entries.Where(t => !t.IsTransfer), start, start.AddMonths(1).AddDays(-1));
        var income = occurrences.Where(o => o.Source.IsIncome).Sum(o => o.Amount);
        var spending = occurrences.Where(o => !o.Source.IsIncome).Sum(o => o.Amount);
        MonthInText = Format.Money(income);
        MonthOutText = Format.Money(spending);
        MonthNetText = Format.SignedMoney(income - spending);
        MonthNetIsNegative = income < spending;
        var toCome = occurrences.Where(o => o.Date > today).Sum(o => o.SignedAmount);
        MonthDetailText = toCome == 0 ? Loc.T("Nothing more expected this month")
            : Loc.F("{0} still expected this month", Format.SignedMoney(toCome));
    }
}
