using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Localization;
using Keystone.Services.Mac;

namespace Keystone.Services;

public sealed record BillReminder(int Id, string Title, string Sender, decimal Amount, DateOnly DueDate);

public sealed record TaskReminder(int Id, string Title, DateOnly DueDate);

/// <param name="Person">Whose policy it is, to tell apart policies of the same kind; empty for the household.</param>
public sealed record DeadlineReminder(int Id, string Insurer, string Kind, DateOnly CancelBy, string Person = "");

public sealed record Pillar3aReminder(string PersonName, decimal Left, int Year);

/// <summary>Turns bills, tasks, insurance deadlines and 3a room into dated notifications.</summary>
public static class ReminderPlanner
{
    /// <summary>Days before an insurance notice deadline to remind on.</summary>
    public static readonly int[] DeadlineWarningDays = [30, 7];

    /// <summary>Days in December to remind about pillar 3a room on.</summary>
    public static readonly int[] Pillar3aDecemberDays = [1, 20];

    public static IReadOnlyList<Reminder> Plan(
        IEnumerable<BillReminder> bills, IEnumerable<TaskReminder> tasks, IEnumerable<DeadlineReminder> deadlines,
        IEnumerable<Pillar3aReminder> pillar3a, DateTime now, int hour)
    {
        DateTime At(DateOnly day) => day.ToDateTime(new TimeOnly(Math.Clamp(hour, 0, 23), 0));
        var reminders = new List<Reminder>();

        foreach (var bill in bills)
        {
            var what = bill.Sender == "" ? bill.Title : $"{bill.Sender}: {bill.Title}";
            var body = Loc.F("{0}, {1}", what, FormatMoney(bill.Amount));
            reminders.Add(new Reminder($"bill-{bill.Id}-before", At(bill.DueDate.AddDays(-1)), Loc.T("Bill due tomorrow"), body));
            reminders.Add(new Reminder($"bill-{bill.Id}-due", At(bill.DueDate), Loc.T("Bill due today"), body));
        }

        foreach (var task in tasks)
            reminders.Add(new Reminder($"task-{task.Id}", At(task.DueDate), Loc.T("Task due today"), task.Title));

        foreach (var deadline in deadlines)
        {
            foreach (var days in DeadlineWarningDays)
            {
                reminders.Add(new Reminder($"insurance-{deadline.Id}-{deadline.CancelBy:yyyyMMdd}-{days}",
                    At(deadline.CancelBy.AddDays(-days)),
                    Loc.Plural(days, "Insurance notice deadline in {0} day", "Insurance notice deadline in {0} days"),
                    Loc.F("{0}: to switch, your cancellation must arrive by {1:d}.",
                        string.Join(", ", new[] { deadline.Insurer, deadline.Kind, deadline.Person }.Where(p => p != "")),
                        deadline.CancelBy.ToDateTime(TimeOnly.MinValue))));
            }
        }

        foreach (var room in pillar3a.Where(r => r.Left > 0))
        {
            foreach (var day in Pillar3aDecemberDays)
            {
                reminders.Add(new Reminder($"pillar3a-{room.Year}-{room.PersonName}-{day}", At(new DateOnly(room.Year, 12, day)),
                    Loc.F("Pillar 3a: {0} left for {1}", FormatMoney(room.Left), room.PersonName),
                    Loc.T("Pay it in by 31 December to deduct it from this year’s taxable income.")));
            }
        }

        return reminders
            .Where(r => r.At > now)
            .OrderBy(r => r.At)
            .Take(MacNotifications.MaxPending)
            .ToList();
    }

    private static string FormatMoney(decimal amount) => amount.ToString("C2", Loc.Instance.Culture);
}
