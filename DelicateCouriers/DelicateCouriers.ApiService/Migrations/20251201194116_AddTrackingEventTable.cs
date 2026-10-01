using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackingEventTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackingEvents",
                columns: table => new
                {
                    TrackingEventID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShipmentID = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EventDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EventLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingEvents", x => x.TrackingEventID);
                    table.ForeignKey(
                        name: "FK_TrackingEvents_Shipments_ShipmentID",
                        column: x => x.ShipmentID,
                        principalTable: "Shipments",
                        principalColumn: "ShipmentID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackingEvents_EventTimestamp",
                table: "TrackingEvents",
                column: "EventTimestamp");

            migrationBuilder.CreateIndex(
                name: "IX_TrackingEvents_ShipmentID",
                table: "TrackingEvents",
                column: "ShipmentID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackingEvents");
        }
    }
}
