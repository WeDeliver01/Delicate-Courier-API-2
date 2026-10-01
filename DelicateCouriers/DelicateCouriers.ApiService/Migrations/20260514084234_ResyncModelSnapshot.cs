using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations
{
    /// <summary>
    /// Schema-no-op migration whose only purpose is to refresh the EF model
    /// snapshot (AppDbContextModelSnapshot.cs) so it matches the current
    /// post-Supabase-migration model. The actual schema changes (drop
    /// InviteCodes, drop User.PasswordHash, add User.SupabaseUserId, swap
    /// indexes) were already applied by 20260514120000_SupabaseAuthMigration.
    /// </summary>
    public partial class ResyncModelSnapshot : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) { }
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
