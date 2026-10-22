# nginx on api01 (45.77.227.115)

What is actually deployed, kept here so a change can be reviewed before it
reaches the box rather than after. Fetched from the server, not written from
memory.

## Hosts

| Host | Config | Serves |
|---|---|---|
| `api2` / `app2` / `webhooks2` | `delicate.conf` | **Live production**: .NET API on `:8080`, portal on `:5000` |
| `app.delicatecourier.co.za` | `app.delicatecourier.co.za.conf` | Portal (Next.js) on `:5000`, API on `:8080` |
| `api.delicatecourier.co.za` | `api.delicatecourier.co.za.conf` | Integration service on `:8096` |

Each host has its own file. `delicate.conf` serves production, so new hosts are
never added to it — the only change made to it was repointing two certificate
lines, below.

## Certificates

All four are now certbot-managed and renew themselves:

| Cert | Names | Expires |
|---|---|---|
| `api2.delicatecourier.co.za` | api2, app2, webhooks2 | 2027-01-20 |
| `api.delicatecourier.co.za` | api | 2027-01-20 |
| `app.delicatecourier.co.za` | app | 2027-01-20 |

**This was not true before.** api2 was served from a hand-copied pair under
`/etc/nginx/ssl/delicate/`, outside certbot's control and absent from
`certbot certificates`. It expired **2026-12-12** and nothing would have renewed
it; every merchant plugin's TLS verification would have failed and order pushes
would have stopped. `delicate.conf` now points at the certbot live path. The old
files are still in place, unused, as a rollback.

Issue with webroot, which does not interrupt nginx:

```bash
certbot certonly --webroot -w /var/www/html -d <host>
```

Never `--standalone` — it wants port 80 and would take nginx down, and
production with it.

## Deliberate omissions

- **`/api/admin/` is not served on `api.`** Its only caller is the portal, on
  the same machine, over `127.0.0.1:8096`. Publishing it would expose every
  merchant's configuration behind one static admin key. Returns 404, not 403: a
  refusal that confirms the path exists is still an answer.
- **`/hangfire` is not served on `app.`** It is a job dashboard and is still
  reachable on app2 for whoever needs it. A second public door buys nothing.

## Applying a change

```bash
tar czf /root/nginx-backup-$(date +%Y%m%d-%H%M%S).tar.gz /etc/nginx
# edit the file
nginx -t                 # must pass before anything else
nginx -s reload          # reload, never restart: keeps live connections
```

Then confirm production is untouched, not merely that the new thing works:

```bash
for h in api2 app2 webhooks2; do
  curl -s -o /dev/null -w "$h %{http_code} tls=%{ssl_verify_result}\n" \
    "https://$h.delicatecourier.co.za/"
done
```
