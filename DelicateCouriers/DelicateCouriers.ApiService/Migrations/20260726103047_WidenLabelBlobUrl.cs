using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Scaffolding also detected pre-existing snapshot drift (audit_logs,
    /// DataProtectionKeys, PluginDebugLogs, plugins/licenses/releases,
    /// SystemEvents, telemetry_events, etc.). Those tables already exist in
    /// the database (created outside the migration history), so the generated
    /// CreateTable/CreateIndex operations were removed — only the BlobURL
    /// column widening is applied. The model snapshot now includes them, which
    /// matches the live schema.
    /// </remarks>
    public partial class WidenLabelBlobUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "BlobURL",
                table: "Labels",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "BlobURL",
                table: "Labels",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
