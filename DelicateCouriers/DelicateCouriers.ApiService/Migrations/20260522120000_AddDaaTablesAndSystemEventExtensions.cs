using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DelicateCouriers.ApiService.Migrations;

/// <summary>
/// Task #16 — DAA Remote Monitoring & Control.
///
/// Adds:
///   * Five DAA tables: DaaMerchants (one row per WP install we monitor),
///     DaaLogEntries (mirrored wp_daa_logs), DaaOrderTraces (per-order
///     decision-trace snapshots), DaaSettingsSnapshots (Panel B history)
///     and DaaRemoteActions (audit of platform-initiated control calls).
///   * Two new columns on SystemEvents: Source (subsystem tag, e.g. "daa")
///     and RequestId (idempotency key for repeatable inbound pushes).
///   * DataProtectionKeys table for the ASP.NET key ring backing the
///     IDataProtector that wraps DAA merchant secrets at rest.
///
/// All DDL is guarded so it's safe to re-run on a partially-provisioned
/// environment, mirroring the style of <c>20260520120000_AddSystemEventsTable</c>.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260522120000_AddDaaTablesAndSystemEventExtensions")]
public partial class AddDaaTablesAndSystemEventExtensions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ----- SystemEvents extensions -----------------------------------------
        migrationBuilder.Sql(@"ALTER TABLE ""SystemEvents"" ADD COLUMN IF NOT EXISTS ""Source"" varchar(50) NULL;");
        migrationBuilder.Sql(@"ALTER TABLE ""SystemEvents"" ADD COLUMN IF NOT EXISTS ""RequestId"" uuid NULL;");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_SystemEvents_Source"" ON ""SystemEvents"" (""Source"");");
        // Partial unique index — RequestId is only unique when present, so we
        // can keep null for in-platform events without conflicting with the
        // de-dup guarantee for inbound DAA pushes.
        migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""UX_SystemEvents_RequestId"" ON ""SystemEvents"" (""RequestId"") WHERE ""RequestId"" IS NOT NULL;");

        // ----- DataProtectionKeys (ASP.NET key ring) ---------------------------
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""DataProtectionKeys"" (
                ""Id"" serial PRIMARY KEY,
                ""FriendlyName"" text NULL,
                ""Xml"" text NULL
            );
        ");

        // ----- DaaMerchants ----------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""DaaMerchants"" (
                ""DaaMerchantID"" serial PRIMARY KEY,
                ""Slug"" varchar(100) NOT NULL,
                ""DisplayName"" varchar(200) NOT NULL,
                ""StoreID"" integer NULL REFERENCES ""Stores""(""StoreID"") ON DELETE SET NULL,
                ""DaaSecretEncrypted"" text NOT NULL,
                ""PlatformApiTokenEncrypted"" text NOT NULL,
                ""WordpressBaseUrl"" varchar(500) NULL,
                ""Status"" varchar(20) NOT NULL DEFAULT 'Pending',
                ""FirstSeenAt"" timestamp with time zone NULL,
                ""LastSeenAt"" timestamp with time zone NULL,
                ""LastSweepAt"" timestamp with time zone NULL,
                ""LastSweepError"" varchar(2000) NULL,
                ""LastLogId"" bigint NOT NULL DEFAULT 0,
                ""LastTraceOrderId"" bigint NOT NULL DEFAULT 0,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""CreatedBy"" varchar(200) NOT NULL DEFAULT '',
                ""ChangedAt"" timestamp with time zone NULL,
                ""ChangedBy"" varchar(200) NULL
            );
        ");
        migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""UX_DaaMerchants_Slug"" ON ""DaaMerchants"" (""Slug"");");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_DaaMerchants_StoreID"" ON ""DaaMerchants"" (""StoreID"");");

        // ----- DaaLogEntries ---------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""DaaLogEntries"" (
                ""DaaLogEntryID"" bigserial PRIMARY KEY,
                ""DaaMerchantID"" integer NOT NULL REFERENCES ""DaaMerchants""(""DaaMerchantID"") ON DELETE CASCADE,
                ""ExternalLogId"" bigint NOT NULL,
                ""OccurredAt"" timestamp with time zone NOT NULL,
                ""Level"" varchar(20) NOT NULL,
                ""Feature"" varchar(100) NOT NULL,
                ""Message"" text NOT NULL,
                ""ContextJson"" text NULL,
                ""OrderId"" bigint NULL,
                ""IngestedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );
        ");
        migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""UX_DaaLogEntries_Merchant_External"" ON ""DaaLogEntries"" (""DaaMerchantID"", ""ExternalLogId"");");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_DaaLogEntries_Merchant_OccurredAt"" ON ""DaaLogEntries"" (""DaaMerchantID"", ""OccurredAt"" DESC);");

        // ----- DaaOrderTraces --------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""DaaOrderTraces"" (
                ""DaaOrderTraceID"" bigserial PRIMARY KEY,
                ""DaaMerchantID"" integer NOT NULL REFERENCES ""DaaMerchants""(""DaaMerchantID"") ON DELETE CASCADE,
                ""OrderId"" bigint NOT NULL,
                ""TraceJson"" text NOT NULL,
                ""IngestedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );
        ");
        migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""UX_DaaOrderTraces_Merchant_Order"" ON ""DaaOrderTraces"" (""DaaMerchantID"", ""OrderId"");");

        // ----- DaaSettingsSnapshots --------------------------------------------
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""DaaSettingsSnapshots"" (
                ""DaaSettingsSnapshotID"" bigserial PRIMARY KEY,
                ""DaaMerchantID"" integer NOT NULL REFERENCES ""DaaMerchants""(""DaaMerchantID"") ON DELETE CASCADE,
                ""CapturedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""Source"" varchar(10) NOT NULL,
                ""SnapshotJson"" text NOT NULL
            );
        ");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_DaaSettingsSnapshots_Merchant_CapturedAt"" ON ""DaaSettingsSnapshots"" (""DaaMerchantID"", ""CapturedAt"" DESC);");

        // ----- DaaRemoteActions ------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS ""DaaRemoteActions"" (
                ""DaaRemoteActionID"" bigserial PRIMARY KEY,
                ""DaaMerchantID"" integer NOT NULL REFERENCES ""DaaMerchants""(""DaaMerchantID"") ON DELETE CASCADE,
                ""OccurredAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""ActorUserID"" integer NULL,
                ""ActorLabel"" varchar(200) NOT NULL DEFAULT '',
                ""Action"" varchar(50) NOT NULL,
                ""RequestJson"" text NULL,
                ""ResponseStatus"" integer NULL,
                ""ResponseBody"" text NULL,
                ""Outcome"" varchar(20) NOT NULL DEFAULT 'success',
                ""ErrorMessage"" text NULL
            );
        ");
        migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_DaaRemoteActions_Merchant_OccurredAt"" ON ""DaaRemoteActions"" (""DaaMerchantID"", ""OccurredAt"" DESC);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaRemoteActions"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaSettingsSnapshots"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaOrderTraces"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaLogEntries"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DaaMerchants"";");
        migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""DataProtectionKeys"";");
        migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""UX_SystemEvents_RequestId"";");
        migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SystemEvents_Source"";");
        migrationBuilder.Sql(@"ALTER TABLE ""SystemEvents"" DROP COLUMN IF EXISTS ""RequestId"";");
        migrationBuilder.Sql(@"ALTER TABLE ""SystemEvents"" DROP COLUMN IF EXISTS ""Source"";");
    }
}
