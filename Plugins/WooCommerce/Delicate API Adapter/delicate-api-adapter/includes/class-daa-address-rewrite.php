<?php
/**
 * DAA_Address_Rewrite
 *
 * For shipping methods that ARE deliveries but where the goods are
 * being moved to a satellite premises (not the customer's address) —
 * e.g. the bakery's "Collect from 198 Watermeyer St, Meyerspark" — the
 * order's shipping address must be set to the satellite address before
 * DCP reads it and books a courier.
 *
 * Without this rewrite, DCP would book a courier to the customer's
 * home, which is exactly wrong: the customer is travelling to the
 * satellite themselves; the courier is moving the goods from the
 * main bakery to the satellite.
 *
 * Why this is a SEPARATE feature from fulfillment-mapping:
 *   - The two are conceptually independent. The fulfillment mapping
 *     answers "is this a courier-needed delivery or an in-person
 *     collect?". This feature answers "where should the courier
 *     deliver TO?". A merchant might want one without the other.
 *   - Each can fail independently. If the address-rewrite table has
 *     a bad entry, fulfillment_type still writes correctly. If the
 *     fulfillment toggle is off but address-rewrite is on, the rewrite
 *     still happens (the courier still gets the right address, even
 *     though DCP's fulfillment heuristic might be wrong about whether
 *     to send a courier at all).
 *   - The admin can disable one without the other, which matters when
 *     debugging.
 *
 * Storage:
 *   daa_settings['address_overrides'] is a JSON-encoded object keyed
 *   by shipping-method id (the same composite "base:instance" string
 *   the fulfillment mapping uses). Each value is a JSON object with:
 *     - address_1  (required if any rewrite is to happen)
 *     - address_2  (optional, often the suburb in ZA)
 *     - city       (required)
 *     - state      (required, ISO state code e.g. "GP")
 *     - postcode   (required)
 *     - country    (optional, default "ZA")
 *     - company    (optional)
 *   Plus one optional convenience field:
 *     - keep_customer_name  (bool, default true — keep the customer's
 *       first/last on the shipping record so the courier waybill
 *       shows who the goods are for, even though the address is the
 *       satellite. If false, name fields are blanked.)
 *
 *   Admin textarea format (one rule per line):
 *     flat_rate:36 = {"address_1":"198 Watermeyer St","address_2":"Meyerspark","city":"Pretoria","state":"GP","postcode":"0184"}
 *
 * Snapshot:
 *   Before rewriting, the original shipping_* fields are JSON-encoded
 *   and stored in _daa_original_shipping_address. This is the audit
 *   trail and the recovery path if a rule turns out to be wrong.
 *
 * Hook timing:
 *   woocommerce_checkout_create_order at priority 10 (same as
 *   fulfillment-mapping — they both write before DCP's priority-20
 *   sync). Order of execution between the two doesn't matter; they
 *   write to disjoint fields.
 *
 * Order note:
 *   When an address is rewritten, we add an order note via
 *   $order->add_order_note() so the merchant sees in the admin "DAA
 *   rewrote shipping address: <original> → <new>". The bakery's
 *   admin will appreciate that audit trail when fulfilling.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Address_Rewrite extends DAA_Feature {

    const OVERRIDES_KEY = 'address_overrides';

    /**
     * Required keys in an override JSON. Missing any of these = invalid
     * rule (skipped at runtime with a warning).
     */
    const REQUIRED_FIELDS = array( 'address_1', 'city', 'state', 'postcode' );

    /**
     * All allowed keys — anything else in the rule is dropped on parse
     * to defend against accidental extra fields.
     */
    const ALLOWED_FIELDS = array(
        'address_1', 'address_2', 'city', 'state', 'postcode', 'country',
        'company', 'keep_customer_name',
    );

    public function feature_slug() {
        return 'address_rewrite';
    }

    /**
     * Side-effect-free predictor for test mode. Returns an associative array:
     *   - chosen           : { method_id, ... } | null
     *   - is_dcp           : bool
     *   - has_override     : bool — does an override exist for this method id?
     *   - parsed_rule      : array | null — the parsed rule, or null if missing/invalid
     *   - would_rewrite    : bool — would maybe_rewrite have rewritten anything?
     *   - shipping_now     : the order's CURRENT shipping address at the moment
     *                        this predictor was called. NOTE: if address-rewrite
     *                        has already run (priority 10) and we're called
     *                        later (e.g. test mode at priority 50), this will
     *                        show the POST-rewrite address. The original
     *                        customer-entered address is still on the order
     *                        at _daa_original_shipping_address — look there
     *                        for the pre-rewrite snapshot.
     *
     * Does NOT touch $order.
     */
    public static function predict( $order ) {
        $self = new self( array() );
        $chosen = $self->get_chosen_method_signature( $order );
        $out = array(
            'chosen'        => $chosen,
            'is_dcp'        => false,
            'has_override'  => false,
            'parsed_rule'   => null,
            'would_rewrite' => false,
            'shipping_now'  => $self->snapshot_shipping_address( $order ),
        );
        if ( $chosen === null ) {
            return $out;
        }
        if ( strpos( $chosen['method_id'], 'delicate_courier_platform' ) === 0 ) {
            $out['is_dcp'] = true;
            return $out;
        }
        $overrides = $self->get_overrides();
        if ( ! isset( $overrides[ $chosen['method_id'] ] ) ) {
            return $out;
        }
        $out['has_override'] = true;
        $rule = $self->parse_rule( $overrides[ $chosen['method_id'] ] );
        if ( $rule === null ) {
            return $out;
        }
        $out['parsed_rule']   = $rule;
        $out['would_rewrite'] = true;
        return $out;
    }

    public function register() {
        // Server-side: rewrite address at order create.
        add_action( 'woocommerce_checkout_create_order', $this->safe_hook( array( $this, 'maybe_rewrite' ) ), 10, 2 );

        // Admin: settings UI for the overrides table.
        add_action( 'admin_init', array( $this, 'register_settings_fields' ), 20 );
    }

    /* ========================================================================
     * SERVER-SIDE
     * ====================================================================== */

    /**
     * Inspect the chosen shipping method, look up an override, snapshot
     * the original address, rewrite the order's shipping fields, log
     * + order-note the change.
     */
    public function maybe_rewrite( $order, $data ) {
        if ( ! $this->is_active_for_request() ) {
            return;
        }
        $order_id = method_exists( $order, 'get_id' ) ? (int) $order->get_id() : null;

        $chosen = $this->get_chosen_method_signature( $order );
        if ( $chosen === null ) {
            DAA_Logger::debug( $this->feature_slug(), 'No shipping method on order; nothing to rewrite', array(), $order_id );
            return;
        }

        // Passive on DCP's own shipping method — DCP is in control end-to-end.
        if ( strpos( $chosen['method_id'], 'delicate_courier_platform' ) === 0 ) {
            DAA_Logger::info( $this->feature_slug(), 'Passive: DCP shipping method, not rewriting', array(
                'method_id' => $chosen['method_id'],
            ), $order_id );
            return;
        }

        $overrides = $this->get_overrides();
        if ( ! isset( $overrides[ $chosen['method_id'] ] ) ) {
            // No rule for this method — pass through unchanged. Common
            // for "Baked Goods Delivery" which goes to the customer's
            // actual address.
            DAA_Logger::debug( $this->feature_slug(), 'No address override for chosen method; passthrough', array(
                'method_id' => $chosen['method_id'],
            ), $order_id );
            return;
        }

        $rule = $this->parse_rule( $overrides[ $chosen['method_id'] ] );
        if ( $rule === null ) {
            DAA_Logger::warning( $this->feature_slug(), 'Override rule failed to parse or missing required fields; passthrough', array(
                'method_id' => $chosen['method_id'],
                'raw_rule'  => $overrides[ $chosen['method_id'] ],
            ), $order_id );
            return;
        }

        // Snapshot the original FIRST, before any mutation.
        $original = $this->snapshot_shipping_address( $order );
        $order->update_meta_data( '_daa_original_shipping_address', wp_json_encode( $original, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE ) );

        // Apply the rule.
        $this->apply_rule( $order, $rule );

        // Audit shadow + order note.
        $order->update_meta_data( '_daa_address_rewritten', '1' );
        $order->update_meta_data( '_daa_address_rewrite_method', $chosen['method_id'] );

        $note = sprintf(
            /* translators: 1: original address, 2: new satellite address */
            __( 'Delicate API Adapter rewrote the shipping address for fulfillment routing: %1$s → %2$s', 'delicate-api-adapter' ),
            $this->format_address_single_line( $original ),
            $this->format_address_single_line( $rule )
        );
        // add_order_note() called inside checkout_create_order is safe — the
        // order is saved later by WC and notes will persist with it.
        $order->add_order_note( $note );

        DAA_Logger::info( $this->feature_slug(), 'Rewrote shipping address', array(
            'method_id' => $chosen['method_id'],
            'original'  => $original,
            'new'       => $rule,
        ), $order_id );
    }

    /**
     * Same signature-extraction logic as DAA_Fulfillment. Duplicated
     * intentionally so neither feature depends on the other internally —
     * if step 3 is later changed or removed, this one keeps working.
     */
    private function get_chosen_method_signature( $order ) {
        foreach ( $order->get_shipping_methods() as $item ) {
            /** @var WC_Order_Item_Shipping $item */
            $base = (string) $item->get_method_id();
            if ( $base === '' ) {
                continue;
            }
            $instance  = (int) $item->get_instance_id();
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
     * Snapshot the current shipping_* fields into a plain array, suitable
     * for JSON encoding and later restoration.
     */
    private function snapshot_shipping_address( $order ) {
        return array(
            'first_name' => (string) $order->get_shipping_first_name(),
            'last_name'  => (string) $order->get_shipping_last_name(),
            'company'    => (string) $order->get_shipping_company(),
            'address_1'  => (string) $order->get_shipping_address_1(),
            'address_2'  => (string) $order->get_shipping_address_2(),
            'city'       => (string) $order->get_shipping_city(),
            'state'      => (string) $order->get_shipping_state(),
            'postcode'   => (string) $order->get_shipping_postcode(),
            'country'    => (string) $order->get_shipping_country(),
            'phone'      => method_exists( $order, 'get_shipping_phone' ) ? (string) $order->get_shipping_phone() : '',
        );
    }

    /**
     * Apply a parsed rule to the order's shipping fields.
     *
     * keep_customer_name: when true (default), the customer's first/last
     * names stay. When false, they're blanked.
     *
     * Phone is always kept (it's the courier's contact for the recipient).
     */
    private function apply_rule( $order, $rule ) {
        // Company.
        if ( isset( $rule['company'] ) ) {
            $order->set_shipping_company( (string) $rule['company'] );
        }

        // Customer name handling.
        $keep_name = ! isset( $rule['keep_customer_name'] ) || $rule['keep_customer_name'] !== false;
        if ( ! $keep_name ) {
            $order->set_shipping_first_name( '' );
            $order->set_shipping_last_name( '' );
        }

        // Address fields. Use empty string for any optional field not
        // supplied so we don't end up with stale customer data mixed
        // with new satellite data (e.g. customer's address_2 lingering
        // after we set a new address_1).
        $order->set_shipping_address_1( (string) $rule['address_1'] );
        $order->set_shipping_address_2( isset( $rule['address_2'] ) ? (string) $rule['address_2'] : '' );
        $order->set_shipping_city( (string) $rule['city'] );
        $order->set_shipping_state( (string) $rule['state'] );
        $order->set_shipping_postcode( (string) $rule['postcode'] );
        $order->set_shipping_country( isset( $rule['country'] ) ? (string) $rule['country'] : 'ZA' );
    }

    /**
     * Render an address array as a single line, for order notes and logs.
     */
    private function format_address_single_line( $a ) {
        $parts = array_filter( array(
            $a['address_1'] ?? '',
            $a['address_2'] ?? '',
            $a['city']      ?? '',
            $a['state']     ?? '',
            $a['postcode']  ?? '',
            $a['country']   ?? '',
        ), function ( $v ) { return trim( (string) $v ) !== ''; } );
        return implode( ', ', $parts );
    }

    /* ========================================================================
     * OVERRIDES ACCESSOR
     * ====================================================================== */

    /**
     * Return the override map. Falls back to empty array (no rewrites)
     * if storage is missing or malformed.
     */
    public function get_overrides() {
        $raw = DAA_Settings::get( self::OVERRIDES_KEY, '' );
        if ( $raw === '' ) {
            return self::default_overrides();
        }
        $decoded = json_decode( (string) $raw, true );
        if ( ! is_array( $decoded ) ) {
            DAA_Logger::warning( $this->feature_slug(), 'Overrides JSON malformed; treating as empty', array( 'raw' => $raw ) );
            return array();
        }
        $clean = array();
        foreach ( $decoded as $k => $v ) {
            $k = trim( (string) $k );
            if ( $k === '' ) {
                continue;
            }
            // Value is stored as either a JSON-string or an already-decoded array.
            // After JSON round-trip via the textarea, it's always a JSON-string here.
            $clean[ $k ] = $v;
        }
        return $clean;
    }

    /**
     * Parse one rule's value into a canonical array. Returns null if
     * required fields are missing.
     *
     * The stored value is one of:
     *   - JSON string  (the round-tripped form after textarea transform)
     *   - Array        (if some future caller wrote it programmatically)
     */
    private function parse_rule( $raw ) {
        if ( is_array( $raw ) ) {
            $arr = $raw;
        } else {
            $arr = json_decode( (string) $raw, true );
            if ( ! is_array( $arr ) ) {
                return null;
            }
        }
        // Drop any unknown keys to defend against typos / extras.
        $clean = array();
        foreach ( self::ALLOWED_FIELDS as $field ) {
            if ( array_key_exists( $field, $arr ) ) {
                $clean[ $field ] = $arr[ $field ];
            }
        }
        foreach ( self::REQUIRED_FIELDS as $req ) {
            if ( ! isset( $clean[ $req ] ) || trim( (string) $clean[ $req ] ) === '' ) {
                return null;
            }
        }
        return $clean;
    }

    /**
     * Default overrides seeded on install. Bakery's two satellite
     * pickup methods get rewrites; the main bakery's local_pickup
     * doesn't need one (customer is collecting from the main address
     * which is the store's own address anyway); the distance-based
     * delivery doesn't need one (the customer's address is the
     * correct destination).
     *
     * Postcodes verified: 0184 = Meyerspark, 0157 = Irene.
     */
    public static function default_overrides() {
        return array(
            'flat_rate:36' => array(
                'company'    => 'Baked by Nataleen (Meyerspark satellite)',
                'address_1'  => '198 Watermeyer Street',
                'address_2'  => 'Meyerspark',
                'city'       => 'Pretoria',
                'state'      => 'GP',
                'postcode'   => '0184',
                'country'    => 'ZA',
            ),
            'flat_rate:37' => array(
                'company'    => 'Baked by Nataleen (Irene satellite)',
                'address_1'  => '1 Clifford Road',
                'address_2'  => 'Irene',
                'city'       => 'Centurion',
                'state'      => 'GP',
                'postcode'   => '0157',
                'country'    => 'ZA',
            ),
        );
    }

    /* ========================================================================
     * is_active_for_request
     * ====================================================================== */

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
        $section = 'daa_address_rewrite_section';
        add_settings_section(
            $section,
            __( 'Shipping Address Overrides', 'delicate-api-adapter' ),
            function () {
                echo '<p>' . wp_kses(
                    __( 'For shipping methods where the goods travel by courier to a satellite location (not the customer\'s address), specify the satellite address here. The customer\'s original address is snapshotted to <code>_daa_original_shipping_address</code> and an order note is added. Format: one rule per line, <code>method_id = {"address_1":"…","city":"…","state":"…","postcode":"…"}</code>. Required fields: <code>address_1</code>, <code>city</code>, <code>state</code>, <code>postcode</code>. Optional: <code>address_2</code>, <code>country</code>, <code>company</code>, <code>keep_customer_name</code> (defaults to true).', 'delicate-api-adapter' ),
                    array( 'code' => array(), 'strong' => array() )
                ) . '</p>';
            },
            'daa-settings'
        );

        add_settings_field(
            DAA_OPTION_KEY . '_' . self::OVERRIDES_KEY,
            esc_html__( 'Method → satellite address', 'delicate-api-adapter' ),
            array( $this, 'render_overrides_field' ),
            'daa-settings',
            $section
        );
    }

    public function render_overrides_field() {
        $raw = DAA_Settings::get( self::OVERRIDES_KEY, '' );
        $overrides = $raw === '' ? self::default_overrides() : ( json_decode( (string) $raw, true ) ?: self::default_overrides() );

        $name  = DAA_OPTION_KEY . '[' . self::OVERRIDES_KEY . ']';
        $lines = array();
        foreach ( $overrides as $k => $v ) {
            // Each value is either an array (default seed) or a JSON string
            // (after round-trip through storage). Normalise to array first.
            if ( ! is_array( $v ) ) {
                $decoded = json_decode( (string) $v, true );
                if ( is_array( $decoded ) ) {
                    $v = $decoded;
                } else {
                    continue;
                }
            }
            $lines[] = $k . ' = ' . wp_json_encode( $v, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
        }
        $textarea_value = implode( "\n", $lines );

        printf(
            '<textarea name="%s" rows="8" cols="80" class="large-text code">%s</textarea>',
            esc_attr( $name ),
            esc_textarea( $textarea_value )
        );

        echo '<p class="description">' . esc_html__( 'One rule per line. Example:', 'delicate-api-adapter' ) . '</p>';
        echo '<pre style="background:#f6f7f7;padding:8px;border-radius:3px;font-size:12px;max-width:900px;white-space:pre-wrap;">';
        echo "flat_rate:36 = {\"company\":\"Baked by Nataleen (Meyerspark)\",\"address_1\":\"198 Watermeyer Street\",\"address_2\":\"Meyerspark\",\"city\":\"Pretoria\",\"state\":\"GP\",\"postcode\":\"0184\",\"country\":\"ZA\"}\n";
        echo "flat_rate:37 = {\"company\":\"Baked by Nataleen (Irene)\",\"address_1\":\"1 Clifford Road\",\"address_2\":\"Irene\",\"city\":\"Centurion\",\"state\":\"GP\",\"postcode\":\"0157\",\"country\":\"ZA\"}";
        echo '</pre>';
    }

    /* ========================================================================
     * SETTINGS STORAGE TRANSFORM
     *
     * Like DAA_Fulfillment::transform_mapping_for_storage, this hooks
     * pre_update_option_daa_settings to convert the textarea "key = {json}"
     * lines into a JSON-encoded map of {method_id: json_value_string}.
     *
     * We store each value as a JSON string (not nested array) so the
     * top-level JSON has a flat shape — keeps the wp_options row compact
     * and the get_overrides() decode trivial.
     * ====================================================================== */

    public static function transform_overrides_for_storage( $value, $old_value ) {
        if ( ! is_array( $value ) ) {
            return $value;
        }
        if ( ! isset( $value[ self::OVERRIDES_KEY ] ) ) {
            return $value;
        }

        $raw  = (string) $value[ self::OVERRIDES_KEY ];
        $trim = ltrim( $raw );

        // Already JSON — leave it.
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
            $k    = trim( substr( $line, 0, $eq ) );
            $vraw = trim( substr( $line, $eq + 1 ) );
            if ( $k === '' || $vraw === '' ) {
                continue;
            }
            // Validate that the value parses as JSON. If not, skip the
            // line (silent — the merchant will see no override and
            // address rewrite logs a warning on first cart hit).
            $arr = json_decode( $vraw, true );
            if ( ! is_array( $arr ) ) {
                continue;
            }
            // Store the canonicalised re-encoding so storage is consistent
            // regardless of whether the merchant typed pretty-printed JSON.
            $parsed[ $k ] = wp_json_encode( $arr, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
        }
        $value[ self::OVERRIDES_KEY ] = wp_json_encode( $parsed );
        return $value;
    }
}
