<?php
/**
 * Plugin Name: Delicate API Adapter
 * Plugin URI:  https://delicatecourier.co.za
 * Description: Bridges merchant-specific checkout configurations to the Delicate Courier Platform (DCP) plugin. Writes the meta keys DCP needs so the courier integration picks up the correct fulfillment_type, delivery date, and shipping address on a per-merchant shipping-method basis. Passive when DCP's own shipping method is chosen.
 * Version:     0.2.0
 * Author:      Delicate Courier
 * Author URI:  https://delicatecourier.co.za
 * License:     Proprietary
 * Text Domain: delicate-api-adapter
 * Requires at least: 5.8
 * Requires PHP:      7.4
 * WC requires at least: 6.0
 * WC tested up to:   10.7
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

/* ============================================================================
 * CONSTANTS
 * ========================================================================== */

define( 'DAA_VERSION',         '0.2.0' );
define( 'DAA_PLUGIN_FILE',     __FILE__ );
define( 'DAA_PLUGIN_DIR',      plugin_dir_path( __FILE__ ) );
define( 'DAA_PLUGIN_URL',      plugin_dir_url( __FILE__ ) );
define( 'DAA_PLUGIN_BASENAME', plugin_basename( __FILE__ ) );

// Minimum DCP version this adapter is built against. If DCP is older than
// this, behaviour features are disabled (the plugin still loads, but does
// not touch any orders). Bump only when adapter relies on a new DCP feature.
define( 'DAA_MIN_DCP_VERSION', '2.4.3' );

// Settings option key (single autoloaded array, mirrors DCP's settings shape).
define( 'DAA_OPTION_KEY', 'daa_settings' );

// Custom log table name (suffix appended to $wpdb->prefix at runtime).
define( 'DAA_LOG_TABLE_SUFFIX', 'daa_logs' );

// Log ring buffer size — table is hard-capped to this many rows. Oldest rows
// pruned on each insert once the ceiling is reached. 1000 is enough for a few
// days of checkout activity at a small merchant; large enough to capture the
// build-up to any incident.
define( 'DAA_LOG_RING_SIZE', 1000 );

/* ============================================================================
 * HPOS COMPATIBILITY DECLARATION
 *
 * WC 10.7 ships with HPOS as the default for new installs. We use
 * $order->update_meta_data() / $order->get_meta() throughout (never the
 * post_meta APIs), so we are compatible.
 * ========================================================================== */

add_action( 'before_woocommerce_init', function() {
    if ( class_exists( '\Automattic\WooCommerce\Utilities\FeaturesUtil' ) ) {
        \Automattic\WooCommerce\Utilities\FeaturesUtil::declare_compatibility(
            'custom_order_tables', DAA_PLUGIN_FILE, true
        );
    }
} );

/* ============================================================================
 * AUTOLOAD INCLUDES
 *
 * Manual requires — no Composer. Order matters: base classes first.
 * ========================================================================== */

require_once DAA_PLUGIN_DIR . 'includes/class-daa-logger.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-feature.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-dependency-check.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-settings.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-date-picker.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-fulfillment.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-address-rewrite.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-test-mode.php';
// 0.2.0 — Remote monitoring & control. Loaded BEFORE class-daa-plugin so the
// singleton can register them alongside the existing behaviour features.
require_once DAA_PLUGIN_DIR . 'includes/class-daa-sig.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-remote-push.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-remote-api.php';
require_once DAA_PLUGIN_DIR . 'includes/class-daa-plugin.php';

/* ============================================================================
 * ACTIVATION / DEACTIVATION
 * ========================================================================== */

register_activation_hook( __FILE__, array( 'DAA_Plugin', 'activate' ) );
register_deactivation_hook( __FILE__, array( 'DAA_Plugin', 'deactivate' ) );

/* ============================================================================
 * BOOTSTRAP
 *
 * Load after WooCommerce (priority 25 — DCP loads at 20, so we load after
 * DCP if both are present, but we do NOT require DCP to be active for the
 * plugin to load. The dependency check decides whether behaviour features
 * are active or dormant.).
 * ========================================================================== */

add_action( 'plugins_loaded', 'daa_bootstrap', 25 );
function daa_bootstrap() {
    DAA_Plugin::instance()->init();
}
