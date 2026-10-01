using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Cuts the platform over to Supabase Auth.
///
/// 1. Drops the InviteCodes table entirely (admins now provision users
///    directly via the Supabase Admin API — no self-service registration).
/// 2. Drops the per-tenant unique index on (TenantID, Email) and replaces
///    it with a global unique index on Email so Supabase Auth (which keys
///    on email) and our local profile rows stay 1:1.
/// 3. Wipes existing Users (the user explicitly chose force-re-registration).
/// 4. Drops PasswordHash (Supabase owns credentials now).
/// 5. Adds SupabaseUserId (uuid) — bridge between auth.users.id and our row.
/// </summary>
public partial class SupabaseAuthMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1) Drop InviteCodes table (and its FKs to Users) so step 3 can wipe Users.
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""InviteCodes"" CASCADE;");

        // 2) Drop the old (TenantID, Email) unique index — replaced below by a
        //    global unique index on Email. Name comes from EF's standard convention.
        migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_User_TenantID_Email"";");

        // 3) Wipe existing users. CASCADE so any FK references go too.
        migrationBuilder.Sql(@"TRUNCATE TABLE ""User"" RESTART IDENTITY CASCADE;");

        // 4) Drop PasswordHash — Supabase owns credentials now.
        migrationBuilder.DropColumn(name: "PasswordHash", table: "User");

        // 5) Add SupabaseUserId. NOT NULL is safe because step 3 emptied the table.
        migrationBuilder.AddColumn<Guid>(
            name: "SupabaseUserId",
            table: "User",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.CreateIndex(
            name: "IX_User_SupabaseUserId",
            table: "User",
            column: "SupabaseUserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_User_Email",
            table: "User",
            column: "Email",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_User_Email", table: "User");
        migrationBuilder.DropIndex(name: "IX_User_SupabaseUserId", table: "User");
        migrationBuilder.DropColumn(name: "SupabaseUserId", table: "User");

        migrationBuilder.AddColumn<string>(
            name: "PasswordHash",
            table: "User",
            type: "character varying(500)",
            maxLength: 500,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_User_TenantID_Email",
            table: "User",
            columns: new[] { "TenantID", "Email" },
            unique: true);

        // We do not recreate the InviteCodes table on a Down migration — if you
        // need to roll back, restore from a backup taken before this migration.
    }
}
