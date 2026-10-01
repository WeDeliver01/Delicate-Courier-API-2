---
name: WeTransfer downloads
description: How to download user-shared WeTransfer links from the shell
---
Resolve the `we.tl/t-...` short link with `curl -L` to get `wetransfer.com/downloads/<transfer_id>/<security_hash>`.
Then `POST https://wetransfer.com/api/v4/transfers/<transfer_id>/download` with JSON `{"security_hash":"<hash>","intent":"entire_transfer"}` (cookies from the page fetch; empty x-csrf-token was accepted).
Response contains `direct_link` — a signed URL valid ~10 min; download it immediately.
**Why:** users prefer sending large plugin ZIPs via WeTransfer; workspace App Storage pane uploads were not reachable.
