# The portal on api01

`delicate-web.service` — Next.js standalone on `127.0.0.1:5000`, serving **two**
hosts through nginx: `app.delicatecourier.co.za` and `app2.delicatecourier.co.za`.

One process, two names. A deploy here changes what production sees on app2, not
only the new host. There is no separate staging copy.

## Where the source lives

`/opt/delicate/src` is a checkout of `WeDeliver01/Delicate-Courier-API` — the
original repo, not this fork, which shares no history with it. Changes from here
are copied in as files rather than pulled.

Before replacing any file that already exists there, check it still matches this
fork's baseline, or production work gets clobbered:

```bash
git show bdd7cda:delicate-couriers-frontend/<path> | tr -d '\r' | md5sum
ssh root@45.77.227.115 "tr -d '\r' < /opt/delicate/src/delicate-couriers-frontend/<path> | md5sum"
```

## Building

The build runs **on the box**, because `NEXT_PUBLIC_*` values are inlined at
build time and live in `/opt/delicate/src/delicate-couriers-frontend/.env.local`.
Building anywhere else produces a bundle with no Supabase configuration, and
sign-in then fails silently — the page renders, the form submits, nothing
happens.

```bash
cd /opt/delicate/src/delicate-couriers-frontend
npm run build
cp -r .next/static .next/standalone/.next/static     # required
cp -r public .next/standalone/public                 # required
chown -R delicate:delicate .next
systemctl restart delicate-web
```

Those two `cp` lines are not optional. `next build` does not place static assets
inside the standalone output, and a fresh build deletes the copies the last
deploy made. Restarting without them serves the portal with no CSS or
JavaScript.

Skip `npm ci` unless dependencies actually changed — it is dependency churn on a
production host for no reason.

## Verifying

Check production, not just the new host, and check that sign-in survived:

```bash
for h in app app2; do
  curl -s -o /dev/null -w "$h %{http_code}\n" "https://$h.delicatecourier.co.za/login"
done
ssh root@45.77.227.115 \
  "grep -rl 'supabase\.co' /opt/delicate/src/delicate-couriers-frontend/.next/standalone/.next/static | wc -l"
```

That last count must be at least 1. Zero means the build lost its Supabase
configuration and nobody can sign in.

## Runtime configuration

`/etc/delicate/web.env` (0600, root), loaded by the unit:

- `INTEGRATION_API_URL=http://127.0.0.1:8096` — loopback, not the public
  hostname. nginx returns 404 for `/api/admin/` on `api.delicatecourier.co.za`,
  and the admin API has no business crossing the internet to reach a service on
  the same machine.
- `INTEGRATION_ADMIN_KEY` — the same key as the integration service's
  `ADMIN_API_KEY`. It stays server-side: the browser calls
  `/integration-api/...` on this app, which checks the operator's Supabase
  session before forwarding. A key in browser JavaScript is a key in devtools.

Secrets are in that file rather than `Environment=` lines because unit files are
world-readable.

## Rolling back

```bash
systemctl stop delicate-web
tar xzf /root/web-build-backup-<ts>.tar.gz -C /opt/delicate/src/delicate-couriers-frontend
systemctl start delicate-web
```
