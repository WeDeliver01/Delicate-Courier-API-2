using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreCollectionConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CollectionAddressLine1",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionAddressLine2",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionCity",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionCompanyName",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionContactEmail",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionContactName",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionContactPhone",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionCountry",
                table: "Stores",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CollectionPostalCode",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionProvince",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultServiceLevel",
                table: "Stores",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectionAddressLine1",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionAddressLine2",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionCity",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionCompanyName",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionContactEmail",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionContactName",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionContactPhone",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionCountry",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionPostalCode",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CollectionProvince",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "DefaultServiceLevel",
                table: "Stores");
        }
    }
}
