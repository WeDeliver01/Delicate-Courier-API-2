using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionDateTimeToOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RequestedCollectionDate",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedCollectionTime",
                table: "Orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedCollectionDate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RequestedCollectionTime",
                table: "Orders");
        }
    }
}
