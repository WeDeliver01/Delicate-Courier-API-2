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

`MERCHANTS_CONNECTION` and `HANGFIRE_CONNECTION` point at the Engine's
production Postgres, the container `delicate-prod-postgres-1`.

That container publishes no port. Adding one means recreating it, and
`delicatecourier.co.za` is served by the same stack — so that is a scheduled
change, not a casual one. Until then the host can reach it directly on the
docker network, but **a container address changes every time the Engine is
redeployed**, and the service will fail to start afterwards until the address
is corrected.

The durable fix, in preference order:

1. Add `ports: ["127.0.0.1:5433:5432"]` to the Engine's compose and recreate
   only the postgres service, during a window. Stable forever.
2. Run a forwarder container on the Engine's network publishing
   `127.0.0.1:5433`. Resolves postgres by container name, so it survives
   redeploys, and recreates nothing.

## Schema

The `merchants` and `merchant_changes` tables come from the EF migration
`20261020185044_AddMerchants`. It has not been applied yet — the service will
start and fail its first database call until it is.

```bash
dotnet ef database update --project DelicateCouriers/DelicateCouriers.Integration
```
