using System;

namespace Keystone.Models;

/// <summary>When a contract that runs for a term must be cancelled: shared by insurance and other contracts.</summary>
public static class NoticeDeadline
{
    /// <summary>
    /// The next term end that can still be cancelled in time, and the last day the cancellation must arrive.
    /// A contract that renews by itself moves on to next year's deadline once this year's has passed.
    /// </summary>
    public static (DateOnly End, DateOnly CancelBy)? Next(DateOnly? contractEnd, bool renews, int noticeMonths, DateOnly today)
    {
        if (contractEnd is not { } end)
            return null;

        DateOnly CancelBy(DateOnly termEnd) => termEnd.AddDays(1).AddMonths(-noticeMonths).AddDays(-1);

        if (renews)
        {
            while (CancelBy(end) < today)
                end = end.AddYears(1);
        }
        else if (end < today)
        {
            return null;
        }

        return (end, CancelBy(end));
    }
}
