using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddPackageTypesAndMappingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackageTypes",
                columns: table => new
                {
                    PackageTypeID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StoreID = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LengthCm = table.Column<int>(type: "integer", nullable: false),
                    WidthCm = table.Column<int>(type: "integer", nullable: false),
                    HeightCm = table.Column<int>(type: "integer", nullable: false),
                    DefaultWeightKg = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    MaxWeightKg = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageTypes", x => x.PackageTypeID);
                    table.ForeignKey(
                        name: "FK_PackageTypes_Stores_StoreID",
                        column: x => x.StoreID,
                        principalTable: "Stores",
                        principalColumn: "StoreID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageMappingRules",
                columns: table => new
                {
                    PackageMappingRuleID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageTypeID = table.Column<int>(type: "integer", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MatchType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageMappingRules", x => x.PackageMappingRuleID);
                    table.ForeignKey(
                        name: "FK_PackageMappingRules_PackageTypes_PackageTypeID",
                        column: x => x.PackageTypeID,
                        principalTable: "PackageTypes",
                        principalColumn: "PackageTypeID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageMappingRules_PackageTypeID",
                table: "PackageMappingRules",
                column: "PackageTypeID");

            migrationBuilder.CreateIndex(
                name: "IX_PackageMappingRules_PackageTypeID_IsActive",
                table: "PackageMappingRules",
                columns: new[] { "PackageTypeID", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PackageTypes_StoreID",
                table: "PackageTypes",
                column: "StoreID");

            migrationBuilder.CreateIndex(
                name: "IX_PackageTypes_StoreID_IsDefault",
                table: "PackageTypes",
                columns: new[] { "StoreID", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackageMappingRules");

            migrationBuilder.DropTable(
                name: "PackageTypes");
        }
    }
}
