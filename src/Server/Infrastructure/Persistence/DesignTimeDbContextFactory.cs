using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Compendio.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>.
/// </summary>
/// <remarks>
/// Without it, the tools try to build the whole host — background services and all — just to read
/// the model. That both fails and would be a bad idea if it worked: a design-time command must not
/// start a file watcher over somebody's content folder.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CompendioDbContext>
{
    public CompendioDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<CompendioDbContext>();

        // A throwaway path. Migrations are generated from the model, not from a live database.
        builder.UseSqlite("Data Source=compendio-design-time.db");

        return new CompendioDbContext(builder.Options);
    }
}
