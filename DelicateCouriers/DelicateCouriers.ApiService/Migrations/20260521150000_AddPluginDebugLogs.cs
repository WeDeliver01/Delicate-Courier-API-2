using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Creates the PluginDebugLogs table that mirrors the WooCommerce plugin's
/// in-WP-options debug-log ring buffer onto the platform side. Each row is
/// one plugin debug entry (server-side or browser-side), capped at 10,000
/// per store via a probabilistic prune in the ingest controller. See
/// <c>PluginDebugLog</c> entity XML doc for the full rationale.
///
/// Indexed on (StoreID, OccurredAt desc) because the SuperAdmin viewer is
/// always scoped to a store and always orders newest-first.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260521150000_AddPluginDebugLogs")]
public partial class AddPluginDebugLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PluginDebugLogs",
            columns: table => new
            {
                PluginDebugLogID = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StoreID = table.Column<int>(type: "integer", nullable: false),
                TenantID = table.Column<int>(type: "integer", nullable: false),
                OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Context = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Detail = table.Column<string>(type: "text", nullable: false),
                DataJson = table.Column<string>(type: "text", nullable: true),
                PluginVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PluginDebugLogs", x => x.PluginDebugLogID);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PluginDebugLogs_StoreID_OccurredAt",
            table: "PluginDebugLogs",
            columns: new[] { "StoreID", "OccurredAt" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "IX_PluginDebugLogs_TenantID",
            table: "PluginDebugLogs",
            column: "TenantID");

        migrationBuilder.CreateIndex(
            name: "IX_PluginDebugLogs_OccurredAt",
            table: "PluginDebugLogs",
            column: "OccurredAt",
            descending: new[] { true });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PluginDebugLogs");
    }
}
