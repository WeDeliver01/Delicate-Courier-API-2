---
name: Shared Hangfire queue between dev and prod
description: Dev and prod workers race for the same jobs; how to test new job code
---

- Dev and production share the same Supabase Postgres AND the same Hangfire schema. Enqueued jobs are grabbed by EITHER worker — in practice prod usually wins, so a job testing NEW dev-only code often executes prod's OLD code instead.
- **Why:** observed live — new orchestration guard never ran via webhook-enqueued jobs because the deployed worker processed them first.
- **How to apply:** to test new background-job code in dev, bypass Hangfire (call the synchronous API endpoint, e.g. POST /api/shipments/create/{orderRef}) or publish first. Also: behavior changes to job code do not take effect for real traffic until the backend is published.
- Related: EF Npgsql uses NpgsqlRetryingExecutionStrategy — user-initiated transactions (BeginTransactionAsync, e.g. for pg_advisory_xact_lock) must be wrapped in Database.CreateExecutionStrategy().ExecuteAsync(...) or they throw InvalidOperationException at runtime.
