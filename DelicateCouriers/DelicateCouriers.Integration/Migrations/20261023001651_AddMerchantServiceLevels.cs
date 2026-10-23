using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.Integration.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantServiceLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add first, carry the data across, drop last. Generated the other way round,
            // which would have discarded every merchant's existing service level — there are
            // none today, so it would have been silent, and wrong the first time it mattered.
            migrationBuilder.AddColumn<string>(
                name: "ServiceLevels",
                table: "merchants",
                type: "jsonb",
                nullable: true);

            // Each merchant's single level becomes a one-item list, unchanged in meaning.
            migrationBuilder.Sql("""
                UPDATE merchants
                SET "ServiceLevels" = jsonb_build_array(
                    jsonb_build_object(
                        'CheckoutCode',          "CheckoutServiceLevelCode",
                        'CheckoutLabel',         "CheckoutRateLabel",
                        'EngineCode',            "EngineServiceLevelCode",
                        'EstimatedDeliveryDays', "EstimatedDeliveryDays"
                    )
                )
                WHERE "ServiceLevels" IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "CheckoutRateLabel",
                table: "merchants");

            migrationBuilder.DropColumn(
                name: "CheckoutServiceLevelCode",
                table: "merchants");

            migrationBuilder.DropColumn(
                name: "EngineServiceLevelCode",
                table: "merchants");

            migrationBuilder.DropColumn(
                name: "EstimatedDeliveryDays",
                table: "merchants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckoutRateLabel",
                table: "merchants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CheckoutServiceLevelCode",
                table: "merchants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EngineServiceLevelCode",
                table: "merchants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EstimatedDeliveryDays",
                table: "merchants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Going back keeps the first level and loses the rest, because one column cannot
            // hold several. Stated here so that is a known cost of rolling back rather than a
            // surprise discovered afterwards.
            migrationBuilder.Sql("""
                UPDATE merchants
                SET "CheckoutServiceLevelCode" = COALESCE("ServiceLevels"->0->>'CheckoutCode', ''),
                    "CheckoutRateLabel"        = COALESCE("ServiceLevels"->0->>'CheckoutLabel', ''),
                    "EngineServiceLevelCode"   = COALESCE("ServiceLevels"->0->>'EngineCode', ''),
                    "EstimatedDeliveryDays"    = COALESCE(("ServiceLevels"->0->>'EstimatedDeliveryDays')::int, 1)
                WHERE "ServiceLevels" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "ServiceLevels",
                table: "merchants");
        }
    }
}
