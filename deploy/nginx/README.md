# nginx on api01 (45.77.227.115)

What is actually deployed, kept here so a change can be reviewed before it
reaches the box rather than after.

## Hosts

| Host | Config | Serves |
|---|---|---|
| `api.delicatecourier.co.za` | `api.delicatecourier.co.za.conf` (this repo) | Integration service, `127.0.0.1:8096` |
| `api2` / `app2` / `webhooks2` | `delicate.conf` (**not** in this repo) | Live production: .NET API on `:8080`, portal on `:5000` |

`delicate.conf` is deliberately absent. It serves production and is not edited
as part of this migration; the new host got its own file so that it cannot be.

## Two things worth knowing

**api2's certificate does not renew itself.** It is a manual copy at
`/etc/nginx/ssl/delicate/`, outside certbot's control — `certbot certificates`
does not list it. It expires **2026-12-12**. When it lapses every merchant
plugin's TLS verification fails and order pushes stop. The new host avoids this
by pointing `ssl_certificate` straight at the certbot live path.

**`webhooks2.delicatecourier.co.za` has no DNS record** but is listed in
`delicate.conf`'s `server_name`. It has never resolved; nothing appears to use
it. Pre-existing, noted so it is not mistaken for breakage.

## Applying a change

Never edit in place on the box without a backup and a test:

```bash
tar czf /root/nginx-backup-$(date +%Y%m%d-%H%M%S).tar.gz /etc/nginx
# edit /etc/nginx/sites-available/api.delicatecourier.co.za
nginx -t                 # must pass before anything else
nginx -s reload          # reload, never restart: keeps live connections
```

Then confirm production is untouched, not just that the new host works:

```bash
curl -s -o /dev/null -w "api2 %{http_code} tls=%{ssl_verify_result}\n" \
  https://api2.delicatecourier.co.za/api/webhooks/plugin/health
```

## Certificate

Issued by certbot in webroot mode, which does not interrupt nginx:

```bash
certbot certonly --webroot -w /var/www/html -d api.delicatecourier.co.za
```

Never `--standalone` — it wants port 80 and would take nginx down, and with it
production.
