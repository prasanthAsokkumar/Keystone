using System;
using System.Collections.Generic;
using System.Linq;
using Keystone.Localization;

namespace Keystone.Models;

/// <summary>
/// A medicine someone takes: what, how much and when, and, when the pills on hand are counted, the day they run
/// out, so a new pack or prescription is fetched in time.
/// </summary>
public class Medicine
{
    public int Id { get; set; }

    public int? PersonId { get; set; }

    /// <summary>Where the row was put by dragging; 0 for rows never moved by hand.</summary>
    public int SortOrder { get; set; }

    /// <summary>"Ibuprofen", "Euthyrox".</summary>
    public string Name { get; set; } = "";

    /// <summary>"400 mg", "50 µg".</summary>
    public string Strength { get; set; } = "";

    /// <summary>How many units at a time, e.g. 1 or 0.5 tablets.</summary>
    public decimal Dose { get; set; } = 1;

    /// <summary>"tablet", "capsule", "drops", "puff".</summary>
    public string Unit { get; set; } = "";

    public bool Morning { get; set; }

    public bool Noon { get; set; }

    public bool Evening { get; set; }

    public bool Night { get; set; }

    /// <summary>Only when needed (pain, allergy); no daily use, so no running-out date.</summary>
    public bool AsNeeded { get; set; }

    /// <summary>What it's for, e.g. "Thyroid".</summary>
    public string Reason { get; set; } = "";

    public string PrescribedBy { get; set; } = "";

    public DateOnly? StartedOn { get; set; }

    /// <summary>The last day, for a course such as antibiotics; null while taken for good.</summary>
    public DateOnly? EndsOn { get; set; }

    /// <summary>Units on hand when last counted (or refilled) on <see cref="CountedOn"/>.</summary>
    public decimal? Stock { get; set; }

    public DateOnly? CountedOn { get; set; }

    /// <summary>Units in one pack, added when "Refilled" is clicked.</summary>
    public int? PackSize { get; set; }

    /// <summary>The prescription (e.g. a repeat prescription, Dauerrezept) is valid until this day.</summary>
    public DateOnly? PrescriptionUntil { get; set; }

    public string Notes { get; set; } = "";

    public int TimesPerDay => (Morning ? 1 : 0) + (Noon ? 1 : 0) + (Evening ? 1 : 0) + (Night ? 1 : 0);

    /// <summary>Units used a day; 0 when only taken as needed.</summary>
    public decimal PerDay => AsNeeded ? 0 : Dose * TimesPerDay;

    public bool IsTaken(DateOnly today) => (StartedOn is not { } start || start <= today) && (EndsOn is not { } end || end >= today);

    public bool HasStopped(DateOnly today) => EndsOn is { } end && end < today;

    /// <summary>Units left on a day, counting down from the last count.</summary>
    public decimal? StockOn(DateOnly date)
    {
        if (Stock is not { } stock || CountedOn is not { } counted)
            return null;
        var days = Math.Max(0, date.DayNumber - counted.DayNumber);
        return Math.Max(0, stock - PerDay * days);
    }

    /// <summary>The last day there's enough for a full day; null when not counted or taken as needed.</summary>
    public DateOnly? RunsOutOn
    {
        get
        {
            if (Stock is not { } stock || CountedOn is not { } counted || PerDay <= 0)
                return null;
            var fullDays = (int)Math.Floor(stock / PerDay);
            return counted.AddDays(Math.Max(0, fullDays - 1));
        }
    }

    /// <summary>"Morning and evening" or "As needed".</summary>
    public string TimesText
    {
        get
        {
            if (AsNeeded)
                return Loc.T("As needed");
            var times = new List<string>();
            if (Morning) times.Add(Loc.T("morning"));
            if (Noon) times.Add(Loc.T("noon"));
            if (Evening) times.Add(Loc.T("evening"));
            if (Night) times.Add(Loc.T("night"));
            if (times.Count == 0)
                return "";
            var text = times.Count == 1 ? times[0] : Loc.F("{0} and {1}", string.Join(", ", times.Take(times.Count - 1)), times[^1]);
            return char.ToUpper(text[0], Loc.Instance.Culture) + text[1..];
        }
    }

    /// <summary>"1 tablet" or "½ tablet".</summary>
    public string DoseText
    {
        get
        {
            var amount = Dose == 0.5m ? "½" : Dose == 0.25m ? "¼" : Dose.ToString("0.##", Loc.Instance.Culture);
            return Unit == "" ? amount : $"{amount} {Unit}";
        }
    }
}

/// <remarks>Stored by name.</remarks>
public enum SickReportChannel { Phone, Email, Text, Portal }

/// <summary>
/// A job of a household member: the employer, and whom to tell (and how, and by when) when they are ill or have
/// had an accident and can't work.
/// </summary>
public class Employment
{
    public int Id { get; set; }

    public int? PersonId { get; set; }

    public int SortOrder { get; set; }

    public string Employer { get; set; } = "";

    public string Role { get; set; } = "";

    /// <summary>Workload in percent (Pensum), e.g. 80.</summary>
    public int? Workload { get; set; }

    public DateOnly? StartedOn { get; set; }

    /// <summary>The last working day, for a job that has ended.</summary>
    public DateOnly? EndedOn { get; set; }

    public string Address { get; set; } = "";

    /// <summary>The employer's main number, e.g. reception.</summary>
    public string Phone { get; set; } = "";

    public string ManagerName { get; set; } = "";

    public string ManagerPhone { get; set; } = "";

    public string ManagerEmail { get; set; } = "";

    public string HrName { get; set; } = "";

    public string HrPhone { get; set; } = "";

    public string HrEmail { get; set; } = "";

    /// <summary>How the employer wants to be told about an absence.</summary>
    public SickReportChannel ReportBy { get; set; } = SickReportChannel.Phone;

    /// <summary>Tell them before this time on the first morning, e.g. 08:00; null if no time is set.</summary>
    public TimeOnly? ReportBefore { get; set; }

    /// <summary>A doctor's certificate is needed from this day of absence on (often the 3rd or 4th).</summary>
    public int CertificateFromDay { get; set; } = 3;

    /// <summary>The daily sickness allowance insurer (Krankentaggeld), if the employer has one.</summary>
    public string SickPayInsurer { get; set; } = "";

    /// <summary>The employer's accident insurer (UVG), e.g. Suva.</summary>
    public string AccidentInsurer { get; set; } = "";

    public string Notes { get; set; } = "";

    public bool HasEnded(DateOnly today) => EndedOn is { } end && end < today;

    /// <summary>Whom to tell first: the manager, else HR, else the employer's own number.</summary>
    public (string Name, string Phone, string Email) FirstContact =>
        ManagerName != "" || ManagerPhone != "" || ManagerEmail != "" ? (ManagerName, ManagerPhone, ManagerEmail)
        : HrName != "" || HrPhone != "" || HrEmail != "" ? (HrName, HrPhone, HrEmail)
        : (Employer, Phone, "");

    public static string ChannelName(SickReportChannel channel) => Loc.T(channel switch
    {
        SickReportChannel.Email => "By email",
        SickReportChannel.Text => "By text message",
        SickReportChannel.Portal => "In the staff portal or app",
        _ => "By phone",
    });
}

/// <summary>A time off work through illness or an accident, from its first day to its last.</summary>
public class SickLeave
{
    public int Id { get; set; }

    public int? PersonId { get; set; }

    /// <summary>The job it was reported to; null if none.</summary>
    public int? EmploymentId { get; set; }

    public DateOnly From { get; set; }

    /// <summary>The last day off; null while still off.</summary>
    public DateOnly? To { get; set; }

    /// <summary>An accident (not illness) goes to the accident insurer, through the employer.</summary>
    public bool IsAccident { get; set; }

    public string Reason { get; set; } = "";

    /// <summary>The employer has been told.</summary>
    public bool Informed { get; set; }

    /// <summary>The doctor's certificate has been handed in.</summary>
    public bool CertificateSent { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>Still off: no last day yet, or one still to come. A time off that ends today is over (back at work).</summary>
    public bool IsOngoing(DateOnly today) => From <= today && (To is null || To > today);

    /// <summary>Calendar days off, counting both ends (today for an ongoing one).</summary>
    public int Days(DateOnly today) => Math.Max(1, (To ?? (today < From ? From : today)).DayNumber - From.DayNumber + 1);

    /// <summary>The day a certificate is due when one is needed from <paramref name="fromDay"/>.</summary>
    public DateOnly CertificateDueOn(int fromDay) => From.AddDays(Math.Max(1, fromDay) - 1);

    public bool NeedsCertificate(DateOnly today, int fromDay) => !CertificateSent && Days(today) >= Math.Max(1, fromDay);
}
