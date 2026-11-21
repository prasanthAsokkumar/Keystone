using Microsoft.EntityFrameworkCore.Design;

namespace Keystone.Data;

/// <summary>Lets the <c>dotnet ef</c> tools create a context when adding migrations.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KeystoneDbContext>
{
    public KeystoneDbContext CreateDbContext(string[] args) => new("design-time.db");
}
