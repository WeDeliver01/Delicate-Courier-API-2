<?php
/**
 * DAA_Test_Mode
 *
 * Coupon-triggered diagnostic mode. When the admin sets a magic coupon
 * code in settings, and an order is placed with that coupon applied,
 * DAA writes a verbose JSON decision trace to the order's meta.
 *
 * The trace records:
 *   - Plugin + DCP version
 *   - Master switch + every per-feature toggle state
 *   - Chosen shipping method id, base id, instance, label
 *   - What DAA_Fulfillment WOULD HAVE WRITTEN (regardless of whether the
 *     fulfillment toggle is on)
 *   - What DAA_Address_Rewrite WOULD HAVE DONE (regardless of toggle)
 *   - The customer's submitted delivery date (if the date picker was used)
 *   - The order's current cart total, currency, item count
 *   - Timestamp
 *
 * Crucially, test mode is purely observational:
 *   - It does NOT change customer-visible behaviour
 *   - It does NOT change what the other features write — those are
 *     gated only by their own toggles + master switch
 *   - Its trace is meta on the order, visible to the merchant in the
 *     admin order screen but not in customer emails or the thank-you page
 *
 * The coupon is matched case-insensitively against the order's applied
 * coupons. If the coupon string in settings is empty (the default),
 * test mode is dormant even if its feature toggle is on. Defence in
 * depth: a misclick on the toggle still won't start writing traces
 * to every order until the coupon string is also set.
 *
 * Why the coupon doesn't need to actually exist in WC:
 *   We match on the literal string the customer typed, BEFORE WC
 *   validates it. A non-existent coupon code will be rejected by
 *   WC during checkout (with the standard "this coupon does not exist"
 *   error), so a customer can't accidentally trigger test mode by
 *   typing the magic string — they'd see the error and remove it.
 *   The developer creates a real, zero-discount coupon with the magic
 *   code to use the feature properly.
 *
 * Recommended coupon code shape: long, random, non-guessable. E.g.
 * "DAA_TRACE_a1b2c3d4". Settings field hint says so.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Test_Mode extends DAA_Feature {

    /**
     * Run LATE on woocommerce_checkout_create_order — after fulfillment
     * (priority 10) and address-rewrite (priority 10) have done their
     * work. We want the trace to capture what they actually did, plus
     * what they WOULD have done if disabled.
     *
     * Priority 50 leaves room for other hooks at 20–40 (DCP fires its
     * sync on a different hook entirely so this doesn't race it).
     */
    const HOOK_PRIORITY = 50;

    public function feature_slug() {
        return 'test_mode';
    }

    public function register() {
        add_action( 'woocommerce_checkout_create_order', $this->safe_hook( array( $this, 'maybe_write_trace' ) ), self::HOOK_PRIORITY, 2 );
    }

    /**
     * Decide whether to write a trace; if yes, build and write it.
     */
    public function maybe_write_trace( $order, $data ) {
        if ( ! $this->is_active_for_request() ) {
            return;
        }

        $magic = trim( (string) DAA_Settings::get( 'test_mode_coupon', '' ) );
        if ( $magic === '' ) {
            // Defence-in-depth: feature toggle is ON but no coupon
            // configured. Stay dormant.
            return;
        }

        if ( ! $this->order_has_magic_coupon( $order, $magic ) ) {
            return;
        }

        $order_id = method_exists( $order, 'get_id' ) ? (int) $order->get_id() : null;
        $trace = $this->build_trace( $order );

        $order->update_meta_data( '_daa_decision_trace', wp_json_encode( $trace, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE ) );

        // Order note for visibility in admin. Internal only — not customer-facing.
        $order->add_order_note( __( 'Delicate API Adapter test mode: decision trace written to _daa_decision_trace meta.', 'delicate-api-adapter' ) );

        DAA_Logger::info( $this->feature_slug(), 'Decision trace written', $trace, $order_id );
    }

    /**
     * Case-insensitive match against the order's applied coupons.
     *
     * WC_Order::get_coupon_codes() is the canonical accessor; we also
     * fall back to checking $data['coupon_codes'] from the checkout
     * hook payload in case the order hasn't had coupons committed yet
     * (rare, but defensive).
     */
    private function order_has_magic_coupon( $order, $magic ) {
        $magic_lc = function_exists( 'mb_strtolower' )
            ? mb_strtolower( $magic, 'UTF-8' )
            : strtolower( $magic );

        $codes = array();
        if ( method_exists( $order, 'get_coupon_codes' ) ) {
            $codes = (array) $order->get_coupon_codes();
        }
        // Fallback: scan applied coupons via WC cart if still empty.
        // (At woocommerce_checkout_create_order, the order has already
        // had cart coupons copied across by WC, so the first path
        // almost always finds them.)
        if ( empty( $codes ) && function_exists( 'WC' ) && WC()->cart ) {
            $codes = (array) WC()->cart->get_applied_coupons();
        }

        foreach ( $codes as $code ) {
            $code_lc = function_exists( 'mb_strtolower' )
                ? mb_strtolower( (string) $code, 'UTF-8' )
                : strtolower( (string) $code );
            if ( $code_lc === $magic_lc ) {
                return true;
            }
        }
        return false;
    }

    /**
     * Build the trace payload. Each top-level key is namespaced so the
     * trace can be parsed by future tooling.
     */
    private function build_trace( $order ) {
        $settings = DAA_Settings::get_all();
        $dep      = DAA_Dependency_Check::get_status();

        // What fulfillment WOULD do, regardless of its toggle.
        $fulfillment_predict = DAA_Fulfillment::predict( $order );

        // What address-rewrite WOULD do, regardless of its toggle.
        $address_predict = DAA_Address_Rewrite::predict( $order );

        // What was actually written by upstream features (if their hooks
        // already ran at priority 10).
        $actual = array(
            '_dcp_fulfillment_type'           => (string) $order->get_meta( '_dcp_fulfillment_type' ),
            '_dtsc_delivery_date'             => (string) $order->get_meta( '_dtsc_delivery_date' ),
            '_daa_delivery_date'              => (string) $order->get_meta( '_daa_delivery_date' ),
            '_daa_fulfillment_source'         => (string) $order->get_meta( '_daa_fulfillment_source' ),
            '_daa_address_rewritten'          => (string) $order->get_meta( '_daa_address_rewritten' ),
            '_daa_address_rewrite_method'     => (string) $order->get_meta( '_daa_address_rewrite_method' ),
            '_daa_original_shipping_address'  => (string) $order->get_meta( '_daa_original_shipping_address' ),
        );

        // Decode the original-snapshot JSON for inline readability — it
        // appears as a tangled string in the actual_writes block which
        // is fine for record-keeping but hard to read in a support
        // conversation. Surface a decoded copy alongside.
        $original_decoded = null;
        if ( $actual['_daa_original_shipping_address'] !== '' ) {
            $tmp = json_decode( $actual['_daa_original_shipping_address'], true );
            if ( is_array( $tmp ) ) {
                $original_decoded = $tmp;
            }
        }

        // The customer's submitted delivery-date POST value, if any.
        // We don't rely on the meta because at priority 50 the picker
        // may or may not have written it yet depending on toggles, but
        // $_POST is always the customer's raw input.
        // phpcs:ignore WordPress.Security.NonceVerification.Missing
        $submitted_date = isset( $_POST['daa_delivery_date'] ) ? trim( (string) wp_unslash( $_POST['daa_delivery_date'] ) ) : '';

        // Order summary — small enough to inline; helps the merchant
        // cross-reference the trace to the right cart in their logs.
        $order_summary = array(
            'currency'    => $order->get_currency(),
            'total'       => floatval( $order->get_total() ),
            'item_count'  => (int) $order->get_item_count(),
            'date_create' => $order->get_date_created() ? $order->get_date_created()->format( 'c' ) : null,
        );

        return array(
            'schema_version' => 1,
            'captured_at'    => gmdate( 'c' ),
            'plugin'         => array(
                'version'     => defined( 'DAA_VERSION' ) ? DAA_VERSION : 'unknown',
                'min_dcp_req' => defined( 'DAA_MIN_DCP_VERSION' ) ? DAA_MIN_DCP_VERSION : 'unknown',
            ),
            'dcp'            => array(
                'present'    => ! empty( $dep['present'] ),
                'version'    => $dep['version'],
                'compatible' => ! empty( $dep['compatible'] ),
                'reason'     => $dep['reason'],
            ),
            'toggles'        => array(
                'master_enabled'          => $settings['master_enabled']          ?? '0',
                'date_picker_enabled'     => $settings['date_picker_enabled']     ?? '0',
                'fulfillment_enabled'     => $settings['fulfillment_enabled']     ?? '0',
                'address_rewrite_enabled' => $settings['address_rewrite_enabled'] ?? '0',
                'test_mode_enabled'       => $settings['test_mode_enabled']       ?? '0',
            ),
            'predictions'    => array(
                'fulfillment'              => $fulfillment_predict,
                'address_rewrite'          => $address_predict,
                'original_shipping_address' => $original_decoded,
            ),
            'actual_writes'  => $actual,
            'submitted'      => array(
                'delivery_date_post' => $submitted_date,
                'coupon_codes'       => method_exists( $order, 'get_coupon_codes' ) ? array_values( (array) $order->get_coupon_codes() ) : array(),
            ),
            'order'          => $order_summary,
        );
    }

    /**
     * Override: test mode is intentionally NOT gated by the master switch.
     *
     * Rationale: the most important scenario for test mode is "master is
     * OFF, the developer wants to verify what WOULD happen if they
     * turned it on". If we required master ON, the developer would have
     * to commit to the very thing they're trying to verify before
     * verifying it. So test mode's only gate is its own toggle (plus
     * the coupon match, checked separately in maybe_write_trace).
     */
    public function is_enabled() {
        $key = $this->feature_slug() . '_enabled';
        return ! empty( $this->settings[ $key ] );
    }

    /**
     * Gate: own toggle only. DCP presence NOT required either — that's
     * a useful state to diagnose ("we thought DCP was active but it
     * isn't"), so we don't gate on it. The dependency status is
     * included in the trace itself.
     */
    private function is_active_for_request() {
        return $this->is_enabled();
    }
}
