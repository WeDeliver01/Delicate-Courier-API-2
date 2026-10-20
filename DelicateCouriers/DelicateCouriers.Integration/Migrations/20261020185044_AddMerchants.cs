using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.Integration.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "merchant_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ActorLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "jsonb", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merchant_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                columns: table => new
                {
                    StoreId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EngineAccountRef = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EngineAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CheckoutServiceLevelCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CheckoutRateLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EngineServiceLevelCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DefaultPackageTypeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EstimatedDeliveryDays = table.Column<int>(type: "integer", nullable: false),
                    WebhookSecret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BookingTriggerStatuses = table.Column<List<string>>(type: "text[]", nullable: false),
                    WooCommerceUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    WooConsumerKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WooConsumerSecret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Collection = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merchants", x => x.StoreId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_merchant_changes_StoreId_OccurredAt",
                table: "merchant_changes",
                columns: new[] { "StoreId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_merchants_EngineAccountId",
                table: "merchants",
                column: "EngineAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "merchant_changes");

            migrationBuilder.DropTable(
                name: "merchants");
        }
    }
}
