<?php
/**
 * DAA_Fulfillment
 *
 * Maps the order's chosen WooCommerce shipping method to DCP's
 * fulfillment_type ('collect' / 'delivery' / 'special_trip'), and
 * writes the result to _dcp_fulfillment_type on the order.
 *
 * Why this matters:
 *   DCP infers fulfillment from the shipping method id heuristically:
 *     - 'local_pickup' or anything containing 'pickup'/'collect' → collect
 *     - everything else → delivery
 *   That heuristic is right for Method #1 (local_pickup:35 "Collect
 *   from shop Pretoria") but WRONG for Methods #2 and #3 (flat_rate:36
 *   and flat_rate:37, the inter-shop "collect from satellite" methods)
 *   because their id is flat_rate, which DCP treats as a delivery —
 *   so a courier shipment would be booked when no courier is needed.
 *
 *   This feature gives the merchant an admin-editable mapping table
 *   that overrides DCP's heuristic via _dcp_fulfillment_type. Method
 *   IDs (e.g. 'flat_rate:36', 'distance_rate:40') are keys.
 *
 * Storage:
 *   The mapping is stored in the same daa_settings array under key
 *   'fulfillment_mapping' as a JSON-encoded array. Example:
 *     {
 *       "local_pickup:35":    "collect",
 *       "flat_rate:36":       "delivery",
 *       "flat_rate:37":       "delivery",
 *       "distance_rate:40":   "delivery"
 *     }
 *   The value 'passive' is a sentinel that means "do nothing for this
 *   method id" — used for DCP's own shipping method.
 *
 * Hook timing:
 *   - woocommerce_checkout_create_order at priority 10 (DCP reads
 *     _dcp_fulfillment_type on woocommerce_checkout_order_processed
 *     at priority 20, so we are safely earlier).
 *
 * Fuzzy fallback:
 *   If the chosen method id is NOT in the mapping table, we attempt
 *   a label-substring match against the configured rules — useful
 *   when the merchant has the labels right but a method id has
 *   changed (e.g. they renamed a flat_rate zone instance). If still
 *   no match: log a warning and write nothing (DCP's heuristic
 *   takes over, which is fine for the common 'local_pickup' case).
 *
 * Passive when DCP's own method is the chosen one:
 *   DCP_Shipping_Method's id is 'delicate_courier_platform'. Any
 *   method id starting with that string is treated as passive
 *   (mapping value sentinel 'passive'). The handoff was firm: when
 *   the bakery eventually kills their own methods, DCP's shipping
 *   method becomes the only choice and DAA must stop touching the
 *   fulfillment meta entirely.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Fulfillment extends DAA_Feature {

    /** Valid fulfillment_type values per DCP_extract_fulfillment_type. */
    const VALID_TYPES = array( 'collect', 'delivery', 'special_trip' );

    /** Sentinel value for "leave this order alone". */
    const PASSIVE = 'passive';

    /** Settings key under daa_settings for the JSON-encoded mapping. */
    const MAPPING_KEY = 'fulfillment_mapping';

    public function feature_slug() {
        return 'fulfillment';
    }

    /**
     * Side-effect-free "what would I write to this order if I ran on it
     * right now?" predictor. Used by test mode (DAA_Test_Mode) to record
     * a decision trace without our hook actually firing.
     *
     * Returns an associative array:
     *   - chosen      : { method_id, base_id, instance, label } | null
     *   - is_dcp      : bool — is the chosen method DCP's own?
     *   - mapping     : the current mapping array as resolved
     *   - verdict     : 'collect'|'delivery'|'special_trip'|'passive'|null
     *   - would_write : bool — would apply_mapping have written meta?
     *
     * Safe to call from any context. Does NOT touch $order.
     */
    public static function predict( $order ) {
        $self = new self( array() );
        $chosen = $self->get_chosen_method_signature( $order );
        $out = array(
            'chosen'      => $chosen,
            'is_dcp'      => false,
            'mapping'     => $self->get_mapping(),
            'verdict'     => null,
            'would_write' => false,
        );
        if ( $chosen === null ) {
            return $out;
        }
        if ( $self->is_dcp_method( $chosen['method_id'] ) ) {
            $out['is_dcp']  = true;
            $out['verdict'] = self::PASSIVE;
            return $out;
        }
        $out['verdict']     = $self->resolve_verdict( $chosen, $out['mapping'] );
        $out['would_write'] = ( $out['verdict'] !== null
            && $out['verdict'] !== self::PASSIVE
            && in_array( $out['verdict'], self::VALID_TYPES, true ) );
        return $out;
    }

    public function register() {
        // Server-side: write meta at order create.
        add_action( 'woocommerce_checkout_create_order', $this->safe_hook( array( $this, 'apply_mapping' ) ), 10, 2 );

        // Admin: settings UI + save handler for the mapping table.
        add_action( 'admin_init', array( $this, 'register_settings_fields' ), 20 );
    }

    /* ========================================================================
     * SERVER-SIDE: write _dcp_fulfillment_type
     * ====================================================================== */

    /**
     * Inspect the order's chosen shipping method, look it up in the
     * mapping table, write _dcp_fulfillment_type if a non-passive rule
     * matches. Logs every decision so the merchant can audit via the
     * settings page.
     */
    public function apply_mapping( $order, $data ) {
        if ( ! $this->is_active_for_request() ) {
            return;
        }

        $order_id = method_exists( $order, 'get_id' ) ? (int) $order->get_id() : null;

        $chosen = $this->get_chosen_method_signature( $order );
        if ( $chosen === null ) {
            // Defensive: order has no shipping method. Let DCP fall
            // through to its own default. Nothing to map.
            DAA_Logger::debug( $this->feature_slug(), 'Order has no shipping method; skipping mapping', array(), $order_id );
            return;
        }

        // Passive bypass: DCP's own shipping method.
        if ( $this->is_dcp_method( $chosen['method_id'] ) ) {
            DAA_Logger::info( $this->feature_slug(), 'Passive: DCP shipping method chosen, no override', array(
                'method_id'    => $chosen['method_id'],
                'method_label' => $chosen['label'],
            ), $order_id );
            return;
        }

        $mapping = $this->get_mapping();
        $verdict = $this->resolve_verdict( $chosen, $mapping );

        if ( $verdict === null ) {
            DAA_Logger::warning( $this->feature_slug(), 'No mapping rule matched; leaving DCP heuristic to decide', array(
                'method_id'    => $chosen['method_id'],
                'method_label' => $chosen['label'],
                'mapping_keys' => array_keys( $mapping ),
            ), $order_id );
            return;
        }

        if ( $verdict === self::PASSIVE ) {
            DAA_Logger::info( $this->feature_slug(), 'Passive rule matched, no override', array(
                'method_id' => $chosen['method_id'],
            ), $order_id );
            return;
        }

        if ( ! in_array( $verdict, self::VALID_TYPES, true ) ) {
            DAA_Logger::warning( $this->feature_slug(), 'Mapping rule has invalid fulfillment_type; skipping', array(
                'method_id' => $chosen['method_id'],
                'verdict'   => $verdict,
            ), $order_id );
            return;
        }

        $order->update_meta_data( '_dcp_fulfillment_type', $verdict );
        $order->update_meta_data( '_daa_fulfillment_source', 'mapping' ); // Audit: this came from DAA, not from DTSC.

        DAA_Logger::info( $this->feature_slug(), 'Wrote _dcp_fulfillment_type', array(
            'method_id'        => $chosen['method_id'],
            'method_label'     => $chosen['label'],
            'fulfillment_type' => $verdict,
        ), $order_id );
    }

    /**
     * Extract the chosen shipping method's id and label from the order.
     *
     * Returns null if no shipping method is present (digital orders, or
     * any edge case where the cart submitted without one).
     *
     * The "method id" we care about is the FULL composite that WC writes
     * to the input value at checkout — e.g. 'flat_rate:36' — because
     * that's what the merchant sees in their WC zone admin and what they
     * will type into the mapping table. We get this from
     * WC_Order_Item_Shipping::get_method_id() (the base id, e.g. 'flat_rate')
     * concatenated with ':' and the instance id, e.g. 36.
     */
    private function get_chosen_method_signature( $order ) {
        foreach ( $order->get_shipping_methods() as $item ) {
            /** @var WC_Order_Item_Shipping $item */
            $base = (string) $item->get_method_id();
            if ( $base === '' ) {
                continue;
            }
            $instance = (int) $item->get_instance_id();
            $method_id = $instance > 0 ? $base . ':' . $instance : $base;
            return array(
                'method_id' => $method_id,
                'base_id'   => $base,
                'instance'  => $instance,
                'label'     => (string) $item->get_name(),
            );
        }
        return null;
    }

    /**
     * Return true if the chosen shipping method id belongs to DCP.
     *
     * DCP_Shipping_Method::$id is 'delicate_courier_platform'. WC writes
     * the composite as 'delicate_courier_platform:N' where N is the
     * zone-instance id, so we match the prefix.
     */
    private function is_dcp_method( $method_id ) {
        return strpos( (string) $method_id, 'delicate_courier_platform' ) === 0;
    }

    /**
     * Resolve a mapping verdict for a given chosen method.
     *
     * Tries in order:
     *   1. Exact match on the full id (e.g. 'flat_rate:36').
     *   2. Exact match on the base id alone (e.g. 'flat_rate').
     *      Risky — many merchants have multiple flat_rate instances
     *      with different intents — so this only succeeds if there's
     *      exactly one matching base-id rule.
     *   3. Label-substring fuzzy match: walk the mapping and check if
     *      any *rule label hint* (stored in the value side as
     *      "verdict|label_hint") is a substring of the chosen label,
     *      case-insensitive. Last resort.
     *
     * Returns 'collect' | 'delivery' | 'special_trip' | 'passive' | null.
     */
    private function resolve_verdict( $chosen, $mapping ) {
        // 1. Exact match.
        if ( isset( $mapping[ $chosen['method_id'] ] ) ) {
            return $this->extract_verdict( $mapping[ $chosen['method_id'] ] );
        }

        // 2. Base-id only, single candidate.
        $base_id    = $chosen['base_id'];
        $candidates = array();
        foreach ( $mapping as $rule_key => $rule_value ) {
            if ( $rule_key === $base_id ) {
                $candidates[] = $rule_value;
            }
        }
        if ( count( $candidates ) === 1 ) {
            return $this->extract_verdict( $candidates[0] );
        }

        // 3. Label-substring fallback. Uses the optional label_hint
        //    field if present.
        $label_lc = function_exists( 'mb_strtolower' )
            ? mb_strtolower( $chosen['label'], 'UTF-8' )
            : strtolower( $chosen['label'] );

        foreach ( $mapping as $rule_value ) {
            $hint = $this->extract_label_hint( $rule_value );
            if ( $hint === '' ) {
                continue;
            }
            $hint_lc = function_exists( 'mb_strtolower' )
                ? mb_strtolower( $hint, 'UTF-8' )
                : strtolower( $hint );
            if ( strpos( $label_lc, $hint_lc ) !== false ) {
                return $this->extract_verdict( $rule_value );
            }
        }

        return null;
    }

    /**
     * Rule values are stored as either:
     *   "collect"                        (verdict only)
     *   "delivery|Watermeyer"            (verdict + label hint)
     *   "passive"                        (sentinel)
     */
    private function extract_verdict( $rule_value ) {
        $parts = explode( '|', (string) $rule_value, 2 );
        return trim( strtolower( $parts[0] ) );
    }

    private function extract_label_hint( $rule_value ) {
        $parts = explode( '|', (string) $rule_value, 2 );
        return isset( $parts[1] ) ? trim( $parts[1] ) : '';
    }

    /* ========================================================================
     * MAPPING ACCESSORS
     * ====================================================================== */

    /**
     * Default mapping seeded into the settings on activation. Reads the
     * REAL method ids from the bakery's site (confirmed from cart HTML):
     *   - local_pickup:35  → collect (in-person at main bakery)
     *   - flat_rate:36     → delivery, label hint "Watermeyer" (satellite pickup, courier moves goods)
     *   - flat_rate:37     → delivery, label hint "Clifford"   (Irene satellite, courier moves goods)
     *   - distance_rate:40 → delivery (real customer-address delivery)
     *
     * The merchant can edit the table from the admin page if zone
     * instance ids change after a WC shipping-zone rebuild.
     */
    public static function default_mapping() {
        return array(
            'local_pickup:35'   => 'collect',
            'flat_rate:36'      => 'delivery|Watermeyer',
            'flat_rate:37'      => 'delivery|Clifford',
            'distance_rate:40'  => 'delivery|Baked Goods Delivery',
        );
    }

    /**
     * Fetch the current mapping (settings → JSON-decoded → array).
     * Falls back to the default seed if the stored value is missing
     * or malformed.
     */
    public function get_mapping() {
        $raw = DAA_Settings::get( self::MAPPING_KEY, '' );
        if ( $raw === '' ) {
            return self::default_mapping();
        }
        $decoded = json_decode( (string) $raw, true );
        if ( ! is_array( $decoded ) ) {
            DAA_Logger::warning( $this->feature_slug(), 'Mapping JSON is not an array; using defaults', array( 'raw' => $raw ) );
            return self::default_mapping();
        }
        // Normalise: keys are strings, values are strings.
        $clean = array();
        foreach ( $decoded as $k => $v ) {
            $k = trim( (string) $k );
            $v = trim( (string) $v );
            if ( $k !== '' && $v !== '' ) {
                $clean[ $k ] = $v;
            }
        }
        return $clean;
    }

    /* ========================================================================
     * is_active_for_request
     * ====================================================================== */

    /**
     * Master + feature + DCP gates. Unlike date-picker, we do NOT
     * stand down when DTSC is present — DTSC writes _dtsc_delivery_method
     * which DCP normalises to its own canonical 'collect'/'delivery' value,
     * but writing _dcp_fulfillment_type still takes precedence and that's
     * intentional: the bakery may eventually install DTSC for other reasons
     * but their shipping-method-based mapping is still authoritative.
     */
    private function is_active_for_request() {
        if ( ! $this->is_enabled() ) {
            return false;
        }
        return DAA_Plugin::dcp_ok();
    }

    /* ========================================================================
     * ADMIN UI
     * ====================================================================== */

    public function register_settings_fields() {
        $section = 'daa_fulfillment_section';
        add_settings_section(
            $section,
            __( 'Fulfillment Mapping', 'delicate-api-adapter' ),
            function () {
                echo '<p>' . wp_kses(
                    sprintf(
                        /* translators: 1: code for collect, 2: code for delivery */
                        __( 'Maps WooCommerce shipping methods to the courier platform\'s fulfillment_type. Each row is <code>method_id = %1$s | label_hint</code>, one per line. The label hint is optional — if set, it provides a fallback for cases where the method id has changed but the human label is still recognisable. Valid values: <code>collect</code>, <code>delivery</code>, <code>special_trip</code>, <code>passive</code>. Methods starting with <code>delicate_courier_platform</code> are always treated as passive (no override written) regardless of mapping.', 'delicate-api-adapter' ),
                        '<code>collect|delivery|special_trip|passive</code>',
                        '<code>delivery</code>'
                    ),
                    array( 'code' => array(), 'strong' => array() )
                ) . '</p>';
            },
            'daa-settings'
        );

        add_settings_field(
            DAA_OPTION_KEY . '_' . self::MAPPING_KEY,
            esc_html__( 'Method → fulfillment_type', 'delicate-api-adapter' ),
            array( $this, 'render_mapping_field' ),
            'daa-settings',
            $section
        );
    }

    /**
     * Render the mapping table as a textarea — one line per rule,
     * "method_id = verdict[|label_hint]". This is the simplest format
     * for a non-technical merchant who'll only edit it under our
     * direction during a support call.
     *
     * We also display the current bakery-default seed inline as a
     * "click to reset" affordance.
     */
    public function render_mapping_field() {
        $raw = DAA_Settings::get( self::MAPPING_KEY, '' );
        $mapping = $raw === '' ? self::default_mapping() : ( json_decode( (string) $raw, true ) ?: self::default_mapping() );

        $name = DAA_OPTION_KEY . '[' . self::MAPPING_KEY . ']';

        // Serialise into "key = value" line format for the textarea.
        // We store back as JSON in the sanitiser; the textarea is just
        // a friendlier I/O surface for the merchant.
        $lines = array();
        foreach ( $mapping as $k => $v ) {
            $lines[] = $k . ' = ' . $v;
        }
        $textarea_value = implode( "\n", $lines );

        printf(
            '<textarea name="%s" rows="6" cols="60" class="large-text code">%s</textarea>',
            esc_attr( $name ),
            esc_textarea( $textarea_value )
        );

        echo '<p class="description">' . esc_html__( 'One rule per line. Example:', 'delicate-api-adapter' ) . '</p>';
        echo '<pre style="background:#f6f7f7;padding:8px;border-radius:3px;font-size:12px;max-width:700px;">';
        echo "local_pickup:35   = collect\n";
        echo "flat_rate:36      = delivery|Watermeyer\n";
        echo "flat_rate:37      = delivery|Clifford\n";
        echo "distance_rate:40  = delivery|Baked Goods Delivery";
        echo '</pre>';
    }

    /* ========================================================================
     * SETTINGS INTEGRATION
     *
     * The line-based textarea has to be converted to JSON before storage
     * (otherwise we'd be doing line-parsing on every read). We hook the
     * pre_update_option_daa_settings filter to transform that one key.
     * ====================================================================== */

    /**
     * Convert "key = value" textarea lines into a JSON blob. Called by
     * the settings sanitiser via this class registering on
     * pre_update_option_daa_settings.
     *
     * Wired in DAA_Plugin::init() so it fires regardless of whether
     * this feature's toggle is on (the admin still needs to be able
     * to edit the mapping before flipping the toggle).
     */
    public static function transform_mapping_for_storage( $value, $old_value ) {
        if ( ! is_array( $value ) ) {
            return $value;
        }
        if ( ! isset( $value[ self::MAPPING_KEY ] ) ) {
            return $value;
        }
        $raw = (string) $value[ self::MAPPING_KEY ];

        // If it already looks like JSON, leave it alone — supports the
        // case where someone programmatically updates the option.
        $trim = ltrim( $raw );
        if ( strlen( $trim ) > 0 && ( $trim[0] === '{' || $trim[0] === '[' ) ) {
            return $value;
        }

        $parsed = array();
        $lines  = preg_split( '/\r\n|\r|\n/', $raw );
        foreach ( $lines as $line ) {
            $line = trim( $line );
            if ( $line === '' || $line[0] === '#' ) {
                continue;
            }
            $eq = strpos( $line, '=' );
            if ( $eq === false ) {
                continue;
            }
            $k = trim( substr( $line, 0, $eq ) );
            $v = trim( substr( $line, $eq + 1 ) );
            if ( $k === '' || $v === '' ) {
                continue;
            }
            $parsed[ $k ] = $v;
        }
        $value[ self::MAPPING_KEY ] = wp_json_encode( $parsed );
        return $value;
    }
}
