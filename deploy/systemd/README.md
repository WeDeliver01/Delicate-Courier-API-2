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

## Still outstanding

`ENGINE_SERVICE_CREDENTIAL` is empty. It is not copied from a config file — the
Engine issues it and keeps only a hash:

```
POST /v1/admin/service-clients     (Engine, super_admin)
```

The response shows `dsk_<keyId>_<secret>` once. Put it in
`/etc/delicate/integration.env` and restart. Until then health, rates and
signature checks work, and anything that books through the Engine fails auth.
