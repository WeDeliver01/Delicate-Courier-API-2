using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DelicateCouriers.Integration.Merchants;

/// <summary>
/// Builds the context for the EF tooling, without starting the application.
///
/// `dotnet ef` otherwise boots the real host to find a context, which means every guard the
/// service has about being misconfigured — no queue, no admin key — fires during what is only
/// a code-generation step. Those guards are right at runtime and wrong here.
/// </summary>
public sealed class MerchantDbContextFactory : IDesignTimeDbContextFactory<MerchantDbContext>
{
    public MerchantDbContext CreateDbContext(string[] args)
    {
        var connection =
            Environment.GetEnvironmentVariable("MERCHANTS_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=delicate;Username=delicate;Password=delicate";

        var options = new DbContextOptionsBuilder<MerchantDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new MerchantDbContext(options);
    }
}
