using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CollectionLatitude",
                table: "Stores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CollectionLongitude",
                table: "Stores",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectionLatitude",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionLongitude",
                table: "Stores");
        }
    }
}
