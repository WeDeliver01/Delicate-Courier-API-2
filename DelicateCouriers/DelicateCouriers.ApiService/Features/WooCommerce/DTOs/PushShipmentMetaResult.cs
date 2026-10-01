namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Diagnostic result of pushing <c>_dcp_*</c> shipment meta to a
    /// WooCommerce store. Surfaced by the manual Resync endpoint so the
    /// operator can see (a) which path actually wrote — the plugin's signed
    /// route or the legacy WC REST API PUT — and (b) the real number of
    /// meta keys WP confirms it saved (vs the number we sent — those two
    /// can differ if WC silently drops protected underscore-prefixed meta).
    /// </summary>
    public sealed class PushShipmentMetaResult
    {
        /// <summary>True iff the underlying HTTP call returned 2xx.</summary>
        public bool Success { get; init; }

        /// <summary>"plugin" | "wc-rest" | "none" (nothing was attempted).</summary>
        public string Path { get; init; } = "none";

        /// <summary>How many keys we sent in the request.</summary>
        public int KeysSent { get; init; }

        /// <summary>
        /// How many keys WP confirms it saved. Only populated when
        /// <see cref="Path"/> == "plugin" (the plugin's response body
        /// includes <c>updated</c>). Null otherwise — WC REST doesn't
        /// tell us per-key whether the meta actually landed.
        /// </summary>
        public int? KeysUpdated { get; init; }

        /// <summary>HTTP status of the final attempt (0 on transport failure).</summary>
        public int HttpStatus { get; init; }

        /// <summary>Trimmed response body (max ~500 chars) for diagnostics.</summary>
        public string? ResponseBody { get; init; }

        /// <summary>
        /// Keys the plugin reports it actually wrote (plugin route only).
        /// Lets the caller see "we sent _dcp_tracking_number; the plugin
        /// confirms it landed".
        /// </summary>
        public IReadOnlyList<string>? AcceptedKeys { get; init; }

        /// <summary>
        /// Keys the plugin rejected because they fell outside the
        /// <c>_dcp_*</c> whitelist (plugin route only). If non-empty, the
        /// platform is sending a key name the plugin doesn't recognise.
        /// </summary>
        public IReadOnlyList<string>? SkippedKeys { get; init; }

        /// <summary>
        /// Keys the plugin refused to clobber with an empty value because
        /// the order already had a non-empty value stored (plugin route
        /// only, v2.4.1+). If e.g. <c>_dcp_tracking_number</c> shows up
        /// here, the platform attempted to re-sync a record whose tracking
        /// number wasn't populated yet — and the plugin protected the
        /// existing one.
        /// </summary>
        public IReadOnlyList<string>? PreservedKeys { get; init; }

        /// <summary>
        /// True iff the plugin confirms it added an order note this call
        /// (plugin route only, v2.4.1+).
        /// </summary>
        public bool? NoteAdded { get; init; }

        /// <summary>
        /// Human-readable short summary, suitable for surfacing in a UI
        /// toast. Built by the service so callers don't have to format.
        /// </summary>
        public string Summary { get; init; } = string.Empty;
    }
}
