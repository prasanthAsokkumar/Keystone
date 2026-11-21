using System;

namespace Runway.Models;

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

    public DateTime CreatedAt { get; set; }

    public bool IsDone => CompletedOn is not null;

    public bool IsOverdue(DateOnly today) => !IsDone && DueDate < today;

    /// <summary>Open and due within the next <paramref name="days"/> days, or already overdue.</summary>
    public bool IsDueWithin(DateOnly today, int days) => !IsDone && DueDate <= today.AddDays(days);
}
