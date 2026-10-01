---
name: Woo plugin order sync semantics
description: How the Woo plugins send order webhooks and the one-time-sync pitfall
---

- The Woo plugins (Default, HoneyBee, Nateleens variants of `dcp_sync_order`) send order webhooks on hooks: checkout_order_processed, payment_complete, and status_processing/completed/on-hold. Historically they returned early when `_dcp_platform_order_id` meta existed → **status changes after first sync never reached the platform**, so status-triggered bookings (BookingTriggerStatuses) silently never fired. Fixed Aug 2026: already-synced orders re-send (`$is_resync`), with order-note spam suppressed on resync failures.
- **How to apply:** the fix only takes effect on a live store once the updated plugin ZIP is installed there; the platform side (PluginWebhookService.HandleExistingOrderAsync) already updates existing orders and re-enqueues booking idempotently. Any change to `dcp_sync_order` must be replicated across all three plugin copies.
- Because live stores can't be forced to update plugins, the platform runs a Hangfire recurring sweep (`woo-status-reconciliation`, WooStatusReconciliationService) that polls Woo REST for unshipped orders on trigger-status stores and books when the trigger status is reached. It has an epoch floor (2026-08-14) so historical unshipped orders are never backfilled — keep that floor when touching the sweep.
- Merchant venue shipping options (e.g. "Shipping - Hatfield Urban Soccer ...") can arrive with no postal code; booking correctly refuses until the address is completed — the venue address is usually derivable from the shipping method title.
