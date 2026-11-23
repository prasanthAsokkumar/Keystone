using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>Keeps the summary for the desktop widget up to date: open tasks, bills to pay, next appointments.</summary>
public partial class MainViewModel
{
    /// <summary>The widget's texts in the app's language; the widget itself only formats dates.</summary>
    private static Dictionary<string, string> WidgetLabels() => new[]
    {
        ("tasks", "Tasks"), ("bills", "Bills to pay"), ("appointments", "Appointments"),
        ("billsShort", "Bills"), ("appointmentsShort", "Coming up"),
        ("noTasks", "Nothing to do"), ("noBills", "No bills to pay"), ("noAppointments", "Nothing planned"),
        ("today", "Today"), ("tomorrow", "Tomorrow"), ("overdue", "Overdue"), ("allDay", "All day"),
        ("empty", "Open Keystone once to show your overview here."),
        ("hidden", "Details are hidden while the app lock is on."),
        ("more", "+{0} more"),
    }.ToDictionary(p => p.Item1, p => Loc.T(p.Item2));

    /// <param name="hideDetails">Counts only, no titles; by default while the app lock is on.</param>
    internal WidgetSnapshot BuildWidgetSnapshot(DateTime now, bool? hideDetails = null)
    {
        var today = DateOnly.FromDateTime(now);
        var hidden = hideDetails ?? IsLockEnabled;
        WidgetItem Item(string title, string? detail, DateOnly? date, DateTime? start = null, bool allDay = false) =>
            hidden ? new WidgetItem("", null, date, start, allDay) : new WidgetItem(title, detail, date, start, allDay);

        // Open tasks: overdue and soonest first, those without a date last.
        var tasks = _tasks.Where(t => !t.IsDone)
            .OrderBy(t => t.DueDate ?? DateOnly.MaxValue).ThenBy(t => t.CreatedAt)
            .ToList();

        var bills = Documents.Where(d => d.IsOpenBill)
            .OrderBy(d => d.NextDueDate ?? DateOnly.MaxValue).ThenBy(d => d.Date)
            .ToList();

        var appointments = _appointments
            .Where(a => !a.HasPassed(now) && a.Start.Date <= now.Date.AddDays(WidgetSnapshot.AppointmentDays))
            .OrderBy(a => a.Start)
            .ToList();

        return new WidgetSnapshot(
            WidgetSnapshot.CurrentVersion,
            now,
            Loc.Instance.Language,
            hidden,
            WidgetLabels(),
            new WidgetSection(tasks.Count, tasks.Count(t => t.DueDate < today), null,
                tasks.Take(WidgetSnapshot.MaxItems).Select(t => Item(t.Title, null, t.DueDate)).ToList()),
            new WidgetSection(bills.Count, bills.Count(d => d.IsOverdue(today)), Format.Money(bills.Sum(d => d.AmountLeft)),
                bills.Take(WidgetSnapshot.MaxItems)
                    .Select(d => Item(d.Sender == "" ? d.Title : d.Sender,
                        d.HasInstalments
                            ? $"{Format.Money(d.NextAmount)} · {d.NextInstalmentNumber}/{d.Instalments.Count}"
                            : Format.Money(d.NextAmount),
                        d.NextDueDate))
                    .ToList()),
            new WidgetSection(appointments.Count, 0, null,
                appointments.Take(WidgetSnapshot.MaxItems)
                    .Select(a => Item(a.Title, a.Location == "" ? null : a.Location, DateOnly.FromDateTime(a.Start), a.Start, a.IsAllDay))
                    .ToList()));
    }

    /// <summary>Writes the widget's summary; a failure only means the widget shows the last one.</summary>
    private void WriteWidgetSnapshot()
    {
        try
        {
            BuildWidgetSnapshot(DateTime.Now).Write(WidgetSnapshot.PathFor(repository.DatabasePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
