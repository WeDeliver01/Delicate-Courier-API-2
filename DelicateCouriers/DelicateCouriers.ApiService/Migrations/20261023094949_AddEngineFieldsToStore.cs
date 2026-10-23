using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddEngineFieldsToStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EngineAccountRef",
                table: "Stores",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EnginePackageTypeId",
                table: "Stores",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EngineAccountRef",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "EnginePackageTypeId",
                table: "Stores");
        }
    }
}
