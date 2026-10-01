<?php
/**
 * DAA_Settings
 *
 * Admin settings page lives at WooCommerce → Delicate API Adapter.
 * Stores all settings in a single autoloaded wp_option array under the
 * key DAA_OPTION_KEY ('daa_settings'). Single-option storage is what DCP
 * does NOT do (they use individual option rows) — we use a single option
 * because our settings are small, always read together, and we get atomic
 * updates for free.
 *
 * Step 1 covers:
 *   - master_enabled toggle (the master kill switch)
 *   - per-feature enable toggles (rendered as checkboxes; the feature
 *     classes themselves are added in later steps but the toggle rows
 *     are scaffolded here)
 *   - DCP dependency status panel
 *   - Log viewer (last 100 entries)
 *   - "Clear logs" button (AJAX, nonce-protected, manage_woocommerce only)
 *
 * Later steps add: date picker config, shipping-method mapping table,
 * address-rewrite editor, test-mode coupon code field.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Settings {

    /** Cached settings array per-request. */
    private static $cache = null;

    /**
     * Default settings used on first install and as the merge floor for
     * partial saves. Keep in sync with the form rendered below.
     */
    public static function defaults() {
        return array(
            // Master kill switch — OFF by default so a fresh install does
            // nothing until the merchant explicitly enables it. This is
            // the second line of defence after "rename folder to disable".
            'master_enabled'           => '0',

            // Per-feature toggles. All OFF by default, same reasoning.
            'date_picker_enabled'      => '0',
            'fulfillment_enabled'      => '0',
            'address_rewrite_enabled'  => '0',
            'test_mode_enabled'        => '0',

            // Test-mode magic coupon. Empty = test mode dormant even if
            // the test_mode_enabled toggle is on (defence in depth).
            'test_mode_coupon'         => '',

            // Date-picker settings (used by DAA_Date_Picker).
            'date_picker_label'          => __( 'Delivery / Collection date', 'delicate-api-adapter' ),
            'date_picker_required'       => '1',
            'date_picker_lead_hours'     => '48',
            'date_picker_max_days_ahead' => '60',
            'date_picker_blackouts'      => '',
            // Weekday opens: 0 = Sunday (off by default for the bakery)
            // through 6 = Saturday. Mon-Sat ON, Sun OFF.
            'date_picker_weekday_0'      => '0',
            'date_picker_weekday_1'      => '1',
            'date_picker_weekday_2'      => '1',
            'date_picker_weekday_3'      => '1',
            'date_picker_weekday_4'      => '1',
            'date_picker_weekday_5'      => '1',
            'date_picker_weekday_6'      => '1',

            // Fulfillment mapping. Stored as JSON-encoded string so the
            // sanitiser doesn't need special-case array handling for
            // nested arrays. Seeded with the bakery's confirmed
            // shipping-zone instance ids (cross-checked from the
            // 2026-05-21 cart page HTML).
            'fulfillment_mapping'        => wp_json_encode( DAA_Fulfillment::default_mapping() ),

            // Address overrides. Same storage pattern as fulfillment_mapping:
            // a JSON-encoded object where each VALUE is itself a JSON-encoded
            // string (a flat top-level object keeps the wp_options row small
            // and the runtime decode trivial — one decode for the outer
            // object, then per-row decode on first hit).
            'address_overrides'          => self::encode_address_overrides_default(),

            // ---- 0.2.0: Remote monitoring & control ----
            // OFF by default — an existing 0.1.0 install upgrading to 0.2.0
            // sees zero behaviour change until the merchant explicitly
            // enables this AND pastes the platform credentials.
            'remote_monitoring_enabled' => '0',
            // Slug assigned by the platform admin when this site is
            // registered (e.g. "honey-bee-baker"). Required for both push
            // and pull. Lower-case alnum + hyphens; no spaces.
            'merchant_id'               => '',
            // HMAC-SHA256 secret. Used by both directions of the channel:
            // we sign push bodies with it; the platform signs control
            // calls with it. Treat as a password — never logged, never
            // echoed back through the REST GET /settings endpoint.
            'daa_secret'                => '',
            // Bearer token the platform sends on every control call to
            // identify itself. Same treatment as daa_secret — never echoed.
            'platform_api_token'        => '',
            // URL on the platform that ingests our push envelopes. Set
            // per environment (dev / staging / prod). Empty disables push.
            'platform_ingest_url'       => '',
        );
    }

    /**
     * Settings keys that are secrets — must be redacted in any UI and
     * never logged. Empty submitted value means "keep existing".
     */
    public static function secret_keys() {
        return array( 'daa_secret', 'platform_api_token' );
    }

    /**
     * Helper for the default seed: turn the array-of-arrays default
     * into the same JSON-of-JSON-strings shape that the storage
     * transform produces. Keeps the on-disk format consistent whether
     * the option was just seeded or re-saved by the merchant.
     */
    private static function encode_address_overrides_default() {
        $out = array();
        foreach ( DAA_Address_Rewrite::default_overrides() as $k => $v ) {
            $out[ $k ] = wp_json_encode( $v, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
        }
        return wp_json_encode( $out );
    }

    /**
     * Fetch the merged settings (defaults overlaid with stored values).
     * Called by every feature when it's instantiated.
     */
    public static function get_all() {
        if ( self::$cache !== null ) {
            return self::$cache;
        }
        $stored = get_option( DAA_OPTION_KEY, array() );
        if ( ! is_array( $stored ) ) {
            $stored = array();
        }
        self::$cache = array_merge( self::defaults(), $stored );
        return self::$cache;
    }

    /** Read a single key with default fallback. */
    public static function get( $key, $fallback = '' ) {
        $all = self::get_all();
        return array_key_exists( $key, $all ) ? $all[ $key ] : $fallback;
    }

    /** Persist settings (admin save handler). */
    public static function save( $new ) {
        if ( ! is_array( $new ) ) {
            return false;
        }
        $merged = array_merge( self::defaults(), self::get_all(), $new );
        update_option( DAA_OPTION_KEY, $merged, true );
        self::$cache = $merged;
        return true;
    }

    /**
     * Wire the admin menu, settings registration, and AJAX handlers.
     */
    public function register() {
        add_action( 'admin_menu', array( $this, 'add_menu_page' ) );
        add_action( 'admin_init', array( $this, 'register_settings' ) );
        add_action( 'wp_ajax_daa_clear_logs', array( $this, 'ajax_clear_logs' ) );
    }

    public function add_menu_page() {
        add_submenu_page(
            'woocommerce',
            __( 'Delicate API Adapter', 'delicate-api-adapter' ),
            __( 'Delicate API Adapter', 'delicate-api-adapter' ),
            'manage_woocommerce',
            'daa-settings',
            array( $this, 'render_page' )
        );
    }

    /**
     * Register settings via WP Settings API.
     *
     * One option, custom sanitiser. Each feature's section is added here
     * even though the inputs they contain are minimal in step 1 (just the
     * toggle). The form-row scaffolding mirrors DTSC's approach so the
     * page reads consistently as more features land.
     */
    public function register_settings() {
        register_setting(
            'daa_settings_group',
            DAA_OPTION_KEY,
            array(
                'type'              => 'array',
                'sanitize_callback' => array( $this, 'sanitize_settings' ),
                'default'           => self::defaults(),
            )
        );

        // Section 1: master switch.
        add_settings_section(
            'daa_master_section',
            __( 'Master Switch', 'delicate-api-adapter' ),
            function () {
                echo '<p>' . esc_html__(
                    'The master switch must be ON for any adapter behaviour to take effect. Per-feature toggles below act as additional gates — they only matter if the master is ON. If anything goes wrong, turn this OFF first; the plugin will become dormant immediately without disabling itself.',
                    'delicate-api-adapter'
                ) . '</p>';
            },
            'daa-settings'
        );
        $this->checkbox_field( 'master_enabled', __( 'Adapter master switch', 'delicate-api-adapter' ), 'daa_master_section',
            __( 'When OFF, the adapter loads but does NOT modify any orders. Default: OFF.', 'delicate-api-adapter' )
        );

        // Section 2: per-feature toggles. The fields for each feature's
        // configuration (date picker options, mapping table, etc.) are
        // added by the feature classes themselves in later build steps.
        add_settings_section(
            'daa_features_section',
            __( 'Feature Toggles', 'delicate-api-adapter' ),
            function () {
                echo '<p>' . esc_html__(
                    'Enable each feature only after testing it. Disabled features have zero effect even if the master switch is ON.',
                    'delicate-api-adapter'
                ) . '</p>';
            },
            'daa-settings'
        );
        $this->checkbox_field( 'date_picker_enabled',     __( 'Checkout delivery date picker', 'delicate-api-adapter' ),    'daa_features_section', __( 'Render flatpickr date input above billing on checkout. Writes _dtsc_delivery_date.', 'delicate-api-adapter' ) );
        $this->checkbox_field( 'fulfillment_enabled',     __( 'Fulfillment-type mapping',      'delicate-api-adapter' ),    'daa_features_section', __( 'Map the chosen WC shipping method to a DCP fulfillment_type (collect/delivery). Writes _dcp_fulfillment_type.', 'delicate-api-adapter' ) );
        $this->checkbox_field( 'address_rewrite_enabled', __( 'Shipping address rewrite',      'delicate-api-adapter' ),    'daa_features_section', __( 'For inter-shop pickup methods, replace the customer\'s shipping address with the satellite location. Original is snapshotted to _daa_original_shipping_address.', 'delicate-api-adapter' ) );
        $this->checkbox_field( 'test_mode_enabled',       __( 'Test mode (coupon-triggered)',  'delicate-api-adapter' ),    'daa_features_section', __( 'When the configured coupon is applied to a cart, write a verbose _daa_decision_trace meta to the order. No customer-visible effect. NOT gated by the master switch — works even when master is OFF, so developers can verify behaviour before going live.', 'delicate-api-adapter' ) );

        $this->text_field( 'test_mode_coupon', __( 'Test-mode coupon code', 'delicate-api-adapter' ), 'daa_features_section',
            __( 'Coupon code (case-insensitive) that activates test-mode meta writes. Create a matching zero-discount coupon in WooCommerce → Coupons. Leave blank to keep test mode dormant. Recommended: a long random string like DAA_TRACE_a1b2c3d4 so customers cannot guess it.', 'delicate-api-adapter' )
        );

        // Section 3 (0.2.0): Remote monitoring & control.
        add_settings_section(
            'daa_remote_section',
            __( 'Remote Monitoring & Control', 'delicate-api-adapter' ),
            function () {
                echo '<p>' . esc_html__(
                    'Lets the Delicate Courier Platform monitor this site and (optionally) toggle features remotely. Paste the credentials your platform administrator gave you. Both fields are write-only — once saved, they are stored encrypted and never displayed again. Leave a secret field blank when saving to keep the existing value.',
                    'delicate-api-adapter'
                ) . '</p>';
            },
            'daa-settings'
        );
        $this->checkbox_field( 'remote_monitoring_enabled', __( 'Enable remote monitoring', 'delicate-api-adapter' ), 'daa_remote_section',
            __( 'When OFF, this site neither pushes events to the platform nor accepts remote control. Default: OFF.', 'delicate-api-adapter' )
        );
        $this->text_field( 'merchant_id', __( 'Merchant slug', 'delicate-api-adapter' ), 'daa_remote_section',
            __( 'Lowercase identifier assigned by the platform when this site was registered. Example: honey-bee-baker.', 'delicate-api-adapter' )
        );
        $this->text_field( 'platform_ingest_url', __( 'Platform ingest URL', 'delicate-api-adapter' ), 'daa_remote_section',
            __( 'Full URL the platform exposes to receive push events. Example: https://api2.delicatecourier.co.za/api/daa/ingest', 'delicate-api-adapter' )
        );
        $this->secret_field( 'daa_secret', __( 'DAA secret (HMAC)', 'delicate-api-adapter' ), 'daa_remote_section',
            __( 'Shared HMAC-SHA256 secret. Used to sign push events leaving this site and to verify control calls arriving from the platform. Leave blank to keep the existing value.', 'delicate-api-adapter' )
        );
        $this->secret_field( 'platform_api_token', __( 'Platform API token', 'delicate-api-adapter' ), 'daa_remote_section',
            __( 'Bearer token the platform must send on every remote control call (header: X-DAA-Token). Leave blank to keep the existing value.', 'delicate-api-adapter' )
        );
    }

    /**
     * Sanitiser for the whole settings array. The Settings API passes the
     * raw submitted value (post-WP unslashing); we coerce types and trim
     * strings. Unknown keys are dropped — defends against extra POST data.
     */
    public function sanitize_settings( $input ) {
        $defaults = self::defaults();
        // Read the currently-stored values straight from the DB (bypass our
        // request-level cache) so we can implement "blank submission means
        // keep existing" for secret fields.
        $stored = get_option( DAA_OPTION_KEY, array() );
        if ( ! is_array( $stored ) ) { $stored = array(); }

        $clean = array();
        if ( ! is_array( $input ) ) {
            return $defaults;
        }

        $int_keys       = array( 'date_picker_lead_hours', 'date_picker_max_days_ahead' );
        $multiline_keys = array( 'date_picker_blackouts', 'fulfillment_mapping', 'address_overrides' );
        $secret_keys    = self::secret_keys();

        foreach ( $defaults as $key => $default_value ) {
            $is_secret = in_array( $key, $secret_keys, true );

            if ( ! array_key_exists( $key, $input ) ) {
                if ( $is_secret ) {
                    // Secrets: missing from POST → keep existing stored value.
                    $clean[ $key ] = array_key_exists( $key, $stored ) ? (string) $stored[ $key ] : '';
                } else {
                    // Unchecked checkboxes don't appear in $_POST at all; default
                    // to '0' so they save as off rather than reverting to default.
                    $clean[ $key ] = ( $default_value === '1' || $default_value === '0' ) ? '0' : $default_value;
                }
                continue;
            }
            $raw = $input[ $key ];

            if ( $is_secret ) {
                // Blank input = "keep existing". Any non-blank value = replace.
                // Also reject the placeholder "***" we render in the UI so a
                // copy-paste round trip doesn't store literal asterisks.
                $candidate = trim( (string) wp_unslash( $raw ) );
                if ( $candidate === '' || $candidate === '***' ) {
                    $clean[ $key ] = array_key_exists( $key, $stored ) ? (string) $stored[ $key ] : '';
                } else {
                    $clean[ $key ] = $candidate;
                }
            } elseif ( $default_value === '1' || $default_value === '0' ) {
                $clean[ $key ] = ( $raw === '1' || $raw === 1 || $raw === true || $raw === 'on' || $raw === 'yes' ) ? '1' : '0';
            } elseif ( in_array( $key, $int_keys, true ) ) {
                $n = (int) $raw;
                if ( $n < 0 ) { $n = 0; }
                $clean[ $key ] = (string) $n;
            } elseif ( in_array( $key, $multiline_keys, true ) ) {
                $clean[ $key ] = sanitize_textarea_field( (string) wp_unslash( $raw ) );
            } else {
                $clean[ $key ] = trim( (string) wp_unslash( $raw ) );
            }
        }
        return $clean;
    }

    /**
     * Render a password-style field for a secret. Existing stored value is
     * NOT placed in `value` — the placeholder hints whether one is already
     * set. Leaving blank on save preserves the stored value.
     */
    private function secret_field( $key, $label, $section, $description = '' ) {
        add_settings_field(
            DAA_OPTION_KEY . '_' . $key,
            esc_html( $label ),
            function () use ( $key, $description ) {
                $has    = self::get( $key, '' ) !== '';
                $name   = DAA_OPTION_KEY . '[' . $key . ']';
                $place  = $has
                    ? __( '(saved — leave blank to keep)', 'delicate-api-adapter' )
                    : __( 'Paste value here', 'delicate-api-adapter' );
                printf(
                    '<input type="password" name="%s" value="" class="regular-text" autocomplete="new-password" placeholder="%s" />',
                    esc_attr( $name ),
                    esc_attr( $place )
                );
                if ( $description !== '' ) {
                    echo '<p class="description">' . esc_html( $description ) . '</p>';
                }
            },
            'daa-settings',
            $section
        );
    }

    /**
     * Render a checkbox row using the Settings API conventions.
     */
    private function checkbox_field( $key, $label, $section, $description = '' ) {
        add_settings_field(
            DAA_OPTION_KEY . '_' . $key,
            esc_html( $label ),
            function () use ( $key, $description ) {
                $value = self::get( $key, '0' );
                $name  = DAA_OPTION_KEY . '[' . $key . ']';
                printf(
                    '<label><input type="checkbox" name="%s" value="1" %s /> %s</label>',
                    esc_attr( $name ),
                    checked( $value, '1', false ),
                    esc_html( $description )
                );
            },
            'daa-settings',
            $section
        );
    }

    /**
     * Render a single-line text field row.
     */
    private function text_field( $key, $label, $section, $description = '' ) {
        add_settings_field(
            DAA_OPTION_KEY . '_' . $key,
            esc_html( $label ),
            function () use ( $key, $description ) {
                $value = self::get( $key, '' );
                $name  = DAA_OPTION_KEY . '[' . $key . ']';
                printf(
                    '<input type="text" name="%s" value="%s" class="regular-text" />',
                    esc_attr( $name ),
                    esc_attr( $value )
                );
                if ( $description !== '' ) {
                    echo '<p class="description">' . esc_html( $description ) . '</p>';
                }
            },
            'daa-settings',
            $section
        );
    }

    /**
     * Render the full settings page — form, dependency panel, log viewer.
     */
    public function render_page() {
        if ( ! current_user_can( 'manage_woocommerce' ) ) {
            wp_die( esc_html__( 'You do not have permission to access this page.', 'delicate-api-adapter' ) );
        }

        $dep = DAA_Dependency_Check::get_status();
        ?>
        <div class="wrap">
            <h1><?php esc_html_e( 'Delicate API Adapter', 'delicate-api-adapter' ); ?></h1>

            <h2><?php esc_html_e( 'Dependency Status', 'delicate-api-adapter' ); ?></h2>
            <table class="widefat striped" style="max-width:800px;margin-bottom:20px;">
                <tbody>
                    <tr>
                        <th style="width:240px;"><?php esc_html_e( 'Delicate Courier Platform', 'delicate-api-adapter' ); ?></th>
                        <td>
                            <?php if ( ! empty( $dep['present'] ) && ! empty( $dep['compatible'] ) ) : ?>
                                <span style="color:#00a32a;">&#10003;</span>
                                <?php echo esc_html( sprintf(
                                    /* translators: %s: detected DCP version */
                                    __( 'Active, version %s — compatible.', 'delicate-api-adapter' ),
                                    (string) $dep['version']
                                ) ); ?>
                            <?php elseif ( ! empty( $dep['present'] ) ) : ?>
                                <span style="color:#d63638;">&#9888;</span>
                                <?php echo esc_html( sprintf(
                                    /* translators: 1: detected version, 2: required version */
                                    __( 'Active version %1$s, but at least %2$s is required.', 'delicate-api-adapter' ),
                                    (string) ( $dep['version'] ?: 'unknown' ),
                                    DAA_MIN_DCP_VERSION
                                ) ); ?>
                            <?php else : ?>
                                <span style="color:#d63638;">&#10005;</span>
                                <?php esc_html_e( 'Not detected. Adapter behaviour features are dormant.', 'delicate-api-adapter' ); ?>
                            <?php endif; ?>
                        </td>
                    </tr>
                    <tr>
                        <th><?php esc_html_e( 'Adapter version', 'delicate-api-adapter' ); ?></th>
                        <td><?php echo esc_html( DAA_VERSION ); ?></td>
                    </tr>
                    <tr>
                        <th><?php esc_html_e( 'HPOS compatibility', 'delicate-api-adapter' ); ?></th>
                        <td><?php esc_html_e( 'Declared at boot. All meta reads/writes use $order->get_meta() / update_meta_data().', 'delicate-api-adapter' ); ?></td>
                    </tr>
                </tbody>
            </table>

            <form action="options.php" method="post">
                <?php
                settings_fields( 'daa_settings_group' );
                do_settings_sections( 'daa-settings' );
                submit_button();
                ?>
            </form>

            <h2><?php esc_html_e( 'Recent Activity', 'delicate-api-adapter' ); ?></h2>
            <?php $this->render_log_panel(); ?>
        </div>
        <?php
    }

    /**
     * Render the last 100 log entries plus a Clear button.
     */
    private function render_log_panel() {
        $entries = DAA_Logger::fetch( 100 );
        $nonce   = wp_create_nonce( 'daa_clear_logs' );
        ?>
        <p>
            <button type="button" class="button" id="daa-clear-logs-btn" data-nonce="<?php echo esc_attr( $nonce ); ?>">
                <?php esc_html_e( 'Clear logs', 'delicate-api-adapter' ); ?>
            </button>
            <span id="daa-clear-logs-status" style="margin-left:10px;"></span>
        </p>
        <table class="widefat striped" style="max-width:1100px;">
            <thead>
                <tr>
                    <th style="width:150px;"><?php esc_html_e( 'Time', 'delicate-api-adapter' ); ?></th>
                    <th style="width:80px;"><?php esc_html_e( 'Level', 'delicate-api-adapter' ); ?></th>
                    <th style="width:140px;"><?php esc_html_e( 'Feature', 'delicate-api-adapter' ); ?></th>
                    <th style="width:80px;"><?php esc_html_e( 'Order', 'delicate-api-adapter' ); ?></th>
                    <th><?php esc_html_e( 'Message', 'delicate-api-adapter' ); ?></th>
                </tr>
            </thead>
            <tbody>
                <?php if ( empty( $entries ) ) : ?>
                    <tr><td colspan="5"><em><?php esc_html_e( 'No log entries yet.', 'delicate-api-adapter' ); ?></em></td></tr>
                <?php else : foreach ( $entries as $row ) : ?>
                    <tr>
                        <td><?php echo esc_html( $row->created_at ); ?></td>
                        <td><?php echo esc_html( $row->level ); ?></td>
                        <td><?php echo esc_html( $row->feature ); ?></td>
                        <td><?php echo $row->order_id ? esc_html( '#' . $row->order_id ) : '&mdash;'; ?></td>
                        <td>
                            <?php echo esc_html( $row->message ); ?>
                            <?php if ( ! empty( $row->context ) ) : ?>
                                <details style="margin-top:4px;">
                                    <summary style="cursor:pointer;color:#2271b1;"><?php esc_html_e( 'context', 'delicate-api-adapter' ); ?></summary>
                                    <pre style="white-space:pre-wrap;background:#f6f7f7;padding:6px;margin:4px 0 0;border-radius:3px;font-size:12px;"><?php echo esc_html( $row->context ); ?></pre>
                                </details>
                            <?php endif; ?>
                        </td>
                    </tr>
                <?php endforeach; endif; ?>
            </tbody>
        </table>
        <script>
        (function(){
            var btn = document.getElementById('daa-clear-logs-btn');
            if (!btn) return;
            btn.addEventListener('click', function(){
                if (!window.confirm(<?php echo wp_json_encode( __( 'Clear all log entries? This cannot be undone.', 'delicate-api-adapter' ) ); ?>)) return;
                var status = document.getElementById('daa-clear-logs-status');
                status.textContent = <?php echo wp_json_encode( __( 'Clearing…', 'delicate-api-adapter' ) ); ?>;
                var fd = new FormData();
                fd.append('action', 'daa_clear_logs');
                fd.append('_wpnonce', btn.getAttribute('data-nonce'));
                fetch(ajaxurl, { method: 'POST', credentials: 'same-origin', body: fd })
                    .then(function(r){ return r.json(); })
                    .then(function(j){
                        if (j && j.success) {
                            status.textContent = <?php echo wp_json_encode( __( 'Cleared. Reloading…', 'delicate-api-adapter' ) ); ?>;
                            setTimeout(function(){ window.location.reload(); }, 500);
                        } else {
                            status.textContent = <?php echo wp_json_encode( __( 'Failed.', 'delicate-api-adapter' ) ); ?>;
                        }
                    })
                    .catch(function(){
                        status.textContent = <?php echo wp_json_encode( __( 'Network error.', 'delicate-api-adapter' ) ); ?>;
                    });
            });
        })();
        </script>
        <?php
    }

    /**
     * AJAX: clear the log table.
     * Nonce + capability checked. Returns wp_send_json_success/error.
     */
    public function ajax_clear_logs() {
        // check_ajax_referer dies on failure with a 403, which is the
        // desired behaviour for nonce mismatch.
        check_ajax_referer( 'daa_clear_logs' );
        if ( ! current_user_can( 'manage_woocommerce' ) ) {
            wp_send_json_error( array( 'message' => 'forbidden' ), 403 );
        }
        DAA_Logger::clear();
        wp_send_json_success( array( 'cleared' => true ) );
    }
}
