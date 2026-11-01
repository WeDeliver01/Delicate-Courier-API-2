using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <summary>
    /// A store offers a list of delivery options, not one.
    ///
    /// Add, backfill, then drop — in that order, so no store is without a service level for
    /// the duration of the migration. The scaffolded version dropped first and backfilled
    /// never, which would have left every existing store with nothing to offer at checkout.
    /// </summary>
    public partial class AddStoreServiceLevels : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ServiceLevels",
                table: "Stores",
                type: "jsonb",
                nullable: true);

            // Every existing store offered exactly one option, so each gets a list of one and
            // behaves tomorrow as it did today.
            //
            // EngineCode is 'standard' rather than the old value. DefaultServiceLevel holds a
            // checkout code — "STD" — and this portal was passing it to the Engine as if it
            // were an Engine service level, which the Engine answers with
            // "service level not found". So the old value was never a working Engine code and
            // copying it across would carry the fault forward. Every store on "STD" means the
            // standard service; a store that later wants express says so in the list.
            migrationBuilder.Sql(@"
                UPDATE ""Stores"" SET ""ServiceLevels"" = jsonb_build_array(
                    jsonb_build_object(
                        'CheckoutCode', COALESCE(NULLIF(""DefaultServiceLevel"", ''), 'STD'),
                        'CheckoutLabel', COALESCE(NULLIF(""CheckoutRateLabel"", ''), 'Delivery'),
                        'EngineCode', 'standard',
                        'EstimatedDeliveryDays', 1
                    )
                )
                WHERE ""ServiceLevels"" IS NULL;
            ");

            // Left over from the courier this platform no longer books. The properties went
            // when ShipLogic did; the columns outlived them because no migration followed.
            migrationBuilder.DropColumn(name: "ShiplogicAccountId", table: "Stores");
            migrationBuilder.DropColumn(name: "ShiplogicProviderId", table: "Stores");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShiplogicAccountId", table: "Stores",
                type: "integer", nullable: false, defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShiplogicProviderId", table: "Stores",
                type: "integer", nullable: false, defaultValue: 0);

            migrationBuilder.DropColumn(name: "ServiceLevels", table: "Stores");
        }
    }
}
