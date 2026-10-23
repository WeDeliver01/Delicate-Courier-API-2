# What runs on api01 (45.77.227.115)

Four systems share this box. They are not variants of one another, and treating
them as if they were is how `app` briefly ended up serving production.

| Host | What it is | Processes | Database |
|---|---|---|---|
| `delicatecourier.co.za` | The **Engine** | containers on `:8094` / `:8095` | `delicate` (its own Postgres container) |
| `app2` / `api2` / `webhooks2` | **Live production**, the original platform | `delicate-web` `:5000`, `delicate-api` `:8080` | Supabase Postgres |
| `app` | **The new portal** — this fork | `delicate-portal-web` `:5100`, `delicate-portal-api` `:8081` | `delicate_portal` |
| `api` | The **integration service** | `delicate-integration` `:8096` | `delicate_integration` |

## app and app2 must never share anything

`app2` is live, serving real merchants. It runs from `/opt/delicate/src`, a
checkout of the **original** repository. This fork deploys to
`/opt/delicate/portal-api` and `/opt/delicate/portal-web` and must never be
built into, or copied over, that checkout.

They share one thing deliberately: the Supabase project. Identities are common
because the account is on a tier limit, and that is safe because each portal
gates entry on **its own** `User` table — `AuthController` looks the caller up
by `SupabaseUserId` in its own database and refuses anyone without a row:

> No local profile exists for this Supabase user.

So a production user can sign in at `app` and gets no further. Nothing of
app2's data is reachable from this process.

One wrinkle: `app_metadata.tenant_id` is global per Supabase user, and tenant 1
means different things in the two databases. The API reads the tenant from the
local row so it is authoritative; the frontend menu reads the claim. A user
deliberately present in both portals could therefore see a slightly wrong menu
while every API call behaves correctly.

## Deploying the new portal

The API is an ordinary publish:

```bash
dotnet publish DelicateCouriers/DelicateCouriers.ApiService -c Release -o out
# upload, unpack into /opt/delicate/portal-api, chown delicate:delicate
systemctl restart delicate-portal-api
```

The web **must be built on the box**. `NEXT_PUBLIC_*` values are inlined at
build time and live in `/opt/delicate/portal-web/.env.local`; a bundle built
anywhere else has no Supabase configuration and sign-in then fails silently —
the page renders, the form submits, nothing happens.

```bash
cd /opt/delicate/portal-web
npm ci && npm run build
cp -r .next/static .next/standalone/.next/static     # required
cp -r public .next/standalone/public                 # required
cp .env.local .next/standalone/.env.local            # required
chown -R delicate:delicate /opt/delicate/portal-web
systemctl restart delicate-portal-web
```

Those three `cp` lines are not optional. `next build` does not put static
assets in the standalone output, and a fresh build deletes what the last deploy
copied. Restarting without them serves the portal with no CSS or JavaScript.

## Verifying

Check production, not only the thing you changed:

```bash
for h in app app2 api2 webhooks2; do
  curl -s -o /dev/null -w "$h %{http_code}\n" "https://$h.delicatecourier.co.za/"
done
curl -s -o /dev/null -w "apex %{http_code}\n" https://delicatecourier.co.za/
```

And that the two portals are genuinely different builds — their first CSS chunk
hashes should differ:

```bash
for h in app app2; do
  curl -s "https://$h.delicatecourier.co.za/login" | grep -oE '/_next/static/chunks/[^"]+\.css' | head -1
done
```

## Secrets

All `0600`, root-owned, outside the repository:

- `/etc/delicate/api.env` — app2's API (**do not edit**)
- `/etc/delicate/portal-api.env` — the new portal's API
- `/etc/delicate/integration.env` — the integration service
- `/opt/delicate/portal-web/.env.local` — the new portal's build and runtime

The Engine credential lives only in `integration.env`. The portal reaches the
Engine through the integration service rather than holding one of its own, so
there is one key to rotate and one grant list to audit.
