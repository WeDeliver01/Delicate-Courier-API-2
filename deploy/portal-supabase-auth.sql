-- The step `dotnet ef migrations script` leaves out.
--
-- 20260514120000_SupabaseAuthMigration has no .Designer.cs, so EF does not treat
-- it as part of the migration chain and omits it from generated scripts — while
-- the model snapshot expects its result. That disagreement is why production's
-- User table still has PasswordHash NOT NULL and a hand-added nullable
-- SupabaseUserId, and why POST /api/admin/users cannot create anyone there.
--
-- This applies it by hand to a fresh database, where every step is a no-op or
-- trivially safe: the table is empty, so the TRUNCATE in the original has nothing
-- to do and is left out entirely rather than copied across. On a database with
-- users in it this script would still be safe; the original migration would not.

BEGIN;

DROP TABLE IF EXISTS "InviteCodes" CASCADE;

-- Replaced by a global unique index on Email: Supabase Auth keys on the address,
-- so one address must mean one profile across every tenant.
DROP INDEX IF EXISTS "IX_User_TenantID_Email";

ALTER TABLE "User" DROP COLUMN IF EXISTS "PasswordHash";

-- The bridge to auth.users.id. NOT NULL is safe on an empty table; the default
-- exists only so the column can be added, and nothing should ever carry it.
ALTER TABLE "User"
    ADD COLUMN IF NOT EXISTS "SupabaseUserId" uuid NOT NULL
    DEFAULT '00000000-0000-0000-0000-000000000000';

CREATE UNIQUE INDEX IF NOT EXISTS "IX_User_SupabaseUserId" ON "User" ("SupabaseUserId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_User_Email" ON "User" ("Email");

-- Recorded so the application does not try to apply it again, and so the next
-- person to read the table can see it was applied rather than guessing.
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260514120000_SupabaseAuthMigration', '8.0.0')
ON CONFLICT ("MigrationId") DO NOTHING;

COMMIT;
