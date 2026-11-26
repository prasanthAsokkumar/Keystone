using System;
using Keystone.Localization;

namespace Keystone.Services;

/// <summary>
/// Swiss rules for pay during illness, as a guide. Without a daily sickness allowance insurance (Krankentaggeld), the
/// employer must keep paying for a limited time (OR 324a) once the job has lasted more than three months; courts in
/// Basel, Bern and most cantons use the Bern scale for how long. Many employers insure instead: then usually 80% of
/// the pay for up to 720 days, after a waiting time. Contracts and staff rules can say more, never less.
/// </summary>
public static class SwissSickPay
{
    /// <summary>Most employers want a doctor's certificate from the third day; contracts say which day.</summary>
    public const int UsualCertificateDay = 3;

    /// <summary>
    /// The pay the law guarantees during illness in a year of service by the Bern scale, in weeks or months:
    /// 1st year 3 weeks, 2nd year 1 month, 3rd–4th 2 months, 5th–9th 3 months, then a month more every 5 years.
    /// Null in the first three months, when the law guarantees nothing.
    /// </summary>
    public static (int Weeks, int Months)? BernScale(DateOnly started, DateOnly today)
    {
        if (today < started.AddMonths(3))
            return null;
        var yearsDone = today.Year - started.Year - (today < started.AddYears(today.Year - started.Year) ? 1 : 0);
        return yearsDone switch
        {
            0 => (3, 0),
            1 => (0, 1),
            2 or 3 => (0, 2),
            _ => (0, 3 + (yearsDone - 4) / 5),
        };
    }

    public static string BernScaleText((int Weeks, int Months) scale) => scale.Weeks > 0
        ? Loc.Plural(scale.Weeks, "{0} week", "{0} weeks")
        : Loc.Plural(scale.Months, "{0} month", "{0} months");
}
