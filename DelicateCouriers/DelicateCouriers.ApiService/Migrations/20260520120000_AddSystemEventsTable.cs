using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Creates the SystemEvents table backing the SuperAdmin "System Events"
/// page. Append-only; starts empty and accumulates from deploy time onward.
///
/// Written as raw SQL with IF NOT EXISTS guards so it's safe to re-run on
/// environments that may have been partially provisioned.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260520120000_AddSystemEventsTable")]
public partial class AddSystemEventsTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""SystemEvents"" (
                ""SystemEventID"" bigserial PRIMARY KEY,
                ""OccurredAt"" timestamp without time zone NOT NULL,
                ""EventType"" varchar(100) NOT NULL,
                ""TenantID"" integer NULL,
                ""ActorUserID"" integer NULL,
                ""ActorKind"" varchar(20) NOT NULL DEFAULT 'System',
                ""ActorLabel"" varchar(200) NULL,
                ""EntityType"" varchar(50) NULL,
                ""EntityRef"" varchar(200) NULL,
                ""Message"" varchar(1000) NOT NULL DEFAULT '',
                ""DetailsJson"" text NULL
            );
        ");

        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_SystemEvents_OccurredAt"" ON ""SystemEvents"" (""OccurredAt"" DESC);");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_SystemEvents_EventType"" ON ""SystemEvents"" (""EventType"");");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_SystemEvents_TenantID"" ON ""SystemEvents"" (""TenantID"");");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""SystemEvents"";");
    }
}
