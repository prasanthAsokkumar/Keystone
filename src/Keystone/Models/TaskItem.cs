using System;

namespace Keystone.Models;

/// <remarks>Stored by name, so new values need no migration.</remarks>
public enum TaskRepeat { Never, Weekly, Monthly, Quarterly, Yearly }

/// <summary>Something to do, optionally by a due date and for a household member.</summary>
public class TaskItem
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string Notes { get; set; } = "";

    public DateOnly? DueDate { get; set; }

    /// <summary>Who has to do it; null means anyone in the household.</summary>
    public int? PersonId { get; set; }

    public DateOnly? CompletedOn { get; set; }

    /// <summary>A repeating task comes back with its next due date when it is done.</summary>
    public TaskRepeat Repeat { get; set; }

    public bool Repeats => Repeat != TaskRepeat.Never;

    /// <summary>The due date after <paramref name="due"/> for a repeating task.</summary>
    public static DateOnly NextDue(DateOnly due, TaskRepeat repeat) => repeat switch
    {
        TaskRepeat.Weekly => due.AddDays(7),
        TaskRepeat.Monthly => due.AddMonths(1),
        TaskRepeat.Quarterly => due.AddMonths(3),
        _ => due.AddYears(1),
    };

    public DateTime CreatedAt { get; set; }

    public bool IsDone => CompletedOn is not null;

    public bool IsOverdue(DateOnly today) => !IsDone && DueDate < today;

    /// <summary>Open and due within the next <paramref name="days"/> days, or already overdue.</summary>
    public bool IsDueWithin(DateOnly today, int days) => !IsDone && DueDate <= today.AddDays(days);
}
