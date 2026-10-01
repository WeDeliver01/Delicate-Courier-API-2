using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddShopifySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Make WooCommerce credential columns nullable (idempotent — DROP NOT NULL is a no-op if already nullable)
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ALTER COLUMN ""WooConsumerSecret"" DROP NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ALTER COLUMN ""WooConsumerKey"" DROP NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ALTER COLUMN ""WooCommerceURL"" DROP NOT NULL;");

            // Add new columns idempotently — a previous partial run may have already created some of them
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ADD COLUMN IF NOT EXISTS ""Platform"" text NOT NULL DEFAULT 'woocommerce';");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ADD COLUMN IF NOT EXISTS ""ShopifyAccessToken"" text NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ADD COLUMN IF NOT EXISTS ""ShopifyStoreUrl"" text NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" ADD COLUMN IF NOT EXISTS ""ShopifyWebhookSecret"" text NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" DROP COLUMN IF EXISTS ""ShopifyWebhookSecret"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" DROP COLUMN IF EXISTS ""ShopifyStoreUrl"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" DROP COLUMN IF EXISTS ""ShopifyAccessToken"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Stores"" DROP COLUMN IF EXISTS ""Platform"";");

            // Note: re-imposing NOT NULL on the WooCommerce columns is omitted because
            // existing rows may now legitimately hold NULLs (e.g. Shopify-only stores).
        }
    }
}
