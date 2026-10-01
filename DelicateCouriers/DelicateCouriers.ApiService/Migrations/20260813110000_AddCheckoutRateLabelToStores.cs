using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Optional per-store checkout rate label (e.g. "Standard baked goods
/// delivery") shown to customers instead of the Shiplogic service-level
/// name / "Special Trip Request".
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260813110000_AddCheckoutRateLabelToStores")]
public partial class AddCheckoutRateLabelToStores : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CheckoutRateLabel",
            table: "Stores",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CheckoutRateLabel", table: "Stores");
    }
}
