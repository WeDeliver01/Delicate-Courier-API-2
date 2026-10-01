---
name: Replit deployment container lacks tzdata
description: TimeZoneInfo.FindSystemTimeZoneById works in dev but crashes in the production deployment container
---

The Replit production deployment container (GCE) does not ship IANA tzdata, while the dev workspace does (via `TZDIR=/etc/zoneinfo`). In .NET, `TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg")` therefore succeeds in dev/tests but throws a file-not-found (`Interop.ThrowExceptionForIoErrno`) in production — and if it sits in a static initializer, the `TypeInitializationException` poisons every code path touching the class (this silently killed all order-webhook ingestion for ~5h; endpoints returned normally to callers while orders were dropped, so the only symptom was "orders stopped coming in").

**Why:** dev/prod parity gap — passing tests prove nothing about tzdata availability in the deploy image.

**How to apply:**
- Never call `FindSystemTimeZoneById` without a fallback, and never let it run bare in a static initializer.
- For South Africa a fixed-offset fallback is exact: `TimeZoneInfo.CreateCustomTimeZone("SAST", TimeSpan.FromHours(2), …)` — SAST is UTC+2 year-round, no DST.
- Regression check: run the affected tests with `TZDIR=/nonexistent` to simulate the production container.
- Log-hunting tip: the deployment logs bury single stack-trace lines among info noise; search for `ThrowExceptionForIoErrno` / `fail:` with context, and correlate with the last-saved row timestamp in the DB.
