using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryDateFieldsToOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Occasion",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequestedDeliveryDate",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedDeliveryTime",
                table: "Orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Occasion",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RequestedDeliveryDate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RequestedDeliveryTime",
                table: "Orders");
        }
    }
}
