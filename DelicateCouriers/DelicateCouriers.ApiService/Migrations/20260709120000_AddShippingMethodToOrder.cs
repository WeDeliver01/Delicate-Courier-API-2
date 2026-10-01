using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Persists the WooCommerce shipping method id(s) and title(s) the
/// customer chose at checkout, verified via the store's Woo REST API.
///
/// Why: some stores (Baked By Nataleen, store #7) sell paid collection
/// points built as `flat_rate` shipping methods whose only collection
/// signal is the merchant-written label ("Collect from 1 Clifford
/// road…"). The plugin's heuristic only inspects the method ID, so
/// those orders arrive tagged `delivery` and were wrongly booked as
/// Shiplogic shipments. The platform now verifies the shipping lines
/// itself on intake and persists them here so the pre-booking guard
/// can re-check without another REST round-trip, and for audit.
///
/// Columns are nullable — orders from Shopify stores, stores without
/// Woo REST credentials, and pre-existing rows simply keep null.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260709120000_AddShippingMethodToOrder")]
public partial class AddShippingMethodToOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShippingMethodId",
            table: "Orders",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ShippingMethodTitle",
            table: "Orders",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShippingMethodId",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ShippingMethodTitle",
            table: "Orders");
    }
}
