using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Adds a dedicated ShippingSuburb column to Orders so the WooCommerce
/// plugin can populate the delivery local_area for Shiplogic. WooCommerce
/// has no native suburb field, so the plugin sources it from billing/
/// shipping address_2 (the conventional ZA placement) and sends it as a
/// new top-level `suburb` field on the order payload.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260519000000_AddShippingSuburbToOrder")]
public partial class AddShippingSuburbToOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShippingSuburb",
            table: "Orders",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShippingSuburb",
            table: "Orders");
    }
}
