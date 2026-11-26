using System;

namespace Keystone.Models;

/// <summary>
/// A household member that income, expenses and papers can belong to, with what's good to have at hand about them:
/// how to reach them, their health basics for an emergency, and whom to call.
/// </summary>
public class Person
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>For retirement planning: the year they turn 65 follows from it. Kept in step with <see cref="BirthDate"/>.</summary>
    public int? BirthYear { get; set; }

    /// <summary>The full date of birth, when known; forms and the doctor ask for it.</summary>
    public DateOnly? BirthDate { get; set; }

    public string Phone { get; set; } = "";

    public string Email { get; set; } = "";

    /// <summary>The AHV/AVS number (756.xxxx.xxxx.xx), asked for by employers, insurers and the tax office.</summary>
    public string AhvNumber { get; set; } = "";

    // ── Health basics, for the doctor or an emergency ──

    public string BloodType { get; set; } = "";

    /// <summary>Allergies and intolerances, e.g. "Penicillin, nuts".</summary>
    public string Allergies { get; set; } = "";

    /// <summary>Lasting conditions worth telling a doctor, e.g. "Asthma".</summary>
    public string Conditions { get; set; } = "";

    public string DoctorName { get; set; } = "";

    public string DoctorPhone { get; set; } = "";

    public string EmergencyName { get; set; } = "";

    public string EmergencyPhone { get; set; } = "";

    /// <summary>How the emergency contact is related, e.g. "Sister".</summary>
    public string EmergencyRelation { get; set; } = "";

    public string Notes { get; set; } = "";

    /// <summary>Age in whole years on a day, from the birth date or else the birth year (then roughly).</summary>
    public int? AgeOn(DateOnly date)
    {
        if (BirthDate is { } born)
            return date.Year - born.Year - (date < born.AddYears(date.Year - born.Year) ? 1 : 0);
        return BirthYear is { } year ? date.Year - year : null;
    }

    /// <summary>The next birthday on or after a day (29 February falls on 28 February in other years).</summary>
    public DateOnly? NextBirthday(DateOnly from)
    {
        if (BirthDate is not { } born)
            return null;
        var next = born.AddYears(from.Year - born.Year);
        return next < from ? born.AddYears(from.Year - born.Year + 1) : next;
    }
}
