---
name: Backup tooling
description: PostgreSQL client version selection in the Replit environment
---
Installing PostgreSQL 17 through system dependencies may leave PostgreSQL 16 first on PATH. Verify the actual pg_dump version and resolve the installed package output explicitly when needed.

**Why:** The existing Supabase database rejected the older client's schema export even after the newer package was installed.

**How to apply:** Check server and client major versions before exports. Do not assume a successful package installation changed command resolution, or preserve a specific Nix store hash as a permanent path.