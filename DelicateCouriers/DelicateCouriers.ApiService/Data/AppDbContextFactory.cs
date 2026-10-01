using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DelicateCouriers.ApiService.Data;

/// <summary>
/// Factory for creating DbContext at design time (for migrations).
/// IHttpContextAccessor is not available at design time so it is passed as null;
/// AppDbContext handles the null case by bypassing the tenant query filter.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        // Use the local PostgreSQL connection string
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=delicatedb_dev;Username=postgres;Password=postgres25");

        return new AppDbContext(optionsBuilder.Options, httpContextAccessor: null);
    }
}
