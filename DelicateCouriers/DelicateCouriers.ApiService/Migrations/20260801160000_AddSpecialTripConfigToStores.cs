using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Per-store configuration for the Shopify "Special Trip" fallback rate:
/// when Shiplogic quotes no rate for a delivery address, the carrier
/// callback offers a distance-based rate instead
/// (max(MinFee, one-way driving km × CostPerKm), capped at MaxKm).
///
/// All columns nullable — null CostPerKm means the fallback is disabled
/// for the store, which is the default for every existing store.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260801160000_AddSpecialTripConfigToStores")]
public partial class AddSpecialTripConfigToStores : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "SpecialTripCostPerKm",
            table: "Stores",
            type: "numeric(10,2)",
            precision: 10,
            scale: 2,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "SpecialTripMinFee",
            table: "Stores",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "SpecialTripMaxKm",
            table: "Stores",
            type: "numeric(10,1)",
            precision: 10,
            scale: 1,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SpecialTripCostPerKm", table: "Stores");
        migrationBuilder.DropColumn(name: "SpecialTripMinFee", table: "Stores");
        migrationBuilder.DropColumn(name: "SpecialTripMaxKm", table: "Stores");
    }
}
