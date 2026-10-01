using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreTimeWindowsAndSuburb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CollectionSuburb",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionTimeFrom",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionTimeTo",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryTimeFrom",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryTimeTo",
                table: "Stores",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectionSuburb",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionTimeFrom",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionTimeTo",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "DeliveryTimeFrom",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "DeliveryTimeTo",
                table: "Stores");
        }
    }
}
