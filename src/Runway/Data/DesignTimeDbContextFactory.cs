using Microsoft.EntityFrameworkCore.Design;

namespace Runway.Data;

/// <summary>Lets the <c>dotnet ef</c> tools create a context when adding migrations.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RunwayDbContext>
{
    public RunwayDbContext CreateDbContext(string[] args) => new("design-time.db");
}
