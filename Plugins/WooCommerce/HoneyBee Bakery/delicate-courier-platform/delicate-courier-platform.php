<?php
/**
 * Plugin Name: Delicate Courier Platform — HoneyBee Bakery
 * Plugin URI:  https://delicatecourier.co.za
 * Description: WooCommerce integration for the Delicate Courier Platform. Provides live courier rates at checkout (server-side geocoded via the platform) and signed order push to the platform for automated shipment creation.
 * Version:     2.5.0
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

define( 'DCP_VERSION',         '2.5.0' );
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

// Mirror every dcp_log() entry up to the platform (SuperAdmin "Debug Log"
// page) — non-blocking, signed with the same per-store webhook secret.
define( 'DCP_PLATFORM_DEBUG_LOG_PATH', '/api/webhooks/plugin/debug-log' );

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
/* --- Pre-baked merchant defaults (injected by branded build) ---
 * These run before the dcp_define_if_unset() calls in the shared source
 * below, so they take precedence and the corresponding settings fields are
 * hidden from the merchant UI. */
define( 'DCP_DEFAULT_SHIPLOGIC_ACCOUNT_ID', 449679 );
define( 'DCP_DEFAULT_SHIPLOGIC_PROVIDER_ID', 35 );
define( 'DCP_DEFAULT_SERVICE_LEVEL_CODE', 'STD' );
define( 'DCP_DEFAULT_COLLECTION_COMPANY', 'HoneyBee Bakery' );
define( 'DCP_DEFAULT_COLLECTION_STREET', '5 Graham Road' );
define( 'DCP_DEFAULT_COLLECTION_SUBURB', 'Shere' );
define( 'DCP_DEFAULT_COLLECTION_CITY', 'Pretoria' );
define( 'DCP_DEFAULT_COLLECTION_PROVINCE', 'Gauteng' );
define( 'DCP_DEFAULT_COLLECTION_POSTAL_CODE', '0084' );
define( 'DCP_DEFAULT_COLLECTION_COUNTRY', 'ZA' );
define( 'DCP_DEFAULT_COLLECTION_LATITUDE', -25.7929 );
define( 'DCP_DEFAULT_COLLECTION_LONGITUDE', 28.361 );

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

    $addr = array(
        'type'           => 'business',
        'company'        => DCP_DEFAULT_COLLECTION_COMPANY ?: get_bloginfo( 'name' ),
        'street_address' => DCP_DEFAULT_COLLECTION_STREET  ?: trim( (string) get_option( 'woocommerce_store_address', '' ) . ' ' . (string) get_option( 'woocommerce_store_address_2', '' ) ),
        'local_area'     => DCP_DEFAULT_COLLECTION_SUBURB,
        'city'           => DCP_DEFAULT_COLLECTION_CITY    ?: (string) get_option( 'woocommerce_store_city', '' ),
        'zone'           => DCP_DEFAULT_COLLECTION_PROVINCE ?: ( $wc_country_state['state'] ?? '' ),
        'code'           => DCP_DEFAULT_COLLECTION_POSTAL_CODE ?: (string) get_option( 'woocommerce_store_postcode', '' ),
        'country'        => DCP_DEFAULT_COLLECTION_COUNTRY ?: ( $wc_country_state['country'] ?? 'ZA' ),
        'lat'            => DCP_DEFAULT_COLLECTION_LATITUDE  !== '' ? floatval( DCP_DEFAULT_COLLECTION_LATITUDE )  : null,
        'lng'            => DCP_DEFAULT_COLLECTION_LONGITUDE !== '' ? floatval( DCP_DEFAULT_COLLECTION_LONGITUDE ) : null,
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

function dcp_log( $context, $detail = '', $level = 'info', $data = null, $source = 'server' ) {
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

    // Fire-and-forget mirror to the platform so SuperAdmins can see this
    // entry inside the platform's Debug Log page without having to log into
    // every merchant's WP-Admin. Non-blocking — checkout latency is not
    // affected even if the platform is slow or unreachable.
    dcp_ship_debug_log_entry( $entry, (string) $source );
}

/**
 * Ship a single debug-log entry to the platform's ingest endpoint.
 *
 * Manual HMAC + wp_remote_post with blocking=false so we never recurse
 * (no dcp_log_* calls inside) and never block the WordPress request that
 * generated the entry. If the store-id / secret aren't configured yet, or
 * the option is disabled, the ship is silently skipped — the merchant's
 * local log still records the entry either way.
 */
function dcp_ship_debug_log_entry( $entry, $source = 'server' ) {
    // Guard: re-entrancy protection. wp_remote_post internally fires
    // http_api_curl / http_api_debug actions; in pathological setups
    // another logging plugin could call dcp_log() from inside one of those
    // hooks. The static flag breaks the cycle.
    static $shipping = false;
    if ( $shipping ) { return; }

    if ( ! apply_filters( 'dcp_ship_debug_logs_to_platform', true ) ) { return; }

    $store_id = dcp_store_id();
    $secret   = dcp_webhook_secret();
    if ( $store_id === '' || $secret === '' ) { return; }

    $url = rtrim( dcp_platform_base_url(), '/' ) . DCP_PLATFORM_DEBUG_LOG_PATH;

    // The local log keeps `time` as the WP site's wall clock (for the
    // merchant's own admin panel) but we ship `time_utc` to the platform
    // so cross-tenant chronological ordering is unambiguous regardless of
    // each merchant's WP timezone setting. The platform parses time_utc
    // strictly as UTC; falls back to `time` only if time_utc is missing.
    $payload = array(
        'time'           => isset( $entry['time'] )    ? (string) $entry['time']    : current_time( 'mysql' ),
        'time_utc'       => gmdate( 'c' ),
        'level'          => isset( $entry['level'] )   ? (string) $entry['level']   : 'info',
        'context'        => isset( $entry['context'] ) ? (string) $entry['context'] : 'uncategorised',
        'detail'         => isset( $entry['detail'] )  ? (string) $entry['detail']  : '',
        'source'         => (string) $source,
        'store_id'       => (string) $store_id,
        'plugin_version' => defined( 'DCP_VERSION' ) ? DCP_VERSION : '',
    );
    if ( isset( $entry['data'] ) ) {
        $payload['data'] = $entry['data'];
    }

    $body = wp_json_encode( $payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
    if ( $body === false ) { return; }

    $sig = base64_encode( hash_hmac( 'sha256', $body, $secret, true ) );

    $shipping = true;
    try {
        // Deliberately bypass dcp_platform_post_signed — that wrapper adds
        // retry + heavy dcp_log_http instrumentation that would loop forever.
        @wp_remote_post( $url, array(
            'timeout'  => 2,
            'blocking' => false,
            'headers'  => array(
                'Content-Type'       => 'application/json',
                'Accept'             => 'application/json',
                'X-Store-ID'         => (string) $store_id,
                'X-Plugin-Signature' => $sig,
                'User-Agent'         => 'DelicateCourierPlatform/' . ( defined( 'DCP_VERSION' ) ? DCP_VERSION : '0' ) . ' WordPress/' . get_bloginfo( 'version' ) . ' (debug-log)',
            ),
            'body'     => $body,
        ) );
    } finally {
        // Always release the re-entrancy guard, even if wp_remote_post or
        // a downstream filter throws — otherwise we'd silently lose all
        // subsequent log entries in this PHP request.
        $shipping = false;
    }
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
        'dcp_collection_country', 'dcp_collection_latitude', 'dcp_collection_longitude',
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

    // Secrets must NOT autoload.
    foreach ( array( 'dcp_store_id', 'dcp_webhook_secret', 'dcp_shiplogic_bearer' ) as $secret ) {
        dcp_ensure_secret_autoload_off( $secret );
    }
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

    // Inbound REST route: the platform posts shipment _dcp_* meta to this
    // endpoint. Bypasses the WooCommerce REST API entirely (and therefore
    // its `edit_shop_orders`/Consumer-Key permission model), because the
    // handler runs as PHP inside WordPress and writes via update_meta_data.
    add_action( 'rest_api_init',                    'dcp_register_meta_receiver_route' );
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
        // empty one. Guards against the "platform re-syncs a record
        // that hasn't yet been populated from Shiplogic and wipes a real
        // tracking number" failure mode. Platform can still clear a
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
        $order->add_order_note( $note_text, $note_is_customer_note ? 1 : 0 );
        $note_added = true;
    }

    if ( $updated > 0 ) {
        $order->save();
        // Belt-and-braces cache invalidation. $order->save() already
        // updates WC's own caches, but an aggressive object-cache layer
        // or page-cache plugin can otherwise serve a stale metabox.
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
                }
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
 * Fetch live courier rates by routing through the Delicate Courier Platform
 * (which geocodes the addresses, looks up the tenant's Shiplogic token, and
 * forwards to /v2/rates on the merchant's behalf).
 *
 * Returns either a WP_Error or an array of rate rows already normalised into
 * the legacy { rate, service_level: { code, name } } shape that
 * calculate_shipping() consumes — so the rest of the pipeline is unchanged.
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
        'timeout' => 30,
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

/* ============================================================================
 * PLATFORM HTTP CLIENT (signed)
 * ========================================================================== */

/**
 * POST a JSON body to the platform with HMAC-SHA256(rawBody, webhook_secret)
 * encoded as base64 in X-Plugin-Signature, and the Store ID in X-Store-ID.
 *
 * @return array { success: bool, status: int, body: array|string, raw: string }
 */
function dcp_platform_post_signed( $path, $payload ) {
    $store_id = dcp_store_id();
    $secret   = dcp_webhook_secret();

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
    $phone = $shipping['phone'] ?? ( $billing['phone'] ?? '' );
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
    // Collection time is intentionally NOT derived plugin-side anymore.
    // The platform backend is the single source of truth for the collection
    // window (120 to 90 minutes before the end of the delivery window, e.g.
    // chosen 16:30 -> delivery 16:00-16:30, collection 14:30-15:00).
    $collection_time = null;

    $fulfillment_type = dcp_extract_fulfillment_type( $order );

    return array(
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
            'name'  => $name,
            'email' => $email,
            'phone' => $phone,
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
}

/**
 * Decide whether the order is a courier delivery or an in-person collect.
 * See Default Plugin for full docblock. Returns 'collect' | 'delivery' |
 * 'special_trip'.
 */
function dcp_extract_fulfillment_type( $order ) {
    $override = (string) $order->get_meta( '_dcp_fulfillment_type' );
    $override = strtolower( trim( $override ) );
    if ( in_array( $override, array( 'collect', 'delivery', 'special_trip' ), true ) ) {
        return $override;
    }

    foreach ( $order->get_shipping_methods() as $method ) {
        /** @var WC_Order_Item_Shipping $method */
        $method_id = strtolower( (string) $method->get_method_id() );
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

    // Generic checkout plugins that store the slot as plain top-level order
    // meta (e.g. `delivery_date` = "2026-07-11", `delivery_time` = "12:00 - 13:00").
    if ( ! $date ) {
        foreach ( array( 'delivery_date', '_delivery_date' ) as $k ) {
            $v = $order->get_meta( $k );
            if ( $v && is_scalar( $v ) ) { $date = (string) $v; break; }
        }
    }
    if ( ! $time ) {
        foreach ( array( 'delivery_time', '_delivery_time' ) as $k ) {
            $v = $order->get_meta( $k );
            if ( $v && is_scalar( $v ) ) { $time = (string) $v; break; }
        }
    }

    // ThemeHigh Checkout Field Editor style pickers (e.g. Baked By
    // Nataleen): top-level `date_picker` = "09/07/2026" (dd/mm/yyyy)
    // and `time_picker` = "05:00 PM" (12h clock). Normalise to the
    // platform's canonical Y-m-d / 24h H:i before sending.
    if ( ! $date ) {
        foreach ( array( 'date_picker', '_date_picker' ) as $k ) {
            $v = $order->get_meta( $k );
            if ( $v && is_scalar( $v ) ) { $date = dcp_normalize_date_dmy( (string) $v ); break; }
        }
    }
    if ( ! $time ) {
        foreach ( array( 'time_picker', '_time_picker' ) as $k ) {
            $v = $order->get_meta( $k );
            if ( $v && is_scalar( $v ) ) { $time = dcp_normalize_time_24h( (string) $v ); break; }
        }
    }

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


/**
 * Normalise a checkout date value to Y-m-d. Accepts ISO Y-m-d as-is and
 * DAY-FIRST dd/mm/yyyy (what ThemeHigh checkout pickers emit in ZA,
 * e.g. "09/07/2026" = 9 July 2026). Returns null when unparseable so
 * the platform falls back to its own defaults instead of mis-reading
 * the date month-first.
 */
function dcp_normalize_date_dmy( $raw ) {
    $raw = trim( (string) $raw );
    if ( $raw === '' ) { return null; }
    if ( preg_match( '/^\d{4}-\d{2}-\d{2}$/', $raw ) ) { return $raw; }
    if ( preg_match( '#^(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})$#', $raw, $m ) ) {
        $year = strlen( $m[3] ) === 2 ? '20' . $m[3] : $m[3];
        return sprintf( '%04d-%02d-%02d', $year, $m[2], $m[1] );
    }
    return null;
}

/**
 * Normalise a checkout time value to 24h H:i. Converts 12h clock values
 * like "05:00 PM" to "17:00"; passes through values that are already
 * 24h or are ranges ("12:00 - 13:00").
 */
function dcp_normalize_time_24h( $raw ) {
    $raw = trim( (string) $raw );
    if ( $raw === '' ) { return null; }
    if ( preg_match( '/^(\d{1,2}):(\d{2})\s*(AM|PM)$/i', $raw, $m ) ) {
        $h = intval( $m[1] ) % 12;
        if ( strtoupper( $m[3] ) === 'PM' ) { $h += 12; }
        return sprintf( '%02d:%02d', $h, intval( $m[2] ) );
    }
    return $raw;
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
        // Config
        'dcp_platform_base_url',
        'dcp_platform_endpoint_ip',
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
        $secrets = array( 'dcp_store_id', 'dcp_webhook_secret', 'dcp_shiplogic_bearer' );
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
        $toggles = array( 'dcp_enable_live_rates', 'dcp_auto_sync_orders', 'dcp_debug_logging' );
        foreach ( $toggles as $name ) {
            update_option( $name, isset( $_POST[ $name ] ) ? 'yes' : 'no' );
        }
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
            'name'  => 'Plugin Test Customer',
            'email' => 'test@example.com',
            'phone' => '0820000000',
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

/* ============================================================================
 * ORDER METABOX — show platform sync status
 * ========================================================================== */

function dcp_register_order_meta_box() {
    $screen = class_exists( '\Automattic\WooCommerce\Internal\DataStores\Orders\CustomOrdersTableController' )
        && wc_get_container()->get( \Automattic\WooCommerce\Internal\DataStores\Orders\CustomOrdersTableController::class )->custom_orders_table_usage_is_enabled()
        ? wc_get_page_screen_id( 'shop-order' )
        : 'shop_order';

    add_meta_box( 'dcp_order_sync', 'Shipment &amp; Tracking', 'dcp_render_order_meta_box', $screen, 'side', 'default' );
}

/**
 * Render the "Shipment & Tracking" sidebar metabox on the WooCommerce order
 * edit screen. All data is read straight from `_dcp_*` order meta that the
 * Delicate Couriers platform writes back via the WooCommerce REST API after
 * every shipment booking and every Shiplogic status webhook. When no
 * tracking has been pushed yet (e.g. the order is still in the queue) the
 * panel shows the documented "Awaiting waybill" empty state.
 */
function dcp_render_order_meta_box( $post_or_order ) {
    $order = ( $post_or_order instanceof WP_Post ) ? wc_get_order( $post_or_order->ID ) : $post_or_order;
    if ( ! $order ) {
        echo '<em>Order not found.</em>';
        return;
    }

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

    // --- Money. Customer-paid is read straight from the WC order so it
    //     always matches what the customer actually paid; the courier rate
    //     is what we paid Shiplogic; margin is the difference. ---
    $customer_paid = (float) $order->get_shipping_total();
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
