using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Adds Shipment.TrackingPushedOn — the persisted idempotency marker used
/// by the platform → WooCommerce tracking pushback. Set the first time the
/// tracking number is successfully pushed as a customer note; both the
/// booking-time push and the Shiplogic-webhook push check this column
/// before posting another note, so the customer never sees duplicates.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260520140000_AddTrackingPushedOnToShipment")]
public partial class AddTrackingPushedOnToShipment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<System.DateTime>(
            name: "TrackingPushedOn",
            table: "Shipments",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TrackingPushedOn",
            table: "Shipments");
    }
}
