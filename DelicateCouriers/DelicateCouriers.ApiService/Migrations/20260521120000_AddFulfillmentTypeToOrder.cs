using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Persists the WooCommerce plugin's `fulfillment_type` field on the
/// Orders table so the shipment-orchestration pipeline can refuse to
/// book a Shiplogic shipment for any order the customer chose to
/// collect in person.
///
/// The check used to live only at the webhook-intake gate
/// (PluginWebhookService.ShouldAutoCreateShipment), but a collect
/// order recently slipped through and got booked anyway — most likely
/// via a second trigger path (manual admin button, retry queue,
/// status-transition hook). Persisting the field lets us add a
/// defensive guard at the top of CreateShipmentForOrderAsync that
/// catches collect orders regardless of which code path queued them.
///
/// Column is nullable so older orders (and orders pushed by older
/// plugin versions that don't send the field) keep working unchanged
/// — null is treated as "delivery" by the guard.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260521120000_AddFulfillmentTypeToOrder")]
public partial class AddFulfillmentTypeToOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FulfillmentType",
            table: "Orders",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FulfillmentType",
            table: "Orders");
    }
}
