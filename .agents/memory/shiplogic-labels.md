---
name: Shiplogic label download
description: Correct Shiplogic v2 label endpoint and presigned-URL gotcha
---
The rule: Shiplogic's label API is `GET /v2/shipments/label?id={shipment_id}` and returns JSON `{"url": "...", "filename": ..., "file_size": ...}` — a short-lived (7-day) presigned S3 URL. Download that URL with NO Authorization header (presigned URLs reject extra auth).

**Why:** Path-style routes like `/v2/shipments/{id}/label` or `/shipments/{id}/label` return 404 ("Unhandled resource path"), and legacy code assumed the endpoint returned raw PDF bytes — it never worked, which is why the Labels table stayed empty for years.

**How to apply:** Any label fetch from Shiplogic: call the query-param endpoint, parse `url`, download with a bare client, then persist. Note the Labels.BlobURL column stores the PDF as a base64 data URL, so it must be unbounded `text` (widened by migration WidenLabelBlobUrl), not varchar(1000).
