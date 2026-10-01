<?php
/**
 * DAA_Date_Picker
 *
 * Adds a delivery-date picker (flatpickr) to the WooCommerce classic
 * checkout. The picker is rendered ABOVE the billing details form so
 * the customer chooses a date before filling in the rest of their
 * checkout info — this matches the cognitive order most bakeries use
 * ("when do you want it?" → "where are you?").
 *
 * Data flow:
 *   1. wp_enqueue_scripts (front-end, checkout page only)
 *      → enqueue flatpickr JS + CSS + our init script + our stylesheet
 *      → wp_add_inline_script() pushes a daaDatePickerConfig object onto
 *        the page with the merchant's settings (min date, max date,
 *        blackouts, disabled weekdays, labels).
 *   2. woocommerce_before_checkout_billing_form (front-end)
 *      → echo a wrapped <input type="text" name="daa_delivery_date" />.
 *        flatpickr binds to it on DOMContentLoaded.
 *   3. woocommerce_checkout_process (POST, server)
 *      → validate: required, ISO format YYYY-MM-DD, within allowed
 *        window, not on a disabled weekday or blackout. On failure,
 *        wc_add_notice('...', 'error') — WC then halts checkout.
 *   4. woocommerce_checkout_create_order (server, priority 10)
 *      → if validation passed, write $_POST['daa_delivery_date']
 *        to order meta key _dtsc_delivery_date. DCP reads this
 *        natively in dcp_extract_delivery_meta().
 *
 * Settings (added to DAA_Settings::defaults() below — see register_settings_fields):
 *   - date_picker_lead_hours       int  (default 48)
 *   - date_picker_max_days_ahead   int  (default 60)
 *   - date_picker_blackouts        string  (YYYY-MM-DD, comma-separated)
 *   - date_picker_weekday_0 .. _6  '1'/'0'  (Sun=0 .. Sat=6; '1' = open)
 *   - date_picker_label            string  (default "Delivery / Collection date")
 *   - date_picker_required         '1'/'0' (default '1')
 *
 * Defaults: Mon-Sat open, Sun closed — matches the bakery's hours.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Date_Picker extends DAA_Feature {

    /**
     * Single-key prefix for $_POST and meta-write paths. Picked to be
     * unique even if DTSC is co-installed; DTSC owns dtsc_delivery_date
     * and we don't want to clash there.
     */
    const POST_FIELD = 'daa_delivery_date';

    public function feature_slug() {
        return 'date_picker';
    }

    public function register() {
        // Front-end: enqueue assets, render input field.
        add_action( 'wp_enqueue_scripts',                       $this->safe_hook( array( $this, 'enqueue_assets' ) ), 20 );
        add_action( 'woocommerce_before_checkout_billing_form', $this->safe_hook( array( $this, 'render_field' ) ), 5 );

        // Server-side: validate + persist.
        add_action( 'woocommerce_checkout_process',             $this->safe_hook( array( $this, 'validate_field' ) ) );
        add_action( 'woocommerce_checkout_create_order',        $this->safe_hook( array( $this, 'save_field' ) ), 10, 2 );

        // Admin: render this feature's settings rows under the Features section.
        add_action( 'admin_init',                               array( $this, 'register_settings_fields' ), 20 );
    }

    /* ========================================================================
     * FRONT-END
     * ====================================================================== */

    /**
     * Only enqueue on the checkout page, and only when the feature is
     * actually going to render. Prevents loading flatpickr on every
     * page of the site.
     */
    public function enqueue_assets() {
        if ( ! $this->is_active_for_request() ) {
            return;
        }

        // is_checkout() is the canonical guard. We exclude order-received
        // ("thank you") screens because those also count as is_checkout()
        // via the same Woo template but don't need the picker.
        if ( ! function_exists( 'is_checkout' ) || ! is_checkout() ) {
            return;
        }
        if ( function_exists( 'is_wc_endpoint_url' ) && is_wc_endpoint_url( 'order-received' ) ) {
            return;
        }

        $base = DAA_PLUGIN_URL . 'assets/';
        wp_enqueue_style(  'daa-flatpickr',     $base . 'vendor/flatpickr.min.css', array(), '4.6.13' );
        wp_enqueue_style(  'daa-checkout',      $base . 'css/daa-checkout.css',     array( 'daa-flatpickr' ), DAA_VERSION );
        wp_enqueue_script( 'daa-flatpickr',     $base . 'vendor/flatpickr.min.js',  array(),                    '4.6.13', true );
        wp_enqueue_script( 'daa-date-picker',   $base . 'js/daa-date-picker.js',    array( 'daa-flatpickr' ),   DAA_VERSION, true );

        // Push the merchant's config to the page as a single JS object.
        // We compute min/max here so the JS file stays static — no PHP
        // interpolation into the JS bundle.
        $config = $this->build_js_config();
        wp_add_inline_script(
            'daa-date-picker',
            'window.daaDatePickerConfig = ' . wp_json_encode( $config ) . ';',
            'before'
        );
    }

    /**
     * Build the merchant-facing config blob handed to flatpickr.
     *
     * Important: we send min/max as ISO date strings computed against
     * the WordPress site timezone (wp_timezone()), NOT the customer's
     * browser timezone. flatpickr accepts ISO date strings directly so
     * there's no JS-side date math needed.
     */
    private function build_js_config() {
        $lead_hours    = max( 0, (int) DAA_Settings::get( 'date_picker_lead_hours', 48 ) );
        $max_days      = max( 1, (int) DAA_Settings::get( 'date_picker_max_days_ahead', 60 ) );
        $required      = DAA_Settings::get( 'date_picker_required', '1' ) === '1';
        $label         = (string) DAA_Settings::get( 'date_picker_label', __( 'Delivery / Collection date', 'delicate-api-adapter' ) );

        $tz   = function_exists( 'wp_timezone' ) ? wp_timezone() : new \DateTimeZone( 'UTC' );
        $now  = new \DateTimeImmutable( 'now', $tz );
        $min  = $now->modify( '+' . $lead_hours . ' hours' );
        $max  = $now->modify( '+' . $max_days . ' days' );

        // Disabled weekdays: collect days where the setting is '0'.
        // flatpickr expects 0..6 where 0 = Sunday — same as PHP's
        // 'w' format and JS's getDay(), so we can hand them straight through.
        $disabled_weekdays = array();
        for ( $i = 0; $i <= 6; $i++ ) {
            if ( DAA_Settings::get( 'date_picker_weekday_' . $i, $i === 0 ? '0' : '1' ) !== '1' ) {
                $disabled_weekdays[] = $i;
            }
        }

        $blackouts = $this->parse_blackouts( (string) DAA_Settings::get( 'date_picker_blackouts', '' ) );

        return array(
            'fieldId'          => 'daa_delivery_date',
            'minDate'          => $min->format( 'Y-m-d' ),
            'maxDate'          => $max->format( 'Y-m-d' ),
            'disabledWeekdays' => $disabled_weekdays,
            'blackouts'        => $blackouts,
            'dateFormat'       => 'Y-m-d',
            'altInput'         => true,
            'altFormat'        => 'D, j M Y',
            'required'         => $required,
            'label'            => $label,
            'placeholder'      => __( 'Click to choose a date', 'delicate-api-adapter' ),
            'noticeRequired'   => __( 'Please choose a delivery / collection date.', 'delicate-api-adapter' ),
        );
    }

    /**
     * Render the date input on checkout. flatpickr binds to #daa_delivery_date
     * on DOMContentLoaded via our daa-date-picker.js.
     */
    public function render_field() {
        if ( ! $this->is_active_for_request() ) {
            return;
        }

        $label    = (string) DAA_Settings::get( 'date_picker_label', __( 'Delivery / Collection date', 'delicate-api-adapter' ) );
        $required = DAA_Settings::get( 'date_picker_required', '1' ) === '1';

        // Re-show the value the customer chose if the checkout was
        // re-rendered after a validation error.
        // phpcs:ignore WordPress.Security.NonceVerification.Missing
        $existing = isset( $_POST[ self::POST_FIELD ] ) ? trim( (string) wp_unslash( $_POST[ self::POST_FIELD ] ) ) : '';

        ?>
        <div class="daa-date-picker-wrap" id="daa-date-picker-wrap">
            <p class="form-row form-row-wide daa-date-picker-row">
                <label for="daa_delivery_date" class="daa-date-picker-label">
                    <?php echo esc_html( $label ); ?>
                    <?php if ( $required ) : ?><abbr class="required" title="<?php esc_attr_e( 'required', 'delicate-api-adapter' ); ?>">*</abbr><?php endif; ?>
                </label>
                <input
                    type="text"
                    id="daa_delivery_date"
                    name="<?php echo esc_attr( self::POST_FIELD ); ?>"
                    class="input-text daa-date-picker-input"
                    value="<?php echo esc_attr( $existing ); ?>"
                    autocomplete="off"
                    readonly
                />
            </p>
        </div>
        <?php
    }

    /**
     * Server-side validation. wc_add_notice('...', 'error') halts checkout
     * via WC's normal flow — no extra wiring needed.
     */
    public function validate_field() {
        if ( ! $this->is_active_for_request() ) {
            return;
        }

        $required = DAA_Settings::get( 'date_picker_required', '1' ) === '1';

        // phpcs:ignore WordPress.Security.NonceVerification.Missing
        $raw = isset( $_POST[ self::POST_FIELD ] ) ? trim( (string) wp_unslash( $_POST[ self::POST_FIELD ] ) ) : '';

        if ( $raw === '' ) {
            if ( $required ) {
                wc_add_notice( __( 'Please choose a delivery / collection date.', 'delicate-api-adapter' ), 'error' );
            }
            return;
        }

        $verdict = $this->validate_date_string( $raw );
        if ( $verdict !== true ) {
            wc_add_notice( $verdict, 'error' );
        }
    }

    /**
     * Persist to the canonical meta key DCP reads (_dtsc_delivery_date).
     *
     * We also write _daa_delivery_date as our own audit shadow — lets
     * us tell "DAA wrote this" apart from "DTSC wrote this" if both
     * plugins ever end up on the same site.
     */
    public function save_field( $order, $data ) {
        if ( ! $this->is_active_for_request() ) {
            return;
        }

        // phpcs:ignore WordPress.Security.NonceVerification.Missing
        $raw = isset( $_POST[ self::POST_FIELD ] ) ? trim( (string) wp_unslash( $_POST[ self::POST_FIELD ] ) ) : '';
        if ( $raw === '' ) {
            return;
        }

        // Defence-in-depth: re-validate at save time in case validate_field
        // didn't run (e.g. a custom checkout that bypasses the standard hook).
        if ( $this->validate_date_string( $raw ) !== true ) {
            DAA_Logger::warning( $this->feature_slug(), 'Invalid date at save time, ignoring', array( 'value' => $raw ), method_exists( $order, 'get_id' ) ? $order->get_id() : null );
            return;
        }

        $order->update_meta_data( '_dtsc_delivery_date', $raw );
        $order->update_meta_data( '_daa_delivery_date',  $raw );

        DAA_Logger::info( $this->feature_slug(), 'Saved delivery date to order', array( 'date' => $raw ), method_exists( $order, 'get_id' ) ? $order->get_id() : null );
    }

    /* ========================================================================
     * VALIDATION HELPERS
     * ====================================================================== */

    /**
     * Returns true if the date is acceptable; otherwise returns a translated
     * error message suitable for wc_add_notice().
     *
     * Checks:
     *   - exact YYYY-MM-DD format
     *   - parseable as a real calendar date
     *   - >= min (now + lead_hours), <= max (now + max_days_ahead)
     *   - weekday is enabled
     *   - not in the blackout list
     */
    private function validate_date_string( $value ) {
        if ( ! preg_match( '/^\d{4}-\d{2}-\d{2}$/', $value ) ) {
            return __( 'Delivery date must be in YYYY-MM-DD format.', 'delicate-api-adapter' );
        }

        $tz = function_exists( 'wp_timezone' ) ? wp_timezone() : new \DateTimeZone( 'UTC' );
        try {
            $picked = \DateTimeImmutable::createFromFormat( '!Y-m-d', $value, $tz );
        } catch ( \Throwable $e ) {
            $picked = false;
        }
        if ( ! $picked || $picked->format( 'Y-m-d' ) !== $value ) {
            return __( 'That doesn\'t look like a real date. Please pick again.', 'delicate-api-adapter' );
        }

        $lead_hours = max( 0, (int) DAA_Settings::get( 'date_picker_lead_hours', 48 ) );
        $max_days   = max( 1, (int) DAA_Settings::get( 'date_picker_max_days_ahead', 60 ) );
        $now        = new \DateTimeImmutable( 'now', $tz );
        $min        = $now->modify( '+' . $lead_hours . ' hours' )->setTime( 0, 0, 0 );
        $max        = $now->modify( '+' . $max_days . ' days' )->setTime( 23, 59, 59 );

        if ( $picked < $min ) {
            return sprintf(
                /* translators: %d: lead-time hours */
                __( 'Please choose a date at least %d hours from now.', 'delicate-api-adapter' ),
                $lead_hours
            );
        }
        if ( $picked > $max ) {
            return sprintf(
                /* translators: %d: max-days-ahead */
                __( 'Please choose a date within the next %d days.', 'delicate-api-adapter' ),
                $max_days
            );
        }

        $weekday = (int) $picked->format( 'w' ); // 0 = Sun .. 6 = Sat
        if ( DAA_Settings::get( 'date_picker_weekday_' . $weekday, $weekday === 0 ? '0' : '1' ) !== '1' ) {
            return __( 'We don\'t deliver / open on that day of the week. Please pick another date.', 'delicate-api-adapter' );
        }

        $blackouts = $this->parse_blackouts( (string) DAA_Settings::get( 'date_picker_blackouts', '' ) );
        if ( in_array( $value, $blackouts, true ) ) {
            return __( 'That date is unavailable. Please pick another.', 'delicate-api-adapter' );
        }

        return true;
    }

    /**
     * Parse the blackouts CSV from settings into a normalised array of
     * YYYY-MM-DD strings. Invalid entries are silently dropped — the
     * merchant gets a settings-page hint to format them properly but
     * shouldn't have a broken checkout if they fat-finger.
     */
    private function parse_blackouts( $csv ) {
        if ( $csv === '' ) {
            return array();
        }
        $out = array();
        foreach ( preg_split( '/[\s,]+/', $csv ) as $piece ) {
            $piece = trim( (string) $piece );
            if ( $piece === '' ) { continue; }
            if ( preg_match( '/^\d{4}-\d{2}-\d{2}$/', $piece ) ) {
                $out[] = $piece;
            }
        }
        return array_values( array_unique( $out ) );
    }

    /**
     * Composite "should this feature do anything on this request" check.
     *
     * Combines:
     *   - is_enabled() : master switch + this feature's toggle
     *   - dcp_ok()     : DCP present & version-compatible (defensive — the
     *                    picker writes meta DCP reads, so without DCP
     *                    those writes are wasted; we skip rendering at all)
     *   - DTSC absent  : if Delicate Two-Step Checkout is on the same site,
     *                    its scheduler renders its own date input and writes
     *                    _dtsc_delivery_date directly. We stand down to
     *                    avoid two date pickers on the same checkout. The
     *                    bakery doesn't have DTSC, so this branch is
     *                    defensive — but it prevents accidental breakage
     *                    if DTSC is later installed.
     *
     * Address-rewrite and fulfillment-mapping features will use a similar
     * pattern. Date picker is the ONE feature where we could argue it's
     * useful even without DCP (the bakery still wants the date on the
     * order). For step 2 we hard-couple to dcp_ok() to stay conservative —
     * we can relax this later if the merchant wants the picker for
     * non-DCP orders too.
     */
    private function is_active_for_request() {
        if ( ! $this->is_enabled() ) {
            return false;
        }
        if ( ! DAA_Plugin::dcp_ok() ) {
            return false;
        }
        // DTSC also writes _dtsc_delivery_date from its own scheduler. Don't
        // double-up. The class name 'DTSC_Order_Meta' is stable across the
        // current DTSC version (2.2.1) per our cross-read.
        if ( class_exists( 'DTSC_Order_Meta' ) ) {
            return false;
        }
        return true;
    }

    /* ========================================================================
     * SETTINGS FIELDS
     *
     * Registered late so they appear under the existing "Feature Toggles"
     * section that DAA_Settings adds at priority 10.
     * ====================================================================== */

    public function register_settings_fields() {
        $section = 'daa_date_picker_section';
        add_settings_section(
            $section,
            __( 'Date Picker Settings', 'delicate-api-adapter' ),
            function () {
                echo '<p>' . esc_html__(
                    'Controls the delivery / collection date picker that renders above the billing form on checkout. Only takes effect when the master switch is ON and the "Checkout delivery date picker" feature toggle is enabled.',
                    'delicate-api-adapter'
                ) . '</p>';
            },
            'daa-settings'
        );

        $this->add_setting_row( $section, 'date_picker_label',           __( 'Label text',                'delicate-api-adapter' ), 'text', __( 'Heading shown above the date input on checkout.', 'delicate-api-adapter' ) );
        $this->add_setting_row( $section, 'date_picker_required',        __( 'Required field',            'delicate-api-adapter' ), 'checkbox', __( 'When ON, customer must pick a date to complete checkout.', 'delicate-api-adapter' ) );
        $this->add_setting_row( $section, 'date_picker_lead_hours',      __( 'Lead time (hours)',         'delicate-api-adapter' ), 'number', __( 'Minimum hours between now and the earliest selectable date. Default 48.', 'delicate-api-adapter' ) );
        $this->add_setting_row( $section, 'date_picker_max_days_ahead',  __( 'Booking window (days)',     'delicate-api-adapter' ), 'number', __( 'How far ahead customers can book. Default 60.', 'delicate-api-adapter' ) );
        $this->add_setting_row( $section, 'date_picker_blackouts',       __( 'Blackout dates',            'delicate-api-adapter' ), 'textarea', __( 'One YYYY-MM-DD per line, or comma-separated. Customers cannot pick these dates.', 'delicate-api-adapter' ) );

        // Per-weekday open/closed. Defaults: Mon-Sat ON, Sun OFF.
        $day_labels = array(
            0 => __( 'Sunday',    'delicate-api-adapter' ),
            1 => __( 'Monday',    'delicate-api-adapter' ),
            2 => __( 'Tuesday',   'delicate-api-adapter' ),
            3 => __( 'Wednesday', 'delicate-api-adapter' ),
            4 => __( 'Thursday',  'delicate-api-adapter' ),
            5 => __( 'Friday',    'delicate-api-adapter' ),
            6 => __( 'Saturday',  'delicate-api-adapter' ),
        );
        foreach ( $day_labels as $i => $label ) {
            $this->add_setting_row( $section, 'date_picker_weekday_' . $i, $label, 'checkbox', __( 'Allow this weekday for delivery / collection.', 'delicate-api-adapter' ) );
        }
    }

    /**
     * Reusable settings-field renderer for this feature's keys.
     * Mirrors DAA_Settings::checkbox_field / text_field but lives here so
     * the date-picker class is self-contained — no settings.php edits
     * needed when this class is dropped in.
     */
    private function add_setting_row( $section, $key, $label, $type, $description = '' ) {
        add_settings_field(
            DAA_OPTION_KEY . '_' . $key,
            esc_html( $label ),
            function () use ( $key, $type, $description ) {
                $name = DAA_OPTION_KEY . '[' . $key . ']';
                $val  = DAA_Settings::get( $key, '' );
                switch ( $type ) {
                    case 'checkbox':
                        printf(
                            '<label><input type="checkbox" name="%s" value="1" %s /> %s</label>',
                            esc_attr( $name ),
                            checked( $val, '1', false ),
                            esc_html( $description )
                        );
                        break;
                    case 'number':
                        printf(
                            '<input type="number" name="%s" value="%s" class="small-text" min="0" />',
                            esc_attr( $name ),
                            esc_attr( $val )
                        );
                        if ( $description !== '' ) {
                            echo '<p class="description">' . esc_html( $description ) . '</p>';
                        }
                        break;
                    case 'textarea':
                        printf(
                            '<textarea name="%s" rows="3" cols="40" class="regular-text">%s</textarea>',
                            esc_attr( $name ),
                            esc_textarea( $val )
                        );
                        if ( $description !== '' ) {
                            echo '<p class="description">' . esc_html( $description ) . '</p>';
                        }
                        break;
                    case 'text':
                    default:
                        printf(
                            '<input type="text" name="%s" value="%s" class="regular-text" />',
                            esc_attr( $name ),
                            esc_attr( $val )
                        );
                        if ( $description !== '' ) {
                            echo '<p class="description">' . esc_html( $description ) . '</p>';
                        }
                }
            },
            'daa-settings',
            $section
        );
    }
}
