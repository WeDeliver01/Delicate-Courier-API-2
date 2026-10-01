using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Persists the "Special Trip Request" quote produced by the WooCommerce
/// plugin (v2.7.0+) when no courier rate is available: the amount quoted
/// to the customer (driving distance × merchant R/km), the distance it was
/// computed from, and the customer coordinates geocoded at checkout.
///
/// The quoted amount becomes the declared value on the Shiplogic SPX
/// booking; the coordinates are attached to the delivery address so
/// Shiplogic doesn't have to geocode a possibly-vague ZA address itself.
///
/// All columns are nullable — orders from older plugin versions, Shopify
/// stores, and non-special-trip orders simply keep null.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260726120000_AddSpecialTripToOrder")]
public partial class AddSpecialTripToOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "SpecialTripQuotedAmount",
            table: "Orders",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "SpecialTripDistanceKm",
            table: "Orders",
            type: "numeric(10,1)",
            precision: 10,
            scale: 1,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "SpecialTripCustomerLat",
            table: "Orders",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "SpecialTripCustomerLng",
            table: "Orders",
            type: "double precision",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SpecialTripQuotedAmount",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "SpecialTripDistanceKm",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "SpecialTripCustomerLat",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "SpecialTripCustomerLng",
            table: "Orders");
    }
}
