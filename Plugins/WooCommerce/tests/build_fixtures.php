<?php
/**
 * Fixture generator for the WooCommerce plugin <-> platform JSON contract.
 *
 * Loads the REAL plugin file (`Plugins/WooCommerce/Default Plugin/
 * delicate-courier-platform.php`) under a minimal WordPress + WooCommerce
 * stub harness, then drives the real `dcp_build_order_payload()` and
 * `dcp_platform_post_signed()` functions. The bytes that the plugin would
 * have transmitted are intercepted at the `wp_remote_post()` boundary and
 * written to disk together with the signature the plugin actually
 * generated. This means the .NET contract test (`PhpFixtureContractTests`)
 * is asserting against the production code path, not a parallel
 * re-implementation.
 *
 * Run from the repo root:
 *   php Plugins/WooCommerce/tests/build_fixtures.php
 *
 * Outputs to DelicateCouriers/DelicateCouriers.Tests/Plugin/Fixtures/.
 * Commit the generated files; the .NET test reads them at `dotnet test` time.
 */

// ---------------------------------------------------------------------------
// 1. WordPress + WooCommerce stubs — just enough surface area for the plugin
//    file to load and for dcp_build_order_payload + dcp_platform_post_signed
//    to run. Anything not exercised by those two functions is a no-op.
// ---------------------------------------------------------------------------

define( 'ABSPATH', __DIR__ . '/' );

// Captured by stubbed wp_remote_post() so we can read what the plugin sent.
$GLOBALS['dcp_test_capture']  = null;
$GLOBALS['dcp_test_options']  = array();

function dcp_test_set_option( $k, $v ) { $GLOBALS['dcp_test_options'][ $k ] = $v; }
function get_option( $name, $default = false ) {
    return array_key_exists( $name, $GLOBALS['dcp_test_options'] )
        ? $GLOBALS['dcp_test_options'][ $name ]
        : $default;
}
function add_option( $name, $value = '', $deprecated = '', $autoload = 'yes' ) {
    if ( ! array_key_exists( $name, $GLOBALS['dcp_test_options'] ) ) {
        $GLOBALS['dcp_test_options'][ $name ] = $value;
    }
    return true;
}
function update_option( $name, $value, $autoload = null ) {
    $GLOBALS['dcp_test_options'][ $name ] = $value;
    return true;
}
function delete_option( $name ) {
    unset( $GLOBALS['dcp_test_options'][ $name ] );
    return true;
}

// Filter / action surface — no-op everywhere except apply_filters, which
// just hands the value back unchanged (no filters are registered in tests).
function add_action( ...$args )            { return true; }
function add_filter( ...$args )            { return true; }
function apply_filters( $tag, $value )     { return $value; }
function register_activation_hook( ...$a ) { return true; }
function register_setting( ...$a )         { return true; }

// Misc WP helpers used in the loaded code paths.
function plugin_dir_path( $file )          { return dirname( $file ) . '/'; }
function plugin_basename( $file )          { return basename( $file ); }
function home_url( $path = '' )            { return 'https://store.example.com' . $path; }
function get_bloginfo( $what = 'name' )    { return $what === 'admin_email' ? 'admin@example.com' : 'Test Store'; }
function current_time( $type )             { return '2026-05-01 10:00:00'; }
function absint( $v )                      { return abs( intval( $v ) ); }
function wp_cache_delete( ...$a )          { return true; }
function wc_get_base_location()            { return array( 'country' => 'ZA', 'state' => 'GT' ); }

// WP_Error stub — only is_wp_error() ever inspects the type. The plugin
// also probes `class_exists( 'WooCommerce' )` etc. inside its bootstrap;
// the built-in class_exists() naturally returns false for those, which is
// what we want (it short-circuits the WC-only code paths).
class WP_Error {
    public $msg;
    public function __construct( $code = '', $msg = '' ) { $this->msg = $msg; }
    public function get_error_message() { return $this->msg; }
}
function is_wp_error( $thing ) { return $thing instanceof WP_Error; }

// Intercepts the plugin's outbound POST. We do NOT touch the network. The
// $args['body'] passed here IS the byte-for-byte output of wp_json_encode()
// inside dcp_platform_post_signed(), and $args['headers']['X-Plugin-Signature']
// is the signature computed over those exact bytes.
function wp_remote_post( $url, $args = array() ) {
    $GLOBALS['dcp_test_capture'] = array(
        'url'     => $url,
        'body'    => $args['body'] ?? '',
        'headers' => $args['headers'] ?? array(),
    );
    return array(
        'response' => array( 'code' => 200 ),
        'body'     => json_encode( array( 'orderId' => 'PLAT-1', 'orderNumber' => 'PN-1' ) ),
    );
}
function wp_remote_get( $url, $args = array() ) {
    return array( 'response' => array( 'code' => 200 ), 'body' => '{}' );
}
function wp_remote_retrieve_response_code( $r ) { return $r['response']['code'] ?? 0; }
function wp_remote_retrieve_body( $r )          { return $r['body'] ?? ''; }

// wp_json_encode — same one-liner WordPress core uses, so the plugin's
// `wp_json_encode( $payload )` call goes through the real json_encode() with
// the same default $options = 0.
function wp_json_encode( $data, $options = 0, $depth = 512 ) {
    return json_encode( $data, $options, $depth );
}

// ---------------------------------------------------------------------------
// 2. Minimal WC_Order / WC_Order_Item_Product / WC_Product fakes. Only the
//    methods that dcp_build_order_payload() + dcp_extract_delivery_meta()
//    actually call are implemented.
// ---------------------------------------------------------------------------

class DcpFakeProduct {
    public $weight, $sku;
    public function get_weight() { return $this->weight; }
    public function get_sku()    { return $this->sku; }
}
class DcpFakeItem {
    public $id, $product_id, $name, $quantity, $total, $product;
    public $meta = array();
    public function get_id()         { return $this->id; }
    public function get_product_id() { return $this->product_id; }
    public function get_product()    { return $this->product; }
    public function get_name()       { return $this->name; }
    public function get_quantity()   { return $this->quantity; }
    public function get_total()      { return $this->total; }
    public function get_meta( $key, $single = true ) { return $this->meta[ $key ] ?? ''; }
}
class DcpFakeDate {
    public $s;
    public function __construct( $s ) { $this->s = $s; }
    public function format( $fmt )    { return $this->s; }
}
class DcpFakeOrder {
    public $id            = 12345;
    public $number        = 'ORD-2025-001';
    public $status        = 'processing';
    public $total         = 300.0;     // floats — matches floatval() in plugin
    public $shipping_total = 50.0;
    public $currency      = 'ZAR';
    public $payment_method_title = 'Credit Card';
    public $date_created  = null;
    public $customer_note = '';
    public $billing_email = 'jane@example.com';
    public $shipping_addr = array();
    public $billing_addr  = array();
    public $items         = array();
    public $meta          = array();

    public function get_id()                  { return $this->id; }
    public function get_order_number()        { return $this->number; }
    public function get_status()              { return $this->status; }
    public function get_total()               { return $this->total; }
    public function get_shipping_total()      { return $this->shipping_total; }
    public function get_currency()            { return $this->currency; }
    public function get_payment_method_title(){ return $this->payment_method_title; }
    public function get_date_created()        { return $this->date_created; }
    public function get_customer_note()       { return $this->customer_note; }
    public function get_billing_email()       { return $this->billing_email; }
    public function get_address( $type )      { return $type === 'shipping' ? $this->shipping_addr : $this->billing_addr; }
    public function get_items()               { return $this->items; }
    public function get_meta( $key, $single = true ) { return $this->meta[ $key ] ?? ''; }
}

// ---------------------------------------------------------------------------
// 3. Load the real plugin file. Constants in the plugin define-guard with
//    dcp_define_if_unset(), so they don't collide with the const we set up.
// ---------------------------------------------------------------------------

require_once __DIR__ . '/../Default Plugin/delicate-courier-platform.php';

// Provide the secret + store id the way the plugin would have read them
// from wp_options when an admin saved the settings page.
dcp_test_set_option( 'dcp_store_id',       '42' );
dcp_test_set_option( 'dcp_webhook_secret', 'test-webhook-secret-32-chars-min!!' );

// ---------------------------------------------------------------------------
// 4. Fixture builders — each returns a freshly populated DcpFakeOrder. The
//    differences between scenarios live ONLY in the order object; the JSON
//    shape and signing are produced by the real plugin code path.
// ---------------------------------------------------------------------------

function dcp_test_make_order() {
    $o = new DcpFakeOrder();
    $o->date_created = new DcpFakeDate( '2026-05-01 10:00:00' );
    $o->shipping_addr = array(
        'first_name' => 'Jane', 'last_name' => 'Customer',
        'address_1'  => '12 Test Avenue', 'address_2' => '',
        'city'       => 'Johannesburg', 'state' => 'Gauteng',
        'postcode'   => '2000',         'country' => 'ZA',
        'phone'      => '+27820000000',
    );
    $o->billing_addr = $o->shipping_addr;

    $p1 = new DcpFakeProduct(); $p1->weight = 1.0; $p1->sku = 'SKU-W';
    $i1 = new DcpFakeItem();
    $i1->id = 1; $i1->product_id = 101; $i1->name = 'Widget';
    $i1->quantity = 2; $i1->total = 200.0; $i1->product = $p1;

    $p2 = new DcpFakeProduct(); $p2->weight = 0.5; $p2->sku = 'SKU-G';
    $i2 = new DcpFakeItem();
    $i2->id = 2; $i2->product_id = 102; $i2->name = 'Gadget';
    $i2->quantity = 1; $i2->total = 100.0; $i2->product = $p2;

    $o->items = array( $i1, $i2 );

    // dcp_extract_delivery_meta() picks DTSC meta first when present.
    $o->meta['_dtsc_delivery_date'] = '2026-05-10';
    $o->meta['_dtsc_delivery_time'] = '14:00';
    // Occasion comes from WAPF (Advanced Product Fields) on a line item.
    $i1->meta['_wapf_meta'] = array(
        'fields' => array( array( 'label' => 'Occasion', 'value' => 'Birthday' ) ),
    );

    return $o;
}

$fixtures = array();

// 1) ASCII happy path.
$fixtures['ascii_happy_path'] = dcp_test_make_order();

// 2) Unicode in customer name — exercises wp_json_encode's default
//    \uXXXX escaping (flags=0). Drop occasion-extraction noise.
$o = dcp_test_make_order();
$o->shipping_addr['first_name'] = 'Renée';
$o->shipping_addr['last_name']  = 'Müller 你好';
$o->billing_email               = 'renee@example.com';
$o->shipping_addr['phone']      = '+27820000001';
$fixtures['unicode_customer_name'] = $o;

// 3) Decimal with trailing zero — PHP floats drop trailing zeros, so
//    floatval('3.50') serialises as `3.5`. The platform's `decimal`
//    deserialiser must accept that without precision loss.
$o = dcp_test_make_order();
$o->total          = floatval( '3.50' );
$o->shipping_total = floatval( '0.10' );
$p = new DcpFakeProduct(); $p->weight = 1.0; $p->sku = 'SKU-DEC';
$i = new DcpFakeItem();
$i->id = 1; $i->product_id = 101; $i->name = 'Single item';
$i->quantity = 1; $i->total = floatval( '3.40' ); $i->product = $p;
$o->items = array( $i );
$fixtures['decimal_trailing_zero'] = $o;

// 4) Optional fields null — clear DTSC meta and customer note so
//    dcp_extract_delivery_meta() returns [null, null, null].
$o = dcp_test_make_order();
$o->customer_note = null;
$o->meta = array();
foreach ( $o->items as $it ) { $it->meta = array(); }
$fixtures['nullable_optional_fields'] = $o;

// ---------------------------------------------------------------------------
// 5. Drive the REAL plugin path for each fixture, capture the bytes, write
//    body + signature next to the .NET test that consumes them.
// ---------------------------------------------------------------------------

$out_dir = __DIR__ . '/../../../DelicateCouriers/DelicateCouriers.Tests/Plugin/Fixtures';
if ( ! is_dir( $out_dir ) ) { mkdir( $out_dir, 0755, true ); }

file_put_contents( $out_dir . '/webhook_secret.txt', get_option( 'dcp_webhook_secret' ) );

foreach ( $fixtures as $name => $order ) {
    // 5a. Real payload assembly.
    $payload = dcp_build_order_payload( $order );

    // 5b. Real signing + POST. wp_remote_post() is the stub above, which
    //     captures $args['body'] (= wp_json_encode($payload)) and the
    //     X-Plugin-Signature header that dcp_platform_post_signed()
    //     computed over those same bytes.
    $GLOBALS['dcp_test_capture'] = null;
    $resp = dcp_platform_post_signed( DCP_PLATFORM_ORDER_PATH, $payload );
    if ( $GLOBALS['dcp_test_capture'] === null ) {
        fwrite( STDERR, "Fixture '$name': wp_remote_post was not invoked\n" );
        exit( 1 );
    }
    $body = $GLOBALS['dcp_test_capture']['body'];
    $sig  = $GLOBALS['dcp_test_capture']['headers']['X-Plugin-Signature'] ?? '';
    if ( $body === '' || $sig === '' ) {
        fwrite( STDERR, "Fixture '$name': empty body or signature captured\n" );
        exit( 1 );
    }

    file_put_contents( $out_dir . '/' . $name . '.json', $body );
    file_put_contents( $out_dir . '/' . $name . '.sig',  $sig );
    echo $name . ' -> ' . strlen( $body ) . ' bytes, sig=' . $sig . PHP_EOL;
}

echo 'Wrote fixtures to: ' . realpath( $out_dir ) . PHP_EOL;
