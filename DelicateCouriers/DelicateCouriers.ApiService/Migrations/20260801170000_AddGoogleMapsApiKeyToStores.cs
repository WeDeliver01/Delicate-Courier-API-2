using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Per-store Google Maps API key used for special-trip driving-distance
/// lookups, so each merchant's Distance Matrix usage bills to their own
/// Google account rather than the platform's key.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260801170000_AddGoogleMapsApiKeyToStores")]
public partial class AddGoogleMapsApiKeyToStores : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "GoogleMapsApiKey",
            table: "Stores",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "GoogleMapsApiKey", table: "Stores");
    }
}
