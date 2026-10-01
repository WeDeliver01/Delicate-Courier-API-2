<?php
/**
 * DAA_Dependency_Check
 *
 * Detects whether Delicate Courier Platform (DCP) is installed, active,
 * and at the minimum required version. Posts an admin notice if not.
 *
 * The adapter loads regardless — it must not fatal-error when DCP is
 * absent — but behaviour features short-circuit via DAA_Plugin::dcp_ok()
 * so they never attempt to write the DCP-specific meta keys when the
 * platform plugin isn't there to read them.
 *
 * Detection strategy:
 *   - The DCP_VERSION constant is the cheapest reliable signal: it is
 *     defined unconditionally at the top of DCP's main file, BEFORE any
 *     class_exists() or function_exists() guards, so it appears the
 *     instant DCP is loaded by WP.
 *   - We fall back to the WC_Shipping_Method subclass name DCP_Shipping_Method
 *     and to a plugin-file scan of plugin_basename() if the constant is
 *     somehow missing (e.g. a future fork that drops it).
 *
 * Caching: results are cached per-request in a static — get_status() is
 * cheap to call from anywhere.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Dependency_Check {

    /** @var array|null Cached result of detect(). */
    private static $status = null;

    /**
     * Return [ 'present' => bool, 'version' => string|null, 'compatible' => bool, 'reason' => string ].
     *   - 'present'    : DCP is installed and currently active.
     *   - 'version'    : DCP_VERSION string if available, else null.
     *   - 'compatible' : version >= DAA_MIN_DCP_VERSION.
     *   - 'reason'     : human-readable summary for admin notice + logs.
     */
    public static function get_status() {
        if ( self::$status !== null ) {
            return self::$status;
        }
        self::$status = self::detect();
        return self::$status;
    }

    /**
     * Convenience: just return the boolean "ok to act on orders" verdict.
     */
    public static function is_compatible() {
        $s = self::get_status();
        return ! empty( $s['present'] ) && ! empty( $s['compatible'] );
    }

    /**
     * Hook the admin notice.
     */
    public static function register_notices() {
        add_action( 'admin_notices', array( __CLASS__, 'render_notice' ) );
    }

    /**
     * Render the admin notice if DCP is missing or out of date.
     * Only displays on screens where the merchant can act on it
     * (Plugins, our settings page, WooCommerce screens).
     */
    public static function render_notice() {
        if ( ! current_user_can( 'manage_woocommerce' ) ) {
            return;
        }
        $status = self::get_status();
        if ( ! empty( $status['present'] ) && ! empty( $status['compatible'] ) ) {
            return; // All good — silent.
        }

        $screen = function_exists( 'get_current_screen' ) ? get_current_screen() : null;
        $screen_id = $screen ? (string) $screen->id : '';
        // Only show on screens the merchant is likely to be on.
        $allowed = array( 'plugins', 'dashboard', 'woocommerce_page_daa-settings' );
        $is_woo = $screen && strpos( $screen_id, 'woocommerce' ) !== false;
        if ( ! in_array( $screen_id, $allowed, true ) && ! $is_woo ) {
            return;
        }

        $msg = ! empty( $status['present'] )
            ? sprintf(
                /* translators: 1: current DCP version, 2: required version */
                __( 'Delicate API Adapter requires Delicate Courier Platform %2$s or higher. You have %1$s. Behaviour features are disabled until DCP is updated.', 'delicate-api-adapter' ),
                esc_html( (string) $status['version'] ),
                esc_html( DAA_MIN_DCP_VERSION )
            )
            : sprintf(
                /* translators: %s: minimum DCP version */
                __( 'Delicate API Adapter is dormant: Delicate Courier Platform (version %s or higher) is not active. Install and activate DCP to enable adapter features.', 'delicate-api-adapter' ),
                esc_html( DAA_MIN_DCP_VERSION )
            );

        echo '<div class="notice notice-warning"><p><strong>Delicate API Adapter:</strong> ' . esc_html( $msg ) . '</p></div>';
    }

    /**
     * Underlying detection. Run once per request.
     */
    private static function detect() {
        // Primary signal: DCP defines this constant in its main file.
        if ( defined( 'DCP_VERSION' ) ) {
            $ver = (string) constant( 'DCP_VERSION' );
            $cmp = version_compare( $ver, DAA_MIN_DCP_VERSION, '>=' );
            return array(
                'present'    => true,
                'version'    => $ver,
                'compatible' => $cmp,
                'reason'     => $cmp
                    ? "DCP {$ver} present and compatible"
                    : "DCP {$ver} present but older than required " . DAA_MIN_DCP_VERSION,
            );
        }

        // Secondary signal: DCP's shipping method class is present once the
        // plugin has bootstrapped (only on the front-end / cart contexts —
        // it may not be loaded in admin yet, so this is a fallback only).
        if ( class_exists( 'DCP_Shipping_Method' ) ) {
            return array(
                'present'    => true,
                'version'    => null,
                'compatible' => false, // No version means we can't confirm — refuse to act.
                'reason'     => 'DCP class detected but DCP_VERSION constant missing — cannot verify version, treating as incompatible',
            );
        }

        // Tertiary signal: plugin file exists on disk and is in active list.
        // This catches the window where another plugin loaded before DCP at
        // the same priority. Cheapest last because get_option('active_plugins')
        // is autoloaded so it's free, but the string searches add up.
        $active = (array) get_option( 'active_plugins', array() );
        foreach ( $active as $plugin_file ) {
            if ( strpos( (string) $plugin_file, 'delicate-courier-platform' ) !== false ) {
                return array(
                    'present'    => true,
                    'version'    => null,
                    'compatible' => false,
                    'reason'     => 'DCP plugin file is in active_plugins but constant not defined — likely a load-order race; treating as incompatible until next request',
                );
            }
        }

        return array(
            'present'    => false,
            'version'    => null,
            'compatible' => false,
            'reason'     => 'DCP not detected on this site',
        );
    }
}
