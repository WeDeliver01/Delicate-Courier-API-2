using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using DelicateCouriers.ApiService.Data;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Adds Stores.BookingTriggerStatuses — an optional per-store override of
/// which WooCommerce order statuses may trigger an automatic shipment
/// booking (comma-separated, lowercase; NULL = platform default of booking
/// on any non-terminal status).
///
/// Seeds the value 'completed' for store 7 (Baked By Nataleen): the client
/// asked that only orders with a 'completed' status be booked — pending,
/// on-hold and processing orders must never trigger a shipment. Their
/// plugin fires a webhook on the transition to completed, so the booking
/// happens at that moment. No other store or tenant is affected.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260725160000_AddBookingTriggerStatuses")]
public partial class AddBookingTriggerStatuses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Stores" ADD COLUMN IF NOT EXISTS "BookingTriggerStatuses" text NULL;
            """);

        migrationBuilder.Sql("""
            UPDATE "Stores" SET "BookingTriggerStatuses" = 'completed' WHERE "StoreID" = 7;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Stores" DROP COLUMN IF EXISTS "BookingTriggerStatuses";
            """);
    }
}
