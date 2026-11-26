using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;
using Keystone.Services;

namespace Keystone.ViewModels;

/// <summary>A member's job on their profile: the employer, whom to call, and the rules when ill.</summary>
public sealed record EmploymentRow(Employment Job, IReadOnlyList<InfoLine> Contacts, string SickRuleText, string CertificateText,
    string SickPayText, bool IsEnded)
{
    public string Employer => Job.Employer;

    /// <summary>"Nurse · 80% · since 1 Mar 2021".</summary>
    public string DetailText => string.Join(" · ", new[]
    {
        Job.Role,
        Job.Workload is { } w ? $"{w}%" : "",
        Job.EndedOn is { } end && IsEnded ? Loc.F("until {0}", Format.Date(end))
            : Job.StartedOn is { } start ? Loc.F("since {0}", Format.Date(start)) : "",
    }.Where(s => s != ""));

    public bool HasContacts => Contacts.Count > 0;

    public bool HasSickPay => SickPayText != "";

    public bool HasNotes => Job.Notes != "";
}

/// <summary>One step of what to do while off sick, ticked off as it's done.</summary>
public sealed record SickStep(string Text, string Detail, bool IsDone, bool IsDue, string Toggle = "", string CopyText = "")
{
    public bool HasDetail => Detail != "";

    public bool CanToggle => Toggle != "";

    public bool CanCopy => CopyText != "";
}

/// <summary>A time off sick on a member's profile.</summary>
public sealed record SickLeaveRow(SickLeave Leave, string EmployerName, DateOnly Today, int CertificateFromDay)
{
    public string DatesText => Leave.To is { } to && to != Leave.From
        ? $"{Format.Date(Leave.From)} – {Format.Date(to)}"
        : Leave.IsOngoing(Today) ? Loc.F("Since {0}", Format.Date(Leave.From)) : Format.Date(Leave.From);

    public string DaysText => Leave.IsOngoing(Today)
        ? Loc.F("Day {0}", Leave.Days(Today))
        : Loc.Plural(Leave.Days(Today), "{0} day", "{0} days");

    public string DetailText => string.Join(" · ", new[]
    {
        Leave.IsAccident ? Loc.T("Accident") : Loc.T("Illness"),
        Leave.Reason,
        EmployerName,
    }.Where(s => s != ""));

    public bool IsOngoing => Leave.IsOngoing(Today);

    public bool NeedsCertificate => Leave.Days(Today) >= CertificateFromDay;

    public string CertificateText => !NeedsCertificate ? ""
        : Leave.CertificateSent ? Loc.T("Certificate handed in") : Loc.T("Certificate needed");

    public bool HasCertificateText => CertificateText != "";

    public bool CertificateMissing => NeedsCertificate && !Leave.CertificateSent;
}

/// <summary>
/// Members' jobs, and what to do when someone is ill or has had an accident: whom to tell, how and by when, from
/// which day a doctor's certificate is needed, and who pays. Sick days are kept per member.
/// </summary>
public partial class MainViewModel
{
    private readonly List<Employment> _employments = [];
    private readonly List<SickLeave> _sickLeaves = [];

    /// <summary>Times the employer wants to hear by on the first morning; index 0 is "no set time".</summary>
    private static readonly TimeOnly?[] ReportTimes =
        [null, new(7, 0), new(7, 30), new(8, 0), new(8, 30), new(9, 0), new(9, 30), new(10, 0)];

    public IReadOnlyList<string> ReportTimeNames =>
        ReportTimes.Select(t => t is { } time ? time.ToString("HH:mm", Loc.Instance.Culture) : Loc.T("No set time")).ToList();

    public IReadOnlyList<string> ReportChannelNames => Enum.GetValues<SickReportChannel>().Select(Employment.ChannelName).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmploymentRows))]
    public partial IReadOnlyList<EmploymentRow> EmploymentRows { get; set; } = [];

    public bool HasEmploymentRows => EmploymentRows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSickLeaveRows))]
    public partial IReadOnlyList<SickLeaveRow> SickLeaveRows { get; set; } = [];

    public bool HasSickLeaveRows => SickLeaveRows.Count > 0;

    /// <summary>The member's ongoing time off, shown at the top with its steps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemberOffSick))]
    public partial SickLeaveRow? CurrentSickLeave { get; set; }

    public bool IsMemberOffSick => CurrentSickLeave is not null;

    [ObservableProperty]
    public partial IReadOnlyList<SickStep> SickSteps { get; set; } = [];

    /// <summary>"Anna is off sick · day 2".</summary>
    [ObservableProperty]
    public partial string SickBannerTitle { get; set; } = "";

    [ObservableProperty]
    public partial string SickBannerDetail { get; set; } = "";

    /// <summary>"4 sick days in 2026 · 9 in 2025".</summary>
    [ObservableProperty]
    public partial string SickDaysText { get; set; } = "";

    /// <summary>The member's current job, for reporting sick.</summary>
    private Employment? CurrentJobOf(int? personId) =>
        _employments.Where(j => j.PersonId == personId && !j.HasEnded(Today)).OrderBy(j => j.SortOrder).ThenBy(j => j.Id).FirstOrDefault();

    private Employment? JobOf(SickLeave leave) =>
        _employments.FirstOrDefault(j => j.Id == leave.EmploymentId) ?? CurrentJobOf(leave.PersonId);

    private int CertificateDayOf(SickLeave leave) => JobOf(leave)?.CertificateFromDay ?? SwissSickPay.UsualCertificateDay;

    private void RefreshWorkRows()
    {
        var id = SelectedMemberId;
        EmploymentRows = _employments.Where(j => j.PersonId == id)
            .OrderBy(j => j.HasEnded(Today)).ThenBy(j => j.SortOrder).ThenByDescending(j => j.StartedOn)
            .Select(EmploymentRowOf).ToList();

        var leaves = _sickLeaves.Where(s => s.PersonId == id).ToList();
        SickLeaveRows = Sorted("sickLeaves", leaves.OrderByDescending(s => s.From)
                .Select(s => new SickLeaveRow(s, JobOf(s)?.Employer ?? "", Today, CertificateDayOf(s))),
            null,
            new SortKey<SickLeaveRow>("date", r => r.Leave.From, DescendingFirst: true),
            new SortKey<SickLeaveRow>("days", r => r.Leave.Days(Today), DescendingFirst: true));

        var current = leaves.Where(s => s.IsOngoing(Today)).OrderByDescending(s => s.From).FirstOrDefault();
        CurrentSickLeave = current is null ? null : new SickLeaveRow(current, JobOf(current)?.Employer ?? "", Today, CertificateDayOf(current));
        SickSteps = current is null ? [] : StepsFor(current);
        SickBannerTitle = current is null ? "" : current.IsAccident
            ? Loc.F("{0} is off after an accident · day {1}", PersonName(current.PersonId), current.Days(Today))
            : Loc.F("{0} is off sick · day {1}", PersonName(current.PersonId), current.Days(Today));
        SickBannerDetail = current is null ? "" : string.Join(" · ", new[]
            { Loc.F("Since {0}", Format.Date(current.From)), JobOf(current)?.Employer ?? "", current.Reason }.Where(s => s != ""));

        var years = leaves.GroupBy(s => s.From.Year).OrderByDescending(g => g.Key).Take(2).ToList();
        SickDaysText = string.Join(" · ", years.Select(g => Loc.Plural(g.Sum(s => s.Days(Today)), "{0} sick day in {1}", "{0} sick days in {1}", g.Key)));
    }

    private EmploymentRow EmploymentRowOf(Employment job)
    {
        var contacts = new List<InfoLine>();
        void Contact(string role, string name, string phone, string email)
        {
            if (name == "" && phone == "" && email == "")
                return;
            var label = name == "" ? role : $"{role}: {name}";
            if (phone != "")
                contacts.Add(new InfoLine(label, phone, CanCopy: true));
            if (email != "")
                contacts.Add(new InfoLine(phone == "" ? label : "", email, CanCopy: true));
            if (phone == "" && email == "")
                contacts.Add(new InfoLine(role, name));
        }
        Contact(Loc.T("Manager"), job.ManagerName, job.ManagerPhone, job.ManagerEmail);
        Contact(Loc.T("HR"), job.HrName, job.HrPhone, job.HrEmail);
        if (job.Phone != "")
            contacts.Add(new InfoLine(Loc.T("Main number"), job.Phone, CanCopy: true));

        var sickPay = job.SickPayInsurer != ""
            ? Loc.F("Sick pay insured with {0}: usually 80% of the pay for up to 720 days after a waiting time.", job.SickPayInsurer)
            : job.StartedOn is { } started && !job.HasEnded(Today) && SwissSickPay.BernScale(started, Today) is { } scale
                ? Loc.F("No sick pay insurance entered: the law’s minimum this year of service is about {0} of full pay (Bern scale).",
                    SwissSickPay.BernScaleText(scale))
                : "";
        return new EmploymentRow(job, contacts, SickRuleTextOf(job),
            Loc.Plural(job.CertificateFromDay, "Doctor’s certificate from day {0}", "Doctor’s certificate from day {0}"),
            sickPay, job.HasEnded(Today));
    }

    /// <summary>"Call Martin Frei before 08:00 on the first day": one whole sentence per way, so it translates.</summary>
    private static string SickRuleTextOf(Employment job)
    {
        var who = job.FirstContact.Name == "" ? job.Employer : job.FirstContact.Name;
        var time = job.ReportBefore?.ToString("HH:mm", Loc.Instance.Culture);
        var key = (job.ReportBy, time is null) switch
        {
            (SickReportChannel.Email, false) => "Email {0} before {1} on the first day",
            (SickReportChannel.Email, true) => "Email {0} on the first day",
            (SickReportChannel.Text, false) => "Text {0} before {1} on the first day",
            (SickReportChannel.Text, true) => "Text {0} on the first day",
            (SickReportChannel.Portal, false) => "Report it in the staff portal or app before {1} on the first day",
            (SickReportChannel.Portal, true) => "Report it in the staff portal or app on the first day",
            (_, false) => "Call {0} before {1} on the first day",
            _ => "Call {0} on the first day",
        };
        return Loc.F(key, who, time);
    }

    /// <summary>What to do while off sick, in order, with what's done ticked.</summary>
    internal IReadOnlyList<SickStep> StepsFor(SickLeave leave)
    {
        var job = JobOf(leave);
        var steps = new List<SickStep>();
        if (job is null)
            steps.Add(new SickStep(Loc.T("Tell your employer"), Loc.T("Add the job under Work to see here whom to call."),
                leave.Informed, !leave.Informed, "informed"));
        else
        {
            var (name, phone, email) = job.FirstContact;
            var reach = job.ReportBy == SickReportChannel.Email && email != "" ? email : phone != "" ? phone : email;
            steps.Add(new SickStep(SickRuleTextOf(job), string.Join(" · ", new[] { name, reach }.Where(s => s != "")),
                leave.Informed, !leave.Informed, "informed", reach));
        }

        if (leave.IsAccident)
            steps.Add(new SickStep(Loc.T("Report the accident to the employer"),
                job?.AccidentInsurer is { Length: > 0 } insurer
                    ? Loc.F("They report it to {0}; treatment and pay are covered from there.", insurer)
                    : Loc.T("They report it to their accident insurer (UVG), e.g. Suva; treatment and pay are covered from there."),
                leave.Informed, false));

        var day = job?.CertificateFromDay ?? SwissSickPay.UsualCertificateDay;
        var due = leave.CertificateDueOn(day);
        steps.Add(new SickStep(
            Loc.Plural(day, "Doctor’s certificate from day {0}", "Doctor’s certificate from day {0}"),
            due <= Today ? Loc.F("Needed now: day {0} was {1}", day, Format.Date(due)) : Loc.F("Needed if still off on {0}", Format.Date(due)),
            leave.CertificateSent, leave.NeedsCertificate(Today, day), "certificate"));

        if (job?.SickPayInsurer is { Length: > 0 } sickPay && leave.Days(Today) >= 14)
            steps.Add(new SickStep(Loc.F("The employer reports a longer absence to {0}", sickPay),
                Loc.T("Ask HR whether they have done it; the daily allowance starts after the waiting time."), false, false));
        return steps;
    }

    /// <summary>A short message to the employer, ready to paste into a text or email.</summary>
    internal string SickMessage(SickLeave leave)
    {
        var job = JobOf(leave);
        var person = People.FirstOrDefault(p => p.Id == leave.PersonId)?.Name ?? "";
        var greeting = job?.FirstContact.Name is { Length: > 0 } name ? Loc.F("Hello {0}", name.Split(' ')[0]) : Loc.T("Hello");
        var body = leave.IsAccident
            ? Loc.F("I’ve had an accident and can’t work today ({0}).", Format.Date(Today))
            : Loc.F("I’m ill and can’t work today ({0}).", Format.Date(Today));
        var certificate = Loc.Plural(job?.CertificateFromDay ?? SwissSickPay.UsualCertificateDay,
            "I’ll keep you posted and send a doctor’s certificate if I’m off from day {0}.",
            "I’ll keep you posted and send a doctor’s certificate if I’m off from day {0}.");
        return $"{greeting}\n\n{body} {certificate}\n\n{Loc.T("Kind regards")}\n{person}";
    }

    [RelayCommand]
    private async Task CopySickMessageAsync()
    {
        if (CurrentSickLeave is not { } row || CopyToClipboard is null)
            return;
        await CopyToClipboard(SickMessage(row.Leave));
        ShowNotice(Loc.T("Message copied: paste it into a text or email"));
    }

    /// <summary>Starts a time off sick from today for the selected member, so the steps show.</summary>
    [RelayCommand]
    private async Task ReportSickAsync(string? kind)
    {
        if (SelectedMember is not { } person)
            return;
        if (_sickLeaves.Any(s => s.PersonId == person.Id && s.IsOngoing(Today)))
            return;
        var leave = new SickLeave
        {
            PersonId = person.Id,
            EmploymentId = CurrentJobOf(person.Id)?.Id,
            From = Today,
            IsAccident = kind == "accident",
        };
        try
        {
            await repository.SaveAsync(leave, isNew: true);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        _sickLeaves.Add(leave);
        SickLeavesChanged();
    }

    /// <summary>Ends the time off: the last day off was yesterday (or today, if it only started today).</summary>
    [RelayCommand]
    private async Task BackAtWorkAsync()
    {
        if (CurrentSickLeave?.Leave is not { } leave)
            return;
        leave.To = Today > leave.From ? Today.AddDays(-1) : leave.From;
        await SaveSickLeaveChangeAsync(leave, () => leave.To = null);
        ShowNotice(Loc.F("Welcome back, {0}: {1} off", PersonName(leave.PersonId), Loc.Plural(leave.Days(Today), "{0} day", "{0} days")),
            async () =>
            {
                leave.To = null;
                await repository.SaveAsync(leave, isNew: false);
                SickLeavesChanged();
            });
    }

    [RelayCommand]
    private async Task ToggleSickStepAsync(SickStep step)
    {
        if (CurrentSickLeave?.Leave is not { } leave)
            return;
        switch (step.Toggle)
        {
            case "informed":
                leave.Informed = !leave.Informed;
                await SaveSickLeaveChangeAsync(leave, () => leave.Informed = !leave.Informed);
                break;
            case "certificate":
                leave.CertificateSent = !leave.CertificateSent;
                await SaveSickLeaveChangeAsync(leave, () => leave.CertificateSent = !leave.CertificateSent);
                break;
        }
    }

    private async Task SaveSickLeaveChangeAsync(SickLeave leave, Action undo)
    {
        try
        {
            await repository.SaveAsync(leave, isNew: false);
        }
        catch (Exception ex)
        {
            undo();
            ErrorMessage = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        SickLeavesChanged();
    }

    private void SickLeavesChanged()
    {
        RefreshHousehold();
        RefreshDashboard();
        ScheduleReminders();
    }

    [RelayCommand]
    private async Task DeleteSickLeaveAsync(SickLeave leave)
    {
        try
        {
            await repository.DeleteAsync<SickLeave>(s => s.Id == leave.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }
        _sickLeaves.Remove(leave);
        if (EditingSickLeave == leave)
            CloseSickLeavePanel();
        SickLeavesChanged();
        ShowNotice(Loc.F("Deleted the time off from {0}", Format.Date(leave.From)), async () =>
        {
            await repository.SaveAsync(leave, isNew: true);
            _sickLeaves.Add(leave);
            SickLeavesChanged();
        });
    }

    [RelayCommand]
    private async Task DeleteEmploymentAsync(Employment job)
    {
        try
        {
            await repository.DeleteAsync<Employment>(j => j.Id == job.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.F("Could not delete: {0}", ex.Message);
            return;
        }
        _employments.Remove(job);
        // The database drops the link from its sick days; do the same here.
        var linked = _sickLeaves.Where(s => s.EmploymentId == job.Id).ToList();
        foreach (var leave in linked)
            leave.EmploymentId = null;
        if (EditingEmployment == job)
            CloseEmploymentPanel();
        SickLeavesChanged();
        ShowNotice(Loc.F("Deleted “{0}”", job.Employer), async () =>
        {
            await repository.SaveAsync(job, isNew: true);
            _employments.Add(job);
            foreach (var leave in linked)
            {
                leave.EmploymentId = job.Id;
                await repository.SaveAsync(leave, isNew: false);
            }
            SickLeavesChanged();
        });
    }

    // ── The job form ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsEmploymentPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmploymentFormTitle))]
    public partial Employment? EditingEmployment { get; set; }

    public string EmploymentFormTitle => Loc.T(EditingEmployment is null ? "Add job" : "Edit job");

    [ObservableProperty] public partial PersonOption EmploymentPerson { get; set; } = PersonOption.Shared;
    [ObservableProperty] public partial string EmploymentEmployer { get; set; } = "";
    [ObservableProperty] public partial string EmploymentRole { get; set; } = "";
    [ObservableProperty] public partial int? EmploymentWorkload { get; set; }
    [ObservableProperty] public partial DateTime? EmploymentStartedOn { get; set; }
    [ObservableProperty] public partial DateTime? EmploymentEndedOn { get; set; }
    [ObservableProperty] public partial string EmploymentAddress { get; set; } = "";
    [ObservableProperty] public partial string EmploymentPhone { get; set; } = "";
    [ObservableProperty] public partial string EmploymentManagerName { get; set; } = "";
    [ObservableProperty] public partial string EmploymentManagerPhone { get; set; } = "";
    [ObservableProperty] public partial string EmploymentManagerEmail { get; set; } = "";
    [ObservableProperty] public partial string EmploymentHrName { get; set; } = "";
    [ObservableProperty] public partial string EmploymentHrPhone { get; set; } = "";
    [ObservableProperty] public partial string EmploymentHrEmail { get; set; } = "";
    [ObservableProperty] public partial int EmploymentReportByIndex { get; set; }
    [ObservableProperty] public partial int EmploymentReportTimeIndex { get; set; }
    [ObservableProperty] public partial int? EmploymentCertificateDay { get; set; } = SwissSickPay.UsualCertificateDay;
    [ObservableProperty] public partial string EmploymentSickPayInsurer { get; set; } = "";
    [ObservableProperty] public partial string EmploymentAccidentInsurer { get; set; } = "";
    [ObservableProperty] public partial string EmploymentNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmploymentError))]
    public partial string? EmploymentError { get; set; }

    public bool HasEmploymentError => !string.IsNullOrEmpty(EmploymentError);

    [RelayCommand]
    private void NewEmployment()
    {
        CloseOtherPanels();
        EditingEmployment = null;
        EmploymentPerson = PersonOptions.FirstOrDefault(o => o.Id == SelectedMemberId) ?? PersonOption.Shared;
        EmploymentEmployer = EmploymentRole = EmploymentAddress = EmploymentPhone = "";
        EmploymentManagerName = EmploymentManagerPhone = EmploymentManagerEmail = "";
        EmploymentHrName = EmploymentHrPhone = EmploymentHrEmail = "";
        EmploymentSickPayInsurer = EmploymentAccidentInsurer = EmploymentNotes = "";
        EmploymentWorkload = null;
        EmploymentStartedOn = EmploymentEndedOn = null;
        EmploymentReportByIndex = 0;
        EmploymentReportTimeIndex = Array.IndexOf(ReportTimes, new TimeOnly(8, 0));
        EmploymentCertificateDay = SwissSickPay.UsualCertificateDay;
        EmploymentError = null;
        IsEmploymentPanelOpen = true;
    }

    [RelayCommand]
    private void EditEmployment(Employment job)
    {
        NewEmployment();
        EditingEmployment = job;
        EmploymentPerson = PersonOptions.FirstOrDefault(o => o.Id == job.PersonId) ?? PersonOption.Shared;
        EmploymentEmployer = job.Employer;
        EmploymentRole = job.Role;
        EmploymentWorkload = job.Workload;
        EmploymentStartedOn = job.StartedOn?.ToDateTime(TimeOnly.MinValue);
        EmploymentEndedOn = job.EndedOn?.ToDateTime(TimeOnly.MinValue);
        EmploymentAddress = job.Address;
        EmploymentPhone = job.Phone;
        (EmploymentManagerName, EmploymentManagerPhone, EmploymentManagerEmail) = (job.ManagerName, job.ManagerPhone, job.ManagerEmail);
        (EmploymentHrName, EmploymentHrPhone, EmploymentHrEmail) = (job.HrName, job.HrPhone, job.HrEmail);
        EmploymentReportByIndex = (int)job.ReportBy;
        EmploymentReportTimeIndex = Math.Max(0, Array.IndexOf(ReportTimes, job.ReportBefore));
        EmploymentCertificateDay = job.CertificateFromDay;
        EmploymentSickPayInsurer = job.SickPayInsurer;
        EmploymentAccidentInsurer = job.AccidentInsurer;
        EmploymentNotes = job.Notes;
    }

    [RelayCommand]
    private async Task SaveEmploymentAsync()
    {
        EmploymentError = null;
        if (string.IsNullOrWhiteSpace(EmploymentEmployer))
        {
            EmploymentError = Loc.T("Enter the employer’s name.");
            return;
        }
        if (EmploymentWorkload is < 1 or > 100)
        {
            EmploymentError = Loc.T("The workload is between 1 and 100%.");
            return;
        }

        var isNew = EditingEmployment is null;
        var job = EditingEmployment ?? new Employment();
        job.PersonId = EmploymentPerson.Id;
        job.Employer = EmploymentEmployer.Trim();
        job.Role = EmploymentRole.Trim();
        job.Workload = EmploymentWorkload;
        job.StartedOn = EmploymentStartedOn is { } start ? DateOnly.FromDateTime(start) : null;
        job.EndedOn = EmploymentEndedOn is { } end ? DateOnly.FromDateTime(end) : null;
        job.Address = EmploymentAddress.Trim();
        job.Phone = EmploymentPhone.Trim();
        (job.ManagerName, job.ManagerPhone, job.ManagerEmail) = (EmploymentManagerName.Trim(), EmploymentManagerPhone.Trim(), EmploymentManagerEmail.Trim());
        (job.HrName, job.HrPhone, job.HrEmail) = (EmploymentHrName.Trim(), EmploymentHrPhone.Trim(), EmploymentHrEmail.Trim());
        job.ReportBy = Enum.IsDefined((SickReportChannel)EmploymentReportByIndex) ? (SickReportChannel)EmploymentReportByIndex : SickReportChannel.Phone;
        job.ReportBefore = ReportTimes[Math.Clamp(EmploymentReportTimeIndex, 0, ReportTimes.Length - 1)];
        job.CertificateFromDay = Math.Clamp(EmploymentCertificateDay ?? SwissSickPay.UsualCertificateDay, 1, 30);
        job.SickPayInsurer = EmploymentSickPayInsurer.Trim();
        job.AccidentInsurer = EmploymentAccidentInsurer.Trim();
        job.Notes = EmploymentNotes.Trim();

        try
        {
            await repository.SaveAsync(job, isNew);
        }
        catch (Exception ex)
        {
            EmploymentError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        if (isNew)
            _employments.Add(job);
        if (job.PersonId is not null)
            SelectedMemberId = job.PersonId;
        CloseEmploymentPanel();
        SickLeavesChanged();
    }

    private void CloseEmploymentPanel()
    {
        IsEmploymentPanelOpen = false;
        EditingEmployment = null;
    }

    // ── The sick-leave form (for past absences, or to correct one) ──────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsSickLeavePanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SickLeaveFormTitle))]
    public partial SickLeave? EditingSickLeave { get; set; }

    public string SickLeaveFormTitle => Loc.T(EditingSickLeave is null ? "Add sick days" : "Edit sick days");

    [ObservableProperty] public partial DateTime? SickLeaveFrom { get; set; } = DateTime.Today;
    [ObservableProperty] public partial DateTime? SickLeaveTo { get; set; }
    [ObservableProperty] public partial bool SickLeaveIsAccident { get; set; }
    [ObservableProperty] public partial string SickLeaveReason { get; set; } = "";
    [ObservableProperty] public partial bool SickLeaveInformed { get; set; }
    [ObservableProperty] public partial bool SickLeaveCertificateSent { get; set; }
    [ObservableProperty] public partial string SickLeaveNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSickLeaveError))]
    public partial string? SickLeaveError { get; set; }

    public bool HasSickLeaveError => !string.IsNullOrEmpty(SickLeaveError);

    [RelayCommand]
    private void NewSickLeave()
    {
        CloseOtherPanels();
        EditingSickLeave = null;
        SickLeaveFrom = DateTime.Today;
        SickLeaveTo = DateTime.Today;
        SickLeaveIsAccident = false;
        SickLeaveReason = "";
        SickLeaveInformed = true;
        SickLeaveCertificateSent = false;
        SickLeaveNotes = "";
        SickLeaveError = null;
        IsSickLeavePanelOpen = true;
    }

    [RelayCommand]
    private void EditSickLeave(SickLeave leave)
    {
        NewSickLeave();
        EditingSickLeave = leave;
        SickLeaveFrom = leave.From.ToDateTime(TimeOnly.MinValue);
        SickLeaveTo = leave.To?.ToDateTime(TimeOnly.MinValue);
        SickLeaveIsAccident = leave.IsAccident;
        SickLeaveReason = leave.Reason;
        SickLeaveInformed = leave.Informed;
        SickLeaveCertificateSent = leave.CertificateSent;
        SickLeaveNotes = leave.Notes;
    }

    [RelayCommand]
    private async Task SaveSickLeaveAsync()
    {
        SickLeaveError = null;
        if (SickLeaveFrom is not { } fromDate)
        {
            SickLeaveError = Loc.T("Enter the first day off.");
            return;
        }
        var from = DateOnly.FromDateTime(fromDate);
        var to = SickLeaveTo is { } toDate ? DateOnly.FromDateTime(toDate) : (DateOnly?)null;
        if (to < from)
        {
            SickLeaveError = Loc.T("The last day can’t be before the first.");
            return;
        }
        var isNew = EditingSickLeave is null;
        var personId = isNew ? SelectedMemberId : EditingSickLeave!.PersonId;
        if (personId is null)
        {
            SickLeaveError = Loc.T("Add a household member first.");
            return;
        }

        var leave = EditingSickLeave ?? new SickLeave { PersonId = personId };
        leave.From = from;
        leave.To = to;
        // The job held on the first day off.
        leave.EmploymentId = _employments.Where(j => j.PersonId == personId && (j.StartedOn is null || j.StartedOn <= from) && (j.EndedOn is null || j.EndedOn >= from))
            .OrderBy(j => j.SortOrder).ThenBy(j => j.Id).FirstOrDefault()?.Id ?? leave.EmploymentId;
        leave.IsAccident = SickLeaveIsAccident;
        leave.Reason = SickLeaveReason.Trim();
        leave.Informed = SickLeaveInformed;
        leave.CertificateSent = SickLeaveCertificateSent;
        leave.Notes = SickLeaveNotes.Trim();
        try
        {
            await repository.SaveAsync(leave, isNew);
        }
        catch (Exception ex)
        {
            SickLeaveError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }
        if (isNew)
            _sickLeaves.Add(leave);
        CloseSickLeavePanel();
        SickLeavesChanged();
    }

    private void CloseSickLeavePanel()
    {
        IsSickLeavePanelOpen = false;
        EditingSickLeave = null;
    }
}
