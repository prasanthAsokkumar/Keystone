using System;

namespace Keystone.Models;

/// <summary>Free-form thoughts, e.g. ideas for a project or a brainstorm.</summary>
public class Note
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>Groups notes that belong together; empty when the note stands on its own.</summary>
    public string Project { get; set; } = "";

    /// <summary>Pinned notes stay at the top of the list.</summary>
    public bool IsPinned { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
