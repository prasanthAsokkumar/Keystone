using Keystone.Models;
using Keystone.Services;

namespace Keystone.Tests;

public sealed class PersonMatcherTests
{
    private static readonly Person Lukas = new() { Id = 1, Name = "Lukas" };
    private static readonly Person Anna = new() { Id = 2, Name = "Anna Meier" };
    private static readonly Person Sofia = new() { Id = 3, Name = "Sofia Meier" };
    private static readonly Person[] Household = [Lukas, Anna, Sofia];

    [Theory]
    [InlineData("Herr Lukas Meier", 1)]
    [InlineData("MEIER LUKAS", 1)]
    [InlineData("lukas", 1)]
    [InlineData("Frau Anna Meier", 2)]
    [InlineData("Anna M.", 2)]
    [InlineData("Sofía", 3)]
    public void FindsTheMember(string name, int id) => Assert.Equal(id, PersonMatcher.Match(name, Household)?.Id);

    [Theory]
    [InlineData("")]
    [InlineData("Familie Meier")] // two members share the family name
    [InlineData("Lukas und Anna Meier")] // several members: a tie on first names
    [InlineData("Max Muster")]
    public void LeavesItOpenWhenUnclear(string name) => Assert.Null(PersonMatcher.Match(name, Household));
}
