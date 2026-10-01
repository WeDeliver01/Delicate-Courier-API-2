using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Removes the DAA (Delicate API Adapter) remote-monitoring feature.
///
/// The feature is no longer used: the SuperAdmin "DAA Monitor" tab, the
/// ingest/admin endpoints, the 30s pull-sweep Hangfire job and all related
/// code have been deleted. This migration drops the five DAA tables and
/// purges the `daa.*` rows from SystemEvents so the events page no longer
/// lists DAA event types.
///
/// The DataProtectionKeys table is intentionally KEPT — it backs the
/// ASP.NET Data Protection key ring, which is generic infrastructure.
///
/// All DDL is guarded (IF EXISTS) so it is safe to re-run, mirroring the
/// style of <c>20260522120000_AddDaaTablesAndSystemEventExtensions</c>.
/// Down() is intentionally a no-op: the feature's data is discarded.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260713130000_RemoveDaa")]
public partial class RemoveDaa : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Children first (FKs reference DaaMerchants).
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaRemoteActions"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaSettingsSnapshots"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaOrderTraces"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaLogEntries"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaMerchants"";");

        // Purge DAA rows from the system events audit stream so the
        // SuperAdmin events page stops offering daa.* event-type filters.
        migrationBuilder.Sql(@"DELETE FROM ""SystemEvents"" WHERE ""Source"" = 'daa' OR ""EventType"" LIKE 'daa.%';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Irreversible by design — DAA data is discarded with the feature.
    }
}
