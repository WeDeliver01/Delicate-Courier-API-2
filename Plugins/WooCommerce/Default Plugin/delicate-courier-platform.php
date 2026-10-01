<?php
/**
 * Plugin Name: Delicate Courier Platform
 * Plugin URI:  https://delicatecourier.co.za
 * Description: WooCommerce integration for the Delicate Courier Platform. Provides live courier rates at checkout (server-side geocoded via the platform) and signed order push to the platform for automated shipment creation.
 * Version:     2.9.1
 * Author:      Delicate Courier
 * Author URI:  https://delicatecourier.co.za
 * License:     Proprietary
 * Requires at least: 5.8
 * Requires PHP:      7.4
 * WC requires at least: 6.0
 * WC tested up to:   9.4
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

/* ============================================================================
 * CONTRACT FREEZE — v2.1.0
 * ============================================================================
 * Any change to anything in this block requires a coordinated change on the
 * platform side. DO NOT modify without bumping the plugin major version.
 *
 *   Platform base URL : https://api2.delicatecourier.co.za
 *
 *   Order push path   : POST /api/webhooks/plugin/order
 *   Order push body   : JSON, snake_case, exactly matches the C#
 *                       PluginWebhookPayload DTO in
 *                       DelicateCouriers.ApiService/Features/WooCommerce/
 *                       DTOs/PluginWebhookPayload.cs
 *   Order push headers:
 *     Content-Type:        application/json
 *     X-Store-ID:          numeric store ID from the platform store record
 *     X-Plugin-Signature:  base64( HMAC_SHA256( raw_json_body, webhook_secret ) )
 *     User-Agent:          DelicateCourierPlatform/<version> WordPress/<wp ver>
 *   Order push auth   : NO Authorization header. Auth is the signature.
 *   Order push retry  : transport errors and HTTP 5xx only (never 4xx),
 *                       bounded attempts with exponential backoff.
 *
 *   Contract additions:
 *     v2.6.0 — adds `customer.alternative_phone` (string, may be empty).
 *              Backward-compatible: older platform DTOs will silently ignore
 *              the new field; older plugins emit no field and the platform
 *              should treat its absence as an empty alt phone.
 *     v2.7.0 — adds optional top-level `special_trip` object:
 *              { quoted_amount: number, distance_km: number,
 *                customer_lat: number|null, customer_lng: number|null }.
 *              Only present when fulfillment_type === 'special_trip' and the
 *              customer accepted the Special Trip Request fallback rate at
 *              checkout. Backward-compatible: older platforms ignore it;
 *              older plugins never emit it.
 *     v2.8.0 — admin-only change, NO contract change: the order metabox is
 *              renamed "Delicate Checkout Data" and now also renders the
 *              checkout details written by the Delicate Two-Step Checkout
 *              (method, delivery/collection slots, alt phone, place ID,
 *              coordinates, quote status/price/message) above the existing
 *              Shipment & Tracking section. Reads order meta only; degrades
 *              gracefully on non-DTSC stores.
 *     v2.9.0 — adds a signed waybill download: the metabox "Download
 *              waybill (PDF)" button POSTs {"wc_order_id": <int>} to
 *              POST /api/webhooks/plugin/label with the same
 *              X-Store-ID + X-Plugin-Signature HMAC scheme as the order
 *              push; platform responds with the label PDF. Also prints
 *              the Delicate Checkout Data block on PDF invoices/packing
 *              slips (WooCommerce PDF Invoices & Packing Slips hooks).
 *
 *   Live rates path   : POST /api/public/shipping/rates  (public, no auth)
 *   Live rates body   : { "storeId": <int>,
 *                         "deliveryAddress": { streetAddress, localArea, city,
 *                           zone, country, code, contactName, contactPhone,
 *                           contactEmail, company },
 *                         "parcels": [ { description, lengthCm, widthCm,
 *                                        heightCm, weightKg } ] }
 *   Live rates resp   : { "success": bool, "rates": [ { serviceLevelId,
 *                         serviceLevelCode, serviceLevelName, cost, currency,
 *                         estimatedDeliveryDays } ], "errorMessage": str|null }
 *
 *   The platform performs server-side geocoding (Google -> Nominatim fallback,
 *   results cached) before calling Shiplogic /v2/rates with explicit lat/lng.
 *   This is what unblocked the prior "rates:null" issue. The merchant's
 *   Shiplogic bearer token is therefore NOT required for live rates from
 *   v2.1.0 onwards — it is read from the tenant record on the platform side.
 *   The token field on the settings page is kept for backwards compatibility
 *   and is only used by the "Test direct Shiplogic call" diagnostic button.
 *
 *   Merchant-editable settings:
 *     Always:
 *       dcp_store_id, dcp_webhook_secret  (autoload=no)
 *       dcp_enable_live_rates, dcp_auto_sync_orders, dcp_debug_logging
 *     Optional (kept for direct-call diagnostic only):
 *       dcp_shiplogic_bearer  (autoload=no)
 *     Default build only (hidden in branded builds where the values are
 *     pre-baked as DCP_DEFAULT_* constants):
 *       dcp_shiplogic_account_id, dcp_shiplogic_provider_id
 *
 *   All other configuration (service level, collection address, offset
 *   minutes, cache TTL, retry count) is sourced from DCP_DEFAULT_*
 *   constants (pre-baked in branded builds) or from apply_filters() hooks
 *   documented in README.md.
 * ========================================================================== */

/* ============================================================================
 * CONSTANTS
 * ========================================================================== */

define( 'DCP_VERSION',         '2.9.1' );
define( 'DCP_PLUGIN_FILE',     __FILE__ );
define( 'DCP_PLUGIN_DIR',      plugin_dir_path( __FILE__ ) );
define( 'DCP_PLUGIN_BASENAME', plugin_basename( __FILE__ ) );

// Platform API — default URL is the production custom domain. Merchants
// can override per-site via the "Platform Endpoint URL" field on the
// Settings page (option: dcp_platform_base_url) — useful for pointing at
// staging, or as a fallback while DNS for the custom domain propagates.
// Always read the active value through dcp_platform_base_url() below,
// not through the constant directly.
define( 'DCP_PLATFORM_BASE_URL',    'https://api2.delicatecourier.co.za' );
define( 'DCP_PLATFORM_ORDER_PATH',  '/api/webhooks/plugin/order' );
define( 'DCP_PLATFORM_HEALTH_PATH', '/api/webhooks/plugin/health' );
define( 'DCP_PLATFORM_RATES_PATH',  '/api/public/shipping/rates' );

// Shiplogic API — used directly from the merchant's site for live rates.
define( 'DCP_SHIPLOGIC_BASE_URL',   'https://api.shiplogic.com' );
define( 'DCP_SHIPLOGIC_RATES_PATH', '/v2/rates' );

// Shiplogic account / provider / service level.
//   - Branded builds pre-bake these via define() before this file is loaded,
//     so the merchant doesn't have to enter them.
//   - The default plugin ships with NO tenant data. The merchant enters their
//     own Shiplogic Account ID and Provider ID on the settings page. Service
//     level defaults to 'STD' (Shiplogic's generic Standard code) and can be
//     overridden via the dcp_service_level_code filter.
dcp_define_if_unset( 'DCP_DEFAULT_SHIPLOGIC_ACCOUNT_ID',  '' );
dcp_define_if_unset( 'DCP_DEFAULT_SHIPLOGIC_PROVIDER_ID', '' );
dcp_define_if_unset( 'DCP_DEFAULT_SERVICE_LEVEL_CODE',    'STD' );

// Optional pre-baked collection address. Branded variants override these via
// define() before this file is loaded — see header of each merchant-branded
// variant. The default plugin leaves them empty and falls back to the
// WooCommerce store address at runtime.
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_COMPANY',     '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_STREET',      '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_SUBURB',      '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_CITY',        '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_PROVINCE',    '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_POSTAL_CODE', '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_COUNTRY',     'ZA' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_LATITUDE',    '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_LONGITUDE',   '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_CONTACT_NAME',  '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_CONTACT_PHONE', '' );
dcp_define_if_unset( 'DCP_DEFAULT_COLLECTION_CONTACT_EMAIL', '' );

function dcp_define_if_unset( $name, $value ) {
    if ( ! defined( $name ) ) {
        define( $name, $value );
    }
}

/* ============================================================================
 * SETTINGS HELPERS
 * ========================================================================== */

function dcp_opt( $key, $default = '' ) {
    $val = get_option( 'dcp_' . $key, null );
    if ( $val === null || $val === '' ) {
        return $default;
    }
    return $val;
}

function dcp_store_id()              { return trim( (string) dcp_opt( 'store_id' ) ); }
function dcp_webhook_secret()        { return (string) dcp_opt( 'webhook_secret' ); }
function dcp_shiplogic_bearer()      { return trim( (string) dcp_opt( 'shiplogic_bearer' ) ); }

// Resolution order for these three values:
//   1. DCP_DEFAULT_* constant (pre-baked in a branded build) if non-empty.
//   2. wp_options value the merchant entered on the settings page.
//   3. Filter override (for developers using a mu-plugin / functions.php).
// Branded builds therefore hide the Account/Provider fields from the UI;
// the default build shows them.
function dcp_shiplogic_account_id() {
    $v = DCP_DEFAULT_SHIPLOGIC_ACCOUNT_ID !== '' ? (string) DCP_DEFAULT_SHIPLOGIC_ACCOUNT_ID : trim( (string) dcp_opt( 'shiplogic_account_id' ) );
    return (string) apply_filters( 'dcp_shiplogic_account_id', $v );
}
function dcp_shiplogic_provider_id() {
    $v = DCP_DEFAULT_SHIPLOGIC_PROVIDER_ID !== '' ? (string) DCP_DEFAULT_SHIPLOGIC_PROVIDER_ID : trim( (string) dcp_opt( 'shiplogic_provider_id' ) );
    return (string) apply_filters( 'dcp_shiplogic_provider_id', $v );
}
function dcp_service_level_code() { return (string) apply_filters( 'dcp_service_level_code', DCP_DEFAULT_SERVICE_LEVEL_CODE ); }

/**
 * True when the build has NOT pre-baked the Shiplogic account/provider — i.e.
 * the default plugin. Used by the settings page to decide whether to render
 * those two input fields.
 */
function dcp_shiplogic_ids_are_editable() {
    return DCP_DEFAULT_SHIPLOGIC_ACCOUNT_ID === '' && DCP_DEFAULT_SHIPLOGIC_PROVIDER_ID === '';
}

/**
 * v2.7.0 — Google Maps API key used from the BROWSER at checkout (Places
 * Autocomplete + Geocoding for the live validity indicator) AND server-side
 * for the special-trip driving-distance lookup. Distinct from
 * dcp_google_geocoding_api_key (server-only collection geocoding) because a
 * browser key must be referrer-restricted while a server key is IP-restricted.
 */
function dcp_google_maps_api_key()   { return trim( (string) dcp_opt( 'google_maps_api_key' ) ); }

/**
 * v2.7.0 — Rand-per-kilometre cost for the "Special Trip Request" fallback
 * shipping rate offered when the platform returns no rates for the address.
 * 0 (unset) disables the special-trip fallback entirely.
 */
function dcp_special_trip_cost_per_km() {
    return floatval( dcp_opt( 'special_trip_cost_per_km', '0' ) );
}

function dcp_special_trip_enabled() {
    return dcp_special_trip_cost_per_km() > 0 && dcp_google_maps_api_key() !== '';
}

function dcp_live_rates_enabled()    { return dcp_opt( 'enable_live_rates', 'yes' ) === 'yes'; }
function dcp_auto_sync_enabled()     { return dcp_opt( 'auto_sync_orders', 'yes' ) === 'yes'; }
function dcp_debug_enabled()         { return dcp_opt( 'debug_logging', 'no' ) === 'yes'; }

/**
 * Collection address. Pre-baked constants from a branded build win; if not
 * present, fall back to the WooCommerce store address; can be fully
 * overridden via the `dcp_collection_address` filter.
 */
function dcp_collection_address() {
    $wc_country_state = function_exists( 'wc_get_base_location' ) ? wc_get_base_location() : array( 'country' => 'ZA', 'state' => '' );

    // Resolve lat/lng with four-tier precedence:
    //   1. Manual override option (added in v2.5.1 — merchant explicitly
    //      pastes lat/lng on the settings page). Wins over everything so
    //      a merchant can always force a known-good value if the
    //      geocoder isn't doing its job.
    //   2. Persisted option set by the geocoder on settings save /
    //      activation / lazy fetch.
    //   3. Pre-baked constant (branded build override).
    //   4. Lazy geocode RIGHT NOW if still missing, then re-read.
    // The fourth tier exists because Shiplogic's /v2/rates returns rates:null
    // when collection lat/lng are absent. We will never knowingly send null
    // coordinates again after the lessons of v2.1.x.
    $manual_lat = trim( (string) get_option( 'dcp_collection_latitude_manual',  '' ) );
    $manual_lng = trim( (string) get_option( 'dcp_collection_longitude_manual', '' ) );
    $lat_opt = $manual_lat !== '' ? $manual_lat : get_option( 'dcp_collection_latitude',  '' );
    $lng_opt = $manual_lng !== '' ? $manual_lng : get_option( 'dcp_collection_longitude', '' );

    $lat = $lat_opt !== '' ? floatval( $lat_opt )
         : ( DCP_DEFAULT_COLLECTION_LATITUDE !== '' ? floatval( DCP_DEFAULT_COLLECTION_LATITUDE ) : null );
    $lng = $lng_opt !== '' ? floatval( $lng_opt )
         : ( DCP_DEFAULT_COLLECTION_LONGITUDE !== '' ? floatval( DCP_DEFAULT_COLLECTION_LONGITUDE ) : null );

    if ( $lat === null || $lng === null ) {
        // Lazy fallback. Best-effort — if the geocoder is down, both
        // tiers still fail and the caller sees nulls (same as before),
        // but the *next* checkout will retry after the cache TTL.
        $coords = dcp_geocode_collection_address_if_needed();
        if ( is_array( $coords ) && isset( $coords['lat'], $coords['lng'] ) ) {
            $lat = floatval( $coords['lat'] );
            $lng = floatval( $coords['lng'] );
        }
    }

    $addr = array(
        'type'           => 'business',
        'company'        => DCP_DEFAULT_COLLECTION_COMPANY ?: get_bloginfo( 'name' ),
        'street_address' => DCP_DEFAULT_COLLECTION_STREET  ?: trim( (string) get_option( 'woocommerce_store_address', '' ) . ' ' . (string) get_option( 'woocommerce_store_address_2', '' ) ),
        'local_area'     => DCP_DEFAULT_COLLECTION_SUBURB,
        'city'           => DCP_DEFAULT_COLLECTION_CITY    ?: (string) get_option( 'woocommerce_store_city', '' ),
        'zone'           => DCP_DEFAULT_COLLECTION_PROVINCE ?: ( $wc_country_state['state'] ?? '' ),
        'code'           => DCP_DEFAULT_COLLECTION_POSTAL_CODE ?: (string) get_option( 'woocommerce_store_postcode', '' ),
        'country'        => DCP_DEFAULT_COLLECTION_COUNTRY ?: ( $wc_country_state['country'] ?? 'ZA' ),
        'lat'            => $lat,
        'lng'            => $lng,
        'contact'        => array(
            'name'          => DCP_DEFAULT_COLLECTION_CONTACT_NAME  ?: get_bloginfo( 'name' ),
            'mobile_number' => DCP_DEFAULT_COLLECTION_CONTACT_PHONE,
            'email'         => DCP_DEFAULT_COLLECTION_CONTACT_EMAIL ?: get_bloginfo( 'admin_email' ),
        ),
    );
    return apply_filters( 'dcp_collection_address', $addr );
}

function dcp_collection_offset_minutes() {
    return intval( apply_filters( 'dcp_collection_offset_minutes', 90 ) );
}

function dcp_is_configured() {
    return dcp_store_id() !== '' && dcp_webhook_secret() !== '';
}

/**
 * Secret options must NOT be autoloaded — they're only read when a request
 * actually needs them (rate quote, order push, admin settings page). Saving
 * the same value back via update_option() preserves the existing autoload
 * flag, so we make sure the row exists with autoload=no before any save.
 */
function dcp_ensure_secret_autoload_off( $option_name ) {
    global $wpdb;
    $exists = $wpdb->get_var( $wpdb->prepare( "SELECT option_id FROM {$wpdb->options} WHERE option_name = %s", $option_name ) );
    if ( ! $exists ) {
        add_option( $option_name, '', '', 'no' );
        return;
    }
    // Force autoload=no in case a prior version created it with autoload=yes.
    $wpdb->update( $wpdb->options, array( 'autoload' => 'no' ), array( 'option_name' => $option_name ) );
    wp_cache_delete( 'alloptions', 'options' );
}

function dcp_save_secret_option( $option_name, $value ) {
    dcp_ensure_secret_autoload_off( $option_name );
    update_option( $option_name, $value );
}

/* ============================================================================
 * DEBUG LOG (in-DB ring buffer; capped at 200 entries)
 * ==========================================================================
 * Each entry can carry an optional `data` block with the full HTTP exchange
 * (request URL/method/headers/body and response status/headers/body). Bodies
 * are JSON-decoded for pretty rendering when possible, truncated at
 * DCP_LOG_BODY_MAX bytes, and secret-bearing headers are redacted.
 */

if ( ! defined( 'DCP_LOG_BODY_MAX' ) ) {
    define( 'DCP_LOG_BODY_MAX', 16 * 1024 ); // 16 KB per request or response body
}

function dcp_log( $context, $detail = '', $level = 'info', $data = null ) {
    if ( ! dcp_debug_enabled() ) {
        return;
    }
    $logs = get_option( 'dcp_debug_logs', array() );
    if ( ! is_array( $logs ) ) {
        $logs = array();
    }
    $entry = array(
        'time'    => current_time( 'mysql' ),
        'level'   => (string) $level,
        'context' => (string) $context,
        'detail'  => is_string( $detail ) ? $detail : wp_json_encode( $detail ),
    );
    if ( is_array( $data ) && ! empty( $data ) ) {
        $entry['data'] = dcp_log_sanitize_data( $data );
    }
    $logs[] = $entry;
    if ( count( $logs ) > 200 ) {
        $logs = array_slice( $logs, -200 );
    }
    update_option( 'dcp_debug_logs', $logs, false );
}

/**
 * Convenience wrapper to log a single HTTP exchange in a uniform shape so
 * the admin log viewer can render it consistently.
 *
 * $data = [
 *   'method'           => 'POST',
 *   'url'              => 'https://...',
 *   'request_headers'  => [ 'X-Foo' => 'bar', ... ],
 *   'request_body'     => '... raw json string OR array ...',
 *   'response_status'  => 200,
 *   'response_headers' => [ ... ],
 *   'response_body'    => '... raw json string OR array ...',
 *   'duration_ms'      => 123,
 *   'error'            => 'optional WP_Error message',
 * ]
 */
function dcp_log_http( $context, $detail, $level, $data ) {
    dcp_log( $context, $detail, $level, $data );
}

/**
 * Strip / mask anything that could leak credentials (Bearer tokens, plugin
 * signatures, webhook secret, Shiplogic token). Operates on a normalized
 * shape produced by dcp_log_http().
 */
function dcp_log_sanitize_data( $data ) {
    $secret_headers = array( 'authorization', 'x-plugin-signature', 'x-debug-key', 'cookie' );

    $mask_headers = function ( $headers ) use ( $secret_headers ) {
        if ( ! is_array( $headers ) ) { return $headers; }
        $out = array();
        foreach ( $headers as $k => $v ) {
            $lk = strtolower( (string) $k );
            if ( in_array( $lk, $secret_headers, true ) ) {
                $sv = is_array( $v ) ? implode( ',', $v ) : (string) $v;
                $out[ $k ] = '[redacted len=' . strlen( $sv ) . ']';
            } else {
                $out[ $k ] = is_array( $v ) ? implode( ',', $v ) : (string) $v;
            }
        }
        return $out;
    };

    // Pretty-print JSON bodies; truncate huge ones.
    $normalize_body = function ( $body ) {
        if ( $body === null || $body === '' ) { return $body; }
        // Bodies passed in as arrays get JSON-encoded for display.
        if ( is_array( $body ) ) {
            $body = wp_json_encode( $body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
        } else {
            $body = (string) $body;
            $decoded = json_decode( $body, true );
            if ( $decoded !== null ) {
                $body = wp_json_encode( $decoded, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
            }
        }
        if ( strlen( $body ) > DCP_LOG_BODY_MAX ) {
            $body = substr( $body, 0, DCP_LOG_BODY_MAX ) . "\n... [truncated " . ( strlen( $body ) - DCP_LOG_BODY_MAX ) . " more bytes]";
        }
        return $body;
    };

    $clean = array();
    if ( isset( $data['method'] ) )           { $clean['method'] = (string) $data['method']; }
    if ( isset( $data['url'] ) )              { $clean['url'] = (string) $data['url']; }
    if ( isset( $data['request_headers'] ) )  { $clean['request_headers'] = $mask_headers( $data['request_headers'] ); }
    if ( array_key_exists( 'request_body', $data ) )  { $clean['request_body']  = $normalize_body( $data['request_body'] ); }
    if ( isset( $data['response_status'] ) )  { $clean['response_status'] = (int) $data['response_status']; }
    if ( isset( $data['response_headers'] ) ) { $clean['response_headers'] = $mask_headers( $data['response_headers'] ); }
    if ( array_key_exists( 'response_body', $data ) ) { $clean['response_body'] = $normalize_body( $data['response_body'] ); }
    if ( isset( $data['duration_ms'] ) )      { $clean['duration_ms'] = (int) $data['duration_ms']; }
    if ( ! empty( $data['error'] ) )          { $clean['error'] = (string) $data['error']; }

    // Final scrub of the bodies — defence in depth in case a token is logged
    // as a literal in the JSON payload (e.g. echoed by an error response).
    foreach ( array( 'request_body', 'response_body' ) as $f ) {
        if ( ! empty( $clean[ $f ] ) && is_string( $clean[ $f ] ) ) {
            $clean[ $f ] = preg_replace(
                '/(Bearer\s+)[A-Za-z0-9._\-]+/i',
                '$1[redacted]',
                $clean[ $f ]
            );
        }
    }
    return $clean;
}

/* ============================================================================
 * ACTIVATION / DEACTIVATION
 * ========================================================================== */

register_activation_hook( DCP_PLUGIN_FILE, 'dcp_on_activate' );
function dcp_on_activate() {
    // Drop legacy options from prior plugin generations so a stale API key
    // can't accidentally be sent against the new platform.
    $legacy = array(
        'dcp_api_key',                 // legacy bearer auth — replaced by HMAC
        'dcp_platform_api_key',        // older alias
        'dcp_shiplogic_api_key',       // legacy Shiplogic option name
        'dcp_api_base_url',            // URL is hard-coded now
        'dcp_shiplogic_account_id',    // moved to constant / filter
        'dcp_shiplogic_provider_id',   // moved to constant / filter
        'dcp_service_level_code',      // moved to constant / filter
        'dcp_collection_company', 'dcp_collection_street', 'dcp_collection_suburb',
        'dcp_collection_city', 'dcp_collection_province', 'dcp_collection_postal_code',
        'dcp_collection_country',
        // NOTE: dcp_collection_latitude / dcp_collection_longitude are NOT in this
        // legacy list as of v2.5.0. They are now first-class persisted options
        // populated by the geocoder. Deleting them on activation would wipe
        // valid coordinates every time the plugin re-activates, forcing a
        // re-geocode and risking Shiplogic-side null-rate failures during the
        // brief window where coordinates are missing.
        'dcp_collection_contact_name', 'dcp_collection_contact_phone', 'dcp_collection_contact_email',
        'dcp_collection_offset_minutes',
    );
    foreach ( $legacy as $opt ) {
        delete_option( $opt );
    }

    // Toggle settings can autoload — they're read on every request.
    add_option( 'dcp_enable_live_rates', 'yes' );
    add_option( 'dcp_auto_sync_orders',  'yes' );
    add_option( 'dcp_debug_logging',     'no' );

    // Secrets must NOT autoload — they're sensitive and only read at signing/quoting time.
    // Added dcp_google_geocoding_api_key in v2.5.0 for optional Google Geocoding
    // (Nominatim is used when this is empty).
    // dcp_google_maps_api_key added in v2.7.0 (checkout Places autocomplete +
    // special-trip distance quotes).
    foreach ( array( 'dcp_store_id', 'dcp_webhook_secret', 'dcp_shiplogic_bearer', 'dcp_google_geocoding_api_key', 'dcp_google_maps_api_key' ) as $secret ) {
        dcp_ensure_secret_autoload_off( $secret );
    }

    // Outbox table — see dcp_outbox_*() functions further down.
    dcp_outbox_install();

    // Schedule the retry cron if not already scheduled. The hook itself is
    // registered in dcp_bootstrap() so it survives between requests.
    if ( ! wp_next_scheduled( 'dcp_outbox_process' ) ) {
        wp_schedule_event( time() + 300, 'dcp_every_five_minutes', 'dcp_outbox_process' );
    }

    // Geocode the collection address on activation if it's filled in but
    // lat/lng are missing. Best-effort — silent failure is fine, the lazy
    // fallback during rate-fetch will catch it.
    if ( function_exists( 'dcp_geocode_collection_address_if_needed' ) ) {
        dcp_geocode_collection_address_if_needed();
    }
}

register_deactivation_hook( DCP_PLUGIN_FILE, 'dcp_on_deactivate' );
function dcp_on_deactivate() {
    // Unschedule the retry cron so it doesn't keep firing on a deactivated plugin.
    $timestamp = wp_next_scheduled( 'dcp_outbox_process' );
    if ( $timestamp ) {
        wp_unschedule_event( $timestamp, 'dcp_outbox_process' );
    }
}

// Custom cron interval — WordPress doesn't ship a 5-minute schedule by default.
add_filter( 'cron_schedules', 'dcp_register_cron_intervals' );
function dcp_register_cron_intervals( $schedules ) {
    $schedules['dcp_every_five_minutes'] = array(
        'interval' => 5 * MINUTE_IN_SECONDS,
        'display'  => __( 'Every 5 minutes (Delicate Courier outbox retry)', 'delicate-courier' ),
    );
    return $schedules;
}


/* ============================================================================
 * WOOCOMMERCE BOOTSTRAP
 * ========================================================================== */

// Declare HPOS (Custom Order Tables) compatibility.
add_action( 'before_woocommerce_init', function() {
    if ( class_exists( '\Automattic\WooCommerce\Utilities\FeaturesUtil' ) ) {
        \Automattic\WooCommerce\Utilities\FeaturesUtil::declare_compatibility(
            'custom_order_tables', DCP_PLUGIN_FILE, true
        );
    }
} );

add_action( 'plugins_loaded', 'dcp_bootstrap', 20 );
function dcp_bootstrap() {
    if ( ! class_exists( 'WooCommerce' ) ) {
        add_action( 'admin_notices', function() {
            echo '<div class="notice notice-error"><p><strong>Delicate Courier Platform:</strong> WooCommerce must be installed and active.</p></div>';
        } );
        return;
    }

    // Shipping method (live rates from Shiplogic).
    add_action( 'woocommerce_shipping_init', 'dcp_load_shipping_method' );
    add_filter( 'woocommerce_shipping_methods', 'dcp_register_shipping_method' );

    // Order sync (push to platform).
    add_action( 'woocommerce_order_status_processing',         'dcp_sync_order' );
    add_action( 'woocommerce_order_status_completed',          'dcp_sync_order' );
    add_action( 'woocommerce_order_status_on-hold',            'dcp_sync_order' );
    add_action( 'woocommerce_payment_complete',                'dcp_sync_order' );
    add_action( 'woocommerce_checkout_order_processed',        'dcp_sync_order', 20 );

    // Admin UI.
    add_action( 'admin_menu',                                  'dcp_admin_menu' );
    add_filter( 'plugin_action_links_' . DCP_PLUGIN_BASENAME,  'dcp_action_links' );
    add_action( 'admin_init',                                  'dcp_register_settings' );
    add_action( 'add_meta_boxes',                              'dcp_register_order_meta_box' );

    // AJAX test handlers.
    add_action( 'wp_ajax_dcp_test_platform_health', 'dcp_ajax_test_platform_health' );
    add_action( 'wp_ajax_dcp_test_platform_order',  'dcp_ajax_test_platform_order' );
    add_action( 'wp_ajax_dcp_test_platform_rates',  'dcp_ajax_test_platform_rates' );
    add_action( 'wp_ajax_dcp_test_shiplogic_rates', 'dcp_ajax_test_shiplogic_rates' );
    add_action( 'wp_ajax_dcp_clear_debug_logs',     'dcp_ajax_clear_debug_logs' );
    add_action( 'wp_ajax_dcp_fetch_debug_logs',     'dcp_ajax_fetch_debug_logs' );
    add_action( 'wp_ajax_dcp_outbox_process_now',   'dcp_ajax_outbox_process_now' );

    // Checkout address autocomplete + validity indicator (v2.7.0, requires
    // the merchant's Google Maps API key; no-op when unset).
    add_action( 'wp_enqueue_scripts', 'dcp_enqueue_checkout_autocomplete' );

    // Inbound REST route: the platform posts shipment _dcp_* meta to this
    // endpoint. Bypasses the WooCommerce REST API entirely (and therefore
    // its `edit_shop_orders`/Consumer-Key permission model), because the
    // handler runs as PHP inside WordPress and writes via update_meta_data.
    add_action( 'rest_api_init',                    'dcp_register_meta_receiver_route' );
}

/* ============================================================================
 * CHECKOUT ADDRESS AUTOCOMPLETE (v2.7.0)
 * ============================================================================
 * Google Places Autocomplete on the checkout street-address fields plus a
 * live validity indicator. Only loaded when the merchant configured a
 * Google Maps API key. Classic checkout is targeted via the well-known
 * field ids; block checkout uses different ids and the script also matches
 * its address inputs by autocomplete attribute as a best effort.
 * ========================================================================== */

function dcp_enqueue_checkout_autocomplete() {
    if ( ! function_exists( 'is_checkout' ) || ! is_checkout() ) {
        return;
    }
    $api_key = dcp_google_maps_api_key();
    if ( $api_key === '' ) {
        return;
    }

    // Maps JS with Places library. The loader is async; our init runs via
    // the `loading=async` callback param.
    wp_enqueue_script(
        'dcp-google-maps',
        add_query_arg(
            array(
                'key'       => rawurlencode( $api_key ),
                'libraries' => 'places',
                'loading'   => 'async',
                'callback'  => 'dcpInitAddressAutocomplete',
                'region'    => 'ZA',
            ),
            'https://maps.googleapis.com/maps/api/js'
        ),
        array(),
        DCP_VERSION,
        true
    );

    $inline = <<<'JS'
window.dcpInitAddressAutocomplete = function() {
    if (!window.google || !google.maps || !google.maps.places) { return; }

    var FIELDS = ['billing_address_1', 'shipping_address_1'];

    function ensureIndicator(input) {
        var id = input.id + '_dcp_validity';
        var el = document.getElementById(id);
        if (!el) {
            el = document.createElement('span');
            el.id = id;
            el.className = 'dcp-address-validity';
            el.style.cssText = 'display:inline-block;margin-top:4px;font-size:12px;';
            input.parentNode.appendChild(el);
        }
        return el;
    }

    function setValidity(input, state) {
        var el = ensureIndicator(input);
        if (state === 'valid') {
            el.textContent = '\u2713 Address verified';
            el.style.color = '#1a7f37';
            input.style.borderColor = '#1a7f37';
        } else if (state === 'invalid') {
            el.textContent = '\u26a0 Address not recognised \u2014 please pick a suggestion or check the address';
            el.style.color = '#b35900';
            input.style.borderColor = '#b35900';
        } else {
            el.textContent = '';
            input.style.borderColor = '';
        }
    }

    var geocoder = new google.maps.Geocoder();
    var debounceTimers = {};

    function verifyTyped(input) {
        var val = (input.value || '').trim();
        if (val.length < 6) { setValidity(input, 'none'); return; }
        geocoder.geocode(
            { address: val, componentRestrictions: { country: 'ZA' } },
            function(results, status) {
                if (status === 'OK' && results && results.length > 0) {
                    var t = results[0].types || [];
                    var precise = t.indexOf('street_address') !== -1 ||
                                  t.indexOf('premise') !== -1 ||
                                  t.indexOf('subpremise') !== -1 ||
                                  t.indexOf('route') !== -1;
                    setValidity(input, precise ? 'valid' : 'invalid');
                } else {
                    setValidity(input, 'invalid');
                }
            }
        );
    }

    function fillFromPlace(prefix, place) {
        var comp = {};
        (place.address_components || []).forEach(function(c) {
            c.types.forEach(function(t) { comp[t] = c; });
        });
        var streetNumber = comp.street_number ? comp.street_number.long_name : '';
        var route        = comp.route ? comp.route.long_name : '';
        var suburb       = comp.sublocality_level_1 ? comp.sublocality_level_1.long_name
                          : (comp.sublocality ? comp.sublocality.long_name : '');
        var city         = comp.locality ? comp.locality.long_name
                          : (comp.administrative_area_level_2 ? comp.administrative_area_level_2.long_name : '');
        var postcode     = comp.postal_code ? comp.postal_code.long_name : '';
        var provinceLong = comp.administrative_area_level_1 ? comp.administrative_area_level_1.long_name : '';

        var provinceMap = {
            'Eastern Cape':'EC','Free State':'FS','Gauteng':'GP','KwaZulu-Natal':'KZN',
            'Limpopo':'LP','Mpumalanga':'MP','North West':'NW','Northern Cape':'NC','Western Cape':'WC'
        };

        function set(id, value) {
            var el = document.getElementById(id);
            if (!el || value === '') { return; }
            el.value = value;
            el.dispatchEvent(new Event('change', { bubbles: true }));
            if (window.jQuery) { window.jQuery(el).trigger('change'); }
        }

        set(prefix + '_address_1', (streetNumber + ' ' + route).trim());
        set(prefix + '_address_2', suburb);
        set(prefix + '_city', city);
        set(prefix + '_postcode', postcode);

        var stateEl = document.getElementById(prefix + '_state');
        if (stateEl && provinceMap[provinceLong]) {
            stateEl.value = provinceMap[provinceLong];
            stateEl.dispatchEvent(new Event('change', { bubbles: true }));
            if (window.jQuery) { window.jQuery(stateEl).trigger('change'); }
        }

        // Nudge WooCommerce to recalculate shipping with the new address.
        if (window.jQuery && window.jQuery(document.body).length) {
            window.jQuery(document.body).trigger('update_checkout');
        }
    }

    FIELDS.forEach(function(fieldId) {
        var input = document.getElementById(fieldId);
        if (!input || input.getAttribute('data-dcp-autocomplete') === '1') { return; }
        input.setAttribute('data-dcp-autocomplete', '1');

        var ac = new google.maps.places.Autocomplete(input, {
            componentRestrictions: { country: 'za' },
            fields: ['address_components', 'geometry', 'types'],
            types: ['address']
        });

        ac.addListener('place_changed', function() {
            var place = ac.getPlace();
            if (!place || !place.address_components) {
                setValidity(input, 'invalid');
                return;
            }
            fillFromPlace(fieldId.replace('_address_1', ''), place);
            setValidity(input, 'valid');
        });

        // Manual typing (no suggestion picked) — debounce-verify via geocoder.
        input.addEventListener('input', function() {
            setValidity(input, 'none');
            clearTimeout(debounceTimers[fieldId]);
            debounceTimers[fieldId] = setTimeout(function() { verifyTyped(input); }, 900);
        });

        // Don't let Enter inside the suggestion dropdown submit the form.
        input.addEventListener('keydown', function(e) {
            if (e.key === 'Enter' && document.querySelector('.pac-container:not([style*="display: none"])')) {
                e.preventDefault();
            }
        });
    });
};

// Checkout fragments re-render can replace inputs; re-init on WC events.
if (window.jQuery) {
    window.jQuery(document.body).on('updated_checkout country_to_state_changed', function() {
        if (window.google && google.maps && google.maps.places) {
            window.dcpInitAddressAutocomplete();
        }
    });
}
JS;
    wp_add_inline_script( 'dcp-google-maps', $inline, 'before' );
}

/* ============================================================================
 * INBOUND META RECEIVER (platform → plugin)
 *
 *   POST {site}/wp-json/delicate-courier/v1/shipment-meta
 *
 *   Headers:
 *     X-Store-ID:          dcp_store_id (must match this site's configured id)
 *     X-Plugin-Signature:  base64( HMAC_SHA256( raw_body, dcp_webhook_secret ) )
 *     Content-Type:        application/json
 *
 *   Body:
 *     { "wc_order_id": 11990,
 *       "meta": { "_dcp_tracking_number": "XJ347V",
 *                 "_dcp_courier_name": "Aramex SA",
 *                 "_dcp_shipment_status": "submitted",
 *                 ... } }
 *
 *   Only keys whose name starts with "_dcp_" are accepted — the platform
 *   must never be able to overwrite arbitrary WC order meta through this
 *   route. Existing keys are upserted; unknown keys are inserted. The
 *   metabox reads straight from `_dcp_*` meta so the panel updates as soon
 *   as the response goes back.
 * ========================================================================== */

function dcp_register_meta_receiver_route() {
    register_rest_route( 'delicate-courier/v1', '/shipment-meta', array(
        'methods'             => 'POST',
        'callback'            => 'dcp_rest_receive_shipment_meta',
        // The signature *is* the auth check; do it inside the handler so we
        // can return an informative 401 body that distinguishes "missing
        // header", "store mismatch", and "bad signature".
        'permission_callback' => '__return_true',
    ) );
}

function dcp_rest_receive_shipment_meta( WP_REST_Request $request ) {
    $secret              = dcp_webhook_secret();
    $store_id_configured = dcp_store_id();
    if ( $secret === '' || $store_id_configured === '' ) {
        return new WP_REST_Response( array(
            'ok'    => false,
            'error' => 'Plugin not configured: Store ID and Webhook Secret are required.',
        ), 503 );
    }

    $raw        = $request->get_body();
    $sig_header = (string) $request->get_header( 'x_plugin_signature' );
    if ( $sig_header === '' ) {
        return new WP_REST_Response( array(
            'ok'    => false,
            'error' => 'Missing X-Plugin-Signature header.',
        ), 401 );
    }
    $expected = base64_encode( hash_hmac( 'sha256', $raw, $secret, true ) );
    if ( ! hash_equals( $expected, $sig_header ) ) {
        dcp_log( 'meta.recv', sprintf(
            'Signature mismatch on shipment-meta. BodyLen=%d BodySha256=%s SecretLen=%d SecretSha256=%s',
            strlen( $raw ), hash( 'sha256', $raw ),
            strlen( $secret ), hash( 'sha256', $secret )
        ), 'warn' );
        return new WP_REST_Response( array(
            'ok'    => false,
            'error' => 'Invalid signature.',
        ), 401 );
    }

    $store_id_header = (string) $request->get_header( 'x_store_id' );
    if ( $store_id_header !== '' && $store_id_header !== $store_id_configured ) {
        return new WP_REST_Response( array(
            'ok'    => false,
            'error' => sprintf( 'X-Store-ID mismatch: received %s, configured %s.', $store_id_header, $store_id_configured ),
        ), 403 );
    }

    $data = json_decode( $raw, true );
    if ( ! is_array( $data ) ) {
        return new WP_REST_Response( array( 'ok' => false, 'error' => 'Body is not valid JSON.' ), 400 );
    }
    $wc_order_id = isset( $data['wc_order_id'] ) ? intval( $data['wc_order_id'] ) : 0;
    $meta        = isset( $data['meta'] ) && is_array( $data['meta'] ) ? $data['meta'] : array();
    // Optional order note. Lets the platform attach a customer-visible
    // (or internal) note in the same signed call instead of needing a
    // separate authenticated WC REST API call to /orders/{id}/notes —
    // which is the path that historically failed silently when the
    // merchant's WC Consumer Key/Secret were stale or under-permissioned.
    $note_text   = isset( $data['note'] ) && is_string( $data['note'] ) ? trim( $data['note'] ) : '';
    $note_is_customer_note = ! empty( $data['note_is_customer_note'] );
    if ( $wc_order_id <= 0 || ( empty( $meta ) && $note_text === '' ) ) {
        return new WP_REST_Response( array(
            'ok'    => false,
            'error' => 'wc_order_id (int) plus either a non-empty meta object or a note is required.',
        ), 400 );
    }

    $order = function_exists( 'wc_get_order' ) ? wc_get_order( $wc_order_id ) : null;
    if ( ! $order ) {
        return new WP_REST_Response( array(
            'ok'    => false,
            'error' => sprintf( 'WC order %d not found on this site.', $wc_order_id ),
        ), 404 );
    }

    $updated         = 0;
    $accepted_keys   = array();
    $skipped         = array();
    $preserved_keys  = array();
    foreach ( $meta as $key => $value ) {
        // Whitelist: the platform may only write into the `_dcp_*` namespace.
        if ( ! is_string( $key ) || strpos( $key, '_dcp_' ) !== 0 ) {
            $skipped[] = (string) $key;
            continue;
        }
        if ( $value === null ) {
            $stored = '';
        } elseif ( is_scalar( $value ) ) {
            $stored = (string) $value;
        } else {
            $stored = wp_json_encode( $value );
        }
        // Refuse to overwrite a previously-saved non-empty value with an
        // empty one. This guards against the "platform re-syncs a record
        // that hasn't yet been populated from Shiplogic and wipes a real
        // tracking number" failure mode. The platform can still clear a
        // field explicitly by sending the sentinel string "__dcp_clear__".
        if ( $stored === '' ) {
            $existing = (string) $order->get_meta( $key, true );
            if ( $existing !== '' ) {
                $preserved_keys[] = $key;
                continue;
            }
        } elseif ( $stored === '__dcp_clear__' ) {
            $stored = '';
        }
        $order->update_meta_data( $key, $stored );
        $accepted_keys[] = $key;
        $updated++;
    }

    $note_added = false;
    if ( $note_text !== '' ) {
        // add_order_note() persists immediately and triggers any
        // customer-note emails WC has configured.
        $order->add_order_note( $note_text, $note_is_customer_note ? 1 : 0 );
        $note_added = true;
    }

    if ( $updated > 0 ) {
        $order->save();
        // Belt-and-braces cache invalidation. $order->save() already
        // updates WC's own caches, but an aggressive object-cache layer
        // (Redis with long TTLs) or page cache plugin can otherwise
        // serve a stale metabox until the admin page is hard-refreshed.
        if ( function_exists( 'clean_post_cache' ) ) {
            clean_post_cache( $wc_order_id );
        }
        if ( function_exists( 'wc_delete_shop_order_transients' ) ) {
            wc_delete_shop_order_transients( $wc_order_id );
        }
    }

    dcp_log( 'meta.recv', sprintf(
        'order=%d updated=%d skipped=%d preserved=%d note=%s keys=%s',
        $wc_order_id, $updated, count( $skipped ), count( $preserved_keys ),
        $note_added ? 'yes' : 'no',
        implode( ',', array_keys( $meta ) )
    ), 'info' );

    return new WP_REST_Response( array(
        'ok'             => true,
        'wc_order_id'    => $wc_order_id,
        'updated'        => $updated,
        'accepted_keys'  => $accepted_keys,
        'skipped'        => $skipped,
        // Keys we explicitly REFUSED to clobber with an empty value.
        // Lets the platform tell "the plugin saved nothing because we
        // sent nothing useful" apart from "the plugin saved nothing
        // because none of our keys matched the whitelist".
        'preserved_keys' => $preserved_keys,
        'note_added'     => $note_added,
    ), 200 );
}

/* ============================================================================
 * SHIPPING METHOD — live Shiplogic rates
 * ========================================================================== */

function dcp_load_shipping_method() {
    if ( class_exists( 'WC_Shipping_Method' ) && ! class_exists( 'DCP_Shipping_Method' ) ) {

        class DCP_Shipping_Method extends WC_Shipping_Method {

            public function __construct( $instance_id = 0 ) {
                $this->id                 = 'delicate_courier_platform';
                $this->instance_id        = absint( $instance_id );
                $this->method_title       = __( 'Delicate Courier Platform', 'dcp' );
                $this->method_description = __( 'Live courier rates from Shiplogic via the Delicate Courier Platform.', 'dcp' );
                $this->supports           = array( 'shipping-zones', 'instance-settings', 'instance-settings-modal' );

                $this->init_form_fields();
                $this->init_settings();

                $this->title          = $this->get_option( 'title', 'Courier Delivery' );
                $this->tax_status     = $this->get_option( 'tax_status', 'taxable' );
                $this->markup_type    = $this->get_option( 'markup_type', 'none' );
                $this->markup_value   = floatval( $this->get_option( 'markup_value', 0 ) );

                $this->enabled = isset( $this->settings['enabled'] ) ? $this->settings['enabled'] : 'yes';

                add_action( 'woocommerce_update_options_shipping_' . $this->id, array( $this, 'process_admin_options' ) );
            }

            public function init_form_fields() {
                $this->instance_form_fields = array(
                    'title' => array(
                        'title'       => __( 'Method title', 'dcp' ),
                        'type'        => 'text',
                        'description' => __( 'Label shown to customers at checkout.', 'dcp' ),
                        'default'     => 'Courier Delivery',
                        'desc_tip'    => true,
                    ),
                    'tax_status' => array(
                        'title'   => __( 'Tax status', 'dcp' ),
                        'type'    => 'select',
                        'default' => 'taxable',
                        'options' => array(
                            'taxable' => __( 'Taxable', 'dcp' ),
                            'none'    => __( 'None', 'dcp' ),
                        ),
                    ),
                    'markup_type' => array(
                        'title'   => __( 'Rate markup', 'dcp' ),
                        'type'    => 'select',
                        'default' => 'none',
                        'options' => array(
                            'none'       => __( 'No markup', 'dcp' ),
                            'fixed'      => __( 'Fixed amount (R)', 'dcp' ),
                            'percentage' => __( 'Percentage (%)', 'dcp' ),
                        ),
                    ),
                    'markup_value' => array(
                        'title'             => __( 'Markup value', 'dcp' ),
                        'type'              => 'number',
                        'default'           => 0,
                        'custom_attributes' => array( 'step' => '0.01', 'min' => '0' ),
                    ),
                );
            }

            public function calculate_shipping( $package = array() ) {
                if ( ! dcp_live_rates_enabled() ) {
                    dcp_log( 'rates.skip', 'Live rates disabled in settings' );
                    return;
                }
                if ( dcp_store_id() === '' ) {
                    dcp_log( 'rates.skip', 'No Store ID configured', 'error' );
                    return;
                }

                $delivery = $this->build_delivery_address( $package );
                if ( ! $delivery ) {
                    dcp_log( 'rates.skip', 'Delivery address incomplete', 'warning' );
                    return;
                }

                $weight  = $this->calculate_total_weight( $package );
                $parcels = array(
                    array(
                        'parcel_description'  => 'Package',
                        'submitted_length_cm' => 20,
                        'submitted_width_cm'  => 15,
                        'submitted_height_cm' => 10,
                        'submitted_weight_kg' => $weight,
                    ),
                );

                // Transient cache to keep checkout snappy. Key = store ID +
                // service code + destination + WooCommerce cart hash (falls
                // back to a hash of the package contents when the cart
                // singleton isn't available, e.g. on pure shipping-zone
                // evaluation).
                $cart_hash = '';
                if ( function_exists( 'WC' ) && WC()->cart instanceof WC_Cart ) {
                    $cart_hash = WC()->cart->get_cart_hash();
                }
                if ( $cart_hash === '' ) {
                    $cart_hash = md5( wp_json_encode( $package['contents'] ?? array() ) );
                }
                $cache_key = 'dcp_rate_' . md5( dcp_store_id() . '|' . dcp_service_level_code() . '|' . wp_json_encode( $delivery ) . '|' . $cart_hash );
                $rates     = get_transient( $cache_key );
                if ( false === $rates ) {
                    $rates = dcp_platform_fetch_rates( $delivery, $parcels );
                    if ( ! is_wp_error( $rates ) ) {
                        // Short TTL — destination + cart hash collisions are
                        // extremely rare, but rate cards do change.
                        set_transient( $cache_key, $rates, intval( apply_filters( 'dcp_rate_cache_ttl_seconds', 300 ) ) );
                    }
                } else {
                    dcp_log( 'rates.cache_hit', 'Served from transient cache' );
                }
                if ( is_wp_error( $rates ) ) {
                    dcp_log( 'rates.error', $rates->get_error_message(), 'error' );
                    return;
                }
                if ( empty( $rates ) ) {
                    dcp_log( 'rates.empty', 'Platform returned no rates' );
                    $this->maybe_add_special_trip_rate( $package );
                    return;
                }

                $wanted = dcp_service_level_code();
                $added  = 0;
                foreach ( $rates as $rate ) {
                    $code = $rate['service_level']['code'] ?? '';
                    $name = $rate['service_level']['name'] ?? $code;
                    if ( $code === '' || ! isset( $rate['rate'] ) ) {
                        continue;
                    }
                    if ( $wanted !== '' && $code !== $wanted ) {
                        continue;
                    }

                    $cost = floatval( $rate['rate'] );
                    if ( $this->markup_type === 'fixed' ) {
                        $cost += $this->markup_value;
                    } elseif ( $this->markup_type === 'percentage' ) {
                        $cost += ( $cost * $this->markup_value / 100 );
                    }

                    $this->add_rate( array(
                        'id'        => $this->id . '_' . strtolower( $code ),
                        'label'     => esc_html( $name ),
                        'cost'      => $cost,
                        'meta_data' => array(
                            'service_level_code' => $code,
                        ),
                    ) );
                    $added++;
                    dcp_log( 'rates.added', $name . ' R' . number_format( $cost, 2 ) );
                }

                if ( $added === 0 ) {
                    dcp_log( 'rates.filtered', 'No rates matched service code ' . $wanted );
                    $this->maybe_add_special_trip_rate( $package );
                }
            }

            /**
             * v2.7.0 — "Special Trip Request" fallback.
             *
             * When the platform yields NO usable courier rate for the
             * destination, and the merchant has configured a cost-per-km +
             * Google Maps API key, offer a shipping option priced from the
             * real driving distance between the store and the customer:
             *
             *   cost = ceil_km( driving_distance ) × dcp_special_trip_cost_per_km
             *
             * The quote details (distance, lat/lng, amount) are attached as
             * rate meta_data, which WooCommerce persists onto the order's
             * WC_Order_Item_Shipping when the customer selects this option —
             * the order-push payload reads them back from there.
             */
            private function maybe_add_special_trip_rate( $package ) {
                if ( ! dcp_special_trip_enabled() ) {
                    return;
                }
                $quote = dcp_special_trip_quote( $package );
                if ( ! $quote ) {
                    return;
                }

                $this->add_rate( array(
                    'id'        => $this->id . '_special_trip',
                    'label'     => sprintf(
                        /* translators: %s: driving distance in km */
                        __( 'Special Trip Request (%s km)', 'dcp' ),
                        number_format_i18n( $quote['distance_km'], 1 )
                    ),
                    'cost'      => $quote['amount'],
                    'meta_data' => array(
                        'dcp_special_trip'   => 'yes',
                        'dcp_st_quote'       => $quote['amount'],
                        'dcp_st_distance_km' => $quote['distance_km'],
                        'dcp_st_lat'         => $quote['lat'],
                        'dcp_st_lng'         => $quote['lng'],
                    ),
                ) );
                dcp_log( 'rates.special_trip', sprintf(
                    'Offered special trip: %.1f km × R%.2f/km = R%.2f',
                    $quote['distance_km'], dcp_special_trip_cost_per_km(), $quote['amount']
                ) );
            }

            private function build_delivery_address( $package ) {
                $dest = isset( $package['destination'] ) ? $package['destination'] : array();
                $street = trim( ( $dest['address']   ?? '' ) . ' ' . ( $dest['address_2'] ?? '' ) );
                $city   = $dest['city']     ?? '';
                $code   = $dest['postcode'] ?? '';
                $state  = $dest['state']    ?? '';

                if ( $street === '' || $city === '' || $code === '' ) {
                    return false;
                }

                return array(
                    'type'           => 'residential',
                    'company'        => null,
                    'street_address' => $street,
                    'local_area'     => '',
                    'city'           => $city,
                    'zone'           => dcp_normalize_province( $state ),
                    'code'           => $code,
                    'country'        => $dest['country'] ?? 'ZA',
                    'lat'            => null,
                    'lng'            => null,
                    'contact'        => array(
                        'name'          => '',
                        'mobile_number' => null,
                        'email'         => null,
                    ),
                );
            }

            private function calculate_total_weight( $package ) {
                $weight = 0;
                if ( ! empty( $package['contents'] ) ) {
                    foreach ( $package['contents'] as $item ) {
                        $product = $item['data'];
                        $qty     = $item['quantity'];
                        $w       = floatval( $product->get_weight() );
                        if ( $w <= 0 ) { $w = 1; }
                        $weight += $w * $qty;
                    }
                }
                return max( 1, $weight );
            }
        }
    }
}

function dcp_register_shipping_method( $methods ) {
    $methods['delicate_courier_platform'] = 'DCP_Shipping_Method';
    return $methods;
}

function dcp_normalize_province( $state ) {
    $map = array(
        'EC'  => 'Eastern Cape',
        'FS'  => 'Free State',
        'GP'  => 'Gauteng',
        'KZN' => 'KwaZulu-Natal',
        'LP'  => 'Limpopo',
        'MP'  => 'Mpumalanga',
        'NW'  => 'North West',
        'NC'  => 'Northern Cape',
        'WC'  => 'Western Cape',
    );
    if ( $state === '' ) {
        return '';
    }
    $upper = strtoupper( trim( $state ) );
    if ( isset( $map[ $upper ] ) ) {
        return $map[ $upper ];
    }
    return $state;
}

/* ============================================================================
 * GEOCODER — Nominatim primary, Google optional
 * ============================================================================
 * Why this exists: Shiplogic's /v2/rates endpoint silently returns
 * `rates: null` when lat/lng are missing on the collection address. We
 * spent serious time discovering that during the v2.1 debug marathon. The
 * platform handles this server-side; the direct-Shiplogic fallback path
 * (added in v2.5.0) doesn't have that luxury, so the plugin now performs
 * its own geocoding and persists the result.
 *
 * Resolution order:
 *   1. Cached coordinates in wp_options (set on save / lazy fetch / activation).
 *   2. If missing, call Google Geocoding API (if dcp_google_geocoding_api_key
 *      is set) — most accurate for SA street addresses.
 *   3. If Google is unavailable or key empty, fall back to Nominatim
 *      (OpenStreetMap) — free, no API key, rate-limited to 1 req/sec.
 *
 * Mirrors the geocoder chain on the platform side so behaviour is
 * predictable whether rates come through the platform or direct.
 * ============================================================================ */

/**
 * Build the human-readable address string we'll geocode against. Uses the same
 * address fields dcp_collection_address() reads from, joined into a single
 * string suitable for either geocoder. The hash of this string is the cache
 * key, so any change to the address invalidates the cached coordinates.
 */
function dcp_collection_address_for_geocoding() {
    $wc_country_state = function_exists( 'wc_get_base_location' ) ? wc_get_base_location() : array( 'country' => 'ZA', 'state' => '' );

    $parts = array_filter( array(
        DCP_DEFAULT_COLLECTION_STREET  ?: trim( (string) get_option( 'woocommerce_store_address', '' ) . ' ' . (string) get_option( 'woocommerce_store_address_2', '' ) ),
        DCP_DEFAULT_COLLECTION_SUBURB,
        DCP_DEFAULT_COLLECTION_CITY    ?: (string) get_option( 'woocommerce_store_city', '' ),
        DCP_DEFAULT_COLLECTION_PROVINCE ?: ( $wc_country_state['state'] ?? '' ),
        DCP_DEFAULT_COLLECTION_POSTAL_CODE ?: (string) get_option( 'woocommerce_store_postcode', '' ),
        DCP_DEFAULT_COLLECTION_COUNTRY ?: ( $wc_country_state['country'] ?? 'ZA' ),
    ) );
    return implode( ', ', array_map( 'trim', $parts ) );
}

/**
 * Geocode the merchant's collection address using Google → Nominatim, store
 * the result in wp_options. Called on settings save and on plugin activation.
 *
 * Returns ['lat' => float, 'lng' => float] on success, or false on total failure.
 */
function dcp_geocode_collection_address() {
    $address = dcp_collection_address_for_geocoding();
    if ( $address === '' ) {
        return false;
    }

    // Hash the input so we know whether the cached coords match the current
    // address — if the merchant edits their address later, the hash changes
    // and the geocoder runs again on next access.
    $address_hash = md5( $address );

    // Pass 1: Google (best quality, requires API key)
    $api_key = trim( (string) get_option( 'dcp_google_geocoding_api_key', '' ) );
    if ( $api_key !== '' ) {
        $coords = dcp_geocode_via_google( $address, $api_key );
        if ( $coords ) {
            update_option( 'dcp_collection_latitude',  $coords['lat'] );
            update_option( 'dcp_collection_longitude', $coords['lng'] );
            update_option( 'dcp_collection_geocoded_hash', $address_hash );
            update_option( 'dcp_collection_geocoded_provider', 'google' );
            update_option( 'dcp_collection_geocoded_at', time() );
            dcp_log( 'geocode.success', "Google: {$address} → {$coords['lat']},{$coords['lng']}" );
            return $coords;
        }
        dcp_log( 'geocode.google_fail', "Falling back to Nominatim for: {$address}", 'warning' );
    }

    // Pass 2: Nominatim (free, no key)
    $coords = dcp_geocode_via_nominatim( $address );
    if ( $coords ) {
        update_option( 'dcp_collection_latitude',  $coords['lat'] );
        update_option( 'dcp_collection_longitude', $coords['lng'] );
        update_option( 'dcp_collection_geocoded_hash', $address_hash );
        update_option( 'dcp_collection_geocoded_provider', 'nominatim' );
        update_option( 'dcp_collection_geocoded_at', time() );
        dcp_log( 'geocode.success', "Nominatim: {$address} → {$coords['lat']},{$coords['lng']}" );
        return $coords;
    }

    dcp_log( 'geocode.fail', "All geocoders failed for: {$address}", 'error' );
    return false;
}

/**
 * Lazy variant: only geocodes if coordinates are missing OR if the address
 * has been edited since the last geocode (hash mismatch). Cheap to call on
 * every rate fetch — short-circuits to a no-op when coords are already valid.
 */
function dcp_geocode_collection_address_if_needed() {
    $lat = get_option( 'dcp_collection_latitude', '' );
    $lng = get_option( 'dcp_collection_longitude', '' );
    $stored_hash = get_option( 'dcp_collection_geocoded_hash', '' );
    $current_hash = md5( dcp_collection_address_for_geocoding() );

    if ( $lat !== '' && $lng !== '' && $stored_hash === $current_hash ) {
        // Already geocoded for this exact address. No-op.
        return array( 'lat' => floatval( $lat ), 'lng' => floatval( $lng ) );
    }

    // Either missing or stale. Trigger fresh geocode.
    // Throttle: don't re-attempt failed geocoding more than once per hour
    // (some addresses are genuinely un-geocodable; hammering the API is rude).
    $last_attempt = (int) get_option( 'dcp_collection_geocode_last_attempt', 0 );
    if ( $lat === '' && $last_attempt > 0 && ( time() - $last_attempt ) < HOUR_IN_SECONDS ) {
        return false;
    }
    update_option( 'dcp_collection_geocode_last_attempt', time() );

    return dcp_geocode_collection_address();
}

/**
 * Google Geocoding API call. Quota: free tier ~28k requests/month per project.
 * Returns ['lat' => float, 'lng' => float] | false.
 */
function dcp_geocode_via_google( $address, $api_key ) {
    $url = add_query_arg(
        array( 'address' => $address, 'key' => $api_key ),
        'https://maps.googleapis.com/maps/api/geocode/json'
    );
    $response = wp_remote_get( $url, array( 'timeout' => 8 ) );
    if ( is_wp_error( $response ) ) {
        return false;
    }
    $code = wp_remote_retrieve_response_code( $response );
    if ( $code !== 200 ) {
        return false;
    }
    $data = json_decode( wp_remote_retrieve_body( $response ), true );
    if ( ! is_array( $data ) || ( $data['status'] ?? '' ) !== 'OK' ) {
        return false;
    }
    $loc = $data['results'][0]['geometry']['location'] ?? null;
    if ( ! is_array( $loc ) || ! isset( $loc['lat'], $loc['lng'] ) ) {
        return false;
    }
    return array( 'lat' => floatval( $loc['lat'] ), 'lng' => floatval( $loc['lng'] ) );
}

/**
 * Nominatim (OpenStreetMap) geocoding call. Free, no API key.
 *
 * Nominatim's usage policy requires:
 *   - A valid HTTP User-Agent identifying the application
 *   - Maximum 1 request per second per IP
 *   - No bulk geocoding
 *
 * We call this at most once on settings save and lazily during rate-fetch,
 * so rate-limiting isn't a concern in practice.
 *
 * Returns ['lat' => float, 'lng' => float] | false.
 */
function dcp_geocode_via_nominatim( $address ) {
    $url = add_query_arg(
        array(
            'q'              => $address,
            'format'         => 'json',
            'limit'          => 1,
            'addressdetails' => 0,
        ),
        'https://nominatim.openstreetmap.org/search'
    );
    $response = wp_remote_get( $url, array(
        'timeout' => 8,
        'headers' => array(
            // Required by Nominatim's usage policy.
            'User-Agent' => 'DelicateCourierPlatform/' . DCP_VERSION . ' (' . home_url() . ')',
            'Accept'     => 'application/json',
        ),
    ) );
    if ( is_wp_error( $response ) ) {
        return false;
    }
    $code = wp_remote_retrieve_response_code( $response );
    if ( $code !== 200 ) {
        return false;
    }
    $data = json_decode( wp_remote_retrieve_body( $response ), true );
    if ( ! is_array( $data ) || empty( $data[0]['lat'] ) || empty( $data[0]['lon'] ) ) {
        return false;
    }
    return array(
        'lat' => floatval( $data[0]['lat'] ),
        'lng' => floatval( $data[0]['lon'] ),
    );
}

/* ============================================================================
 * SPECIAL TRIP QUOTE (v2.7.0)
 * ============================================================================
 * Driving-distance quote for the "Special Trip Request" fallback rate.
 * Uses the merchant's Google Maps API key:
 *   1. Geocode the customer's destination address (Google Geocoding API) —
 *      gives us lat/lng that ride along to the platform for the eventual
 *      Shiplogic SPX booking.
 *   2. Distance Matrix API (mode=driving) from the store's collection
 *      coordinates to the customer's coordinates.
 *   3. cost = distance_km × dcp_special_trip_cost_per_km, rounded to cents.
 * Results are transient-cached per destination for 15 minutes so repeated
 * checkout recalculations don't hammer the (paid) Google APIs.
 * ========================================================================== */

/**
 * Returns array{amount: float, distance_km: float, lat: float|null, lng: float|null}
 * or false when a quote cannot be produced (no store coords, geocode failure,
 * Distance Matrix failure, zero distance).
 */
function dcp_special_trip_quote( $package ) {
    $api_key = dcp_google_maps_api_key();
    $per_km  = dcp_special_trip_cost_per_km();
    if ( $api_key === '' || $per_km <= 0 ) {
        return false;
    }

    // Store origin — same coordinates used for Shiplogic collection.
    $origin = dcp_geocode_collection_address_if_needed();
    $manual_lat = get_option( 'dcp_collection_latitude_manual', '' );
    $manual_lng = get_option( 'dcp_collection_longitude_manual', '' );
    if ( $manual_lat !== '' && $manual_lng !== '' ) {
        $origin = array( 'lat' => floatval( $manual_lat ), 'lng' => floatval( $manual_lng ) );
    }
    if ( ! is_array( $origin ) || empty( $origin['lat'] ) || empty( $origin['lng'] ) ) {
        dcp_log( 'special_trip.no_origin', 'Store coordinates unavailable — cannot quote special trip', 'warning' );
        return false;
    }

    // Destination address string from the shipping package.
    $dest  = isset( $package['destination'] ) ? $package['destination'] : array();
    $parts = array_filter( array_map( 'trim', array(
        (string) ( $dest['address']   ?? '' ),
        (string) ( $dest['address_2'] ?? '' ),
        (string) ( $dest['city']      ?? '' ),
        dcp_normalize_province( (string) ( $dest['state'] ?? '' ) ),
        (string) ( $dest['postcode']  ?? '' ),
        (string) ( $dest['country']   ?? 'ZA' ),
    ) ) );
    if ( count( $parts ) < 2 ) {
        return false;
    }
    $dest_address = implode( ', ', $parts );

    $cache_key = 'dcp_sttrip_' . md5( $origin['lat'] . ',' . $origin['lng'] . '|' . $dest_address . '|' . $per_km );
    $cached    = get_transient( $cache_key );
    if ( is_array( $cached ) ) {
        return $cached;
    }
    // Negative cache: a recent failure is cached as the string 'fail' so a
    // customer tabbing through checkout fields doesn't retrigger paid calls.
    if ( $cached === 'fail' ) {
        return false;
    }

    // 1. Geocode the destination for lat/lng (needed by the platform for
    //    the Shiplogic SPX booking).
    $coords = dcp_geocode_via_google( $dest_address, $api_key );

    // 2. Driving distance.
    $dm_dest = $coords ? ( $coords['lat'] . ',' . $coords['lng'] ) : $dest_address;
    $url = add_query_arg(
        array(
            'origins'      => $origin['lat'] . ',' . $origin['lng'],
            'destinations' => $dm_dest,
            'mode'         => 'driving',
            'region'       => 'za',
            'key'          => $api_key,
        ),
        'https://maps.googleapis.com/maps/api/distancematrix/json'
    );
    $response = wp_remote_get( $url, array( 'timeout' => 8 ) );
    $meters   = 0;
    if ( ! is_wp_error( $response ) && wp_remote_retrieve_response_code( $response ) === 200 ) {
        $data = json_decode( wp_remote_retrieve_body( $response ), true );
        $el   = $data['rows'][0]['elements'][0] ?? null;
        if ( is_array( $el ) && ( $el['status'] ?? '' ) === 'OK' && ! empty( $el['distance']['value'] ) ) {
            $meters = intval( $el['distance']['value'] );
        } else {
            dcp_log( 'special_trip.dm_status', 'Distance Matrix element status: ' . ( is_array( $el ) ? ( $el['status'] ?? 'missing' ) : ( $data['status'] ?? 'bad response' ) ), 'warning' );
        }
    } else {
        dcp_log( 'special_trip.dm_error', is_wp_error( $response ) ? $response->get_error_message() : 'HTTP ' . wp_remote_retrieve_response_code( $response ), 'warning' );
    }

    if ( $meters <= 0 ) {
        set_transient( $cache_key, 'fail', 5 * MINUTE_IN_SECONDS );
        return false;
    }

    $distance_km = round( $meters / 1000, 1 );
    $amount      = round( $distance_km * $per_km, 2 );
    if ( $amount <= 0 ) {
        set_transient( $cache_key, 'fail', 5 * MINUTE_IN_SECONDS );
        return false;
    }

    $quote = array(
        'amount'      => $amount,
        'distance_km' => $distance_km,
        'lat'         => $coords ? $coords['lat'] : null,
        'lng'         => $coords ? $coords['lng'] : null,
    );
    set_transient( $cache_key, $quote, 15 * MINUTE_IN_SECONDS );
    return $quote;
}

/* ============================================================================
 * SHIPLOGIC HTTP CLIENT
 * ========================================================================== */

function dcp_shiplogic_fetch_rates( $bearer, $payload ) {
    $url     = DCP_SHIPLOGIC_BASE_URL . DCP_SHIPLOGIC_RATES_PATH;
    $headers = array(
        'Content-Type'  => 'application/json',
        'Accept'        => 'application/json',
        'Authorization' => 'Bearer ' . $bearer,
    );
    $body_str = wp_json_encode( $payload );
    $started  = microtime( true );
    $response = wp_remote_post( $url, array(
        'timeout' => 30,
        'headers' => $headers,
        'body'    => $body_str,
    ) );
    $duration = (int) ( ( microtime( true ) - $started ) * 1000 );

    if ( is_wp_error( $response ) ) {
        dcp_log_http( 'shiplogic.rates.error', 'Transport error calling Shiplogic', 'error', array(
            'method' => 'POST', 'url' => $url, 'request_headers' => $headers,
            'request_body' => $body_str, 'duration_ms' => $duration,
            'error' => $response->get_error_message(),
        ) );
        return $response;
    }
    $code        = wp_remote_retrieve_response_code( $response );
    $body        = wp_remote_retrieve_body( $response );
    $resp_hdrs   = wp_remote_retrieve_headers( $response );
    $hdrs_array  = ( is_object( $resp_hdrs ) && method_exists( $resp_hdrs, 'getAll' ) ) ? $resp_hdrs->getAll() : (array) $resp_hdrs;

    $level = ( $code >= 200 && $code < 300 ) ? 'info' : 'error';
    dcp_log_http( 'shiplogic.rates', 'Direct Shiplogic call', $level, array(
        'method' => 'POST', 'url' => $url, 'request_headers' => $headers,
        'request_body' => $body_str, 'response_status' => $code,
        'response_headers' => $hdrs_array, 'response_body' => $body,
        'duration_ms' => $duration,
    ) );

    if ( $code < 200 || $code >= 300 ) {
        return new WP_Error( 'shiplogic_http_' . $code, 'Shiplogic returned HTTP ' . $code . ': ' . substr( $body, 0, 500 ) );
    }
    $data = json_decode( $body, true );
    if ( ! is_array( $data ) || empty( $data['rates'] ) || ! is_array( $data['rates'] ) ) {
        return array();
    }
    return $data['rates'];
}

/**
 * Coerce a delivery-address array into the canonical snake_case shape used
 * by every internal consumer. Accepts either:
 *   - camelCase as built by DTSC's ajax-quote-v2 (streetAddress, localArea,
 *     contactName, contactPhone, contactEmail, …)
 *   - snake_case as built by DCP_Shipping_Method::calculate_shipping
 *     (street_address, local_area, contact.name, contact.mobile_number, …)
 *
 * Output shape (always snake_case):
 *   array(
 *     'company'         => string|null,
 *     'street_address'  => string,
 *     'local_area'      => string,
 *     'city'            => string,
 *     'zone'            => string,
 *     'code'            => string,
 *     'country'         => string,
 *     'lat'             => float|null,
 *     'lng'             => float|null,
 *     'contact'         => array( name, mobile_number, email ),
 *   )
 */
function dcp_normalize_delivery_address_shape( $in ) {
    if ( ! is_array( $in ) ) {
        return $in;
    }

    // Pull contact subfields from whichever shape was supplied. DTSC sends
    // them as flat camelCase siblings (contactName / contactPhone /
    // contactEmail). The DCP shipping method sends them nested inside a
    // 'contact' sub-array (name / mobile_number / email).
    $contact_in = isset( $in['contact'] ) && is_array( $in['contact'] ) ? $in['contact'] : array();
    $contact = array(
        'name'          => $contact_in['name']
            ?? $in['contactName']
            ?? '',
        'mobile_number' => $contact_in['mobile_number']
            ?? $contact_in['phone']
            ?? $in['contactPhone']
            ?? '',
        'email'         => $contact_in['email']
            ?? $in['contactEmail']
            ?? '',
    );

    return array(
        'company'        => $in['company'] ?? null,
        'street_address' => (string) ( $in['street_address'] ?? $in['streetAddress'] ?? '' ),
        'local_area'     => (string) ( $in['local_area']     ?? $in['localArea']     ?? '' ),
        'city'           => (string) ( $in['city']           ?? '' ),
        'zone'           => (string) ( $in['zone']           ?? '' ),
        'code'           => (string) ( $in['code']           ?? '' ),
        'country'        => (string) ( $in['country']        ?? 'ZA' ),
        'lat'            => isset( $in['lat'] ) && $in['lat'] !== '' ? floatval( $in['lat'] ) : null,
        'lng'            => isset( $in['lng'] ) && $in['lng'] !== '' ? floatval( $in['lng'] ) : null,
        'contact'        => $contact,
    );
}

/**
 * Coerce a parcels array into the canonical submitted_*_cm / submitted_weight_kg
 * shape that all internal consumers expect. Accepts either:
 *   - DTSC's camelCase (lengthCm, widthCm, heightCm, weightKg)
 *   - DCP's submitted_* (submitted_length_cm, submitted_width_cm, …)
 *
 * Defaults conservatively (20×15×10cm, 1kg) for any missing field so a
 * minimally-populated parcel still yields a valid Shiplogic request.
 */
function dcp_normalize_parcels_shape( $parcels ) {
    if ( ! is_array( $parcels ) ) {
        return array();
    }
    $out = array();
    foreach ( $parcels as $p ) {
        if ( ! is_array( $p ) ) { continue; }
        $out[] = array(
            'parcel_description'   => (string) ( $p['parcel_description'] ?? $p['description'] ?? 'Package' ),
            'submitted_length_cm'  => floatval( $p['submitted_length_cm'] ?? $p['lengthCm'] ?? 20 ),
            'submitted_width_cm'   => floatval( $p['submitted_width_cm']  ?? $p['widthCm']  ?? 15 ),
            'submitted_height_cm'  => floatval( $p['submitted_height_cm'] ?? $p['heightCm'] ?? 10 ),
            'submitted_weight_kg'  => floatval( $p['submitted_weight_kg'] ?? $p['weightKg'] ?? 1 ),
        );
    }
    return $out;
}

/**
 * PUBLIC: Fetch live courier rates with full fallback chain.
 *
 * Tier 1: Platform via api2.delicatecourier.co.za (default, server-side
 *         geocoded, uses the tenant's Shiplogic token from platform DB).
 *         If circuit breaker is OPEN (recent failures), skip directly to T2.
 *
 * Tier 2: Direct Shiplogic from this WordPress install. Requires merchant-side
 *         credentials (bearer, account, provider). Plugin geocodes the
 *         collection address itself via the geocoder block above so we don't
 *         re-hit the rates:null trap that previously plagued v2.1.x.
 *
 * Tier 3: Last-known-rate cache. 24-hour WP transient keyed on the full
 *         destination address. Used only when both T1 and T2 fail. Lets
 *         the customer still complete checkout during a sustained outage.
 *
 * Every successful T1 or T2 fetch writes into the last-known-rate cache
 * so T3 has data to fall back on.
 *
 * @param array $delivery Delivery address (the same shape build_delivery_address builds).
 * @param array $parcels  List of parcel arrays in the Shiplogic submitted_* shape.
 * @return array|WP_Error
 */
function dcp_platform_fetch_rates( $delivery, $parcels ) {
    $store_id = dcp_store_id();
    if ( $store_id === '' || ! is_numeric( $store_id ) ) {
        return new WP_Error( 'dcp_no_store_id', 'Plugin not configured: numeric Store ID is required.' );
    }

    // ── Input normalisation ──────────────────────────────────────────────
    // Historical mess: DTSC builds delivery addresses in camelCase
    // (streetAddress, contactName, …) and parcels with camelCase
    // dimensions (lengthCm). The DCP shipping-method class builds
    // snake_case throughout (street_address, contact.name, …) with
    // submitted_*_cm parcels. Both shapes have been calling this
    // orchestrator and going partially wrong in different ways.
    //
    // Normalise everything to the canonical snake_case shape used
    // throughout the rest of the plugin so all three tiers consume
    // the same data regardless of how it arrived.
    $delivery = dcp_normalize_delivery_address_shape( $delivery );
    $parcels  = dcp_normalize_parcels_shape( $parcels );

    // ── Tier 1: Platform (unless circuit breaker is open) ────────────────
    $platform_rates = null;
    if ( ! dcp_breaker_is_open() ) {
        $platform_rates = dcp_platform_fetch_rates_inner( $store_id, $delivery, $parcels );

        if ( ! is_wp_error( $platform_rates ) && is_array( $platform_rates ) && ! empty( $platform_rates ) ) {
            // Successful T1. Cache rates for last-known fallback (T3) and
            // reset any open circuit. Return immediately.
            dcp_rate_cache_store( $delivery, $parcels, $platform_rates, 'platform' );
            dcp_breaker_record_success();
            return $platform_rates;
        }
        // T1 either failed (WP_Error / empty rates / network error) — count
        // it against the circuit breaker. WP_Error and empty-rates both
        // qualify as "platform isn't giving us a rate."
        dcp_breaker_record_failure();
    } else {
        dcp_log( 'platform.rates.breaker_open',
            'Circuit breaker OPEN — skipping platform, going direct to Shiplogic.',
            'warning'
        );
    }

    // ── Tier 2: Direct Shiplogic ─────────────────────────────────────────
    $direct_rates = dcp_fetch_rates_direct_shiplogic( $delivery, $parcels );
    if ( ! is_wp_error( $direct_rates ) && is_array( $direct_rates ) && ! empty( $direct_rates ) ) {
        dcp_rate_cache_store( $delivery, $parcels, $direct_rates, 'shiplogic_direct' );
        dcp_log( 'platform.rates.tier2_ok',
            'Direct Shiplogic fallback returned rates.', 'warning'
        );
        return $direct_rates;
    }

    // ── Tier 3: Last-known-rate cache ────────────────────────────────────
    $cached = dcp_rate_cache_fetch( $delivery, $parcels );
    if ( is_array( $cached ) && ! empty( $cached['rates'] ) ) {
        dcp_log( 'platform.rates.tier3_ok',
            sprintf( 'Using last-known rate from %s, cached %d minutes ago.',
                $cached['source'],
                round( ( time() - $cached['cached_at'] ) / 60 )
            ),
            'warning'
        );
        return $cached['rates'];
    }

    // Total failure: return the most informative WP_Error we have.
    if ( is_wp_error( $platform_rates ) ) {
        return $platform_rates;
    }
    if ( is_wp_error( $direct_rates ) ) {
        return $direct_rates;
    }
    return new WP_Error( 'dcp_no_rates', 'No rates available from platform, direct Shiplogic, or cache.' );
}

/**
 * Inner platform-rates call. Was the public API in v2.4.x; renamed in v2.5.0
 * so it can be wrapped by the orchestrator above. Returns the same shape as
 * before (array of rate rows, or WP_Error).
 */
function dcp_platform_fetch_rates_inner( $store_id, $delivery, $parcels ) {

    // Map the legacy in-plugin shape onto the platform's request DTO.
    $platform_parcels = array();
    foreach ( $parcels as $p ) {
        $platform_parcels[] = array(
            'description' => (string) ( $p['parcel_description'] ?? 'Package' ),
            'lengthCm'    => floatval( $p['submitted_length_cm'] ?? 20 ),
            'widthCm'     => floatval( $p['submitted_width_cm']  ?? 15 ),
            'heightCm'    => floatval( $p['submitted_height_cm'] ?? 10 ),
            'weightKg'    => floatval( $p['submitted_weight_kg'] ?? 1 ),
        );
    }

    $contact  = isset( $delivery['contact'] ) && is_array( $delivery['contact'] ) ? $delivery['contact'] : array();
    $payload  = array(
        'storeId'         => intval( $store_id ),
        'deliveryAddress' => array(
            'company'        => $delivery['company']        ?? null,
            'streetAddress'  => (string) ( $delivery['street_address'] ?? '' ),
            'localArea'      => (string) ( $delivery['local_area']     ?? '' ),
            'city'           => (string) ( $delivery['city']           ?? '' ),
            'zone'           => (string) ( $delivery['zone']           ?? '' ),
            'country'        => (string) ( $delivery['country']        ?? 'ZA' ),
            'code'           => (string) ( $delivery['code']           ?? '' ),
            'contactName'    => (string) ( $contact['name']            ?? '' ),
            'contactPhone'   => (string) ( $contact['mobile_number']   ?? '' ),
            'contactEmail'   => (string) ( $contact['email']           ?? '' ),
        ),
        'parcels' => $platform_parcels,
    );

    $url      = rtrim( dcp_platform_base_url(), '/' ) . DCP_PLATFORM_RATES_PATH;
    $headers  = array(
        'Content-Type' => 'application/json',
        'Accept'       => 'application/json',
        'User-Agent'   => 'DelicateCourierPlatform/' . DCP_VERSION . ' WordPress/' . get_bloginfo( 'version' ),
    );
    $body_str = wp_json_encode( $payload );
    $started  = microtime( true );
    $response = wp_remote_post( $url, array(
        // Aggressive timeout — we WANT to fall through to the direct
        // Shiplogic path quickly during an outage. 8s is long enough for a
        // healthy platform to respond, short enough that customers don't
        // notice when we're falling back.
        'timeout' => 8,
        'headers' => $headers,
        'body'    => $body_str,
    ) );
    $duration = (int) ( ( microtime( true ) - $started ) * 1000 );

    if ( is_wp_error( $response ) ) {
        dcp_log_http( 'platform.rates.error', 'Transport error calling platform rates', 'error', array(
            'method' => 'POST', 'url' => $url, 'request_headers' => $headers,
            'request_body' => $body_str, 'duration_ms' => $duration,
            'error' => $response->get_error_message(),
        ) );
        return $response;
    }
    $code       = wp_remote_retrieve_response_code( $response );
    $body       = wp_remote_retrieve_body( $response );
    $resp_hdrs  = wp_remote_retrieve_headers( $response );
    $hdrs_array = ( is_object( $resp_hdrs ) && method_exists( $resp_hdrs, 'getAll' ) ) ? $resp_hdrs->getAll() : (array) $resp_hdrs;

    $level = ( $code >= 200 && $code < 300 ) ? 'info' : 'error';
    dcp_log_http( 'platform.rates', 'Platform live rates call', $level, array(
        'method' => 'POST', 'url' => $url, 'request_headers' => $headers,
        'request_body' => $body_str, 'response_status' => $code,
        'response_headers' => $hdrs_array, 'response_body' => $body,
        'duration_ms' => $duration,
    ) );

    if ( $code < 200 || $code >= 300 ) {
        return new WP_Error( 'platform_rates_http_' . $code, 'Platform returned HTTP ' . $code . ': ' . substr( $body, 0, 500 ) );
    }
    $data = json_decode( $body, true );
    if ( ! is_array( $data ) ) {
        return new WP_Error( 'platform_rates_bad_json', 'Platform returned non-JSON response.' );
    }
    if ( empty( $data['success'] ) ) {
        $msg = (string) ( $data['errorMessage'] ?? 'Platform reported failure.' );
        return new WP_Error( 'platform_rates_failed', $msg );
    }
    if ( empty( $data['rates'] ) || ! is_array( $data['rates'] ) ) {
        return array();
    }

    // Normalise into the legacy shape calculate_shipping() expects.
    $out = array();
    foreach ( $data['rates'] as $r ) {
        $out[] = array(
            'rate'          => floatval( $r['cost'] ?? 0 ),
            'currency'      => (string) ( $r['currency'] ?? 'ZAR' ),
            'service_level' => array(
                'id'   => $r['serviceLevelId']   ?? null,
                'code' => (string) ( $r['serviceLevelCode'] ?? '' ),
                'name' => (string) ( $r['serviceLevelName'] ?? ( $r['serviceLevelCode'] ?? '' ) ),
            ),
            'estimated_delivery_days' => $r['estimatedDeliveryDays'] ?? null,
        );
    }
    return $out;
}

/**
 * Tier 2: direct Shiplogic call when the platform is unreachable.
 * Uses merchant-stored Shiplogic credentials and locally-geocoded collection
 * coordinates. Returns the same normalised shape as the platform path.
 */
function dcp_fetch_rates_direct_shiplogic( $delivery, $parcels ) {
    $bearer   = dcp_shiplogic_bearer();
    $account  = dcp_shiplogic_account_id();
    $provider = dcp_shiplogic_provider_id();
    if ( $bearer === '' || $account === '' || $provider === '' ) {
        return new WP_Error( 'dcp_direct_no_creds',
            'Direct Shiplogic fallback skipped: merchant credentials not configured. ' .
            'Add Shiplogic Bearer Token, Account ID and Provider ID on the Delicate Courier settings page to enable fallback.'
        );
    }

    $collection = dcp_collection_address(); // already includes geocoded lat/lng
    if ( empty( $collection['lat'] ) || empty( $collection['lng'] ) ) {
        return new WP_Error( 'dcp_direct_no_collection_coords',
            'Direct Shiplogic fallback skipped: collection address could not be geocoded. ' .
            'Check the collection address on the Delicate Courier settings page.'
        );
    }

    // Construct the direct Shiplogic request payload. v2 of Shiplogic /v2/rates
    // expects collection/delivery as flat objects with lat/lng embedded.
    $payload = array(
        'account_id'  => intval( $account ),
        'provider_id' => intval( $provider ),
        'collection_address' => array(
            'type'           => 'business',
            'company'        => (string) ( $collection['company']        ?? '' ),
            'street_address' => (string) ( $collection['street_address'] ?? '' ),
            'local_area'     => (string) ( $collection['local_area']     ?? '' ),
            'city'           => (string) ( $collection['city']           ?? '' ),
            'zone'           => (string) ( $collection['zone']           ?? '' ),
            'code'           => (string) ( $collection['code']           ?? '' ),
            'country'        => (string) ( $collection['country']        ?? 'ZA' ),
            'lat'            => floatval( $collection['lat'] ),
            'lng'            => floatval( $collection['lng'] ),
        ),
        'delivery_address' => array(
            'type'           => 'residential',
            'street_address' => (string) ( $delivery['street_address'] ?? '' ),
            'local_area'     => (string) ( $delivery['local_area']     ?? '' ),
            'city'           => (string) ( $delivery['city']           ?? '' ),
            'zone'           => (string) ( $delivery['zone']           ?? '' ),
            'code'           => (string) ( $delivery['code']           ?? '' ),
            'country'        => (string) ( $delivery['country']        ?? 'ZA' ),
        ),
        'parcels' => $parcels, // already in Shiplogic submitted_* shape
    );

    $rates = dcp_shiplogic_fetch_rates( $bearer, $payload );
    if ( is_wp_error( $rates ) ) {
        return $rates;
    }
    if ( empty( $rates ) ) {
        return array();
    }

    // Shiplogic returns rates in the shape we already normalise to —
    // dcp_shiplogic_fetch_rates returns $data['rates'] directly. We
    // just need to ensure each row has the legacy keys our caller
    // expects. Shiplogic responses already match.
    return $rates;
}

/* ============================================================================
 * CIRCUIT BREAKER  (rates path)
 * ============================================================================
 * Goals:
 *   - During a healthy platform: every checkout hits the platform.
 *   - During a sustained outage: skip the platform attempt entirely so the
 *     customer doesn't wait 8 seconds per checkout for the timeout to fire.
 *
 * State stored in two transients:
 *   dcp_breaker_failures   integer count of recent consecutive failures
 *   dcp_breaker_open_until unix timestamp until which the breaker is open
 *
 * After 3 consecutive failures, the breaker opens for 60 seconds. Each
 * subsequent failure while the breaker is open extends it by 60 seconds
 * (capped at 10 minutes — long enough to be useful, short enough that we
 * recover quickly when the platform comes back).
 * ============================================================================ */

function dcp_breaker_is_open() {
    $open_until = (int) get_transient( 'dcp_breaker_open_until' );
    return $open_until > 0 && $open_until > time();
}

function dcp_breaker_record_failure() {
    $failures = (int) get_transient( 'dcp_breaker_failures' );
    $failures++;
    set_transient( 'dcp_breaker_failures', $failures, 5 * MINUTE_IN_SECONDS );

    if ( $failures >= 3 ) {
        $current_open_until = (int) get_transient( 'dcp_breaker_open_until' );
        $extension = MINUTE_IN_SECONDS;
        $new_open_until = max( time() + $extension, $current_open_until + $extension );
        $cap = time() + 10 * MINUTE_IN_SECONDS;
        $new_open_until = min( $new_open_until, $cap );

        set_transient( 'dcp_breaker_open_until', $new_open_until, 15 * MINUTE_IN_SECONDS );
        dcp_log( 'breaker.opened',
            sprintf( 'Circuit breaker OPEN after %d failures. Will retry platform at %s.',
                $failures, gmdate( 'H:i:s', $new_open_until ) ),
            'warning'
        );
    }
}

function dcp_breaker_record_success() {
    delete_transient( 'dcp_breaker_failures' );
    if ( dcp_breaker_is_open() ) {
        dcp_log( 'breaker.closed', 'Platform succeeded — circuit breaker CLOSED.' );
    }
    delete_transient( 'dcp_breaker_open_until' );
}

/* ============================================================================
 * LAST-KNOWN-RATE CACHE
 * ============================================================================
 * 24-hour WP transient keyed on (delivery address + parcels signature). Every
 * successful platform or direct-Shiplogic call writes here; total-failure path
 * reads from here. This is the safety net that keeps checkout working during
 * a multi-hour outage when both platform AND direct Shiplogic are unreachable.
 *
 * NOT used as a regular cache: the regular sessionStorage cache in DTSC and
 * the WC transient in this plugin are the fast paths. This is purely the
 * disaster-recovery fallback.
 * ============================================================================ */

function dcp_rate_cache_key( $delivery, $parcels ) {
    $signature = array(
        'street'   => $delivery['street_address'] ?? '',
        'city'     => $delivery['city']           ?? '',
        'code'     => $delivery['code']           ?? '',
        'country'  => $delivery['country']        ?? 'ZA',
        'parcels'  => count( $parcels ),
        // Coarse weight bucket: rates rarely differ within 0.5kg granularity,
        // so we bucket to avoid cache misses on small weight changes.
        'weight'   => round( array_sum( array_map( function ( $p ) {
            return floatval( $p['submitted_weight_kg'] ?? 1 );
        }, $parcels ) ) * 2 ) / 2,
    );
    return 'dcp_lkr_' . md5( wp_json_encode( $signature ) );
}

function dcp_rate_cache_store( $delivery, $parcels, $rates, $source ) {
    if ( empty( $rates ) ) return;
    set_transient(
        dcp_rate_cache_key( $delivery, $parcels ),
        array(
            'rates'      => $rates,
            'source'     => $source, // 'platform' | 'shiplogic_direct'
            'cached_at'  => time(),
        ),
        DAY_IN_SECONDS
    );
}

function dcp_rate_cache_fetch( $delivery, $parcels ) {
    $row = get_transient( dcp_rate_cache_key( $delivery, $parcels ) );
    return is_array( $row ) ? $row : null;
}

/* ============================================================================
 * OUTBOX — Reliable order-push retry
 * ============================================================================
 * Custom MySQL table that persists order-push payloads when the platform is
 * unreachable, so a 5-minute cron can retry them until they succeed (or
 * exhaust the backoff schedule).
 *
 * Why a custom table rather than wp_options or post_meta:
 *   - We need to query by `next_attempt_at <= NOW()` to find rows ready
 *     for retry. Options and post_meta don't index that efficiently.
 *   - We expect dozens to hundreds of rows during a multi-hour outage,
 *     and a dedicated table keeps the rest of WP fast.
 *   - Atomic UPDATE of attempt_count + next_attempt_at avoids race
 *     conditions when the cron and a real-time push fire on the same row.
 *
 * Backoff schedule (chosen for ~8 days total of attempts before giving up):
 *   attempts  1-12  → every 5 min   (first hour, fast recovery)
 *   attempts 13-60  → every 30 min  (next 24h, sustained outage)
 *   attempts 61-88  → every 6 hr    (next week, slow drip)
 *   attempts >= 89  → status = 'manual', admin notice raised
 *
 * Re-signing on retry: we deliberately do NOT store the original payload+sig.
 * Instead we re-fetch the WC order at retry time and rebuild the payload
 * from scratch. This handles webhook_secret rotation cleanly (old queued
 * orders automatically sign with the new secret).
 * ============================================================================ */

function dcp_outbox_table_name() {
    global $wpdb;
    return $wpdb->prefix . 'dcp_outbox';
}

/**
 * Install or upgrade the outbox table. Idempotent — safe to call on every
 * activation. Uses dbDelta() so schema migrations are handled by WP.
 */
function dcp_outbox_install() {
    global $wpdb;
    require_once ABSPATH . 'wp-admin/includes/upgrade.php';

    $table = dcp_outbox_table_name();
    $charset_collate = $wpdb->get_charset_collate();

    $sql = "CREATE TABLE $table (
        id BIGINT(20) UNSIGNED NOT NULL AUTO_INCREMENT,
        woo_order_id BIGINT(20) UNSIGNED NOT NULL,
        event_type VARCHAR(50) NOT NULL DEFAULT 'order.created',
        status VARCHAR(20) NOT NULL DEFAULT 'pending',
        attempt_count INT(11) NOT NULL DEFAULT 0,
        last_error TEXT NULL,
        last_http_status INT(11) NULL,
        created_at DATETIME NOT NULL,
        next_attempt_at DATETIME NOT NULL,
        last_attempt_at DATETIME NULL,
        synced_at DATETIME NULL,
        PRIMARY KEY  (id),
        UNIQUE KEY woo_order_event (woo_order_id, event_type),
        KEY status_next (status, next_attempt_at),
        KEY woo_order (woo_order_id)
    ) $charset_collate;";

    dbDelta( $sql );
}

/**
 * Enqueue a failed order push for retry. Called from dcp_platform_post_signed()
 * when all immediate retry attempts (3 attempts inside the original push)
 * have failed.
 *
 * If a row already exists for this (woo_order_id, event_type), we update its
 * last_error rather than creating a duplicate — the cron will pick it up at
 * its scheduled time. This is the unique-key behaviour the schema enforces.
 */
function dcp_outbox_enqueue( $woo_order_id, $event_type, $error_message, $http_status = null ) {
    global $wpdb;
    $table = dcp_outbox_table_name();
    $now = current_time( 'mysql', true ); // UTC

    // 5 minutes from now — same as initial backoff tier.
    $next_attempt = gmdate( 'Y-m-d H:i:s', time() + 5 * MINUTE_IN_SECONDS );

    // INSERT … ON DUPLICATE KEY UPDATE — atomic upsert that handles the
    // case where the same order failed multiple times in a row.
    $sql = $wpdb->prepare(
        "INSERT INTO $table (woo_order_id, event_type, status, attempt_count,
            last_error, last_http_status, created_at, next_attempt_at)
         VALUES (%d, %s, 'pending', 0, %s, %s, %s, %s)
         ON DUPLICATE KEY UPDATE
            last_error = VALUES(last_error),
            last_http_status = VALUES(last_http_status),
            status = IF(status = 'manual', status, 'pending')",
        $woo_order_id,
        $event_type,
        substr( (string) $error_message, 0, 1000 ),
        $http_status,
        $now,
        $next_attempt
    );
    $wpdb->query( $sql );

    dcp_log( 'outbox.enqueued',
        sprintf( 'Order %d (%s) queued for retry. Error: %s',
            $woo_order_id, $event_type, $error_message
        ),
        'warning'
    );
}

/**
 * Mark an outbox row as successfully synced. Called when a retry POST returns
 * 2xx. We don't delete the row — keeping it lets the admin UI show recent
 * sync history. A separate sweep can prune very old synced rows.
 */
function dcp_outbox_mark_synced( $id ) {
    global $wpdb;
    $wpdb->update(
        dcp_outbox_table_name(),
        array(
            'status'    => 'synced',
            'synced_at' => current_time( 'mysql', true ),
        ),
        array( 'id' => $id ),
        array( '%s', '%s' ),
        array( '%d' )
    );
}

/**
 * Cron handler: find pending outbox rows whose next_attempt_at <= now,
 * retry each, update bookkeeping. Bounded per invocation to avoid runaway
 * cron jobs during huge outage recoveries — we process at most 25 rows
 * per cron tick, so a 100-order backlog drains over four ticks (~20 min).
 */
add_action( 'dcp_outbox_process', 'dcp_outbox_process' );
function dcp_outbox_process() {
    global $wpdb;
    $table = dcp_outbox_table_name();
    $now = current_time( 'mysql', true );

    $rows = $wpdb->get_results( $wpdb->prepare(
        "SELECT * FROM $table
         WHERE status = 'pending' AND next_attempt_at <= %s
         ORDER BY next_attempt_at ASC
         LIMIT 25",
        $now
    ) );

    if ( empty( $rows ) ) {
        return;
    }

    dcp_log( 'outbox.process.batch_start',
        sprintf( 'Processing %d outbox rows.', count( $rows ) )
    );

    foreach ( $rows as $row ) {
        $order = wc_get_order( $row->woo_order_id );
        if ( ! $order ) {
            // Order has been deleted in WooCommerce — there's nothing to
            // retry. Mark the row so we stop trying.
            $wpdb->update( $table,
                array(
                    'status'     => 'orphaned',
                    'last_error' => 'WooCommerce order no longer exists.',
                    'last_attempt_at' => current_time( 'mysql', true ),
                ),
                array( 'id' => $row->id )
            );
            dcp_log( 'outbox.orphaned',
                sprintf( 'Order %d no longer exists in WC. Marked orphaned.', $row->woo_order_id ),
                'warning'
            );
            continue;
        }

        // Re-build the payload from the current WC order state. This is
        // intentional — if the merchant edited the order between failure
        // and retry, we want to push the latest state.
        $payload = dcp_build_order_payload( $order );

        // Mark as in-flight before posting so a parallel cron doesn't pick it up.
        $wpdb->update( $table,
            array( 'last_attempt_at' => current_time( 'mysql', true ) ),
            array( 'id' => $row->id )
        );

        $attempt_count_after = ( (int) $row->attempt_count ) + 1;

        // Call the actual signed push. This function returns a structured
        // result so we know whether to count this as success or failure.
        // Important: we pass a flag that suppresses re-enqueuing on
        // failure — otherwise we'd recursively enqueue ourselves.
        $result = dcp_platform_post_signed( DCP_PLATFORM_ORDER_PATH, $payload, array(
            'from_outbox' => true,
        ) );

        if ( is_array( $result ) && ! empty( $result['success'] ) ) {
            dcp_outbox_mark_synced( $row->id );
            dcp_log( 'outbox.retry.success',
                sprintf( 'Order %d synced after %d attempt(s).', $row->woo_order_id, $attempt_count_after )
            );
            continue;
        }

        // Still failing. Compute the next attempt time using the backoff schedule.
        $next_delay_seconds = dcp_outbox_next_delay_seconds( $attempt_count_after );
        $next_status        = $attempt_count_after >= 89 ? 'manual' : 'pending';

        $wpdb->update( $table,
            array(
                'attempt_count'    => $attempt_count_after,
                'next_attempt_at'  => gmdate( 'Y-m-d H:i:s', time() + $next_delay_seconds ),
                'last_error'       => substr( (string) ( $result['body']['message'] ?? $result['raw'] ?? 'Unknown error' ), 0, 1000 ),
                'last_http_status' => isset( $result['status'] ) ? (int) $result['status'] : null,
                'status'           => $next_status,
            ),
            array( 'id' => $row->id )
        );

        if ( $next_status === 'manual' ) {
            dcp_log( 'outbox.give_up',
                sprintf( 'Order %d failed after 88 attempts. Manual intervention required.', $row->woo_order_id ),
                'error'
            );
        }
    }
}

/**
 * Backoff schedule. Maps attempt number → seconds until next retry.
 */
function dcp_outbox_next_delay_seconds( $attempt ) {
    if ( $attempt < 12 )  return 5  * MINUTE_IN_SECONDS;   // first hour
    if ( $attempt < 60 )  return 30 * MINUTE_IN_SECONDS;   // next 24 hours
    return 6 * HOUR_IN_SECONDS;                            // after that, slow drip
}

/**
 * Admin notice when there are >10 pending outbox rows. The whole point of
 * the outbox is silent recovery, but if it gets backlogged the operator
 * needs to know. Without this notice, the outbox would silently grow
 * forever — the worst failure mode for any queue.
 */
add_action( 'admin_notices', 'dcp_outbox_admin_notice' );
function dcp_outbox_admin_notice() {
    if ( ! current_user_can( 'manage_woocommerce' ) ) return;
    global $wpdb;
    $count = (int) $wpdb->get_var(
        "SELECT COUNT(*) FROM " . dcp_outbox_table_name() . " WHERE status IN ('pending','manual')"
    );
    if ( $count < 10 ) return;
    $url = admin_url( 'admin.php?page=dcp-settings#dcp-outbox' );
    echo '<div class="notice notice-warning"><p><strong>Delicate Courier:</strong> '
        . esc_html( sprintf( '%d order(s) are waiting to sync to the platform. ', $count ) )
        . '<a href="' . esc_url( $url ) . '">View outbox →</a></p></div>';
}

/* ============================================================================
 * PLATFORM HTTP CLIENT (signed)
 * ========================================================================== */

/**
 * POST a JSON body to the platform with HMAC-SHA256(rawBody, webhook_secret)
 * encoded as base64 in X-Plugin-Signature, and the Store ID in X-Store-ID.
 *
 * @return array { success: bool, status: int, body: array|string, raw: string }
 */
function dcp_platform_post_signed( $path, $payload, $options = array() ) {
    $store_id = dcp_store_id();
    $secret   = dcp_webhook_secret();

    $from_outbox = ! empty( $options['from_outbox'] );

    if ( $store_id === '' || $secret === '' ) {
        return array(
            'success' => false,
            'status'  => 0,
            'body'    => array( 'message' => 'Plugin not configured: Store ID and Webhook Secret are required.' ),
            'raw'     => '',
        );
    }

    $body = wp_json_encode( $payload );
    $url  = rtrim( dcp_platform_base_url(), '/' ) . $path;

    // We sign LATE, inside the http_request_args filter, so that the bytes
    // we hash are the same bytes that actually leave WordPress. If another
    // plugin / mu-plugin / security filter mutates args['body'] after we
    // build $args, our signature would otherwise be computed over stale
    // bytes and the backend would reject every request with "Invalid
    // signature" (which is exactly the bug we hit on Honey Bee staging).
    //
    // The filter uses a per-request marker header to identify our own
    // outgoing request and avoid touching anything else. After the call
    // we unhook it.
    $marker = 'dcp-' . wp_generate_password( 16, false, false );
    $sign_state = array(
        'secret'      => $secret,
        'marker'      => $marker,
        'final_sig'   => '',
        'final_body'  => '',
    );

    $signer = function( $args ) use ( &$sign_state ) {
        if ( ! is_array( $args ) || empty( $args['headers'] ) || ! is_array( $args['headers'] ) ) {
            return $args;
        }
        // Case-insensitive lookup: another filter may have normalised header
        // keys to lowercase before us. Find the marker and signature keys by
        // matching on lowercase name, then mutate the original keys in place.
        $marker_key = '';
        $sig_key    = '';
        foreach ( array_keys( $args['headers'] ) as $h ) {
            $lh = strtolower( (string) $h );
            if ( $lh === 'x-dcp-request-marker' ) { $marker_key = $h; }
            if ( $lh === 'x-plugin-signature' )   { $sig_key    = $h; }
        }
        if ( $marker_key === '' || (string) $args['headers'][ $marker_key ] !== $sign_state['marker'] ) {
            return $args;
        }
        $final_body = isset( $args['body'] ) ? (string) $args['body'] : '';
        $sig        = base64_encode( hash_hmac( 'sha256', $final_body, $sign_state['secret'], true ) );
        if ( $sig_key === '' ) { $sig_key = 'X-Plugin-Signature'; }
        $args['headers'][ $sig_key ] = $sig;
        unset( $args['headers'][ $marker_key ] );
        $sign_state['final_sig']  = $sig;
        $sign_state['final_body'] = $final_body;
        return $args;
    };
    add_filter( 'http_request_args', $signer, PHP_INT_MAX );

    $args = array(
        'timeout' => 30,
        'headers' => array(
            'Content-Type'         => 'application/json',
            'Accept'               => 'application/json',
            'X-Store-ID'           => $store_id,
            'X-Plugin-Signature'   => '',      // placeholder, replaced by filter
            'X-DCP-Request-Marker' => $marker, // stripped by filter
            'User-Agent'           => 'DelicateCourierPlatform/' . DCP_VERSION . ' WordPress/' . get_bloginfo( 'version' ),
        ),
        'body' => $body,
    );

    // Bounded retry with exponential backoff. Retry only on transport errors
    // and 5xx responses — never on 4xx (those mean our request is bad and a
    // retry will fail identically).
    $max_attempts = intval( apply_filters( 'dcp_platform_post_max_attempts', 3 ) );
    $last_error   = '';
    $last_status  = 0;
    $last_raw     = '';

    $result = null;
    try {
        for ( $attempt = 1; $attempt <= $max_attempts; $attempt++ ) {
            $started  = microtime( true );
            $response = wp_remote_post( $url, $args );
            $duration = (int) ( ( microtime( true ) - $started ) * 1000 );

            // Diagnostic: log the EXACT body and signature the http_request_args
            // filter ended up sending (after any other plugin/mu-plugin filter
            // ran), plus the secret fingerprint. Backend logs the same SHAs on
            // a signature rejection so the two can be compared byte-for-byte.
            $sent_body = (string) $sign_state['final_body'];
            $sent_sig  = (string) $sign_state['final_sig'];
            dcp_log( 'platform.sign', sprintf(
                'BodyLen=%d BodySha256=%s SecretLen=%d SecretSha256=%s Sig=%s',
                strlen( $sent_body ),
                hash( 'sha256', $sent_body ),
                strlen( $secret ),
                hash( 'sha256', $secret ),
                $sent_sig
            ), 'info' );

            if ( is_wp_error( $response ) ) {
                $last_error  = $response->get_error_message();
                $last_status = 0;
                $last_raw    = '';
                dcp_log_http( 'platform.order.error', 'Attempt ' . $attempt . '/' . $max_attempts . ' transport error: ' . $last_error, 'warning', array(
                    'method' => 'POST', 'url' => $url, 'request_headers' => $args['headers'],
                    'request_body' => $body, 'duration_ms' => $duration, 'error' => $last_error,
                ) );
            } else {
                $status     = wp_remote_retrieve_response_code( $response );
                $raw        = wp_remote_retrieve_body( $response );
                $parsed     = json_decode( $raw, true );
                $resp_hdrs  = wp_remote_retrieve_headers( $response );
                $hdrs_array = ( is_object( $resp_hdrs ) && method_exists( $resp_hdrs, 'getAll' ) ) ? $resp_hdrs->getAll() : (array) $resp_hdrs;
                $http_level = ( $status >= 200 && $status < 300 ) ? 'info' : ( $status >= 500 ? 'warning' : 'error' );
                dcp_log_http( 'platform.order', 'Signed order push (attempt ' . $attempt . '/' . $max_attempts . ')', $http_level, array(
                    'method' => 'POST', 'url' => $url, 'request_headers' => $args['headers'],
                    'request_body' => $body, 'response_status' => $status,
                    'response_headers' => $hdrs_array, 'response_body' => $raw,
                    'duration_ms' => $duration,
                ) );

                // Success or non-retryable failure — stop retrying.
                if ( $status < 500 ) {
                    $result = array(
                        'success' => ( $status >= 200 && $status < 300 ),
                        'status'  => $status,
                        'body'    => is_array( $parsed ) ? $parsed : $raw,
                        'raw'     => $raw,
                    );
                    break;
                }
                $last_error  = 'HTTP ' . $status;
                $last_status = $status;
                $last_raw    = $raw;
                dcp_log( 'platform.retry', 'Attempt ' . $attempt . '/' . $max_attempts . ' got ' . $last_error, 'warning' );
            }

            if ( $attempt < $max_attempts ) {
                // 1s, 2s, 4s... — small enough not to hang admin UX.
                sleep( min( 4, pow( 2, $attempt - 1 ) ) );
            }
        }
    } finally {
        remove_filter( 'http_request_args', $signer, PHP_INT_MAX );
    }

    if ( $result !== null ) {
        return $result;
    }

    // All immediate attempts failed. Enqueue for cron-based retry — unless
    // we ARE the cron-based retry (from_outbox), in which case the caller
    // (dcp_outbox_process) handles bookkeeping.
    if ( ! $from_outbox && is_array( $payload ) && ! empty( $payload['woo_order_id'] ) ) {
        $event_type = (string) ( $payload['event'] ?? 'order.created' );
        dcp_outbox_enqueue(
            (int) $payload['woo_order_id'],
            $event_type,
            $last_error ?: 'Platform unreachable',
            $last_status
        );
    }

    return array(
        'success' => false,
        'status'  => $last_status,
        'body'    => array( 'message' => 'Platform push failed after ' . $max_attempts . ' attempts: ' . $last_error ),
        'raw'     => $last_raw,
    );
}

/**
 * Best-effort wipe of cached Shiplogic rate transients. Called when the
 * settings page saves so a new token / endpoint takes effect immediately.
 */
function dcp_clear_rate_cache() {
    global $wpdb;
    $wpdb->query( "DELETE FROM {$wpdb->options} WHERE option_name LIKE '_transient_dcp_rate_%' OR option_name LIKE '_transient_timeout_dcp_rate_%'" );
}

function dcp_platform_get( $path ) {
    $url      = rtrim( dcp_platform_base_url(), '/' ) . $path;
    $headers  = array( 'Accept' => 'application/json' );
    $started  = microtime( true );
    $response = wp_remote_get( $url, array(
        'timeout' => 15,
        'headers' => $headers,
    ) );
    $duration = (int) ( ( microtime( true ) - $started ) * 1000 );
    if ( is_wp_error( $response ) ) {
        dcp_log_http( 'platform.get.error', 'Transport error on GET ' . $path, 'error', array(
            'method' => 'GET', 'url' => $url, 'request_headers' => $headers,
            'duration_ms' => $duration, 'error' => $response->get_error_message(),
        ) );
        return array( 'success' => false, 'status' => 0, 'body' => array( 'message' => $response->get_error_message() ) );
    }
    $status     = wp_remote_retrieve_response_code( $response );
    $raw        = wp_remote_retrieve_body( $response );
    $parsed     = json_decode( $raw, true );
    $resp_hdrs  = wp_remote_retrieve_headers( $response );
    $hdrs_array = ( is_object( $resp_hdrs ) && method_exists( $resp_hdrs, 'getAll' ) ) ? $resp_hdrs->getAll() : (array) $resp_hdrs;
    $http_level = ( $status >= 200 && $status < 300 ) ? 'info' : 'error';
    dcp_log_http( 'platform.get', 'GET ' . $path, $http_level, array(
        'method' => 'GET', 'url' => $url, 'request_headers' => $headers,
        'response_status' => $status, 'response_headers' => $hdrs_array,
        'response_body' => $raw, 'duration_ms' => $duration,
    ) );
    return array(
        'success' => ( $status >= 200 && $status < 300 ),
        'status'  => $status,
        'body'    => is_array( $parsed ) ? $parsed : $raw,
    );
}

/* ============================================================================
 * ORDER SYNC — push to platform
 * ========================================================================== */

function dcp_sync_order( $order_id ) {
    $order = wc_get_order( $order_id );
    if ( ! $order ) {
        return;
    }
    if ( ! dcp_auto_sync_enabled() ) {
        return;
    }
    $is_resync = (bool) $order->get_meta( '_dcp_platform_order_id' );
    if ( ! dcp_is_configured() ) {
        if ( ! $is_resync ) {
            $order->add_order_note( 'Delicate Courier: order not synced — plugin missing Store ID or Webhook Secret.' );
        }
        return;
    }

    $payload = dcp_build_order_payload( $order );
    $resp    = dcp_platform_post_signed( DCP_PLATFORM_ORDER_PATH, $payload );

    if ( $resp['success'] ) {
        $platform_id = is_array( $resp['body'] ) ? ( $resp['body']['orderId'] ?? '' ) : '';
        $order_num   = is_array( $resp['body'] ) ? ( $resp['body']['orderNumber'] ?? '' ) : '';
        if ( $platform_id !== '' ) {
            $order->update_meta_data( '_dcp_platform_order_id', $platform_id );
        }
        $order->update_meta_data( '_dcp_synced_at', current_time( 'mysql' ) );
        $order->save();
        if ( $is_resync ) {
            dcp_log( 'order.resynced', 'Order ' . $order_id . ' status update re-synced to platform' );
        } else {
            $order->add_order_note( sprintf( 'Delicate Courier: order synced to platform (ID %s).', $platform_id ) );
            dcp_log( 'order.synced', 'Order ' . $order_id . ' -> platform ' . $platform_id );
        }
    } else {
        $msg = is_array( $resp['body'] ) ? ( $resp['body']['message'] ?? 'Unknown error' ) : (string) $resp['body'];
        if ( ! $is_resync ) {
            $order->add_order_note( 'Delicate Courier: sync failed (HTTP ' . $resp['status'] . ') — ' . $msg );
        }
        dcp_log( 'order.failed', 'Order ' . $order_id . ' status=' . $resp['status'] . ' msg=' . $msg, 'error' );
    }
}

function dcp_build_order_payload( $order ) {
    $shipping = $order->get_address( 'shipping' );
    $billing  = $order->get_address( 'billing' );

    $street = trim( ( $shipping['address_1'] ?? '' ) . ' ' . ( $shipping['address_2'] ?? '' ) );
    if ( $street === '' ) {
        $street = trim( ( $billing['address_1'] ?? '' ) . ' ' . ( $billing['address_2'] ?? '' ) );
    }
    // ZA convention: WooCommerce has no native suburb field, merchants put
    // it in address_2. We still concatenate address_2 into $street above for
    // backward compatibility (older platform builds expect it there), and
    // ALSO send it as a dedicated `suburb` so Shiplogic can populate
    // `local_area` accurately.
    $suburb = trim( (string) ( $shipping['address_2'] ?? '' ) );
    if ( $suburb === '' ) {
        $suburb = trim( (string) ( $billing['address_2'] ?? '' ) );
    }
    $city     = $shipping['city']     ?: ( $billing['city']     ?? '' );
    $postcode = $shipping['postcode'] ?: ( $billing['postcode'] ?? '' );
    $state    = $shipping['state']    ?: ( $billing['state']    ?? '' );
    $country  = $shipping['country']  ?: ( $billing['country']  ?? 'ZA' );

    $name = trim( ( $shipping['first_name'] ?? '' ) . ' ' . ( $shipping['last_name'] ?? '' ) );
    if ( $name === '' ) {
        $name = trim( ( $billing['first_name'] ?? '' ) . ' ' . ( $billing['last_name'] ?? '' ) );
    }

    // ---------------------------------------------------------------
    // PRIMARY DELIVERY PHONE.
    //
    // Try, in order:
    //   1. shipping_phone — set by DTSC for "sending to someone else"
    //      orders, or by other plugins / merchants who collect it.
    //   2. _dtsc_recipient_phone meta — DTSC always saves this for
    //      "someone else" orders, even when set_shipping_phone() isn't
    //      available (older WC versions don't have it).
    //   3. billing_phone — the buyer's own number, used when the buyer
    //      is the recipient (self_receiving) or when no recipient was
    //      collected.
    //
    // The previous implementation used PHP's null-coalescing operator
    // (??), which does NOT fall through on an empty string. That left
    // the payload with phone="" whenever shipping_phone was set but
    // empty (the common case for self-receiving DTSC orders), and the
    // platform substituted a "+27 00 000 0000" placeholder downstream.
    // Switching to !empty() makes the chain behave as intended.
    // ---------------------------------------------------------------
    $shipping_phone       = isset( $shipping['phone'] ) ? (string) $shipping['phone'] : '';
    $dtsc_recipient_phone = (string) $order->get_meta( '_dtsc_recipient_phone' );
    $billing_phone        = isset( $billing['phone'] ) ? (string) $billing['phone'] : '';

    if ( ! empty( $shipping_phone ) ) {
        $phone = $shipping_phone;
    } elseif ( ! empty( $dtsc_recipient_phone ) ) {
        $phone = $dtsc_recipient_phone;
    } elseif ( ! empty( $billing_phone ) ) {
        $phone = $billing_phone;
    } else {
        $phone = '';
    }

    // ---------------------------------------------------------------
    // ALTERNATIVE DELIVERY PHONE.
    //
    // Backup contact the courier can try if the primary phone is
    // unreachable. Two distinct cases:
    //
    //   A. self_receiving=yes — customer is the recipient. They were
    //      shown an "Alternative phone for delivery" field on the
    //      checkout and what they entered lives in _dtsc_delivery_phone.
    //
    //   B. self_receiving=no — gift / send-to-someone-else order. The
    //      recipient's number is the primary; the buyer's own number
    //      (billing_phone) is the natural backup.
    //
    // For non-DTSC orders neither meta is present and we leave the
    // alt empty rather than guessing.
    // ---------------------------------------------------------------
    $self_receiving      = (string) $order->get_meta( '_dtsc_self_receiving' );
    $dtsc_delivery_phone = (string) $order->get_meta( '_dtsc_delivery_phone' );

    if ( ! empty( $dtsc_delivery_phone ) ) {
        // Explicit alt the customer entered — always preferred when present.
        $alternative_phone = $dtsc_delivery_phone;
    } elseif ( $self_receiving === 'no' && ! empty( $billing_phone ) && $billing_phone !== $phone ) {
        // Send-to-someone-else: buyer is the secondary contact. Guard
        // against the degenerate case where billing_phone is already
        // the primary (would make the alt redundant).
        $alternative_phone = $billing_phone;
    } else {
        $alternative_phone = '';
    }

    $email = $order->get_billing_email();

    $line_items   = array();
    $total_weight = 0;
    foreach ( $order->get_items() as $item ) {
        /** @var WC_Order_Item_Product $item */
        $product = $item->get_product();
        $weight  = $product ? floatval( $product->get_weight() ) : 1;
        if ( $weight <= 0 ) { $weight = 1; }
        $qty = $item->get_quantity();

        $line_items[] = array(
            'id'         => $item->get_id(),
            'product_id' => $item->get_product_id(),
            'name'       => $item->get_name(),
            'quantity'   => $qty,
            'price'      => floatval( $item->get_total() ),
            'sku'        => $product ? (string) $product->get_sku() : '',
        );
        $total_weight += $weight * $qty;
    }

    // Optional delivery/collection date+time and occasion: support the most
    // common merchant plugins. All optional — payload accepts nulls.
    list( $delivery_date, $delivery_time, $occasion ) = dcp_extract_delivery_meta( $order );

    $collection_date = $delivery_date;
    $collection_time = null;
    if ( ! empty( $delivery_time ) ) {
        $offset = dcp_collection_offset_minutes();
        $collection_time = dcp_apply_minutes_offset( $delivery_time, -$offset );
    }

    $fulfillment_type = dcp_extract_fulfillment_type( $order );

    // v2.7.0 — additive `special_trip` block, only when the customer chose
    // the Special Trip Request fallback rate at checkout.
    $special_trip = null;
    if ( $fulfillment_type === 'special_trip' ) {
        $special_trip = dcp_extract_special_trip( $order );
    }

    $payload = array(
        'event'           => 'order.created',
        'woo_order_id'    => $order->get_id(),
        'order_number'    => (string) $order->get_order_number(),
        'status'          => $order->get_status(),
        'total'           => floatval( $order->get_total() ),
        'shipping_total'  => floatval( $order->get_shipping_total() ),
        'currency'        => $order->get_currency(),
        'payment_method'  => $order->get_payment_method_title(),
        'date_created'    => $order->get_date_created() ? $order->get_date_created()->format( 'Y-m-d H:i:s' ) : '',
        'customer_note'   => $order->get_customer_note(),
        'customer'        => array(
            'name'              => $name,
            'email'             => $email,
            'phone'             => $phone,
            'alternative_phone' => $alternative_phone,
        ),
        'shipping_address' => array(
            'street'   => $street,
            'suburb'   => $suburb,
            'city'     => $city,
            'state'    => $state,
            'postcode' => $postcode,
            'country'  => $country ?: 'ZA',
        ),
        'line_items'      => $line_items,
        'total_weight'    => max( 1, $total_weight ),
        'store_url'       => home_url(),
        'store_id'        => dcp_store_id(),
        'delivery_date'   => $delivery_date,
        'delivery_time'   => $delivery_time,
        'collection_date' => $collection_date,
        'collection_time' => $collection_time,
        'occasion'        => $occasion,
        'fulfillment_type' => $fulfillment_type,
    );
    if ( $special_trip !== null ) {
        $payload['special_trip'] = $special_trip;
    }
    return $payload;
}

/**
 * Decide whether the order is a courier delivery or an in-person collect.
 *
 * Order of precedence:
 *   1. Explicit order meta `_dcp_fulfillment_type` — lets merchant code
 *      (custom checkout, third-party plugin) override the heuristic
 *      with a definitive 'collect' / 'delivery' / 'special_trip' value.
 *   2. Delicate Two-Step Checkout meta `_dtsc_delivery_method` — DTSC
 *      writes the customer's actual choice here. This is the path that
 *      matters for any DTSC merchant; it must be checked BEFORE shipping-
 *      method inspection because Collection orders in DTSC submit with no
 *      WC shipping method at all (the panel is hidden), which would
 *      otherwise default to 'delivery' and cause the platform to book a
 *      courier shipment for a customer who's collecting in person.
 *   3. Chosen WooCommerce shipping method id — `local_pickup`, or any
 *      method whose id contains 'pickup' or 'collect', is treated as
 *      collection. Anything else (flat_rate, dcp_shiplogic_rate,
 *      free_shipping, …) is a delivery.
 *   4. If the order has NO shipping method at all (digital-goods-only,
 *      pre-checkout draft, etc.) we default to 'delivery' to preserve
 *      the platform's pre-v2.4.2 behaviour — the platform-side guard
 *      will still skip Shiplogic booking only when this value is
 *      exactly 'collect'.
 *
 * Returns a string: 'collect' | 'delivery' | 'special_trip'.
 */
function dcp_extract_fulfillment_type( $order ) {
    $override = (string) $order->get_meta( '_dcp_fulfillment_type' );
    $override = strtolower( trim( $override ) );
    if ( in_array( $override, array( 'collect', 'delivery', 'special_trip' ), true ) ) {
        return $override;
    }

    // DTSC writes 'delivery', 'collect', 'collection', or 'special_trip' to
    // this meta key at order-save time (see class-order-meta.php in DTSC,
    // around line 131). Normalize 'collection' → 'collect' so downstream
    // platform code only has to recognise one canonical spelling.
    $dtsc = (string) $order->get_meta( '_dtsc_delivery_method' );
    $dtsc = strtolower( trim( $dtsc ) );
    if ( $dtsc === 'special_trip' ) {
        return 'special_trip';
    }
    if ( $dtsc === 'collect' || $dtsc === 'collection' ) {
        return 'collect';
    }
    if ( $dtsc === 'delivery' ) {
        return 'delivery';
    }

    foreach ( $order->get_shipping_methods() as $method ) {
        /** @var WC_Order_Item_Shipping $method */
        // v2.7.0: the Special Trip Request fallback rate id is
        // 'delicate_courier_platform_special_trip'. The rate id lives in
        // WC_Order_Item_Shipping's method_id+instance data; check both the
        // method id and the item meta flag set by the shipping method.
        if ( (string) $method->get_meta( 'dcp_special_trip', true ) === 'yes' ) {
            return 'special_trip';
        }
        $method_id = strtolower( (string) $method->get_method_id() );
        if ( strpos( $method_id, 'special_trip' ) !== false ) {
            return 'special_trip';
        }
        if ( $method_id === '' ) { continue; }
        if ( $method_id === 'local_pickup'
             || strpos( $method_id, 'pickup' )  !== false
             || strpos( $method_id, 'collect' ) !== false ) {
            return 'collect';
        }
    }

    return 'delivery';
}

/**
 * v2.7.0 — pull the special-trip quote details off the order's shipping
 * line item (WooCommerce copies the rate meta_data onto the
 * WC_Order_Item_Shipping when the customer selects the rate).
 *
 * Returns array{quoted_amount: float, distance_km: float,
 *               customer_lat: float|null, customer_lng: float|null} | null.
 */
function dcp_extract_special_trip( $order ) {
    foreach ( $order->get_shipping_methods() as $method ) {
        /** @var WC_Order_Item_Shipping $method */
        $flag      = (string) $method->get_meta( 'dcp_special_trip', true );
        $method_id = strtolower( (string) $method->get_method_id() );
        if ( $flag !== 'yes' && strpos( $method_id, 'special_trip' ) === false ) {
            continue;
        }
        $quote    = $method->get_meta( 'dcp_st_quote', true );
        $distance = $method->get_meta( 'dcp_st_distance_km', true );
        $lat      = $method->get_meta( 'dcp_st_lat', true );
        $lng      = $method->get_meta( 'dcp_st_lng', true );
        // Fall back to the actual charged shipping total if the quote meta
        // is missing (defensive — meta copying is WC-core behaviour, but a
        // checkout plugin recreating shipping items could drop it).
        $quoted_amount = ( $quote !== '' && $quote !== null ) ? floatval( $quote ) : floatval( $method->get_total() );
        return array(
            'quoted_amount' => $quoted_amount,
            'distance_km'   => ( $distance !== '' && $distance !== null ) ? floatval( $distance ) : 0.0,
            'customer_lat'  => ( $lat !== '' && $lat !== null ) ? floatval( $lat ) : null,
            'customer_lng'  => ( $lng !== '' && $lng !== null ) ? floatval( $lng ) : null,
        );
    }
    return null;
}

/**
 * Best-effort extraction of delivery date/time/occasion from common merchant
 * checkout plugins. Returns [ date YYYY-MM-DD or null, time HH:MM or null,
 * occasion string or null ].
 */
function dcp_extract_delivery_meta( $order ) {
    $date = null; $time = null; $occasion = null;

    // Delicate Two-Step Checkout (DTSC) — authoritative when present.
    $dtsc_date = $order->get_meta( '_dtsc_delivery_date' );
    $dtsc_time = $order->get_meta( '_dtsc_delivery_time' );
    if ( $dtsc_date ) { $date = $dtsc_date; }
    if ( $dtsc_time ) { $time = $dtsc_time; }

    // Fall back to WP Time Slots Booking and WAPF per line item.
    if ( ! $date || ! $time || ! $occasion ) {
        foreach ( $order->get_items() as $item ) {
            // WP Time Slots Booking
            $ts = $item->get_meta( 'woocommerce_wptimeslots_order_details', true );
            if ( is_array( $ts ) && isset( $ts['cff_params']['apps'][0] ) ) {
                if ( ! $date && ! empty( $ts['cff_params']['apps'][0]['date'] ) ) {
                    $date = $ts['cff_params']['apps'][0]['date'];
                }
                if ( ! $time && ! empty( $ts['cff_params']['apps'][0]['slot'] ) ) {
                    $time = $ts['cff_params']['apps'][0]['slot'];
                }
            }
            // WAPF (Advanced Product Fields)
            $wapf = $item->get_meta( '_wapf_meta', true );
            if ( is_array( $wapf ) && ! empty( $wapf['fields'] ) ) {
                foreach ( $wapf['fields'] as $f ) {
                    $label = strtolower( $f['label'] ?? '' );
                    $value = $f['value'] ?? '';
                    if ( ! $occasion && strpos( $label, 'occasion' ) !== false ) {
                        $occasion = $value;
                    }
                    if ( ! $date && ( strpos( $label, 'function date' ) !== false || strpos( $label, 'delivery date' ) !== false ) ) {
                        if ( preg_match( '/(\d{1,2})[\/\-](\d{1,2})(?:[\/\-](\d{2,4}))?/', (string) $value, $m ) ) {
                            $year = ! empty( $m[3] ) ? $m[3] : date( 'Y' );
                            if ( strlen( $year ) === 2 ) { $year = '20' . $year; }
                            $date = sprintf( '%04d-%02d-%02d', $year, $m[2], $m[1] );
                        }
                    }
                }
            }
            // Direct meta keys
            if ( ! $occasion ) {
                $direct = $item->get_meta( 'Occasion', true );
                if ( $direct ) { $occasion = $direct; }
            }
        }
    }

    return array( $date, $time, $occasion );
}

function dcp_apply_minutes_offset( $hhmm, $offset_minutes ) {
    if ( ! preg_match( '/^(\d{1,2}):(\d{2})/', $hhmm, $m ) ) {
        return $hhmm;
    }
    $total = intval( $m[1] ) * 60 + intval( $m[2] ) + intval( $offset_minutes );
    $total = max( 0, min( 24 * 60 - 1, $total ) );
    return sprintf( '%02d:%02d', intdiv( $total, 60 ), $total % 60 );
}

/* ============================================================================
 * ADMIN — settings page, action links, meta box
 * ========================================================================== */

function dcp_action_links( $links ) {
    array_unshift( $links, '<a href="' . esc_url( admin_url( 'admin.php?page=dcp-settings' ) ) . '">' . esc_html__( 'Settings', 'dcp' ) . '</a>' );
    return $links;
}

function dcp_admin_menu() {
    add_menu_page(
        __( 'Delicate Courier', 'dcp' ),
        __( 'Delicate Courier', 'dcp' ),
        'manage_woocommerce',
        'dcp-settings',
        'dcp_render_settings_page',
        'dashicons-airplane',
        56
    );
}

/**
 * Returns the platform base URL the plugin should POST orders / fetch
 * rates against. Prefers the per-site override saved via the admin
 * Settings page (option: dcp_platform_base_url); falls back to the
 * build-time DCP_PLATFORM_BASE_URL constant if the override is empty
 * or doesn't look like an http(s) URL.
 */
function dcp_platform_base_url() {
    $opt = trim( (string) get_option( 'dcp_platform_base_url', '' ) );
    if ( $opt !== '' && preg_match( '#^https?://#i', $opt ) ) {
        return rtrim( $opt, '/' );
    }
    return DCP_PLATFORM_BASE_URL;
}

/**
 * DNS bypass for hosts whose resolver can't reach the platform hostname.
 * If a merchant has set the "Platform Endpoint IP" override on the settings
 * page, hook the underlying cURL handle that WP_Http uses and pre-populate
 * CURLOPT_RESOLVE so cURL skips DNS for the platform host and connects
 * straight to the override IP. SNI + certificate validation still use the
 * real hostname, so TLS is not weakened. Only fires for requests whose
 * target host matches the configured platform host — never for anything
 * else WordPress is fetching.
 */
add_action( 'http_api_curl', 'dcp_curl_resolve_platform', 10, 3 );
function dcp_curl_resolve_platform( $handle, $args, $url ) {
    $ip = trim( (string) get_option( 'dcp_platform_endpoint_ip', '' ) );
    if ( $ip === '' || ! filter_var( $ip, FILTER_VALIDATE_IP ) ) {
        return;
    }
    $platform_host = parse_url( dcp_platform_base_url(), PHP_URL_HOST );
    $request_host  = parse_url( (string) $url, PHP_URL_HOST );
    if ( ! $platform_host || ! $request_host || strcasecmp( $platform_host, $request_host ) !== 0 ) {
        return;
    }
    curl_setopt( $handle, CURLOPT_RESOLVE, array(
        $platform_host . ':443:' . $ip,
        $platform_host . ':80:'  . $ip,
    ) );
}

function dcp_register_settings() {
    $options = array(
        // Secrets
        'dcp_store_id', 'dcp_webhook_secret', 'dcp_shiplogic_bearer',
        'dcp_google_maps_api_key',
        // Config
        'dcp_platform_base_url',
        'dcp_platform_endpoint_ip',
        'dcp_special_trip_cost_per_km',
        // Toggles
        'dcp_enable_live_rates', 'dcp_auto_sync_orders', 'dcp_debug_logging',
    );
    // Default-build only: Account ID + Provider ID are merchant-entered.
    // Branded builds have these pre-baked as constants and never read the
    // option, so we don't register them there.
    if ( dcp_shiplogic_ids_are_editable() ) {
        $options[] = 'dcp_shiplogic_account_id';
        $options[] = 'dcp_shiplogic_provider_id';
    }
    foreach ( $options as $opt ) {
        register_setting( 'dcp_settings', $opt );
    }
}

function dcp_render_settings_page() {
    if ( ! current_user_can( 'manage_woocommerce' ) ) {
        wp_die( __( 'Insufficient permissions.', 'dcp' ) );
    }

    // Handle save.
    if ( isset( $_POST['dcp_save'] ) && check_admin_referer( 'dcp_save_settings' ) ) {
        // Credentials are opaque tokens that must be preserved byte-for-byte.
        // WordPress's sanitize_text_field() strips/normalises characters
        // (notably collapsing whitespace and dropping things that look like
        // control bytes), which silently mangles base64 secrets containing
        // '+', '/' or '=' and makes every HMAC signature fail. Use trim()
        // only — wp_unslash undoes WP's automatic addslashes, and trim()
        // removes accidental leading/trailing whitespace from copy-paste.
        $secrets = array( 'dcp_store_id', 'dcp_webhook_secret', 'dcp_shiplogic_bearer', 'dcp_google_geocoding_api_key', 'dcp_google_maps_api_key' );
        foreach ( $secrets as $name ) {
            $val = isset( $_POST[ $name ] ) ? trim( wp_unslash( $_POST[ $name ] ) ) : '';
            dcp_save_secret_option( $name, $val );
        }
        // Account ID + Provider ID are only saved for the default build —
        // branded builds use constants and ignore the option entirely.
        if ( dcp_shiplogic_ids_are_editable() ) {
            foreach ( array( 'dcp_shiplogic_account_id', 'dcp_shiplogic_provider_id' ) as $name ) {
                $val = isset( $_POST[ $name ] ) ? sanitize_text_field( wp_unslash( $_POST[ $name ] ) ) : '';
                update_option( $name, $val );
            }
        }
        // Platform endpoint URL — plain config, sanitize as URL, allow blank
        // (blank means "use the build-time default constant").
        $url_in = isset( $_POST['dcp_platform_base_url'] ) ? trim( wp_unslash( $_POST['dcp_platform_base_url'] ) ) : '';
        if ( $url_in === '' || preg_match( '#^https?://#i', $url_in ) ) {
            update_option( 'dcp_platform_base_url', $url_in === '' ? '' : esc_url_raw( $url_in ) );
        }
        // Platform endpoint IP override (advanced, DNS bypass). Accepts IPv4
        // or IPv6; blank disables. When set, see dcp_curl_resolve_platform().
        $ip_in = isset( $_POST['dcp_platform_endpoint_ip'] ) ? trim( wp_unslash( $_POST['dcp_platform_endpoint_ip'] ) ) : '';
        if ( $ip_in === '' || filter_var( $ip_in, FILTER_VALIDATE_IP ) ) {
            update_option( 'dcp_platform_endpoint_ip', $ip_in );
        }
        // Special trip cost per km (v2.7.0). Numeric, >= 0. Blank clears
        // (disables the special-trip fallback).
        $st_raw = isset( $_POST['dcp_special_trip_cost_per_km'] ) ? trim( wp_unslash( $_POST['dcp_special_trip_cost_per_km'] ) ) : '';
        if ( $st_raw === '' ) {
            delete_option( 'dcp_special_trip_cost_per_km' );
        } elseif ( is_numeric( $st_raw ) && floatval( $st_raw ) >= 0 ) {
            update_option( 'dcp_special_trip_cost_per_km', floatval( $st_raw ) );
        } else {
            echo '<div class="notice notice-error"><p>Special trip cost per km must be a non-negative number. Ignored.</p></div>';
        }
        $toggles = array( 'dcp_enable_live_rates', 'dcp_auto_sync_orders', 'dcp_debug_logging' );
        foreach ( $toggles as $name ) {
            update_option( $name, isset( $_POST[ $name ] ) ? 'yes' : 'no' );
        }

        // Manual collection coordinates (override). Blank values clear any
        // previous override. Validate as numeric and within plausible
        // global bounds to catch typos — but accept the value otherwise,
        // because the merchant is asserting they know their own location.
        foreach ( array(
            'dcp_collection_latitude_manual'  => array( -90.0,   90.0  ),
            'dcp_collection_longitude_manual' => array( -180.0, 180.0  ),
        ) as $opt_name => $bounds ) {
            $raw = isset( $_POST[ $opt_name ] ) ? trim( wp_unslash( $_POST[ $opt_name ] ) ) : '';
            if ( $raw === '' ) {
                delete_option( $opt_name );
                continue;
            }
            if ( ! is_numeric( $raw ) ) {
                echo '<div class="notice notice-error"><p>Manual ' . esc_html( str_replace( 'dcp_collection_', '', str_replace( '_manual', '', $opt_name ) ) ) . ' must be a number. Ignored.</p></div>';
                continue;
            }
            $val = floatval( $raw );
            if ( $val < $bounds[0] || $val > $bounds[1] ) {
                echo '<div class="notice notice-error"><p>Manual ' . esc_html( str_replace( 'dcp_collection_', '', str_replace( '_manual', '', $opt_name ) ) ) . ' out of range. Ignored.</p></div>';
                continue;
            }
            update_option( $opt_name, $val );
        }

        // Trigger a geocode on save. Cheap if nothing changed (the
        // hash-based check inside _if_needed makes it a no-op), but if
        // the merchant edited an upstream WC store-address field, this is
        // when we want to re-resolve their coordinates. Best-effort —
        // failure here is OK because the lazy fallback during rate-fetch
        // will retry.
        dcp_geocode_collection_address_if_needed();

        // Bust the rate cache so a new Shiplogic token / account takes effect immediately.
        dcp_clear_rate_cache();
        echo '<div class="notice notice-success is-dismissible"><p>Settings saved.</p></div>';
    }

    $ajax_nonce = wp_create_nonce( 'dcp_admin' );
    ?>
    <div class="wrap">
        <h1><span class="dashicons dashicons-airplane" style="font-size:30px;"></span> Delicate Courier Platform</h1>
        <p>Platform endpoint: <code><?php echo esc_html( dcp_platform_base_url() ); ?></code> &middot; Plugin version <?php echo esc_html( DCP_VERSION ); ?></p>

        <form method="post" action="">
            <?php wp_nonce_field( 'dcp_save_settings' ); ?>

            <h2 class="title">Platform credentials</h2>
            <p>These three values come from the Store record in the Delicate Courier admin portal. The Shiplogic token is from your Shiplogic dashboard.</p>
            <table class="form-table" role="presentation">
                <tr><th><label for="dcp_store_id">Store ID</label></th>
                    <td><input name="dcp_store_id" id="dcp_store_id" type="text" class="regular-text" value="<?php echo esc_attr( dcp_store_id() ); ?>" required></td></tr>
                <tr><th><label for="dcp_webhook_secret">Webhook Secret</label></th>
                    <td><input name="dcp_webhook_secret" id="dcp_webhook_secret" type="password" class="regular-text" value="<?php echo esc_attr( dcp_webhook_secret() ); ?>" autocomplete="off" required>
                        <p class="description">Used to HMAC-sign every order pushed to the platform.</p></td></tr>
                <tr><th><label for="dcp_shiplogic_bearer">Shiplogic Bearer Token <em>(optional)</em></label></th>
                    <td><input name="dcp_shiplogic_bearer" id="dcp_shiplogic_bearer" type="password" class="regular-text" value="<?php echo esc_attr( dcp_shiplogic_bearer() ); ?>" autocomplete="off">
                        <p class="description"><strong>Not required for normal operation.</strong> Live rates at checkout are fetched through the Delicate Courier Platform using the token stored on your tenant. Only fill this in if you want the "Test direct Shiplogic call" diagnostic button to work.</p></td></tr>
                <?php if ( dcp_shiplogic_ids_are_editable() ) : ?>
                <tr><th><label for="dcp_shiplogic_account_id">Shiplogic Account ID <em>(optional)</em></label></th>
                    <td><input name="dcp_shiplogic_account_id" id="dcp_shiplogic_account_id" type="text" class="regular-text" value="<?php echo esc_attr( dcp_shiplogic_account_id() ); ?>">
                        <p class="description">Diagnostic only. The platform supplies the correct account ID for live rates automatically.</p></td></tr>
                <tr><th><label for="dcp_shiplogic_provider_id">Shiplogic Provider ID <em>(optional)</em></label></th>
                    <td><input name="dcp_shiplogic_provider_id" id="dcp_shiplogic_provider_id" type="text" class="regular-text" value="<?php echo esc_attr( dcp_shiplogic_provider_id() ); ?>">
                        <p class="description">Diagnostic only. The platform supplies the correct provider ID for live rates automatically.</p></td></tr>
                <?php endif; ?>
                <tr><th><label for="dcp_platform_base_url">Platform Endpoint URL <em>(optional)</em></label></th>
                    <td><input name="dcp_platform_base_url" id="dcp_platform_base_url" type="url" class="regular-text" value="<?php echo esc_attr( get_option( 'dcp_platform_base_url', '' ) ); ?>" placeholder="<?php echo esc_attr( DCP_PLATFORM_BASE_URL ); ?>">
                        <p class="description">Leave blank to use the default (<code><?php echo esc_html( DCP_PLATFORM_BASE_URL ); ?></code>). Only change this if instructed by your platform administrator &mdash; e.g. to point at staging, or as a fallback while DNS for the custom domain propagates.</p></td></tr>
                <tr><th><label for="dcp_platform_endpoint_ip">Platform Endpoint IP <em>(advanced &mdash; DNS bypass)</em></label></th>
                    <td><input name="dcp_platform_endpoint_ip" id="dcp_platform_endpoint_ip" type="text" class="regular-text" value="<?php echo esc_attr( get_option( 'dcp_platform_endpoint_ip', '' ) ); ?>" placeholder="e.g. 34.111.179.208">
                        <p class="description"><strong>Leave blank unless your server cannot resolve the platform hostname via DNS.</strong> When set, the plugin tells cURL to connect directly to this IP for the platform host above, skipping DNS entirely. TLS (SNI &amp; certificate validation) still uses the hostname, so the connection stays secure. The IP is not contractually stable &mdash; treat this as a temporary workaround while your hosting provider fixes their DNS.</p></td></tr>
            </table>

            <h2 class="title">Geocoding <em>(collection address)</em></h2>
            <p class="description" style="max-width:60em">Shiplogic requires latitude/longitude on the collection address to return rates. This plugin geocodes your store address automatically &mdash; Google Geocoding API if you provide a key, falling back to free OpenStreetMap (Nominatim) otherwise.</p>
            <table class="form-table" role="presentation">
                <tr><th><label for="dcp_google_geocoding_api_key">Google Geocoding API key <em>(optional)</em></label></th>
                    <td><input name="dcp_google_geocoding_api_key" id="dcp_google_geocoding_api_key" type="password" class="regular-text" value="<?php echo esc_attr( get_option( 'dcp_google_geocoding_api_key', '' ) ); ?>" autocomplete="off" placeholder="<?php echo esc_attr( get_option( 'dcp_google_geocoding_api_key', '' ) ? '(stored)' : 'Leave blank to use free OpenStreetMap Nominatim' ); ?>">
                        <p class="description">If filled in, the plugin uses Google for collection-address geocoding (best accuracy for SA addresses). If blank, falls back to free Nominatim.</p></td></tr>
                <tr><th>Resolved coordinates</th>
                    <td>
                        <?php
                        $cur_lat = get_option( 'dcp_collection_latitude', '' );
                        $cur_lng = get_option( 'dcp_collection_longitude', '' );
                        $cur_provider = get_option( 'dcp_collection_geocoded_provider', '' );
                        $cur_at = (int) get_option( 'dcp_collection_geocoded_at', 0 );
                        if ( $cur_lat !== '' && $cur_lng !== '' ) {
                            echo '<code>' . esc_html( $cur_lat ) . ', ' . esc_html( $cur_lng ) . '</code>';
                            echo ' <span style="color:#666">— via ' . esc_html( $cur_provider ?: 'unknown' );
                            if ( $cur_at > 0 ) {
                                echo ', ' . esc_html( human_time_diff( $cur_at, time() ) ) . ' ago';
                            }
                            echo '</span>';
                        } else {
                            echo '<em style="color:#a00">Not yet geocoded.</em> Save the page to trigger, OR enter coordinates manually below.';
                        }
                        ?>
                        <p class="description">Coordinates are re-resolved automatically when you change your store address. Click <strong>Save changes</strong> below to force a fresh geocode.</p>
                    </td></tr>
                <tr><th><label for="dcp_collection_latitude_manual">Manual coordinates <em>(override)</em></label></th>
                    <td>
                        <?php
                        $manual_lat = get_option( 'dcp_collection_latitude_manual',  '' );
                        $manual_lng = get_option( 'dcp_collection_longitude_manual', '' );
                        ?>
                        <input name="dcp_collection_latitude_manual"  id="dcp_collection_latitude_manual"  type="text" class="small-text" value="<?php echo esc_attr( $manual_lat ); ?>" placeholder="e.g. -25.7479">
                        <input name="dcp_collection_longitude_manual" id="dcp_collection_longitude_manual" type="text" class="small-text" value="<?php echo esc_attr( $manual_lng ); ?>" placeholder="e.g. 28.2293">
                        <p class="description">
                            Optional. If filled in, these win over the geocoded values above and are sent to Shiplogic as-is.
                            Use this when the geocoder can't resolve your address, or when you need to pin a known-good location
                            (e.g. inside a shopping centre where the street address geocodes to the wrong building).
                            Get coordinates by right-clicking your store in Google Maps and choosing the "What's here" entry &mdash;
                            the popup at the bottom of the screen shows the lat/lng you can paste here.
                            Leave both blank to revert to the geocoded values.
                        </p>
                        <?php
                        // Surface the effective coords being sent right now,
                        // regardless of which source they come from, so the
                        // operator can sanity-check at a glance.
                        $effective = dcp_collection_address();
                        if ( ! empty( $effective['lat'] ) && ! empty( $effective['lng'] ) ) {
                            $source = $manual_lat !== '' && $manual_lng !== '' ? 'manual override' : 'geocoded';
                            echo '<p><strong>Currently sent to Shiplogic:</strong> <code>'
                                . esc_html( $effective['lat'] ) . ', ' . esc_html( $effective['lng'] )
                                . '</code> <em>(' . esc_html( $source ) . ')</em></p>';
                        } else {
                            echo '<p style="color:#a00"><strong>⚠ No coordinates currently configured.</strong> '
                                . 'Shiplogic will return empty rates until either the geocoder succeeds or you enter coordinates manually above.</p>';
                        }
                        ?>
                    </td></tr>
            </table>

            <h2 class="title">Checkout &amp; Special Trips <em>(v2.7.0)</em></h2>
            <p class="description" style="max-width:60em">Optional. With a Google Maps API key, the checkout shipping-address field gains Google Places autocomplete (restricted to South Africa) plus a live address-validity indicator. With a cost-per-km set, customers in areas where no courier rate is available are offered a &ldquo;Special Trip Request&rdquo; option priced from the real driving distance between your store and their address.</p>
            <table class="form-table" role="presentation">
                <tr><th><label for="dcp_google_maps_api_key">Google Maps API key <em>(checkout)</em></label></th>
                    <td><input name="dcp_google_maps_api_key" id="dcp_google_maps_api_key" type="password" class="regular-text" value="<?php echo esc_attr( dcp_google_maps_api_key() ); ?>" autocomplete="off">
                        <p class="description">Used in the customer's browser for address autocomplete/validation and server-side for special-trip distance quotes. Enable the <strong>Places API</strong>, <strong>Geocoding API</strong>, <strong>Maps JavaScript API</strong> and <strong>Distance Matrix API</strong> on this key. Because it is exposed on the checkout page, restrict it to your site's domain (HTTP referrer restriction) in the Google Cloud console.</p></td></tr>
                <tr><th><label for="dcp_special_trip_cost_per_km">Special trip cost per km (R)</label></th>
                    <td><input name="dcp_special_trip_cost_per_km" id="dcp_special_trip_cost_per_km" type="number" step="0.01" min="0" class="small-text" value="<?php echo esc_attr( dcp_special_trip_cost_per_km() > 0 ? dcp_special_trip_cost_per_km() : '' ); ?>" placeholder="e.g. 12.50">
                        <p class="description">Rand charged per driving-kilometre (store &rarr; customer) for the &ldquo;Special Trip Request&rdquo; fallback when no courier rates are available. Leave blank or 0 to disable the fallback. Requires the Google Maps API key above.</p></td></tr>
            </table>

            <h2 class="title">Behaviour</h2>
            <table class="form-table" role="presentation">
                <tr><th>Live rates at checkout</th>
                    <td><label><input type="checkbox" name="dcp_enable_live_rates" <?php checked( dcp_live_rates_enabled() ); ?>> Enable</label></td></tr>
                <tr><th>Auto-sync orders to platform</th>
                    <td><label><input type="checkbox" name="dcp_auto_sync_orders" <?php checked( dcp_auto_sync_enabled() ); ?>> Enable</label></td></tr>
                <tr><th>Debug logging</th>
                    <td><label><input type="checkbox" name="dcp_debug_logging" <?php checked( dcp_debug_enabled() ); ?>> Enable</label>
                        <p class="description">Keeps the last 200 events in the database. View them at the bottom of this page.</p></td></tr>
            </table>

            <p class="submit"><button type="submit" name="dcp_save" class="button button-primary">Save changes</button></p>
        </form>

        <hr>
        <h2>Pre-configured values (read-only)</h2>
        <p class="description">These values are derived from this plugin build or from your WooCommerce store address. To change them, override with a small code snippet using the documented filters, or ask your platform administrator for a custom-branded plugin build.</p>
        <?php $addr = dcp_collection_address(); ?>
        <table class="form-table" role="presentation">
            <?php if ( ! dcp_shiplogic_ids_are_editable() ) : ?>
            <tr><th>Shiplogic Account ID</th><td><code><?php echo esc_html( dcp_shiplogic_account_id() ?: '(not set)' ); ?></code></td></tr>
            <tr><th>Shiplogic Provider ID</th><td><code><?php echo esc_html( dcp_shiplogic_provider_id() ?: '(not set)' ); ?></code></td></tr>
            <?php endif; ?>
            <tr><th>Service Level Code</th><td><code><?php echo esc_html( dcp_service_level_code() ); ?></code></td></tr>
            <tr><th>Collection address</th><td>
                <code><?php echo esc_html( trim( ($addr['company'] ?? '') . ', ' . ($addr['street_address'] ?? '') . ', ' . ($addr['city'] ?? '') . ' ' . ($addr['code'] ?? '') . ', ' . ($addr['country'] ?? ''), ', ' ) ); ?></code>
                <?php if ( ! DCP_DEFAULT_COLLECTION_STREET ) : ?><p class="description">Falls back to your WooCommerce store address (Settings → General).</p><?php endif; ?>
            </td></tr>
        </table>

        <?php dcp_uc_render_license_section(); ?>

        <hr>
        <h2>Connection tests</h2>
        <p>
            <button type="button" class="button" id="dcp-test-health">Test platform connection</button>
            <button type="button" class="button" id="dcp-test-order">Send signed test order</button>
            <button type="button" class="button button-primary" id="dcp-test-platform-rates">Test live rates (via platform)</button>
            <button type="button" class="button" id="dcp-test-rates">Test direct Shiplogic call</button>
        </p>
        <p class="description">
            <strong>Test live rates (via platform)</strong> is what your customers' checkout actually uses — the platform geocodes addresses and uses your tenant's Shiplogic token.
            <strong>Test direct Shiplogic call</strong> bypasses the platform and is only useful for low-level debugging; it requires the legacy Shiplogic token field to be filled in.
        </p>
        <pre id="dcp-test-output" style="background:#1d2327;color:#e3e7ee;padding:12px;border-radius:4px;min-height:80px;white-space:pre-wrap;"></pre>

        <hr>
        <h2 id="dcp-outbox">Outbox <em style="font-weight:normal;color:#646970">— pending order syncs</em></h2>
        <p class="description">
            When the platform is unreachable, failed order pushes are queued here for automatic retry every 5 minutes.
            Orders marked <strong>manual</strong> have exhausted all retry attempts (~8 days) and require manual intervention.
        </p>
        <?php
        global $wpdb;
        $outbox_table = dcp_outbox_table_name();
        $pending_count = (int) $wpdb->get_var( "SELECT COUNT(*) FROM $outbox_table WHERE status = 'pending'" );
        $manual_count  = (int) $wpdb->get_var( "SELECT COUNT(*) FROM $outbox_table WHERE status = 'manual'" );
        $synced_count  = (int) $wpdb->get_var( "SELECT COUNT(*) FROM $outbox_table WHERE status = 'synced'" );
        ?>
        <p>
            <strong style="color:<?php echo $pending_count > 0 ? '#dba617' : '#646970'; ?>"><?php echo (int) $pending_count; ?> pending</strong> &middot;
            <strong style="color:<?php echo $manual_count  > 0 ? '#d63638' : '#646970'; ?>"><?php echo (int) $manual_count;  ?> manual</strong> &middot;
            <span style="color:#646970"><?php echo (int) $synced_count; ?> synced</span>
            <button type="button" class="button" id="dcp-outbox-process-now" style="margin-left:20px;">Retry pending now</button>
        </p>
        <table class="widefat striped" style="max-width:100%;margin-top:8px;">
            <thead><tr>
                <th>Order</th><th>Event</th><th>Status</th><th>Attempts</th>
                <th>Next attempt</th><th>Last error</th>
            </tr></thead>
            <tbody>
            <?php
            $rows = $wpdb->get_results(
                "SELECT * FROM $outbox_table WHERE status IN ('pending','manual','orphaned') ORDER BY next_attempt_at ASC LIMIT 25"
            );
            if ( empty( $rows ) ) {
                echo '<tr><td colspan="6"><em style="color:#646970">No pending order syncs.</em></td></tr>';
            } else {
                foreach ( $rows as $r ) {
                    $order_link = admin_url( 'post.php?post=' . (int) $r->woo_order_id . '&action=edit' );
                    $status_color = $r->status === 'manual' ? '#d63638' : ( $r->status === 'orphaned' ? '#646970' : '#dba617' );
                    echo '<tr>';
                    echo '<td><a href="' . esc_url( $order_link ) . '">#' . (int) $r->woo_order_id . '</a></td>';
                    echo '<td>' . esc_html( $r->event_type ) . '</td>';
                    echo '<td><strong style="color:' . esc_attr( $status_color ) . '">' . esc_html( $r->status ) . '</strong></td>';
                    echo '<td>' . (int) $r->attempt_count . '</td>';
                    echo '<td>' . esc_html( $r->next_attempt_at ) . ' UTC</td>';
                    echo '<td><small style="color:#646970">' . esc_html( (string) $r->last_error ) . '</small></td>';
                    echo '</tr>';
                }
            }
            ?>
            </tbody>
        </table>

        <hr>
        <h2>Debug log</h2>
        <p class="description">
            The last 200 events are kept here when "Debug logging" is enabled.
            Click any row to expand the full request and response. Sensitive headers
            (Authorization, X-Plugin-Signature) are redacted.
        </p>
        <p>
            <button type="button" class="button button-primary" id="dcp-refresh-logs">Refresh logs</button>
            <label style="margin-left:12px;"><input type="checkbox" id="dcp-auto-refresh"> Auto-refresh every 5s</label>
            <label style="margin-left:12px;">Filter level:
                <select id="dcp-log-level-filter">
                    <option value="">All</option>
                    <option value="info">info</option>
                    <option value="warning">warning</option>
                    <option value="error">error</option>
                </select>
            </label>
            <label style="margin-left:12px;">Context contains:
                <input type="text" id="dcp-log-context-filter" placeholder="e.g. platform.rates" style="width:200px;">
            </label>
            <button type="button" class="button" id="dcp-clear-logs" style="margin-left:12px;">Clear log</button>
            <span id="dcp-log-status" style="margin-left:12px;color:#646970;"></span>
        </p>
        <table class="widefat striped" id="dcp-log-table" style="max-width:100%;">
            <thead><tr>
                <th style="width:160px;">Time</th>
                <th style="width:70px;">Level</th>
                <th style="width:200px;">Context</th>
                <th>Detail</th>
            </tr></thead>
            <tbody id="dcp-log-tbody">
                <tr><td colspan="4"><em>Loading...</em></td></tr>
            </tbody>
        </table>
    </div>

    <style>
        #dcp-log-table tr.dcp-log-row { cursor: pointer; }
        #dcp-log-table tr.dcp-log-row td { vertical-align: top; }
        #dcp-log-table tr.dcp-log-data > td { background: #f6f7f7; padding: 0 12px 12px 12px; }
        #dcp-log-table .dcp-log-level-error    { color: #b32d2e; font-weight: 600; }
        #dcp-log-table .dcp-log-level-warning  { color: #996800; font-weight: 600; }
        #dcp-log-table .dcp-log-level-info     { color: #2271b1; }
        #dcp-log-table .dcp-toggle { display:inline-block; width:14px; color:#646970; font-family:monospace; }
        #dcp-log-table pre.dcp-body { background:#1d2327; color:#e3e7ee; padding:10px; border-radius:4px; max-height:360px; overflow:auto; white-space:pre-wrap; word-break:break-word; margin:6px 0; font-size:11px; }
        #dcp-log-table .dcp-section-title { font-weight:600; margin-top:8px; color:#1d2327; }
        #dcp-log-table .dcp-kv { font-family:monospace; font-size:11px; color:#1d2327; }
    </style>

    <script>
    (function(){
        var nonce = '<?php echo esc_js( $ajax_nonce ); ?>';
        var out = document.getElementById('dcp-test-output');

        function postAjax(action){
            var fd = new FormData();
            fd.append('action', action);
            fd.append('_ajax_nonce', nonce);
            return fetch(ajaxurl, { method:'POST', credentials:'same-origin', body: fd })
                .then(function(r){ return r.json(); });
        }

        function run(action, label){
            out.textContent = label + '...';
            postAjax(action)
                .then(function(j){ out.textContent = JSON.stringify(j, null, 2); refreshLogs(); })
                .catch(function(e){ out.textContent = 'Request failed: ' + e; });
        }
        document.getElementById('dcp-test-health').addEventListener('click', function(){ run('dcp_test_platform_health', 'Pinging platform'); });
        document.getElementById('dcp-test-order').addEventListener('click', function(){ run('dcp_test_platform_order', 'Sending signed test order'); });
        document.getElementById('dcp-test-platform-rates').addEventListener('click', function(){ run('dcp_test_platform_rates', 'Requesting live rates via platform'); });
        document.getElementById('dcp-test-rates').addEventListener('click', function(){ run('dcp_test_shiplogic_rates', 'Requesting direct Shiplogic rates'); });

        var outboxBtn = document.getElementById('dcp-outbox-process-now');
        if (outboxBtn) {
            outboxBtn.addEventListener('click', function(){
                outboxBtn.disabled = true;
                var oldLabel = outboxBtn.textContent;
                outboxBtn.textContent = 'Processing…';
                postAjax('dcp_outbox_process_now').then(function(j){
                    outboxBtn.disabled = false;
                    outboxBtn.textContent = oldLabel;
                    alert(j && j.data && j.data.message ? j.data.message : 'Done.');
                    location.reload();
                }).catch(function(e){
                    outboxBtn.disabled = false;
                    outboxBtn.textContent = oldLabel;
                    alert('Request failed: ' + e);
                });
            });
        }

        /* ------------------- Log viewer ------------------- */

        var tbody    = document.getElementById('dcp-log-tbody');
        var status   = document.getElementById('dcp-log-status');
        var lvlFilt  = document.getElementById('dcp-log-level-filter');
        var ctxFilt  = document.getElementById('dcp-log-context-filter');
        var autoChk  = document.getElementById('dcp-auto-refresh');
        var autoTimer = null;
        var lastLogs = [];

        function escHtml(s){
            return String(s == null ? '' : s)
                .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
                .replace(/"/g,'&quot;').replace(/'/g,'&#39;');
        }

        function renderHeaders(h){
            if (!h || typeof h !== 'object') return '';
            var rows = [];
            Object.keys(h).forEach(function(k){
                rows.push('<div class="dcp-kv">' + escHtml(k) + ': ' + escHtml(h[k]) + '</div>');
            });
            return rows.join('');
        }

        function renderDetailBlock(d){
            if (!d || typeof d !== 'object') return '';
            var html = '';
            var meta = [];
            if (d.method)          meta.push(escHtml(d.method));
            if (d.url)             meta.push(escHtml(d.url));
            if (d.response_status !== undefined) meta.push('HTTP ' + escHtml(d.response_status));
            if (d.duration_ms !== undefined)     meta.push(escHtml(d.duration_ms) + ' ms');
            if (meta.length) html += '<div class="dcp-kv" style="margin-bottom:6px;">' + meta.join('  &middot;  ') + '</div>';
            if (d.error)           html += '<div class="dcp-section-title">Transport error</div><div class="dcp-kv" style="color:#b32d2e;">' + escHtml(d.error) + '</div>';
            if (d.request_headers) html += '<div class="dcp-section-title">Request headers</div>' + renderHeaders(d.request_headers);
            if (d.request_body !== undefined && d.request_body !== null && d.request_body !== '')
                html += '<div class="dcp-section-title">Request body</div><pre class="dcp-body">' + escHtml(d.request_body) + '</pre>';
            if (d.response_headers) html += '<div class="dcp-section-title">Response headers</div>' + renderHeaders(d.response_headers);
            if (d.response_body !== undefined && d.response_body !== null && d.response_body !== '')
                html += '<div class="dcp-section-title">Response body</div><pre class="dcp-body">' + escHtml(d.response_body) + '</pre>';
            return html;
        }

        function rowMatchesFilters(row){
            var lvl = lvlFilt.value;
            var ctx = ctxFilt.value.trim().toLowerCase();
            if (lvl && (row.level || '') !== lvl) return false;
            if (ctx && (row.context || '').toLowerCase().indexOf(ctx) === -1) return false;
            return true;
        }

        function renderLogs(logs){
            var html = '';
            var shown = 0;
            logs.forEach(function(row, idx){
                if (!rowMatchesFilters(row)) return;
                shown++;
                var hasData = row.data && typeof row.data === 'object';
                var arrow = hasData ? '&#9656;' : '&nbsp;';
                var lvlCls = 'dcp-log-level-' + (row.level || 'info');
                html += '<tr class="dcp-log-row" data-idx="' + idx + '">' +
                    '<td>' + escHtml(row.time) + '</td>' +
                    '<td class="' + lvlCls + '">' + escHtml(row.level || '') + '</td>' +
                    '<td><span class="dcp-toggle">' + arrow + '</span> ' + escHtml(row.context || '') + '</td>' +
                    '<td><code>' + escHtml(row.detail || '') + '</code></td>' +
                '</tr>';
                if (hasData) {
                    html += '<tr class="dcp-log-data" data-for="' + idx + '" style="display:none;">' +
                        '<td colspan="4">' + renderDetailBlock(row.data) + '</td>' +
                    '</tr>';
                }
            });
            if (shown === 0) {
                html = '<tr><td colspan="4"><em>No log entries.</em></td></tr>';
            }
            tbody.innerHTML = html;
            // Wire expand/collapse.
            tbody.querySelectorAll('tr.dcp-log-row').forEach(function(tr){
                tr.addEventListener('click', function(){
                    var idx = tr.getAttribute('data-idx');
                    var detail = tbody.querySelector('tr.dcp-log-data[data-for="' + idx + '"]');
                    if (!detail) return;
                    var open = detail.style.display !== 'none';
                    detail.style.display = open ? 'none' : 'table-row';
                    var tog = tr.querySelector('.dcp-toggle');
                    if (tog) tog.innerHTML = open ? '&#9656;' : '&#9662;';
                });
            });
        }

        function refreshLogs(){
            status.textContent = 'Refreshing...';
            postAjax('dcp_fetch_debug_logs')
                .then(function(j){
                    if (!j || !j.success) {
                        status.textContent = 'Failed to load logs';
                        return;
                    }
                    lastLogs = j.logs || [];
                    renderLogs(lastLogs);
                    var when = new Date().toLocaleTimeString();
                    status.textContent = (j.debug_enabled ? '' : '(debug logging is OFF) ') +
                        lastLogs.length + ' entries, refreshed at ' + when;
                })
                .catch(function(e){ status.textContent = 'Refresh failed: ' + e; });
        }

        document.getElementById('dcp-refresh-logs').addEventListener('click', refreshLogs);
        lvlFilt.addEventListener('change', function(){ renderLogs(lastLogs); });
        ctxFilt.addEventListener('input',  function(){ renderLogs(lastLogs); });

        autoChk.addEventListener('change', function(){
            if (autoChk.checked) {
                refreshLogs();
                autoTimer = setInterval(refreshLogs, 5000);
            } else if (autoTimer) {
                clearInterval(autoTimer);
                autoTimer = null;
            }
        });

        document.getElementById('dcp-clear-logs').addEventListener('click', function(){
            if (!confirm('Clear the debug log?')) return;
            postAjax('dcp_clear_debug_logs').then(function(){ refreshLogs(); });
        });

        // Initial load.
        refreshLogs();
    })();
    </script>
    <?php
}

/* ============================================================================
 * AJAX HANDLERS
 * ========================================================================== */

function dcp_check_ajax() {
    check_ajax_referer( 'dcp_admin' );
    if ( ! current_user_can( 'manage_woocommerce' ) ) {
        wp_send_json_error( array( 'message' => 'Insufficient permissions.' ), 403 );
    }
}

function dcp_ajax_test_platform_health() {
    dcp_check_ajax();
    $resp = dcp_platform_get( DCP_PLATFORM_HEALTH_PATH );
    wp_send_json( $resp );
}

function dcp_ajax_test_platform_order() {
    dcp_check_ajax();
    if ( ! dcp_is_configured() ) {
        wp_send_json( array( 'success' => false, 'status' => 0, 'body' => array( 'message' => 'Store ID and Webhook Secret are required first.' ) ) );
    }
    $test_suffix = strtoupper( wp_generate_password( 6, false, false ) );
    // Backend DTO is int32 (max 2,147,483,647) so the value must fit.
    // (time() % 1_000_000) * 10_000 gives a monotonic-ish 10-digit prefix that
    // wraps every ~11.5 days; the 4-digit random suffix makes same-second clicks
    // collide only 1-in-10_000. Final value is always < 1.1e10... wait, cap it:
    // (time() % 200_000) * 10_000 + wp_rand(0, 9999)  →  max 2_000_009_999 < int32 max.
    $test_woo_order_id = ( ( time() % 200000 ) * 10000 ) + wp_rand( 0, 9999 );
    $payload = array(
        'event'           => 'order.test',
        'woo_order_id'    => $test_woo_order_id,
        'order_number'    => 'TEST-' . $test_suffix,
        'status'          => 'processing',
        'total'           => 100.00,
        'shipping_total'  => 0,
        'currency'        => get_woocommerce_currency(),
        'payment_method'  => 'test',
        'date_created'    => current_time( 'mysql' ),
        'customer_note'   => 'Plugin connectivity test',
        'customer'        => array(
            'name'              => 'Plugin Test Customer',
            'email'             => 'test@example.com',
            'phone'             => '0820000000',
            'alternative_phone' => '0820000001',
        ),
        'shipping_address' => array(
            'street'   => '1 Test Street',
            'suburb'   => 'Test Suburb',
            'city'     => 'Pretoria',
            'state'    => 'Gauteng',
            'postcode' => '0001',
            'country'  => 'ZA',
        ),
        'line_items' => array( array(
            'id' => 1, 'product_id' => 1, 'name' => 'Test product', 'quantity' => 1, 'price' => 100.00, 'sku' => 'TEST',
        ) ),
        'total_weight'    => 1,
        'store_url'       => home_url(),
        'store_id'        => dcp_store_id(),
        'delivery_date'   => null,
        'delivery_time'   => null,
        'collection_date' => null,
        'collection_time' => null,
        'occasion'        => null,
        'fulfillment_type' => 'delivery',
    );
    $resp = dcp_platform_post_signed( DCP_PLATFORM_ORDER_PATH, $payload );
    wp_send_json( $resp );
}

function dcp_ajax_test_platform_rates() {
    dcp_check_ajax();
    $delivery = array(
        'type'           => 'residential',
        'company'        => null,
        'street_address' => '1 Test Street',
        'local_area'     => '',
        'city'           => 'Johannesburg',
        'zone'           => 'Gauteng',
        'code'           => '2000',
        'country'        => 'ZA',
        'contact'        => array( 'name' => 'Test', 'mobile_number' => null, 'email' => null ),
    );
    $parcels = array( array(
        'parcel_description'  => 'Test Parcel',
        'submitted_length_cm' => 20,
        'submitted_width_cm'  => 15,
        'submitted_height_cm' => 10,
        'submitted_weight_kg' => 1,
    ) );
    $rates = dcp_platform_fetch_rates( $delivery, $parcels );
    if ( is_wp_error( $rates ) ) {
        wp_send_json( array( 'success' => false, 'status' => 0, 'body' => array( 'message' => $rates->get_error_message() ) ) );
    }
    wp_send_json( array( 'success' => true, 'status' => 200, 'body' => array( 'rates' => $rates ) ) );
}

function dcp_ajax_test_shiplogic_rates() {
    dcp_check_ajax();
    $bearer = dcp_shiplogic_bearer();
    if ( $bearer === '' ) {
        wp_send_json( array( 'success' => false, 'status' => 0, 'body' => array( 'message' => 'Shiplogic Bearer Token is required first.' ) ) );
    }
    $payload = array(
        'account_id'         => is_numeric( dcp_shiplogic_account_id() ) ? intval( dcp_shiplogic_account_id() ) : dcp_shiplogic_account_id(),
        'provider_id'        => is_numeric( dcp_shiplogic_provider_id() ) ? intval( dcp_shiplogic_provider_id() ) : dcp_shiplogic_provider_id(),
        'service_level_code' => dcp_service_level_code(),
        'collection_address' => dcp_collection_address(),
        'delivery_address'   => array(
            'type'           => 'residential',
            'company'        => null,
            'street_address' => '1 Test Street',
            'local_area'     => '',
            'city'           => 'Johannesburg',
            'zone'           => 'Gauteng',
            'code'           => '2000',
            'country'        => 'ZA',
            'lat'            => null,
            'lng'            => null,
            'contact'        => array( 'name' => 'Test', 'mobile_number' => null, 'email' => null ),
        ),
        'parcels' => array( array(
            'parcel_description'  => 'Test Parcel',
            'submitted_length_cm' => 20,
            'submitted_width_cm'  => 15,
            'submitted_height_cm' => 10,
            'submitted_weight_kg' => 1,
        ) ),
    );
    $rates = dcp_shiplogic_fetch_rates( $bearer, $payload );
    if ( is_wp_error( $rates ) ) {
        wp_send_json( array( 'success' => false, 'status' => 0, 'body' => array( 'message' => $rates->get_error_message() ) ) );
    }
    wp_send_json( array( 'success' => true, 'status' => 200, 'body' => array( 'rates' => $rates ) ) );
}

function dcp_ajax_clear_debug_logs() {
    dcp_check_ajax();
    update_option( 'dcp_debug_logs', array(), false );
    wp_send_json( array( 'success' => true ) );
}

/**
 * Returns the current debug log as JSON so the settings page can refresh the
 * log table without a full page reload.
 */
function dcp_ajax_fetch_debug_logs() {
    dcp_check_ajax();
    $logs = get_option( 'dcp_debug_logs', array() );
    if ( ! is_array( $logs ) ) {
        $logs = array();
    }
    // Newest first — matches the order rendered in the table.
    wp_send_json( array(
        'success'       => true,
        'debug_enabled' => dcp_debug_enabled(),
        'count'         => count( $logs ),
        'logs'          => array_reverse( $logs ),
    ) );
}

/**
 * Manually trigger an outbox process pass. Useful when the merchant has
 * been notified that the platform is back up and wants to flush the
 * backlog without waiting for the next 5-min cron tick.
 */
function dcp_ajax_outbox_process_now() {
    dcp_check_ajax();
    if ( ! current_user_can( 'manage_woocommerce' ) ) {
        wp_send_json_error( array( 'message' => 'Insufficient permissions.' ), 403 );
    }
    dcp_outbox_process();
    wp_send_json_success( array( 'message' => 'Outbox processed. Refresh the page to see updated status.' ) );
}

/* ============================================================================
 * ORDER METABOX — show platform sync status
 * ========================================================================== */

function dcp_register_order_meta_box() {
    $screen = class_exists( '\Automattic\WooCommerce\Internal\DataStores\Orders\CustomOrdersTableController' )
        && wc_get_container()->get( \Automattic\WooCommerce\Internal\DataStores\Orders\CustomOrdersTableController::class )->custom_orders_table_usage_is_enabled()
        ? wc_get_page_screen_id( 'shop-order' )
        : 'shop_order';

    add_meta_box( 'dcp_order_sync', 'Delicate Checkout Data', 'dcp_render_order_meta_box', $screen, 'side', 'default' );
}

/**
 * Derive what the customer actually paid for shipping/delivery on an order,
 * regardless of how the cost was attached to the order.
 *
 * The Delicate Two-Step Checkout plugin (DTSC) stores the delivery cost as a
 * WooCommerce cart fee named "Delivery" rather than as a shipping line item,
 * so `$order->get_shipping_total()` returns 0 for those orders even when the
 * customer was charged correctly. Vanilla WC orders (and orders from non-DTSC
 * checkouts) use the standard shipping_total. This helper unifies both.
 *
 * Returns the NET amount (excluding tax) so that the figure is directly
 * comparable to `_dcp_courier_rate`, which is also the net Shiplogic rate.
 * Comparing like-for-like net values keeps the "Markup Margin" calculation
 * accurate — VAT on the fee is collected for SARS, not retained as margin.
 *
 * Order of contribution (all summed; both can legitimately be non-zero
 * on edge-case orders such as legacy data or mid-deploy):
 *   - sum of order fee items whose name matches "Delivery" (case-insensitive)
 *   - $order->get_shipping_total()
 *
 * @param WC_Order $order
 * @return float Net shipping amount the customer paid, rounded to 2 dp.
 */
function dcp_get_customer_paid_shipping( $order ) {
    if ( ! $order instanceof WC_Order ) {
        return 0.0;
    }

    $fee_total = 0.0;
    foreach ( $order->get_fees() as $fee ) {
        $name = (string) $fee->get_name();
        if ( strcasecmp( $name, 'Delivery' ) === 0 ) {
            $fee_total += (float) $fee->get_total();
        }
    }

    $shipping_total = (float) $order->get_shipping_total();

    return round( $fee_total + $shipping_total, 2 );
}

/**
 * v2.8.0 — read the checkout details that the Delicate Two-Step Checkout
 * (DTSC) writes onto the order at checkout time. All values are optional:
 * non-DTSC stores simply get nulls and the checkout section of the metabox
 * collapses to only the fields that exist.
 *
 * Meta keys observed on live DTSC orders:
 *   _dtsc_delivery_method     'delivery' | 'collect' | 'special_trip'
 *   _dtsc_delivery_date       'YYYY-MM-DD'   (mirrored as _dcp_delivery_date)
 *   _dtsc_delivery_time       '10:30 - 12:30' (mirrored as _dcp_delivery_time)
 *   _dtsc_collection_time     '09:00 - 11:00' (mirrored as _dcp_collection_time)
 *   _dtsc_delivery_phone      alt/recipient phone entered at checkout
 *   _dtsc_shipping_place_id   Google Place ID of the delivery address
 *   _dtsc_shipping_latitude / _dtsc_shipping_longitude
 *   _dtsc_courier_quote       array|JSON { status, price, message, ... }
 *
 * @param WC_Order $order
 * @return array<string,mixed>
 */
function dcp_get_checkout_data( $order ) {
    $meta = function( $keys ) use ( $order ) {
        foreach ( (array) $keys as $k ) {
            $v = $order->get_meta( $k );
            if ( $v !== '' && $v !== null ) {
                return $v;
            }
        }
        return null;
    };

    // Quote block: stored as a PHP array by DTSC; tolerate a JSON string too.
    $quote = $order->get_meta( '_dtsc_courier_quote' );
    if ( is_string( $quote ) && $quote !== '' ) {
        $decoded = json_decode( $quote, true );
        $quote   = is_array( $decoded ) ? $decoded : null;
    }
    if ( ! is_array( $quote ) ) {
        $quote = null;
    }

    $lat = $meta( '_dtsc_shipping_latitude' );
    $lng = $meta( '_dtsc_shipping_longitude' );

    // Fall back to Special Trip coordinates from the shipping line meta
    // (v2.7.0 rate) when the DTSC address meta is absent.
    if ( ( $lat === null || $lng === null ) && function_exists( 'dcp_extract_special_trip' ) ) {
        $st = dcp_extract_special_trip( $order );
        if ( $st && $st['customer_lat'] !== null && $st['customer_lng'] !== null ) {
            $lat = $st['customer_lat'];
            $lng = $st['customer_lng'];
        }
    }

    // Delivery slot: direct meta first, then the generic extractor that also
    // understands WP Time Slots Booking / WAPF line-item meta.
    $date = $meta( array( '_dtsc_delivery_date', '_dcp_delivery_date' ) );
    $time = $meta( array( '_dtsc_delivery_time', '_dcp_delivery_time' ) );
    if ( ( ! $date || ! $time ) && function_exists( 'dcp_extract_delivery_meta' ) ) {
        list( $x_date, $x_time, ) = dcp_extract_delivery_meta( $order );
        if ( ! $date && $x_date ) { $date = $x_date; }
        if ( ! $time && $x_time ) { $time = $x_time; }
    }

    return array(
        'method'          => $meta( '_dtsc_delivery_method' ) ?: ( function_exists( 'dcp_extract_fulfillment_type' ) ? dcp_extract_fulfillment_type( $order ) : null ),
        'delivery_date'   => $date,
        'delivery_time'   => $time,
        'collection_time' => $meta( array( '_dtsc_collection_time', '_dcp_collection_time' ) ),
        'alt_phone'       => $meta( array( '_dtsc_delivery_phone', '_dtsc_recipient_phone' ) ),
        'place_id'        => $meta( '_dtsc_shipping_place_id' ),
        'lat'             => $lat,
        'lng'             => $lng,
        'quote'           => $quote,
    );
}

/**
 * Render the "Delicate Checkout Data" sidebar metabox on the WooCommerce
 * order edit screen. Two sections:
 *   1. Checkout data written by the Delicate Two-Step Checkout at order time
 *      (method, delivery slot, alt phone, place ID, coordinates, quote) —
 *      only rendered when the corresponding meta exists.
 *   2. Shipment & Tracking, read straight from `_dcp_*` order meta that the
 *      Delicate Couriers platform writes back via the WooCommerce REST API
 *      after every shipment booking and every Shiplogic status webhook. When
 *      no tracking has been pushed yet (e.g. the order is still in the queue)
 *      the panel shows the documented "Awaiting waybill" empty state.
 */
function dcp_render_order_meta_box( $post_or_order ) {
    $order = ( $post_or_order instanceof WP_Post ) ? wc_get_order( $post_or_order->ID ) : $post_or_order;
    if ( ! $order ) {
        echo '<em>Order not found.</em>';
        return;
    }

    $cd = dcp_get_checkout_data( $order );

    // --- Sync state (set by dcp_sync_order on plugin order push). ---
    $platform_id = $order->get_meta( '_dcp_platform_order_id' );
    $synced_at   = $order->get_meta( '_dcp_synced_at' );

    // --- Shipment state (set by the platform on booking + status webhooks). ---
    $tracking_no   = $order->get_meta( '_dcp_tracking_number' );
    $consign_id    = $order->get_meta( '_dcp_consignment_id' );
    $courier       = $order->get_meta( '_dcp_courier_name' );
    $status_raw    = $order->get_meta( '_dcp_shipment_status' );
    $status_label  = $order->get_meta( '_dcp_shipment_status_label' );
    $tracking_url  = $order->get_meta( '_dcp_tracking_url' );
    $courier_rate  = $order->get_meta( '_dcp_courier_rate' );

    // --- Money. Customer-paid is derived from the order itself (fee lines +
    //     shipping lines) so it always matches what the customer was actually
    //     charged regardless of whether shipping was attached as a WC shipping
    //     line item or as a "Delivery" cart fee (DTSC). Courier rate is what
    //     we paid Shiplogic; margin is the difference. ---
    $customer_paid = dcp_get_customer_paid_shipping( $order );
    $rate_float    = is_numeric( $courier_rate ) ? (float) $courier_rate : null;
    $margin        = ( $rate_float !== null ) ? ( $customer_paid - $rate_float ) : null;

    $symbol = function_exists( 'get_woocommerce_currency_symbol' )
        ? get_woocommerce_currency_symbol( $order->get_currency() )
        : 'R ';

    $fmt_money = function( $value ) use ( $symbol ) {
        return $symbol . number_format( (float) $value, 2 );
    };
    ?>
    <div class="dcp-shipment-metabox">
        <?php
        // ---- Checkout data section (DTSC / checkout-time meta). ----
        $method_labels = array(
            'delivery'     => "\xF0\x9F\x9A\x9A Delivery",
            'collect'      => "\xF0\x9F\x8F\xAA Collection",
            'collection'   => "\xF0\x9F\x8F\xAA Collection",
            'special_trip' => "\xF0\x9F\x9B\xBB Special Trip",
        );
        $method_key   = strtolower( trim( (string) $cd['method'] ) );
        $method_label = $method_key !== '' ? ( $method_labels[ $method_key ] ?? ucwords( str_replace( '_', ' ', $method_key ) ) ) : null;

        $date_label = null;
        if ( ! empty( $cd['delivery_date'] ) ) {
            $ts         = strtotime( (string) $cd['delivery_date'] );
            $date_label = $ts ? date_i18n( 'F j, Y', $ts ) : (string) $cd['delivery_date'];
        }

        $has_checkout_data = $method_label || $date_label || ! empty( $cd['delivery_time'] )
            || ! empty( $cd['collection_time'] ) || ! empty( $cd['alt_phone'] )
            || ! empty( $cd['place_id'] ) || ( $cd['lat'] !== null && $cd['lng'] !== null )
            || ! empty( $cd['quote'] );
        ?>
        <?php if ( $has_checkout_data ) : ?>
            <?php if ( $method_label ) : ?>
                <p style="margin:.25em 0;"><strong>Method:</strong> <?php echo esc_html( $method_label ); ?></p>
            <?php endif; ?>
            <?php if ( $date_label ) : ?>
                <p style="margin:.25em 0;"><strong>&#128197; Delivery Date:</strong> <?php echo esc_html( $date_label ); ?></p>
            <?php endif; ?>
            <?php if ( ! empty( $cd['delivery_time'] ) ) : ?>
                <p style="margin:.25em 0;"><strong>&#128336; Delivery Time:</strong> <?php echo esc_html( $cd['delivery_time'] ); ?></p>
            <?php endif; ?>
            <?php if ( ! empty( $cd['collection_time'] ) ) : ?>
                <p style="margin:.25em 0;"><strong>&#128230; Courier Collection Time:</strong> <?php echo esc_html( $cd['collection_time'] ); ?></p>
            <?php endif; ?>

            <?php if ( ! empty( $cd['alt_phone'] ) || ! empty( $cd['place_id'] ) || ( $cd['lat'] !== null && $cd['lng'] !== null ) ) : ?>
                <hr style="margin:.75em 0;border:0;border-top:1px solid #eee;">
                <?php if ( ! empty( $cd['alt_phone'] ) ) : ?>
                    <p style="margin:.25em 0;"><strong>&#128222; Alt. Phone:</strong> <?php echo esc_html( $cd['alt_phone'] ); ?></p>
                <?php endif; ?>
                <?php if ( ! empty( $cd['place_id'] ) ) : ?>
                    <p style="margin:.25em 0;word-break:break-all;"><strong>Place ID:</strong> <?php echo esc_html( $cd['place_id'] ); ?></p>
                <?php endif; ?>
                <?php if ( $cd['lat'] !== null && $cd['lng'] !== null ) : ?>
                    <p style="margin:.25em 0;"><strong>Coordinates:</strong> <?php echo esc_html( $cd['lat'] . ', ' . $cd['lng'] ); ?></p>
                    <p style="margin:.25em 0;"><a href="<?php echo esc_url( 'https://www.google.com/maps/search/?api=1&query=' . rawurlencode( $cd['lat'] . ',' . $cd['lng'] ) ); ?>" target="_blank" rel="noopener">View on Google Maps &rarr;</a></p>
                <?php endif; ?>
            <?php endif; ?>

            <?php if ( ! empty( $cd['quote'] ) ) :
                $q_status  = isset( $cd['quote']['status'] )  ? (string) $cd['quote']['status']  : '';
                $q_price   = isset( $cd['quote']['price'] ) && is_numeric( $cd['quote']['price'] ) ? (float) $cd['quote']['price'] : null;
                $q_message = isset( $cd['quote']['message'] ) ? (string) $cd['quote']['message'] : '';
                if ( $q_status !== '' || $q_price !== null || $q_message !== '' ) : ?>
                    <hr style="margin:.75em 0;border:0;border-top:1px solid #eee;">
                    <?php if ( $q_status !== '' ) : ?>
                        <p style="margin:.25em 0;"><strong>Quote Status:</strong> <?php echo esc_html( $q_status ); ?></p>
                    <?php endif; ?>
                    <?php if ( $q_price !== null ) : ?>
                        <p style="margin:.25em 0;"><strong>Quoted Price:</strong> <?php echo esc_html( $fmt_money( $q_price ) ); ?></p>
                    <?php endif; ?>
                    <?php if ( $q_message !== '' ) : ?>
                        <p style="margin:.25em 0;"><strong>Message:</strong> <?php echo esc_html( $q_message ); ?></p>
                    <?php endif; ?>
                <?php endif; ?>
            <?php endif; ?>

            <hr style="margin:.75em 0;border:0;border-top:1px solid #eee;">
        <?php endif; ?>

        <p style="margin:.5em 0 .25em;"><strong>&#128230; Shipment &amp; Tracking</strong></p>
        <?php if ( empty( $tracking_no ) ) : ?>
            <p style="margin:.25em 0;color:#666;"><em>Awaiting waybill &mdash; shipment not yet created.</em></p>
        <?php else : ?>
            <p style="margin:.25em 0;">
                <strong>Tracking:</strong>
                <?php if ( ! empty( $tracking_url ) ) : ?>
                    <a href="<?php echo esc_url( $tracking_url ); ?>" target="_blank" rel="noopener"><?php echo esc_html( $tracking_no ); ?></a>
                <?php else : ?>
                    <?php echo esc_html( $tracking_no ); ?>
                <?php endif; ?>
            </p>
            <?php if ( ! empty( $courier ) ) : ?>
                <p style="margin:.25em 0;"><strong>Courier:</strong> <?php echo esc_html( $courier ); ?></p>
            <?php endif; ?>
            <?php if ( ! empty( $status_label ) || ! empty( $status_raw ) ) : ?>
                <p style="margin:.25em 0;"><strong>Status:</strong> <?php echo esc_html( $status_label ?: $status_raw ); ?></p>
            <?php endif; ?>
            <?php if ( ! empty( $consign_id ) ) : ?>
                <p style="margin:.25em 0;color:#888;font-size:11px;">Consignment ID: <?php echo esc_html( $consign_id ); ?></p>
            <?php endif; ?>
            <p style="margin:.5em 0 .25em;">
                <a class="button button-secondary" href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin-post.php?action=dcp_download_waybill&order_id=' . $order->get_id() ), 'dcp_download_waybill_' . $order->get_id() ) ); ?>">&#11015;&#65039; Download waybill (PDF)</a>
            </p>
        <?php endif; ?>

        <hr style="margin:.75em 0;border:0;border-top:1px solid #eee;">

        <p style="margin:.25em 0;"><strong>Customer Ref:</strong> #<?php echo esc_html( $order->get_order_number() ); ?></p>
        <p style="margin:.25em 0;"><strong>Customer Paid (Shipping):</strong> <?php echo esc_html( $fmt_money( $customer_paid ) ); ?></p>
        <?php if ( $rate_float !== null ) : ?>
            <p style="margin:.25em 0;"><strong>Courier Rate (Shiplogic):</strong> <?php echo esc_html( $fmt_money( $rate_float ) ); ?></p>
            <p style="margin:.25em 0;<?php echo ( $margin < 0 ) ? 'color:#b00020;' : ''; ?>"><strong>Markup Margin:</strong> <?php echo esc_html( $fmt_money( $margin ) ); ?></p>
        <?php endif; ?>

        <hr style="margin:.75em 0;border:0;border-top:1px solid #eee;">

        <p style="margin:.25em 0;color:#888;font-size:11px;">
            Platform ID: <?php echo $platform_id ? esc_html( $platform_id ) : '<em>not synced</em>'; ?>
            <?php if ( $synced_at ) : ?><br>Last sync: <?php echo esc_html( $synced_at ); ?><?php endif; ?>
        </p>

        <?php if ( ! $platform_id ) : ?>
            <p style="margin:.5em 0 0;"><a class="button button-secondary" href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin-post.php?action=dcp_manual_sync&order_id=' . $order->get_id() ), 'dcp_manual_sync_' . $order->get_id() ) ); ?>">Sync to platform now</a></p>
        <?php endif; ?>
    </div>
    <?php
}

/**
 * v2.9.0 — "Download waybill (PDF)" button handler. Fetches the shipping
 * label PDF from the platform over the signed plugin channel
 * (POST /api/webhooks/plugin/label, HMAC over the raw JSON body) and
 * streams it to the browser as a download. Admin-only, nonce-protected.
 */
add_action( 'admin_post_dcp_download_waybill', function() {
    $order_id = isset( $_GET['order_id'] ) ? intval( $_GET['order_id'] ) : 0;
    if ( ! $order_id || ! current_user_can( 'manage_woocommerce' ) ) {
        wp_die( 'Forbidden' );
    }
    check_admin_referer( 'dcp_download_waybill_' . $order_id );

    $order = wc_get_order( $order_id );
    if ( ! $order ) {
        wp_die( 'Order not found.' );
    }

    $result = dcp_platform_post_signed( '/api/webhooks/plugin/label', array( 'wc_order_id' => $order_id ) );

    $raw = isset( $result['raw'] ) ? (string) $result['raw'] : '';
    if ( empty( $result['success'] ) || strpos( $raw, '%PDF' ) !== 0 ) {
        $msg = 'Waybill not available yet.';
        if ( is_array( $result['body'] ) && ! empty( $result['body']['error'] ) ) {
            $msg = (string) $result['body']['error'];
        } elseif ( is_array( $result['body'] ) && ! empty( $result['body']['message'] ) ) {
            $msg = (string) $result['body']['message'];
        }
        wp_die(
            esc_html( 'Could not download waybill: ' . $msg ),
            'Waybill download',
            array( 'back_link' => true )
        );
    }

    $tracking = (string) $order->get_meta( '_dcp_tracking_number' );
    $filename = $tracking !== '' ? 'waybill_' . sanitize_file_name( $tracking ) . '.pdf' : 'waybill_order_' . $order_id . '.pdf';

    nocache_headers();
    header( 'Content-Type: application/pdf' );
    header( 'Content-Disposition: attachment; filename="' . $filename . '"' );
    header( 'Content-Length: ' . strlen( $raw ) );
    echo $raw; // phpcs:ignore WordPress.Security.EscapeOutput -- binary PDF stream
    exit;
} );

/* ============================================================================
 * v2.9.0 — PDF INVOICE / PACKING SLIP OUTPUT
 * Prints the Delicate Checkout Data block (method, delivery slot, collection
 * time, alt phone, coordinates) plus tracking number on documents generated
 * by "WooCommerce PDF Invoices & Packing Slips" (wpo_wcpdf). No-op when that
 * plugin is not installed — the hook simply never fires.
 * ========================================================================== */
add_action( 'wpo_wcpdf_after_order_data', 'dcp_wcpdf_render_checkout_rows', 10, 2 );
function dcp_wcpdf_render_checkout_rows( $document_type, $order ) {
    if ( ! $order instanceof WC_Order ) {
        return;
    }
    $rows = dcp_get_document_rows( $order );
    foreach ( $rows as $label => $value ) {
        echo '<tr class="dcp-checkout-data"><th>' . esc_html( $label ) . ':</th><td>' . esc_html( $value ) . '</td></tr>';
    }
}

/**
 * Fallback placement for wpo_wcpdf templates whose order-data table hook is
 * not reached (some custom templates): print a compact block after the
 * customer notes area instead. Guarded so the data is never printed twice.
 */
add_action( 'wpo_wcpdf_after_customer_notes', 'dcp_wcpdf_render_checkout_block', 10, 2 );
function dcp_wcpdf_render_checkout_block( $document_type, $order ) {
    if ( ! $order instanceof WC_Order ) {
        return;
    }
    if ( did_action( 'wpo_wcpdf_after_order_data' ) ) {
        return; // already rendered inside the order-data table
    }
    $rows = dcp_get_document_rows( $order );
    if ( empty( $rows ) ) {
        return;
    }
    echo '<div class="dcp-checkout-data" style="margin-top:8px;">';
    echo '<strong>Delicate Checkout Data</strong><br>';
    foreach ( $rows as $label => $value ) {
        echo esc_html( $label . ': ' . $value ) . '<br>';
    }
    echo '</div>';
}

/**
 * Shared label => value rows for printable documents. Only rows with a
 * value are returned, so non-DTSC stores print exactly what they have
 * (typically method + delivery date/time from the slot extractor, plus
 * tracking once the platform has written it back).
 *
 * @param WC_Order $order
 * @return array<string,string>
 */
function dcp_get_document_rows( $order ) {
    $cd   = dcp_get_checkout_data( $order );
    $rows = array();

    $method_key = strtolower( trim( (string) $cd['method'] ) );
    if ( $method_key !== '' ) {
        $method_labels = array(
            'delivery'     => 'Delivery',
            'collect'      => 'Collection',
            'collection'   => 'Collection',
            'special_trip' => 'Special Trip',
        );
        $rows['Fulfillment'] = $method_labels[ $method_key ] ?? ucwords( str_replace( '_', ' ', $method_key ) );
    }
    if ( ! empty( $cd['delivery_date'] ) ) {
        $ts = strtotime( (string) $cd['delivery_date'] );
        $rows['Delivery Date'] = $ts ? date_i18n( 'F j, Y', $ts ) : (string) $cd['delivery_date'];
    }
    if ( ! empty( $cd['delivery_time'] ) ) {
        $rows['Delivery Time'] = (string) $cd['delivery_time'];
    }
    if ( ! empty( $cd['collection_time'] ) ) {
        $rows['Courier Collection'] = (string) $cd['collection_time'];
    }
    if ( ! empty( $cd['alt_phone'] ) ) {
        $rows['Alt. Phone'] = (string) $cd['alt_phone'];
    }

    $tracking = (string) $order->get_meta( '_dcp_tracking_number' );
    if ( $tracking !== '' ) {
        $rows['Tracking No.'] = $tracking;
        $courier = (string) $order->get_meta( '_dcp_courier_name' );
        if ( $courier !== '' ) {
            $rows['Courier'] = $courier;
        }
    }

    /**
     * Filter the printable Delicate Checkout Data rows.
     *
     * @param array<string,string> $rows
     * @param WC_Order             $order
     */
    return apply_filters( 'dcp_document_rows', $rows, $order );
}

add_action( 'admin_post_dcp_manual_sync', function() {
    $order_id = isset( $_GET['order_id'] ) ? intval( $_GET['order_id'] ) : 0;
    if ( ! $order_id || ! current_user_can( 'manage_woocommerce' ) ) {
        wp_die( 'Forbidden' );
    }
    check_admin_referer( 'dcp_manual_sync_' . $order_id );
    dcp_sync_order( $order_id );
    wp_safe_redirect( wp_get_referer() ?: admin_url() );
    exit;
} );

/* ============================================================================
 * UPDATE CLIENT — Plugin Platform integration (license, updates, heartbeat)
 * ============================================================================
 * Talks to the platform's Plugin Platform endpoints (/api/v1/plugins/*):
 *
 *   POST /api/v1/plugins/activate-license    activate this site's license
 *   POST /api/v1/plugins/deactivate-license  release it
 *   POST /api/v1/plugins/check-update        WP-cron + transient driven
 *   GET  /api/v1/plugins/download?token=     signed, short-lived (~60s)
 *   GET  /api/v1/plugins/changelog?slug=     details popup content
 *   POST /api/v1/plugins/heartbeat           versions + latency + errors
 *   POST /api/v1/plugins/telemetry           batched events
 *
 * All bodies are JSON snake_case matching the C# DTOs in
 * DelicateCouriers.ApiService/Features/PluginPlatform/PluginPlatformDtos.cs.
 * Every request carries a unix `timestamp` + random `nonce` (replay guard).
 *
 * Because download tokens expire in ~60 seconds, the update offer injected
 * into the WP updater uses an internal `dcp-uc://` package marker; the real
 * signed URL is fetched fresh via upgrader_pre_download at install time and
 * the downloaded ZIP is verified against the server-published SHA256 before
 * WordPress is allowed to install it.
 * ========================================================================== */

dcp_define_if_unset( 'DCP_UPDATE_SLUG', 'delicate-courier-platform' );

define( 'DCP_UC_ACTIVATE_PATH',   '/api/v1/plugins/activate-license' );
define( 'DCP_UC_DEACTIVATE_PATH', '/api/v1/plugins/deactivate-license' );
define( 'DCP_UC_CHECK_PATH',      '/api/v1/plugins/check-update' );
define( 'DCP_UC_CHANGELOG_PATH',  '/api/v1/plugins/changelog' );
define( 'DCP_UC_HEARTBEAT_PATH',  '/api/v1/plugins/heartbeat' );
define( 'DCP_UC_TELEMETRY_PATH',  '/api/v1/plugins/telemetry' );

define( 'DCP_UC_CACHE_TRANSIENT', 'dcp_uc_update_info' );
define( 'DCP_UC_CACHE_TTL',       6 * HOUR_IN_SECONDS );
define( 'DCP_UC_CRON_HOOK',       'dcp_uc_cron_event' );
define( 'DCP_UC_PACKAGE_MARKER',  'dcp-uc://' );

/* ---------------------------------------------------------------------------
 * State helpers
 * ------------------------------------------------------------------------- */

function dcp_uc_license_key()    { return trim( (string) dcp_opt( 'license_key' ) ); }
function dcp_uc_license_status() { return (string) get_option( 'dcp_license_status', 'inactive' ); }
function dcp_uc_license_active() { return dcp_uc_license_key() !== '' && dcp_uc_license_status() === 'active'; }

/** Stable per-installation identifier, generated once. */
function dcp_uc_install_key() {
    $key = (string) get_option( 'dcp_install_key', '' );
    if ( $key === '' ) {
        $key = function_exists( 'wp_generate_uuid4' ) ? wp_generate_uuid4() : bin2hex( random_bytes( 16 ) );
        update_option( 'dcp_install_key', $key, false );
    }
    return $key;
}

function dcp_uc_domain() {
    $host = parse_url( home_url(), PHP_URL_HOST );
    return $host ? strtolower( $host ) : '';
}

function dcp_uc_wc_version() {
    return defined( 'WC_VERSION' ) ? WC_VERSION : ( function_exists( 'WC' ) && is_object( WC() ) ? WC()->version : null );
}

/** Common snake_case body for every plugin-platform request. */
function dcp_uc_body( $extra = array() ) {
    $body = array(
        'slug'           => DCP_UPDATE_SLUG,
        'license_key'    => dcp_uc_license_key(),
        'install_key'    => dcp_uc_install_key(),
        'domain'         => dcp_uc_domain(),
        'timestamp'      => time(),
        'nonce'          => bin2hex( random_bytes( 16 ) ),
        'plugin_version' => DCP_VERSION,
        'wp_version'     => get_bloginfo( 'version' ),
        'wc_version'     => dcp_uc_wc_version(),
        'php_version'    => PHP_VERSION,
    );
    return array_merge( $body, $extra );
}

/**
 * POST JSON to a plugin-platform path. Returns
 *   [ 'code' => int|0, 'json' => array|null, 'error' => string|null, 'latency_ms' => int ]
 */
function dcp_uc_post( $path, $body, $timeout = 15 ) {
    $url   = rtrim( dcp_platform_base_url(), '/' ) . $path;
    $raw   = wp_json_encode( $body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
    $start = microtime( true );
    $res   = wp_remote_post( $url, array(
        'timeout' => $timeout,
        'headers' => array(
            'Content-Type' => 'application/json',
            'Accept'       => 'application/json',
            'User-Agent'   => 'DelicateCourierPlatform/' . DCP_VERSION . ' WordPress/' . get_bloginfo( 'version' ) . ' (update-client)',
        ),
        'body'    => $raw,
    ) );
    $latency = (int) round( ( microtime( true ) - $start ) * 1000 );
    update_option( 'dcp_uc_last_latency_ms', $latency, false );

    if ( is_wp_error( $res ) ) {
        dcp_log( 'update.http', 'POST ' . $path . ' failed: ' . $res->get_error_message(), 'error' );
        return array( 'code' => 0, 'json' => null, 'error' => $res->get_error_message(), 'latency_ms' => $latency );
    }
    $code = (int) wp_remote_retrieve_response_code( $res );
    $json = json_decode( (string) wp_remote_retrieve_body( $res ), true );
    $err  = null;
    if ( $code >= 400 ) {
        $err = is_array( $json ) && ! empty( $json['error'] ) ? (string) $json['error'] : ( 'http_' . $code );
    }
    return array( 'code' => $code, 'json' => is_array( $json ) ? $json : null, 'error' => $err, 'latency_ms' => $latency );
}

/** Human-readable text for the platform's machine error codes. */
function dcp_uc_error_text( $code ) {
    $map = array(
        'invalid_license'         => 'License key not recognised. Check for typos and try again.',
        'unknown_plugin'          => 'This plugin build is not registered on the platform. Contact support.',
        'domain_mismatch'         => 'This license is bound to a different website domain. Contact support to move it.',
        'license_expired'         => 'This license has expired. Contact support to renew it.',
        'license_revoked'         => 'This license has been revoked. Contact support.',
        'license_not_active'      => 'License is not active. Activate it first.',
        'timestamp_out_of_window' => 'Your server clock appears to be wrong — activation requests are being rejected. Fix the server time and retry.',
        'nonce_replayed'          => 'Request was rejected as a duplicate. Please retry.',
    );
    return isset( $map[ $code ] ) ? $map[ $code ] : 'Request failed (' . $code . ').';
}

/* ---------------------------------------------------------------------------
 * License activate / deactivate
 * ------------------------------------------------------------------------- */

function dcp_uc_activate( $license_key ) {
    dcp_save_secret_option( 'dcp_license_key', $license_key );
    $res = dcp_uc_post( DCP_UC_ACTIVATE_PATH, dcp_uc_body() );
    if ( $res['error'] === null && ! empty( $res['json']['activated'] ) ) {
        update_option( 'dcp_license_status', 'active', false );
        update_option( 'dcp_license_message', '', false );
        dcp_uc_queue_telemetry( 'license_activated', array( 'domain' => dcp_uc_domain() ) );
        delete_transient( DCP_UC_CACHE_TRANSIENT );
        dcp_log( 'update.license', 'License activated for ' . dcp_uc_domain() );
        return true;
    }
    $code = $res['error'] ?: 'unknown_error';
    update_option( 'dcp_license_status', 'error', false );
    update_option( 'dcp_license_message', dcp_uc_error_text( $code ), false );
    delete_transient( DCP_UC_CACHE_TRANSIENT );
    dcp_log( 'update.license', 'License activation failed: ' . $code, 'error' );
    return false;
}

function dcp_uc_deactivate() {
    $res = dcp_uc_post( DCP_UC_DEACTIVATE_PATH, dcp_uc_body() );
    // Locally deactivate regardless — the merchant asked us to stop.
    update_option( 'dcp_license_status', 'inactive', false );
    update_option( 'dcp_license_message', '', false );
    delete_transient( DCP_UC_CACHE_TRANSIENT );
    dcp_log( 'update.license', 'License deactivated (server said: ' . ( $res['error'] ?: 'ok' ) . ')' );
    return $res['error'] === null;
}

/* ---------------------------------------------------------------------------
 * Update check (cron + transient)
 * ------------------------------------------------------------------------- */

/**
 * Ask the platform for the best release for this installation.
 * Result cached in a transient; pass $force to bypass the cache
 * (used right before an actual download so the token is fresh).
 */
function dcp_uc_check_update( $force = false ) {
    if ( dcp_uc_license_key() === '' ) {
        return null;
    }
    if ( ! $force ) {
        $cached = get_transient( DCP_UC_CACHE_TRANSIENT );
        if ( is_array( $cached ) ) {
            return $cached;
        }
    }
    $res = dcp_uc_post( DCP_UC_CHECK_PATH, dcp_uc_body() );
    if ( $res['error'] !== null || ! is_array( $res['json'] ) ) {
        // License problems disable update offers until the merchant fixes them.
        if ( in_array( $res['error'], array( 'invalid_license', 'license_expired', 'license_revoked', 'license_not_active', 'domain_mismatch' ), true ) ) {
            update_option( 'dcp_license_status', 'error', false );
            update_option( 'dcp_license_message', dcp_uc_error_text( $res['error'] ), false );
        }
        // Cache the miss briefly so a broken platform doesn't get hammered.
        set_transient( DCP_UC_CACHE_TRANSIENT, array( 'update_available' => false, 'error' => $res['error'] ), 15 * MINUTE_IN_SECONDS );
        return null;
    }
    if ( dcp_uc_license_status() !== 'active' ) {
        // A successful check-update proves the license is good again.
        update_option( 'dcp_license_status', 'active', false );
        update_option( 'dcp_license_message', '', false );
    }
    $info = $res['json'];
    update_option( 'dcp_uc_last_check', time(), false );
    set_transient( DCP_UC_CACHE_TRANSIENT, $info, DCP_UC_CACHE_TTL );
    return $info;
}

/** Inject the platform's offer into WordPress's update system. */
add_filter( 'pre_set_site_transient_update_plugins', 'dcp_uc_inject_update' );
function dcp_uc_inject_update( $transient ) {
    if ( ! is_object( $transient ) ) {
        return $transient;
    }
    if ( ! dcp_uc_license_active() && dcp_uc_license_key() === '' ) {
        return $transient;
    }
    $info = dcp_uc_check_update();
    $slug = dirname( DCP_PLUGIN_BASENAME );
    if ( is_array( $info ) && ! empty( $info['update_available'] ) && ! empty( $info['version'] )
         && version_compare( $info['version'], DCP_VERSION, '>' ) ) {
        $update = (object) array(
            'id'           => DCP_UPDATE_SLUG,
            'slug'         => $slug,
            'plugin'       => DCP_PLUGIN_BASENAME,
            'new_version'  => (string) $info['version'],
            'url'          => rtrim( dcp_platform_base_url(), '/' ),
            // Marker URL — resolved to a fresh signed URL in
            // dcp_uc_pre_download() because real tokens expire in ~60s.
            'package'      => DCP_UC_PACKAGE_MARKER . DCP_UPDATE_SLUG,
            'requires'     => isset( $info['min_wp_version'] ) ? (string) $info['min_wp_version'] : '',
            'requires_php' => isset( $info['min_php_version'] ) ? (string) $info['min_php_version'] : '',
            'icons'        => array(),
        );
        $transient->response[ DCP_PLUGIN_BASENAME ] = $update;
        unset( $transient->no_update[ DCP_PLUGIN_BASENAME ] );
    } else {
        // Tell WP explicitly that we're up to date so it stops asking w.org.
        $transient->no_update[ DCP_PLUGIN_BASENAME ] = (object) array(
            'id'          => DCP_UPDATE_SLUG,
            'slug'        => $slug,
            'plugin'      => DCP_PLUGIN_BASENAME,
            'new_version' => DCP_VERSION,
            'url'         => rtrim( dcp_platform_base_url(), '/' ),
            'package'     => '',
        );
        unset( $transient->response[ DCP_PLUGIN_BASENAME ] );
    }
    return $transient;
}

/**
 * Resolve our package marker into a real download: fetch a fresh signed
 * URL, download the ZIP, verify its SHA256 against the server-published
 * hash, and hand WordPress the verified temp file.
 */
add_filter( 'upgrader_pre_download', 'dcp_uc_pre_download', 10, 3 );
function dcp_uc_pre_download( $reply, $package, $upgrader ) {
    if ( ! is_string( $package ) || strpos( $package, DCP_UC_PACKAGE_MARKER ) !== 0 ) {
        return $reply;
    }
    $info = dcp_uc_check_update( true ); // force → fresh 60s token
    if ( ! is_array( $info ) || empty( $info['update_available'] ) || empty( $info['download_url'] ) ) {
        return new WP_Error( 'dcp_uc_no_update', 'The platform no longer offers this update. Refresh and try again.' );
    }
    $url = (string) $info['download_url'];
    if ( strpos( $url, 'http' ) !== 0 ) {
        $url = rtrim( dcp_platform_base_url(), '/' ) . ( $url[0] === '/' ? '' : '/' ) . $url;
    }
    $file = download_url( $url, 120 );
    if ( is_wp_error( $file ) ) {
        dcp_log( 'update.download', 'Download failed: ' . $file->get_error_message(), 'error' );
        return $file;
    }
    if ( ! empty( $info['sha256'] ) ) {
        $actual = hash_file( 'sha256', $file );
        if ( ! hash_equals( strtolower( (string) $info['sha256'] ), strtolower( (string) $actual ) ) ) {
            @unlink( $file );
            dcp_log( 'update.download', 'SHA256 mismatch on downloaded package — install aborted.', 'error' );
            dcp_uc_queue_telemetry( 'update_checksum_mismatch', array( 'expected' => $info['sha256'], 'actual' => $actual, 'version' => $info['version'] ) );
            return new WP_Error( 'dcp_uc_bad_checksum', 'Downloaded update failed its integrity check and was discarded. Please try again.' );
        }
    }
    dcp_log( 'update.download', 'Verified package for v' . ( $info['version'] ?? '?' ) . ' downloaded.' );
    return $file;
}

/** Telemetry + cache-bust after WordPress finishes installing our update. */
add_action( 'upgrader_process_complete', 'dcp_uc_after_update', 10, 2 );
function dcp_uc_after_update( $upgrader, $hook_extra ) {
    if ( empty( $hook_extra['type'] ) || $hook_extra['type'] !== 'plugin' ) {
        return;
    }
    $plugins = isset( $hook_extra['plugins'] ) ? (array) $hook_extra['plugins'] : array();
    if ( ! in_array( DCP_PLUGIN_BASENAME, $plugins, true ) && ( $hook_extra['plugin'] ?? '' ) !== DCP_PLUGIN_BASENAME ) {
        return;
    }
    delete_transient( DCP_UC_CACHE_TRANSIENT );
    dcp_uc_queue_telemetry( 'update_installed', array( 'from_version' => DCP_VERSION ) );
    dcp_uc_flush_telemetry();
}

/* ---------------------------------------------------------------------------
 * Plugin-details popup (changelog)
 * ------------------------------------------------------------------------- */

add_filter( 'plugins_api', 'dcp_uc_plugins_api', 20, 3 );
function dcp_uc_plugins_api( $result, $action, $args ) {
    if ( $action !== 'plugin_information' || ! isset( $args->slug ) ) {
        return $result;
    }
    $our_slug = dirname( DCP_PLUGIN_BASENAME );
    if ( $args->slug !== $our_slug && $args->slug !== DCP_UPDATE_SLUG ) {
        return $result;
    }
    $url = rtrim( dcp_platform_base_url(), '/' ) . DCP_UC_CHANGELOG_PATH . '?slug=' . rawurlencode( DCP_UPDATE_SLUG ) . '&limit=10';
    $res = wp_remote_get( $url, array( 'timeout' => 10, 'headers' => array( 'Accept' => 'application/json' ) ) );
    $releases = array();
    if ( ! is_wp_error( $res ) && (int) wp_remote_retrieve_response_code( $res ) === 200 ) {
        $json = json_decode( (string) wp_remote_retrieve_body( $res ), true );
        if ( isset( $json['releases'] ) && is_array( $json['releases'] ) ) {
            $releases = $json['releases'];
        }
    }
    $changelog_html = '';
    foreach ( $releases as $rel ) {
        $ver  = isset( $rel['version'] ) ? esc_html( (string) $rel['version'] ) : '?';
        $date = isset( $rel['released_on'] ) ? esc_html( substr( (string) $rel['released_on'], 0, 10 ) ) : '';
        $body = isset( $rel['changelog'] ) ? nl2br( esc_html( (string) $rel['changelog'] ) ) : '';
        $changelog_html .= '<h4>' . $ver . ( $date ? ' <small>(' . $date . ')</small>' : '' ) . '</h4><p>' . $body . '</p>';
    }
    if ( $changelog_html === '' ) {
        $changelog_html = '<p>No changelog available.</p>';
    }
    $info    = dcp_uc_check_update();
    $offered = is_array( $info ) && ! empty( $info['update_available'] ) ? (string) $info['version'] : DCP_VERSION;
    return (object) array(
        'name'          => 'Delicate Courier Platform',
        'slug'          => $args->slug,
        'version'       => $offered,
        'author'        => '<a href="https://delicatecourier.co.za">Delicate Courier</a>',
        'homepage'      => 'https://delicatecourier.co.za',
        'requires'      => is_array( $info ) && ! empty( $info['min_wp_version'] ) ? $info['min_wp_version'] : '5.8',
        'requires_php'  => is_array( $info ) && ! empty( $info['min_php_version'] ) ? $info['min_php_version'] : '7.4',
        'last_updated'  => isset( $releases[0]['released_on'] ) ? (string) $releases[0]['released_on'] : '',
        'sections'      => array(
            'description' => '<p>WooCommerce integration for the Delicate Courier Platform: live courier rates at checkout and signed order push for automated shipment creation.</p>',
            'changelog'   => $changelog_html,
        ),
        'download_link' => '', // installs go through the signed-token flow only
    );
}

/* ---------------------------------------------------------------------------
 * Heartbeat + telemetry
 * ------------------------------------------------------------------------- */

/** Queue a telemetry event; flushed on cron (or right after an update). */
function dcp_uc_queue_telemetry( $event_type, $payload = null ) {
    $queue = get_option( 'dcp_uc_telemetry_queue', array() );
    if ( ! is_array( $queue ) ) {
        $queue = array();
    }
    $queue[] = array(
        'event_type'  => (string) $event_type,
        'occurred_at' => gmdate( 'c' ),
        'payload'     => $payload,
    );
    if ( count( $queue ) > 100 ) {
        $queue = array_slice( $queue, -100 );
    }
    update_option( 'dcp_uc_telemetry_queue', $queue, false );
}

function dcp_uc_flush_telemetry() {
    if ( dcp_uc_license_key() === '' ) {
        return;
    }
    $queue = get_option( 'dcp_uc_telemetry_queue', array() );
    if ( ! is_array( $queue ) || empty( $queue ) ) {
        return;
    }
    $res = dcp_uc_post( DCP_UC_TELEMETRY_PATH, dcp_uc_body( array( 'events' => array_values( $queue ) ) ), 10 );
    if ( $res['error'] === null ) {
        update_option( 'dcp_uc_telemetry_queue', array(), false );
    }
}

/** Last few error-level rows from the local debug log, for the heartbeat. */
function dcp_uc_recent_errors() {
    $logs = get_option( 'dcp_debug_logs', array() );
    if ( ! is_array( $logs ) ) {
        return array();
    }
    $errors = array();
    foreach ( array_reverse( $logs ) as $entry ) {
        if ( ( $entry['level'] ?? '' ) !== 'error' ) {
            continue;
        }
        $errors[] = array(
            'time'    => (string) ( $entry['time'] ?? '' ),
            'context' => (string) ( $entry['context'] ?? '' ),
            'detail'  => substr( (string) ( $entry['detail'] ?? '' ), 0, 300 ),
        );
        if ( count( $errors ) >= 5 ) {
            break;
        }
    }
    return $errors;
}

function dcp_uc_heartbeat() {
    if ( dcp_uc_license_key() === '' ) {
        return;
    }
    $body = dcp_uc_body( array(
        'api_latency_ms' => (int) get_option( 'dcp_uc_last_latency_ms', 0 ),
        'recent_errors'  => dcp_uc_recent_errors(),
    ) );
    dcp_uc_post( DCP_UC_HEARTBEAT_PATH, $body, 10 );
}

/* ---------------------------------------------------------------------------
 * Cron wiring
 * ------------------------------------------------------------------------- */

add_action( 'init', 'dcp_uc_schedule_cron' );
function dcp_uc_schedule_cron() {
    if ( ! wp_next_scheduled( DCP_UC_CRON_HOOK ) ) {
        wp_schedule_event( time() + MINUTE_IN_SECONDS, 'twicedaily', DCP_UC_CRON_HOOK );
    }
}

add_action( DCP_UC_CRON_HOOK, 'dcp_uc_cron_run' );
function dcp_uc_cron_run() {
    delete_transient( DCP_UC_CACHE_TRANSIENT ); // force a fresh check
    dcp_uc_check_update( true );
    dcp_uc_heartbeat();
    dcp_uc_flush_telemetry();
}

register_deactivation_hook( DCP_PLUGIN_FILE, 'dcp_uc_clear_cron' );
function dcp_uc_clear_cron() {
    wp_clear_scheduled_hook( DCP_UC_CRON_HOOK );
}

/* ---------------------------------------------------------------------------
 * Settings-page section (rendered from dcp_render_settings_page)
 * ------------------------------------------------------------------------- */

function dcp_uc_render_license_section() {
    if ( ! current_user_can( 'manage_woocommerce' ) ) {
        return;
    }
    // Handle the license form posts for this section.
    if ( isset( $_POST['dcp_uc_action'] ) && check_admin_referer( 'dcp_uc_license' ) ) {
        $action = sanitize_key( wp_unslash( $_POST['dcp_uc_action'] ) );
        if ( $action === 'activate' ) {
            $key = isset( $_POST['dcp_license_key'] ) ? trim( wp_unslash( $_POST['dcp_license_key'] ) ) : '';
            if ( $key === '' ) {
                echo '<div class="notice notice-error is-dismissible"><p>Enter a license key first.</p></div>';
            } elseif ( dcp_uc_activate( $key ) ) {
                echo '<div class="notice notice-success is-dismissible"><p>License activated. Automatic updates are now enabled.</p></div>';
            } else {
                echo '<div class="notice notice-error is-dismissible"><p>' . esc_html( get_option( 'dcp_license_message', 'Activation failed.' ) ) . '</p></div>';
            }
        } elseif ( $action === 'deactivate' ) {
            dcp_uc_deactivate();
            echo '<div class="notice notice-success is-dismissible"><p>License deactivated on this site.</p></div>';
        } elseif ( $action === 'check_now' ) {
            delete_transient( DCP_UC_CACHE_TRANSIENT );
            $info = dcp_uc_check_update( true );
            if ( is_array( $info ) && ! empty( $info['update_available'] ) ) {
                delete_site_transient( 'update_plugins' ); // make WP re-ask us immediately
                echo '<div class="notice notice-success is-dismissible"><p>Update available: version ' . esc_html( (string) $info['version'] ) . '. Install it from the <a href="' . esc_url( admin_url( 'plugins.php' ) ) . '">Plugins page</a>.</p></div>';
            } elseif ( is_array( $info ) ) {
                echo '<div class="notice notice-info is-dismissible"><p>You are on the latest version (' . esc_html( DCP_VERSION ) . ').</p></div>';
            } else {
                echo '<div class="notice notice-error is-dismissible"><p>Could not reach the update service' . ( get_option( 'dcp_license_message' ) ? ': ' . esc_html( get_option( 'dcp_license_message' ) ) : '.' ) . '</p></div>';
            }
        }
    }

    $status  = dcp_uc_license_status();
    $message = (string) get_option( 'dcp_license_message', '' );
    $last    = (int) get_option( 'dcp_uc_last_check', 0 );
    ?>
    <hr>
    <h2>License &amp; automatic updates</h2>
    <p class="description">Activate your license to receive automatic plugin updates directly from the Delicate Courier Platform. Your platform administrator issues the license key.</p>
    <form method="post" action="">
        <?php wp_nonce_field( 'dcp_uc_license' ); ?>
        <table class="form-table" role="presentation">
            <tr><th><label for="dcp_license_key">License key</label></th>
                <td>
                    <input name="dcp_license_key" id="dcp_license_key" type="text" class="regular-text code"
                           value="<?php echo esc_attr( dcp_uc_license_key() ); ?>"
                           placeholder="DCP-XXXX-XXXX-XXXX-XXXX" <?php echo dcp_uc_license_active() ? 'readonly' : ''; ?>>
                    <p class="description">
                        Status:
                        <?php if ( dcp_uc_license_active() ) : ?>
                            <strong style="color:#00a32a;">Active</strong> on <code><?php echo esc_html( dcp_uc_domain() ); ?></code>
                        <?php elseif ( $status === 'error' ) : ?>
                            <strong style="color:#b32d2e;">Problem</strong> — <?php echo esc_html( $message ?: 'License needs attention.' ); ?> Updates are paused until this is fixed.
                        <?php else : ?>
                            <strong style="color:#996800;">Not activated</strong> — automatic updates are disabled.
                        <?php endif; ?>
                        <?php if ( $last ) : ?>
                            &middot; Last update check: <?php echo esc_html( human_time_diff( $last ) ); ?> ago
                        <?php endif; ?>
                    </p>
                </td></tr>
        </table>
        <p>
            <?php if ( dcp_uc_license_active() ) : ?>
                <button type="submit" name="dcp_uc_action" value="check_now" class="button button-primary">Check for updates now</button>
                <button type="submit" name="dcp_uc_action" value="deactivate" class="button" onclick="return confirm('Deactivate the license on this site? Automatic updates will stop.');">Deactivate license</button>
            <?php else : ?>
                <button type="submit" name="dcp_uc_action" value="activate" class="button button-primary">Activate license</button>
                <?php if ( dcp_uc_license_key() !== '' ) : ?>
                    <button type="submit" name="dcp_uc_action" value="check_now" class="button">Retry / check for updates</button>
                <?php endif; ?>
            <?php endif; ?>
        </p>
    </form>
    <?php
}
