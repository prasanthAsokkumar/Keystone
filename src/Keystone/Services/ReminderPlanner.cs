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

/// <summary>An important date: reminded <paramref name="DaysBefore"/> days ahead and on the day.</summary>
public sealed record DateReminder(int Id, string Title, DateOnly Date, int DaysBefore, string Person = "");

public sealed record WarrantyReminder(int Id, string Item, DateOnly ExpiresOn);

/// <summary>An appointment, reminded at <paramref name="At"/>; <paramref name="When"/> says when it starts.</summary>
public sealed record AppointmentReminder(int Id, string Title, DateTime At, string When, string Location = "");

/// <summary>A medicine: reminded a few days before it runs out and before its prescription ends.</summary>
public sealed record MedicineReminder(int Id, string Name, string Person, DateOnly? RunsOutOn, DateOnly? PrescriptionUntil, string Doctor = "");

/// <summary>A doctor's certificate due for an ongoing time off sick.</summary>
public sealed record CertificateReminder(int Id, string Person, string Employer, DateOnly DueOn, int Day);

/// <summary>Turns bills, tasks, insurance deadlines and 3a room into dated notifications.</summary>
public static class ReminderPlanner
{
    /// <summary>Days before an insurance notice deadline to remind on.</summary>
    public static readonly int[] DeadlineWarningDays = [30, 7];

    /// <summary>Days in December to remind about pillar 3a room on.</summary>
    public static readonly int[] Pillar3aDecemberDays = [1, 20];

    /// <summary>Days before a medicine runs out to remind, leaving time to fetch a pack or a prescription.</summary>
    public const int MedicineRefillDays = 5;

    /// <summary>Days before a prescription ends to remind, to see the doctor in time.</summary>
    public const int PrescriptionDays = 14;

    public static IReadOnlyList<Reminder> Plan(
        IEnumerable<BillReminder> bills, IEnumerable<TaskReminder> tasks, IEnumerable<DeadlineReminder> deadlines,
        IEnumerable<Pillar3aReminder> pillar3a, DateTime now, int hour,
        IEnumerable<DeadlineReminder>? contracts = null, IEnumerable<DateReminder>? dates = null,
        IEnumerable<WarrantyReminder>? warranties = null, IEnumerable<AppointmentReminder>? appointments = null,
        IEnumerable<MedicineReminder>? medicines = null, IEnumerable<CertificateReminder>? certificates = null)
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

        foreach (var contract in contracts ?? [])
        {
            foreach (var days in DeadlineWarningDays)
            {
                reminders.Add(new Reminder($"contract-{contract.Id}-{contract.CancelBy:yyyyMMdd}-{days}",
                    At(contract.CancelBy.AddDays(-days)),
                    Loc.Plural(days, "Contract notice deadline in {0} day", "Contract notice deadline in {0} days"),
                    Loc.F("{0}: to switch, your cancellation must arrive by {1:d}.",
                        string.Join(", ", new[] { contract.Insurer, contract.Kind, contract.Person }.Where(p => p != "")),
                        contract.CancelBy.ToDateTime(TimeOnly.MinValue))));
            }
        }

        foreach (var date in dates ?? [])
        {
            var what = date.Person == "" ? date.Title : $"{date.Title} · {date.Person}";
            if (date.DaysBefore > 0)
                reminders.Add(new Reminder($"date-{date.Id}-{date.Date:yyyyMMdd}-before", At(date.Date.AddDays(-date.DaysBefore)),
                    Loc.F("Coming up: {0}", what), Loc.F("Due on {0:d}.", date.Date.ToDateTime(TimeOnly.MinValue))));
            reminders.Add(new Reminder($"date-{date.Id}-{date.Date:yyyyMMdd}", At(date.Date), Loc.F("Today: {0}", what), ""));
        }

        foreach (var warranty in warranties ?? [])
        {
            reminders.Add(new Reminder($"warranty-{warranty.Id}", At(warranty.ExpiresOn.AddDays(-30)),
                Loc.F("Warranty ends in a month: {0}", warranty.Item),
                Loc.F("If something is wrong with it, report it before {0:d}.", warranty.ExpiresOn.ToDateTime(TimeOnly.MinValue))));
        }

        foreach (var appointment in appointments ?? [])
        {
            reminders.Add(new Reminder($"appointment-{appointment.Id}-{appointment.At:yyyyMMddHHmm}", appointment.At,
                appointment.Title,
                appointment.Location == "" ? appointment.When : $"{appointment.When} · {appointment.Location}"));
        }

        foreach (var medicine in medicines ?? [])
        {
            var what = medicine.Person == "" ? medicine.Name : $"{medicine.Name} · {medicine.Person}";
            if (medicine.RunsOutOn is { } runsOut)
                reminders.Add(new Reminder($"medicine-{medicine.Id}-refill-{runsOut:yyyyMMdd}", At(runsOut.AddDays(-MedicineRefillDays)),
                    Loc.F("Running out soon: {0}", what),
                    Loc.F("Enough until {0:d}. Time to get a new pack.", runsOut.ToDateTime(TimeOnly.MinValue))));
            if (medicine.PrescriptionUntil is { } until)
                reminders.Add(new Reminder($"medicine-{medicine.Id}-prescription-{until:yyyyMMdd}", At(until.AddDays(-PrescriptionDays)),
                    Loc.F("Prescription ends soon: {0}", what),
                    medicine.Doctor == ""
                        ? Loc.F("Valid until {0:d}. Ask the doctor for a new one.", until.ToDateTime(TimeOnly.MinValue))
                        : Loc.F("Valid until {0:d}. Ask {1} for a new one.", until.ToDateTime(TimeOnly.MinValue), medicine.Doctor)));
        }

        foreach (var certificate in certificates ?? [])
            reminders.Add(new Reminder($"sick-{certificate.Id}-certificate-{certificate.DueOn:yyyyMMdd}", At(certificate.DueOn),
                Loc.T("Doctor’s certificate needed today"),
                certificate.Employer == ""
                    ? Loc.F("{0}: day {1} off sick.", certificate.Person, certificate.Day)
                    : Loc.F("{0}: day {1} off sick. Send it to {2}.", certificate.Person, certificate.Day, certificate.Employer)));

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
