using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keystone.Localization;
using Keystone.Models;

namespace Keystone.ViewModels;

/// <summary>A household member in the Household page's row of names.</summary>
public sealed record MemberTab(Person Person, bool IsSelected, bool HasAlert)
{
    public string Name => Person.Name;
}

/// <summary>A labelled fact on a profile card ("Phone", "079 123 45 67"), copyable when it's something to type elsewhere.</summary>
public sealed record InfoLine(string Label, string Value, bool IsWarning = false, bool CanCopy = false);

/// <summary>
/// The Household page: one profile per member, with how to reach them, health basics for a doctor or an emergency,
/// the medicines they take, their job and whom to tell there when they are ill, and their sick days.
/// </summary>
public partial class MainViewModel
{
    public static readonly IReadOnlyList<string> BloodTypes = ["", "0+", "0−", "A+", "A−", "B+", "B−", "AB+", "AB−"];

    [ObservableProperty]
    public partial int? SelectedMemberId { get; set; }

    private Person? SelectedMember => People.FirstOrDefault(p => p.Id == SelectedMemberId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMembers))]
    public partial IReadOnlyList<MemberTab> MemberTabs { get; set; } = [];

    public bool HasMembers => MemberTabs.Count > 0;

    [ObservableProperty]
    public partial string MemberName { get; set; } = "";

    /// <summary>"34 years · born 3 Mar 1992 · birthday in 12 days".</summary>
    [ObservableProperty]
    public partial string MemberSummary { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> MemberContactLines { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> MemberHealthLines { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> MemberEmergencyLines { get; set; } = [];

    public bool HasMemberContactLines => MemberContactLines.Count > 0;

    public bool HasMemberHealthLines => MemberHealthLines.Count > 0;

    public bool HasMemberEmergencyLines => MemberEmergencyLines.Count > 0;

    partial void OnMemberContactLinesChanged(IReadOnlyList<InfoLine> value) => OnPropertyChanged(nameof(HasMemberContactLines));

    partial void OnMemberHealthLinesChanged(IReadOnlyList<InfoLine> value) => OnPropertyChanged(nameof(HasMemberHealthLines));

    partial void OnMemberEmergencyLinesChanged(IReadOnlyList<InfoLine> value) => OnPropertyChanged(nameof(HasMemberEmergencyLines));

    [RelayCommand]
    private void SelectMember(MemberTab tab)
    {
        SelectedMemberId = tab.Person.Id;
        RefreshHousehold();
    }

    [RelayCommand]
    private void OpenHousehold() => ShowMember(null);

    /// <summary>Opens the Household page on a member, e.g. from the Overview.</summary>
    private void ShowMember(int? personId)
    {
        if (personId is not null)
            SelectedMemberId = personId;
        CurrentPage = AppPage.Household;
        RefreshHousehold();
    }

    private async Task LoadHouseholdAsync()
    {
        _medicines.Clear();
        _medicines.AddRange(await repository.GetAllAsync<Medicine>());
        _employments.Clear();
        _employments.AddRange(await repository.GetAllAsync<Employment>());
        _sickLeaves.Clear();
        _sickLeaves.AddRange(await repository.GetAllAsync<SickLeave>());
        RefreshHousehold();
    }

    /// <summary>Rebuilds the Household page for the selected member (the first one if none is chosen).</summary>
    private void RefreshHousehold()
    {
        if (SelectedMember is null)
            SelectedMemberId = People.FirstOrDefault()?.Id;
        var person = SelectedMember;
        MemberTabs = People.Select(p => new MemberTab(p, p.Id == SelectedMemberId, MemberNeedsAttention(p.Id))).ToList();

        MemberName = person?.Name ?? "";
        MemberSummary = person is null ? "" : MemberSummaryText(person);
        MemberContactLines = person is null ? [] : new[]
        {
            new InfoLine(Loc.T("Phone"), person.Phone, CanCopy: true),
            new InfoLine(Loc.T("Email"), person.Email, CanCopy: true),
            new InfoLine(Loc.T("AHV number"), person.AhvNumber, CanCopy: true),
        }.Where(l => l.Value != "").ToList();
        MemberHealthLines = person is null ? [] : new[]
        {
            new InfoLine(Loc.T("Allergies"), person.Allergies, IsWarning: true),
            new InfoLine(Loc.T("Conditions"), person.Conditions),
            new InfoLine(Loc.T("Blood type"), person.BloodType),
            new InfoLine(Loc.T("Family doctor"), string.Join(" · ", new[] { person.DoctorName, person.DoctorPhone }.Where(s => s != "")),
                CanCopy: person.DoctorPhone != ""),
        }.Where(l => l.Value != "").ToList();
        MemberEmergencyLines = person is null || person.EmergencyName == "" && person.EmergencyPhone == "" ? [] :
        [
            new InfoLine(person.EmergencyRelation == "" ? Loc.T("In an emergency") : person.EmergencyRelation,
                string.Join(" · ", new[] { person.EmergencyName, person.EmergencyPhone }.Where(s => s != "")), CanCopy: person.EmergencyPhone != ""),
        ];

        RefreshMedicineRows();
        RefreshWorkRows();
    }

    private string MemberSummaryText(Person person)
    {
        var parts = new List<string>();
        if (person.AgeOn(Today) is { } age and >= 0)
            parts.Add(person.BirthDate is null ? Loc.F("born {0}", person.BirthYear!.Value) : Loc.Plural(age, "{0} year", "{0} years"));
        if (person.BirthDate is { } born)
            parts.Add(Loc.F("born {0}", Format.Date(born)));
        if (person.NextBirthday(Today) is { } birthday)
        {
            var days = birthday.DayNumber - Today.DayNumber;
            if (days == 0)
                parts.Add(Loc.T("birthday today 🎂"));
            else if (days <= 30)
                parts.Add(Loc.Plural(days, "birthday in {0} day", "birthday in {0} days"));
        }
        return parts.Count == 0 ? Loc.T("Add a birth date, contacts and health basics with Edit.") : string.Join(" · ", parts);
    }

    /// <summary>Something to act on for this member: medicine running out, a prescription ending, or off sick.</summary>
    private bool MemberNeedsAttention(int personId) =>
        _medicines.Any(m => m.PersonId == personId && MedicineAlert(m) is not null) ||
        _sickLeaves.Any(s => s.PersonId == personId && s.IsOngoing(Today));

    /// <summary>The member's profile as text, to read out to a doctor or hand to a babysitter.</summary>
    internal string HealthSummaryText(Person person)
    {
        var lines = new List<string> { person.Name };
        if (person.BirthDate is { } born)
            lines.Add(Loc.F("Born {0}", Format.Date(born)));
        void Add(string label, string value)
        {
            if (value != "")
                lines.Add($"{label}: {value}");
        }
        Add(Loc.T("Allergies"), person.Allergies);
        Add(Loc.T("Conditions"), person.Conditions);
        Add(Loc.T("Blood type"), person.BloodType);
        var medicines = _medicines.Where(m => m.PersonId == person.Id && m.IsTaken(Today)).OrderBy(m => m.Name).ToList();
        if (medicines.Count > 0)
        {
            lines.Add(Loc.T("Medicines") + ":");
            lines.AddRange(medicines.Select(m => "– " + string.Join(", ", new[]
                { $"{m.Name} {m.Strength}".Trim(), m.AsNeeded ? m.TimesText : $"{m.DoseText} {m.TimesText}".Trim(), m.Reason }.Where(s => s != ""))));
        }
        Add(Loc.T("Family doctor"), string.Join(", ", new[] { person.DoctorName, person.DoctorPhone }.Where(s => s != "")));
        Add(Loc.T("In an emergency"), string.Join(", ", new[] { person.EmergencyName, person.EmergencyRelation, person.EmergencyPhone }.Where(s => s != "")));
        Add(Loc.T("AHV number"), person.AhvNumber);
        return string.Join("\n", lines);
    }

    [RelayCommand]
    private async Task CopyHealthSummaryAsync()
    {
        if (SelectedMember is not { } person || CopyToClipboard is null)
            return;
        await CopyToClipboard(HealthSummaryText(person));
        ShowNotice(Loc.F("{0}’s health details copied", person.Name));
    }

    [RelayCommand]
    private async Task CopyInfoAsync(string? text)
    {
        if (CopyToClipboard is not null && !string.IsNullOrWhiteSpace(text))
            await CopyToClipboard(text);
    }

    /// <summary>The copyable part of a line: the phone number of "Dr. Keller · 061 123 45 67", else the whole value.</summary>
    [RelayCommand]
    private Task CopyInfoLineAsync(InfoLine line) => CopyInfoAsync(line.Value.Split(" · ")[^1]);

    // ── The member form ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelOpen))]
    public partial bool IsPersonPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonFormTitle))]
    public partial Person? EditingPerson { get; set; }

    public string PersonFormTitle => EditingPerson is null ? Loc.T("Add household member") : Loc.F("Edit {0}", EditingPerson.Name);

    [ObservableProperty]
    public partial string MemberFormName { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? PersonBirthDate { get; set; }

    [ObservableProperty]
    public partial string PersonPhone { get; set; } = "";

    [ObservableProperty]
    public partial string PersonEmail { get; set; } = "";

    [ObservableProperty]
    public partial string PersonAhvNumber { get; set; } = "";

    [ObservableProperty]
    public partial int PersonBloodTypeIndex { get; set; }

    [ObservableProperty]
    public partial string PersonAllergies { get; set; } = "";

    [ObservableProperty]
    public partial string PersonConditions { get; set; } = "";

    [ObservableProperty]
    public partial string PersonDoctorName { get; set; } = "";

    [ObservableProperty]
    public partial string PersonDoctorPhone { get; set; } = "";

    [ObservableProperty]
    public partial string PersonEmergencyName { get; set; } = "";

    [ObservableProperty]
    public partial string PersonEmergencyPhone { get; set; } = "";

    [ObservableProperty]
    public partial string PersonEmergencyRelation { get; set; } = "";

    [ObservableProperty]
    public partial string PersonNotes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersonError))]
    public partial string? PersonError { get; set; }

    public bool HasPersonError => !string.IsNullOrEmpty(PersonError);

    public IReadOnlyList<string> BloodTypeNames => BloodTypes.Select(t => t == "" ? Loc.T("Not known") : t).ToList();

    [RelayCommand]
    private void NewMember() => OpenPersonForm(null);

    [RelayCommand]
    private void EditMember()
    {
        if (SelectedMember is { } person)
            OpenPersonForm(person);
    }

    private void OpenPersonForm(Person? person)
    {
        CloseOtherPanels();
        EditingPerson = person;
        MemberFormName = person?.Name ?? "";
        PersonBirthDate = person?.BirthDate?.ToDateTime(TimeOnly.MinValue);
        PersonPhone = person?.Phone ?? "";
        PersonEmail = person?.Email ?? "";
        PersonAhvNumber = person?.AhvNumber ?? "";
        PersonBloodTypeIndex = Math.Max(0, BloodTypes.ToList().IndexOf(person?.BloodType ?? ""));
        PersonAllergies = person?.Allergies ?? "";
        PersonConditions = person?.Conditions ?? "";
        PersonDoctorName = person?.DoctorName ?? "";
        PersonDoctorPhone = person?.DoctorPhone ?? "";
        PersonEmergencyName = person?.EmergencyName ?? "";
        PersonEmergencyPhone = person?.EmergencyPhone ?? "";
        PersonEmergencyRelation = person?.EmergencyRelation ?? "";
        PersonNotes = person?.Notes ?? "";
        PersonError = null;
        IsPersonPanelOpen = true;
    }

    [RelayCommand]
    private async Task SavePersonAsync()
    {
        PersonError = null;
        var name = MemberFormName.Trim();
        if (name.Length == 0)
        {
            PersonError = Loc.T("Enter a name.");
            return;
        }
        if (name.Equals(PersonOption.SharedName, StringComparison.CurrentCultureIgnoreCase) ||
            People.Any(p => p != EditingPerson && p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            PersonError = Loc.F("“{0}” is already in the list.", name);
            return;
        }

        var isNew = EditingPerson is null;
        var person = EditingPerson ?? new Person();
        var before = isNew ? null : person.MemberwiseCloneForUndo();
        person.Name = name;
        person.BirthDate = PersonBirthDate is { } born ? DateOnly.FromDateTime(born) : null;
        // The retirement plan counts from the birth year; a full date sets it.
        if (person.BirthDate is { } date)
            person.BirthYear = date.Year;
        person.Phone = PersonPhone.Trim();
        person.Email = PersonEmail.Trim();
        person.AhvNumber = PersonAhvNumber.Trim();
        person.BloodType = BloodTypes[Math.Clamp(PersonBloodTypeIndex, 0, BloodTypes.Count - 1)];
        person.Allergies = PersonAllergies.Trim();
        person.Conditions = PersonConditions.Trim();
        person.DoctorName = PersonDoctorName.Trim();
        person.DoctorPhone = PersonDoctorPhone.Trim();
        person.EmergencyName = PersonEmergencyName.Trim();
        person.EmergencyPhone = PersonEmergencyPhone.Trim();
        person.EmergencyRelation = PersonEmergencyRelation.Trim();
        person.Notes = PersonNotes.Trim();

        try
        {
            if (isNew)
                await repository.AddPersonAsync(person);
            else
                await repository.UpdatePersonAsync(person);
        }
        catch (Exception ex)
        {
            if (before is not null)
                before.CopyTo(person);
            PersonError = Loc.F("Could not save: {0}", ex.Message);
            return;
        }

        // Keep the list in name order, as it is loaded.
        People.Remove(person);
        var index = 0;
        while (index < People.Count && string.Compare(People[index].Name, person.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
            index++;
        People.Insert(index, person);
        SelectedMemberId = person.Id;
        IsPersonPanelOpen = false;
        EditingPerson = null;
        PeopleChanged();
    }

    /// <summary>After a member was added or renamed: every list that shows names.</summary>
    private void PeopleChanged()
    {
        RefreshPersonOptions();
        RefreshBirthYearRows();
        RefreshEntryRows();
        RefreshDocumentRows();
        RefreshTaskRows();
        RefreshRetirement();
        RefreshInsurance();
        RefreshTaxes();
        RefreshDeadlines();
        RefreshPlanning();
        RefreshAssets();
        RefreshHousehold();
        RefreshDashboard();
        ScheduleReminders();
    }

    private void CloseHouseholdPanels()
    {
        IsPersonPanelOpen = false;
        EditingPerson = null;
        CloseMedicinePanel();
        CloseEmploymentPanel();
        CloseSickLeavePanel();
    }
}

internal static class PersonCopy
{
    /// <summary>A copy of a member's fields, to put back if saving fails.</summary>
    public static Person MemberwiseCloneForUndo(this Person person)
    {
        var copy = new Person();
        person.CopyTo(copy);
        return copy;
    }

    public static void CopyTo(this Person from, Person to)
    {
        to.Name = from.Name;
        to.BirthYear = from.BirthYear;
        to.BirthDate = from.BirthDate;
        to.Phone = from.Phone;
        to.Email = from.Email;
        to.AhvNumber = from.AhvNumber;
        to.BloodType = from.BloodType;
        to.Allergies = from.Allergies;
        to.Conditions = from.Conditions;
        to.DoctorName = from.DoctorName;
        to.DoctorPhone = from.DoctorPhone;
        to.EmergencyName = from.EmergencyName;
        to.EmergencyPhone = from.EmergencyPhone;
        to.EmergencyRelation = from.EmergencyRelation;
        to.Notes = from.Notes;
    }
}
