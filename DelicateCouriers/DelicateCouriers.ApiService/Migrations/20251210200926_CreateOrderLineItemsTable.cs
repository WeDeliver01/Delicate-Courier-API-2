using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class CreateOrderLineItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderLineItems",
                columns: table => new
                {
                    OrderLineItemID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderID = table.Column<int>(type: "integer", nullable: false),
                    WooLineItemID = table.Column<int>(type: "integer", nullable: false),
                    ProductName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProductSKU = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WooProductID = table.Column<int>(type: "integer", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    WeightPerUnit = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    TotalWeight = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VariationDetails = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLineItems", x => x.OrderLineItemID);
                    table.ForeignKey(
                        name: "FK_OrderLineItems_Orders_OrderID",
                        column: x => x.OrderID,
                        principalTable: "Orders",
                        principalColumn: "OrderID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineItems_OrderID",
                table: "OrderLineItems",
                column: "OrderID");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineItems_ProductSKU",
                table: "OrderLineItems",
                column: "ProductSKU");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineItems_WooLineItemID",
                table: "OrderLineItems",
                column: "WooLineItemID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderLineItems");
        }
    }
}
