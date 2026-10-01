<?php
/**
 * DAA_Remote_API — exposes the read + control surface that the platform's
 * Hangfire sweep job and admin controller call into.
 *
 * Routes are registered under the namespace 'delicate-api-adapter/v1' so the
 * full path on a merchant's site is:
 *     https://merchant.tld/wp-json/delicate-api-adapter/v1/<route>
 *
 * Auth (every request):
 *   - X-DAA-Token  — bearer-style shared secret, compared via hash_equals to
 *                    the stored platform_api_token. Required on ALL routes.
 *   - X-DAA-Signature — base64(HMAC-SHA256(daa_secret, raw_body)). Required
 *                    on POST/PUT/DELETE. Computed against the EXACT bytes
 *                    of $request->get_body() so the platform's signing is
 *                    bit-for-bit reproducible.
 *   - X-DAA-Actor  — opaque label (platform user id/email) recorded into
 *                    the local wp_daa_logs row for each control call so
 *                    the merchant can audit "who on the platform did this".
 *
 * Failure mode: 401 with a generic body. No information about why (token
 * vs signature vs body) is ever revealed.
 *
 * Backward compat: when remote_monitoring_enabled='0' all routes 401
 * immediately because the stored token is empty and hash_equals on an
 * empty string fails — net effect is the same as the feature being absent.
 */

if ( ! defined( 'ABSPATH' ) ) {
        exit;
}

class DAA_Remote_API extends DAA_Feature {

        const NAMESPACE_V1 = 'delicate-api-adapter/v1';

        public function feature_slug() {
                return 'remote_api';
        }

        /**
         * The REST routes are always registered (so the platform can probe), but
         * authorize() refuses every call when remote_monitoring_enabled is off
         * (token is empty → hash_equals fails). is_enabled() here is used by
         * the rest of the codebase only for diagnostic purposes.
         */
        public function is_enabled() {
                return ! empty( $this->settings['remote_monitoring_enabled'] );
        }

        public function register() {
                add_action( 'rest_api_init', $this->safe_hook( array( $this, 'register_routes' ) ) );
        }

        public function register_routes() {
                $auth = array( $this, 'authorize' );

                // ----- READ -----
                register_rest_route( self::NAMESPACE_V1, '/heartbeat', array(
                        'methods'             => 'GET',
                        'callback'            => array( $this, 'route_heartbeat' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/logs', array(
                        'methods'             => 'GET',
                        'callback'            => array( $this, 'route_logs' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/settings', array(
                        'methods'             => 'GET',
                        'callback'            => array( $this, 'route_settings_get' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/dependency', array(
                        'methods'             => 'GET',
                        'callback'            => array( $this, 'route_dependency' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/traces', array(
                        'methods'             => 'GET',
                        'callback'            => array( $this, 'route_traces' ),
                        'permission_callback' => $auth,
                ) );

                // ----- WRITE -----
                register_rest_route( self::NAMESPACE_V1, '/settings', array(
                        'methods'             => 'POST',
                        'callback'            => array( $this, 'route_settings_post' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/master/on', array(
                        'methods'             => 'POST',
                        'callback'            => array( $this, 'route_master_on' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/master/off', array(
                        'methods'             => 'POST',
                        'callback'            => array( $this, 'route_master_off' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/logs/clear', array(
                        'methods'             => 'POST',
                        'callback'            => array( $this, 'route_logs_clear' ),
                        'permission_callback' => $auth,
                ) );
                register_rest_route( self::NAMESPACE_V1, '/coupon/rotate', array(
                        'methods'             => 'POST',
                        'callback'            => array( $this, 'route_coupon_rotate' ),
                        'permission_callback' => $auth,
                ) );
        }

        /* ============================================================
         * AUTH
         * ============================================================ */

        public function authorize( WP_REST_Request $request ) {
                $settings = DAA_Settings::get_all();

                // HARD GATE: when remote_monitoring_enabled is off, refuse EVERY route
                // before doing any token/signature work. This is the kill-switch the
                // bakery operator sees in the DAA admin page — flipping it off must
                // make the entire /daa/v1/* namespace behave as if it doesn't exist,
                // regardless of whether secrets are still persisted in the DB. The
                // 401 (rather than 403/404) keeps the response identical to a bad
                // token, so an outside scanner can't tell why it was rejected.
                if ( empty( $settings['remote_monitoring_enabled'] ) ) {
                        return new WP_Error( 'daa_forbidden', __( 'Unauthorized', 'delicate-api-adapter' ), array( 'status' => 401 ) );
                }

                $token    = isset( $settings['platform_api_token'] ) ? (string) $settings['platform_api_token'] : '';
                $secret   = isset( $settings['daa_secret'] ) ? (string) $settings['daa_secret'] : '';

                // Header names: WP normalises X-DAA-Token to "x_daa_token" (lowercase + underscores).
                $presented_token = (string) ( $request->get_header( 'x_daa_token' ) ?: '' );

                // hash_equals returns false on length mismatch; comparing against an
                // empty stored token will always fail, which is exactly the no-op
                // behaviour we want when remote_monitoring_enabled is '0'.
                if ( $token === '' || ! hash_equals( $token, $presented_token ) ) {
                        return new WP_Error( 'daa_forbidden', __( 'Unauthorized', 'delicate-api-adapter' ), array( 'status' => 401 ) );
                }

                $method = strtoupper( $request->get_method() );
                if ( in_array( $method, array( 'POST', 'PUT', 'PATCH', 'DELETE' ), true ) ) {
                        $presented_sig = (string) ( $request->get_header( 'x_daa_signature' ) ?: '' );
                        $body          = (string) $request->get_body();
                        if ( $secret === '' || ! DAA_Sig::verify( $body, $presented_sig, $secret ) ) {
                                return new WP_Error( 'daa_forbidden', __( 'Unauthorized', 'delicate-api-adapter' ), array( 'status' => 401 ) );
                        }
                }

                return true;
        }

        private function actor( WP_REST_Request $request ) {
                $a = (string) ( $request->get_header( 'x_daa_actor' ) ?: '' );
                return $a === '' ? 'platform' : $a;
        }

        /* ============================================================
         * READ ROUTES
         * ============================================================ */

        public function route_heartbeat( WP_REST_Request $request ) {
                $dep = DAA_Dependency_Check::get_status();
                return new WP_REST_Response( array(
                        'daa_version'      => DAA_VERSION,
                        'time'             => gmdate( 'Y-m-d\TH:i:s\Z' ),
                        'wp_version'       => get_bloginfo( 'version' ),
                        'wc_version'       => defined( 'WC_VERSION' ) ? WC_VERSION : null,
                        'dcp_version'      => isset( $dep['version'] ) ? $dep['version'] : null,
                        'dcp_compatible'   => ! empty( $dep['compatible'] ),
                        'master_enabled'   => DAA_Settings::get( 'master_enabled', '0' ) === '1',
                ), 200 );
        }

        public function route_logs( WP_REST_Request $request ) {
                global $wpdb;
                $since_id = (int) ( $request->get_param( 'since_id' ) ?: 0 );
                $limit    = max( 1, min( (int) ( $request->get_param( 'limit' ) ?: 100 ), 500 ) );
                $level    = (string) ( $request->get_param( 'level' ) ?: '' );

                $table = DAA_Logger::table_name();
                if ( $level !== '' && in_array( $level, array( 'debug', 'info', 'warning', 'error' ), true ) ) {
                        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
                        $rows = $wpdb->get_results( $wpdb->prepare(
                                "SELECT id, created_at, level, feature, message, context, order_id
                                 FROM {$table}
                                 WHERE id > %d AND level = %s
                                 ORDER BY id ASC LIMIT %d",
                                $since_id, $level, $limit
                        ) );
                } else {
                        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
                        $rows = $wpdb->get_results( $wpdb->prepare(
                                "SELECT id, created_at, level, feature, message, context, order_id
                                 FROM {$table}
                                 WHERE id > %d
                                 ORDER BY id ASC LIMIT %d",
                                $since_id, $limit
                        ) );
                }
                $items = array();
                foreach ( (array) $rows as $r ) {
                        $ctx = null;
                        if ( ! empty( $r->context ) ) {
                                $decoded = json_decode( $r->context, true );
                                $ctx = ( json_last_error() === JSON_ERROR_NONE ) ? $decoded : $r->context;
                        }
                        $items[] = array(
                                'id'         => (int) $r->id,
                                'created_at' => (string) $r->created_at,
                                'level'      => (string) $r->level,
                                'feature'    => (string) $r->feature,
                                'message'    => (string) $r->message,
                                'context'    => $ctx,
                                'order_id'   => $r->order_id ? (int) $r->order_id : null,
                        );
                }
                return new WP_REST_Response( array( 'items' => $items ), 200 );
        }

        public function route_settings_get( WP_REST_Request $request ) {
                $snap = DAA_Remote_Push::redact_settings( DAA_Settings::get_all() );
                return new WP_REST_Response( $snap, 200 );
        }

        public function route_dependency( WP_REST_Request $request ) {
                return new WP_REST_Response( DAA_Dependency_Check::get_status(), 200 );
        }

        public function route_traces( WP_REST_Request $request ) {
                $since_id = (int) ( $request->get_param( 'since_id' ) ?: 0 );
                $limit    = max( 1, min( (int) ( $request->get_param( 'limit' ) ?: 20 ), 100 ) );

                global $wpdb;
                // Pull from order meta (HPOS or postmeta). We use $wpdb directly with
                // a join on the orders table because wc_get_orders is order-of-magnitude
                // slower for this kind of "give me the N newest orders with this
                // meta key" query. Falls back gracefully on either storage backend.
                $items = array();

                // Try HPOS table first (WC 8+).
                $hpos_meta = $wpdb->prefix . 'wc_orders_meta';
                $hpos_exists = $wpdb->get_var( $wpdb->prepare( 'SHOW TABLES LIKE %s', $hpos_meta ) );
                if ( $hpos_exists === $hpos_meta ) {
                        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
                        $rows = $wpdb->get_results( $wpdb->prepare(
                                "SELECT order_id, meta_value FROM {$hpos_meta}
                                 WHERE meta_key = '_daa_decision_trace' AND order_id > %d
                                 ORDER BY order_id ASC LIMIT %d",
                                $since_id, $limit
                        ) );
                        foreach ( (array) $rows as $r ) {
                                $items[] = array(
                                        'order_id' => (int) $r->order_id,
                                        'trace'    => self::decode_maybe_json( $r->meta_value ),
                                );
                        }
                }

                // If HPOS returned nothing (or doesn't exist), fall back to postmeta.
                if ( empty( $items ) ) {
                        $pm = $wpdb->postmeta;
                        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
                        $rows = $wpdb->get_results( $wpdb->prepare(
                                "SELECT post_id AS order_id, meta_value FROM {$pm}
                                 WHERE meta_key = '_daa_decision_trace' AND post_id > %d
                                 ORDER BY post_id ASC LIMIT %d",
                                $since_id, $limit
                        ) );
                        foreach ( (array) $rows as $r ) {
                                $items[] = array(
                                        'order_id' => (int) $r->order_id,
                                        'trace'    => self::decode_maybe_json( $r->meta_value ),
                                );
                        }
                }

                return new WP_REST_Response( array( 'items' => $items ), 200 );
        }

        private static function decode_maybe_json( $value ) {
                if ( ! is_string( $value ) || $value === '' ) { return $value; }
                $decoded = json_decode( $value, true );
                return ( json_last_error() === JSON_ERROR_NONE ) ? $decoded : $value;
        }

        /* ============================================================
         * WRITE ROUTES
         * ============================================================ */

        public function route_settings_post( WP_REST_Request $request ) {
                $body = $request->get_json_params();
                if ( ! is_array( $body ) ) {
                        return new WP_REST_Response( array( 'error' => 'invalid_body' ), 400 );
                }
                // Never let the platform overwrite the secret pair through this
                // channel — those are paste-once on the merchant's WP and the
                // platform doesn't need to round-trip them.
                unset( $body['daa_secret'], $body['platform_api_token'] );

                // update_option fires the same hooks as the admin form save, so
                // DAA_Remote_Push will diff and emit push events from this same
                // write. That's exactly what we want — every control call is
                // audited symmetrically on both sides.
                $merged = array_merge( DAA_Settings::get_all(), $body );
                update_option( DAA_OPTION_KEY, $merged, true );

                $this->audit_write( $request, 'settings.update', array( 'changed_keys' => array_keys( $body ) ) );

                return new WP_REST_Response( array( 'ok' => true ), 200 );
        }

        public function route_master_on( WP_REST_Request $request ) {
                $this->set_master( '1' );
                $this->audit_write( $request, 'master.on' );
                return new WP_REST_Response( array( 'ok' => true, 'master_enabled' => true ), 200 );
        }

        public function route_master_off( WP_REST_Request $request ) {
                $this->set_master( '0' );
                $this->audit_write( $request, 'master.off' );
                return new WP_REST_Response( array( 'ok' => true, 'master_enabled' => false ), 200 );
        }

        private function set_master( $value ) {
                $current = DAA_Settings::get_all();
                $current['master_enabled'] = ( $value === '1' ) ? '1' : '0';
                update_option( DAA_OPTION_KEY, $current, true );
        }

        public function route_logs_clear( WP_REST_Request $request ) {
                DAA_Logger::clear();
                $this->audit_write( $request, 'logs.clear' );
                return new WP_REST_Response( array( 'ok' => true ), 200 );
        }

        public function route_coupon_rotate( WP_REST_Request $request ) {
                $body = $request->get_json_params();
                $supplied = is_array( $body ) && isset( $body['new_code'] ) ? trim( (string) $body['new_code'] ) : '';

                // If the platform didn't supply a code, generate one server-side here
                // — that's the whole point of "rotate". Format: DAA-<8 uppercased
                // alphanumeric>, easy to dictate over the phone if the merchant ever
                // needs to (e.g. "DAA-9F4B7K2P").
                if ( $supplied === '' ) {
                        $new_code = self::generate_coupon_code();
                } else {
                        $new_code = $supplied;
                }

                $current = DAA_Settings::get_all();
                $current['test_mode_coupon'] = $new_code;
                update_option( DAA_OPTION_KEY, $current, true );

                $this->audit_write( $request, 'coupon.rotate', array(
                        'new_code' => $new_code,
                        'source'   => ( $supplied === '' ) ? 'generated' : 'supplied',
                ) );

                // Return the new code so the platform can display it to the operator
                // AND record it in DaaRemoteActions for audit symmetry.
                return new WP_REST_Response( array(
                        'ok'       => true,
                        'new_code' => $new_code,
                ), 200 );
        }

        /**
         * Generate a fresh test-mode coupon code. 8 chars from an unambiguous
         * alphabet (no O/0/I/1) so it survives being dictated. Prefixed "DAA-"
         * so it's obvious in WooCommerce's coupon list where it came from.
         */
        private static function generate_coupon_code() {
                $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
                $out = '';
                $max = strlen( $alphabet ) - 1;
                for ( $i = 0; $i < 8; $i++ ) {
                        $out .= $alphabet[ function_exists( 'random_int' ) ? random_int( 0, $max ) : mt_rand( 0, $max ) ];
                }
                return 'DAA-' . $out;
        }

        /**
         * Write a 'remote_control' row to wp_daa_logs so the bakery can audit
         * every platform-initiated action locally. The actor label comes from
         * the X-DAA-Actor header.
         */
        private function audit_write( WP_REST_Request $request, $action, $extra = array() ) {
                $ctx = array_merge( array(
                        'actor'      => $this->actor( $request ),
                        'route'      => $request->get_route(),
                        'method'     => $request->get_method(),
                ), is_array( $extra ) ? $extra : array() );
                DAA_Logger::info( 'remote_control', 'Remote action: ' . $action, $ctx );
        }
}
