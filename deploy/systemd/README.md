# delicate-integration.service

The integration service on api01, behind `api.delicatecourier.co.za`.
Mirrors `delicate-api.service`: binary in `/opt/delicate/integration`, secrets
in `/etc/delicate/integration.env` (0600, root), running as `delicate`, bound
to `127.0.0.1:8096` with nginx in front.

## Deploying a new build

```bash
dotnet publish DelicateCouriers/DelicateCouriers.Integration -c Release -o out
tar czf out.tar.gz -C out .
scp out.tar.gz root@45.77.227.115:/tmp/
ssh root@45.77.227.115 'systemctl stop delicate-integration \
  && tar xzf /tmp/out.tar.gz -C /opt/delicate/integration \
  && chown -R delicate:delicate /opt/delicate/integration \
  && systemctl start delicate-integration'
```

## The database

`MERCHANTS_CONNECTION` and `HANGFIRE_CONNECTION` point at
`127.0.0.1:5433`, which is the Engine's production Postgres
(`delicate-prod-postgres-1`) published on loopback only. That binding was added
to the Engine's compose on 2026-10-22 and the container recreated during a
no-traffic window; the data is on the named volume `delicate-prod_pgdata`, so
recreating it does not touch the data. A `pg_dumpall` was taken first.

The service uses its own database, `delicate_integration`, on that instance —
the same server, not the same database. These tables belong to a different
service with a different lifecycle.

## Schema

From the EF migration `20261020185044_AddMerchants`, applied as idempotent SQL
so the server needs no EF tooling:

```bash
dotnet ef migrations script --idempotent   --project DelicateCouriers/DelicateCouriers.Integration   --context MerchantDbContext -o schema.sql
psql -h 127.0.0.1 -p 5433 -U delicate -d delicate_integration -v ON_ERROR_STOP=1 -f schema.sql
```

## Onboarding a merchant

Creating one in the console is not enough on its own. The Engine needs two more
things, and neither can be created from here — both are super_admin-only, and a
service credential can never reach an admin route, by design:

1. **Map their reference to an account**
   `POST /v1/admin/account-external-refs`, with `system` set to
   `integration-service` (the service client's slug — the Engine matches on its
   own slug, never on a value the caller sends) and `externalId` set to the
   merchant's `engineAccountRef`.
2. **Grant that account to this service**
   `PATCH /v1/admin/service-clients/{id}` on the `integration-service` client,
   adding the account to `accountIds`.

The console will tell you which of these is missing. Each merchant's page calls
`GET /api/admin/merchants/{storeId}/engine-readiness`, which makes a real
authorised call to the Engine and reports back, with the fix. Without it a
merchant looks perfectly configured until a shopper reaches checkout and is
offered no delivery option.

## The service credential

`ENGINE_SERVICE_CREDENTIAL` holds a `dsk_<keyId>_<secret>` issued by the Engine
(`POST /v1/admin/service-clients`), with five scopes: `quotes:write`,
`bookings:write`, `bookings:read`, `shipments:read`, `labels:read`. Not
`bookings:cancel` — nothing here cancels a booking, and a standing credential
that can is a different thing to leave in an env file.

If it is ever rejected, every merchant's readiness check reports
`CredentialRejected` rather than blaming the merchant. Rotate with
`POST /v1/admin/service-clients/{id}/rotate` and update the env file.
