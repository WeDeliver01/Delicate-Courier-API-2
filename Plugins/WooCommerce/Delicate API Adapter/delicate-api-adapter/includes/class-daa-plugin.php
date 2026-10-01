<?php
/**
 * DAA_Plugin — the plugin singleton.
 *
 * Responsibilities:
 *   - Activation: create the log table, seed defaults, write an activation
 *     log entry, run dependency detection so the merchant immediately sees
 *     whether DCP is present.
 *   - Init: register settings, register dependency notices, instantiate
 *     feature classes (steps 2–5 register their classes here).
 *   - dcp_ok(): single source of truth for "should adapter act on orders".
 *     Features call this AFTER their own is_enabled() check.
 *
 * Why a singleton: features need to reach back to the settings and the
 * dependency status without us threading them through every constructor.
 * Settings are also fine as static, which is what we use — the singleton
 * just gives a clean instance() handle for orchestration.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Plugin {

    /** @var DAA_Plugin|null */
    private static $instance = null;

    /** @var DAA_Feature[] Registered feature instances. */
    private $features = array();

    /** Returns the singleton. */
    public static function instance() {
        if ( self::$instance === null ) {
            self::$instance = new self();
        }
        return self::$instance;
    }

    /** @return DAA_Feature[] */
    public function features() {
        return $this->features;
    }

    /**
     * Wire everything up. Called from daa_bootstrap() on plugins_loaded.
     */
    public function init() {
        // Always-on: settings page and dependency notices. These work
        // regardless of DCP presence so the merchant can see what's
        // happening and toggle the master switch.
        ( new DAA_Settings() )->register();
        DAA_Dependency_Check::register_notices();

        // 0.2.0 — Remote monitoring & control. These do NOT depend on
        // WooCommerce (they expose REST + push log/setting/dependency
        // events that are useful even on a broken WC install), so they
        // register before the WC-gated behaviour features below.
        $settings = DAA_Settings::get_all();
        $this->register_feature( new DAA_Remote_Push( $settings ) );
        $this->register_feature( new DAA_Remote_API( $settings ) );

        // The fulfillment mapping textarea needs a server-side textarea
        // → JSON transform on save. Register this filter unconditionally:
        // the admin needs to be able to edit the mapping even while the
        // fulfillment feature toggle is OFF (we want them to configure
        // BEFORE enabling). This is a no-op when the value already looks
        // like JSON, so it's safe to leave hooked on every request.
        add_filter( 'pre_update_option_' . DAA_OPTION_KEY, array( 'DAA_Fulfillment', 'transform_mapping_for_storage' ), 10, 2 );

        // Same pattern for the address-overrides textarea. Each rule's
        // value is itself a JSON object; the transform handles the
        // outer "key = json" line format.
        add_filter( 'pre_update_option_' . DAA_OPTION_KEY, array( 'DAA_Address_Rewrite', 'transform_overrides_for_storage' ), 10, 2 );

        // Behaviour features: instantiated only when WooCommerce is loaded.
        // Each feature class is responsible for its own hook registration;
        // we just hand it a settings snapshot.
        if ( ! class_exists( 'WooCommerce' ) ) {
            // No WC means no orders, no checkout — nothing for us to bridge.
            // The settings page is still reachable; dependency notice will
            // tell the merchant what's missing.
            return;
        }

        // Step 2: date picker. Renders flatpickr above billing on checkout
        // and writes _dtsc_delivery_date (the meta key DCP reads natively).
        $this->register_feature( new DAA_Date_Picker( $settings ) );

        // Step 3: fulfillment mapping. Translates the chosen WC shipping
        // method into DCP's fulfillment_type and writes _dcp_fulfillment_type
        // on order create, overriding DCP's heuristic.
        $this->register_feature( new DAA_Fulfillment( $settings ) );

        // Step 4: address rewriting. For shipping methods that route to a
        // satellite premises (not the customer's address), replaces the
        // order's shipping_* fields with the satellite address so DCP's
        // payload builder picks up the right destination. Original is
        // snapshotted to _daa_original_shipping_address.
        $this->register_feature( new DAA_Address_Rewrite( $settings ) );

        // Step 5: test mode. Coupon-triggered observational tracing.
        // Records what each feature did (or would have done) into
        // _daa_decision_trace on the order. Does NOT change customer
        // behaviour. Intentionally NOT gated by master switch — see
        // class header for rationale.
        $this->register_feature( new DAA_Test_Mode( $settings ) );
    }

    /**
     * Register a feature: instantiate it (already done by caller), call its
     * register() to wire hooks. Stored for later inspection / debugging.
     */
    public function register_feature( DAA_Feature $feature ) {
        $this->features[ $feature->feature_slug() ] = $feature;
        $feature->register();
    }

    /**
     * Single source of truth for "is it safe for behaviour features to write
     * to orders right now". Composes master switch + DCP presence + DCP
     * version compatibility. Per-feature toggles are checked separately
     * inside each feature's is_enabled().
     */
    public static function dcp_ok() {
        if ( DAA_Settings::get( 'master_enabled', '0' ) !== '1' ) {
            return false;
        }
        return DAA_Dependency_Check::is_compatible();
    }

    /* ========================================================================
     * ACTIVATION / DEACTIVATION
     * ====================================================================== */

    /**
     * Activation hook.
     *
     * Idempotent — safe to run on re-activation. Does NOT enable any
     * behaviour by default; the merchant must flip the master switch.
     */
    public static function activate() {
        // Hard refuse if PHP is below our minimum, so we fail loudly at
        // activation time rather than at runtime with a fatal.
        if ( version_compare( PHP_VERSION, '7.4', '<' ) ) {
            deactivate_plugins( DAA_PLUGIN_BASENAME );
            wp_die(
                esc_html__( 'Delicate API Adapter requires PHP 7.4 or higher.', 'delicate-api-adapter' ),
                esc_html__( 'Plugin activation error', 'delicate-api-adapter' ),
                array( 'back_link' => true )
            );
        }

        // Create the log table.
        DAA_Logger::create_table();

        // Seed defaults if no settings exist yet. We use add_option to
        // avoid clobbering anyone's saved settings on re-activation.
        if ( get_option( DAA_OPTION_KEY, null ) === null ) {
            add_option( DAA_OPTION_KEY, DAA_Settings::defaults(), '', true );
        }

        // Record the activation in our log. Best-effort.
        $dep = DAA_Dependency_Check::get_status();
        $activation_ctx = array(
            'version'     => DAA_VERSION,
            'php'         => PHP_VERSION,
            'wp'          => get_bloginfo( 'version' ),
            'wc'          => defined( 'WC_VERSION' ) ? WC_VERSION : 'unknown',
            'dcp_present' => ! empty( $dep['present'] ),
            'dcp_version' => $dep['version'],
            'dcp_compat'  => ! empty( $dep['compatible'] ),
            'reason'      => $dep['reason'],
        );
        DAA_Logger::info( 'bootstrap', 'Plugin activated', $activation_ctx );

        // 0.2.0: notify the platform of the activation. No-op when remote
        // monitoring is disabled or credentials are unset. Wrapped because
        // an activation hook MUST NOT throw — that would brick activation.
        if ( class_exists( 'DAA_Remote_Push' ) ) {
            try {
                DAA_Remote_Push::send( 'plugin.activated', $activation_ctx );
            } catch ( \Throwable $e ) { /* swallow */ }
        }
    }

    /**
     * Deactivation hook.
     *
     * Intentionally NOT destructive — settings stay in wp_options, the
     * log table stays in place. The merchant may be deactivating briefly
     * to debug an unrelated issue. Full cleanup belongs to uninstall.php.
     */
    public static function deactivate() {
        DAA_Logger::info( 'bootstrap', 'Plugin deactivated' );
        if ( class_exists( 'DAA_Remote_Push' ) ) {
            try {
                DAA_Remote_Push::send( 'plugin.deactivated', array( 'version' => DAA_VERSION ) );
            } catch ( \Throwable $e ) { /* swallow */ }
        }
    }
}
