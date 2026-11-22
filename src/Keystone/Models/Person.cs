namespace Keystone.Models;

/// <summary>A household member that income and expenses can belong to.</summary>
public class Person
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>For retirement planning: the year they turn 65 follows from it.</summary>
    public int? BirthYear { get; set; }
}
