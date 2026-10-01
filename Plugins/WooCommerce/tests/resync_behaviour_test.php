<?php
/**
 * Regression tests for the $is_resync paths in dcp_sync_order().
 *
 * Why this exists: order #50829 went "completed" on WooCommerce but never
 * got booked on the platform. The completed hook must ALWAYS re-send an
 * already-synced order (so the platform's HandleExistingOrderAsync path can
 * re-check the booking), and a resync failure must NOT spam the order with
 * duplicate "sync failed" notes (the first sync already added one; the
 * outbox retry queue owns the failure from then on).
 *
 * Like build_fixtures.php, this loads the REAL plugin file under a minimal
 * WordPress/WooCommerce stub harness — no parallel re-implementation.
 *
 * Run from the repo root:
 *   php Plugins/WooCommerce/tests/resync_behaviour_test.php
 *
 * Exit code 0 = all assertions passed. Non-zero = failure (message on STDERR).
 * The .NET test PluginEndToEndTests.Php_resync_behaviour_suite_passes runs
 * this at `dotnet test` time.
 */

// ---------------------------------------------------------------------------
// 1. WordPress + WooCommerce stubs (superset of build_fixtures.php: adds
//    wc_get_order, filter overrides, hook capture, a $wpdb no-op, and a
//    configurable wp_remote_post response).
// ---------------------------------------------------------------------------

define( 'ABSPATH', __DIR__ . '/' );
define( 'MINUTE_IN_SECONDS', 60 );
define( 'HOUR_IN_SECONDS', 3600 );
define( 'DAY_IN_SECONDS', 86400 );
define( 'WEEK_IN_SECONDS', 604800 );

$GLOBALS['dcp_test_options']          = array();
$GLOBALS['dcp_test_actions']          = array();   // hook => [callbacks]
$GLOBALS['dcp_test_filter_overrides'] = array();   // tag  => forced value
$GLOBALS['dcp_test_post_calls']       = array();   // every wp_remote_post invocation
$GLOBALS['dcp_test_post_status']      = 200;       // response code wp_remote_post returns
$GLOBALS['dcp_test_current_order']    = null;      // returned by wc_get_order()

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

// Capture add_action registrations so the test can assert the plugin hooks
// dcp_sync_order to the completed-status transition.
function add_action( $hook = '', $cb = null, ...$rest ) {
    if ( is_string( $hook ) ) {
        $GLOBALS['dcp_test_actions'][ $hook ][] = $cb;
    }
    return true;
}
function add_filter( ...$args )            { return true; }
function remove_filter( ...$args )         { return true; }
function apply_filters( $tag, $value ) {
    // Tests force dcp_platform_post_max_attempts=1 so the 5xx retry loop
    // (with real sleep() backoff) doesn't slow the suite down. Everything
    // else passes through unchanged, like build_fixtures.php.
    return array_key_exists( $tag, $GLOBALS['dcp_test_filter_overrides'] )
        ? $GLOBALS['dcp_test_filter_overrides'][ $tag ]
        : $value;
}
function register_activation_hook( ...$a )   { return true; }
function register_deactivation_hook( ...$a ) { return true; }
function register_setting( ...$a )           { return true; }

function plugin_dir_path( $file )          { return dirname( $file ) . '/'; }
function plugin_basename( $file )          { return basename( $file ); }
function home_url( $path = '' )            { return 'https://store.example.com' . $path; }
function get_bloginfo( $what = 'name' )    { return $what === 'admin_email' ? 'admin@example.com' : 'Test Store'; }
function current_time( $type, $gmt = 0 )   { return '2026-05-01 10:00:00'; }
function absint( $v )                      { return abs( intval( $v ) ); }
function wp_cache_delete( ...$a )          { return true; }
function wc_get_base_location()            { return array( 'country' => 'ZA', 'state' => 'GT' ); }
function wp_generate_password( $len = 12, $s = true, $x = false ) { return str_repeat( 'a', max( 1, (int) $len ) ); }

class WP_Error {
    public $msg;
    public function __construct( $code = '', $msg = '' ) { $this->msg = $msg; }
    public function get_error_message() { return $this->msg; }
}
function is_wp_error( $thing ) { return $thing instanceof WP_Error; }

// $wpdb no-op — dcp_outbox_enqueue() writes to the retry-outbox table on a
// failed push; here we only need it not to crash.
class DcpFakeWpdb {
    public $options = 'wp_options';
    public $prefix  = 'wp_';
    public function prepare( $sql, ...$args ) { return $sql; }
    public function query( $sql )             { return 0; }
    public function get_var( $sql )           { return null; }
    public function get_results( $sql, $out = OBJECT ) { return array(); }
    public function update( ...$a )           { return 0; }
    public function insert( ...$a )           { return 0; }
}
$GLOBALS['wpdb'] = new DcpFakeWpdb();

function wp_remote_post( $url, $args = array() ) {
    $GLOBALS['dcp_test_post_calls'][] = array( 'url' => $url, 'args' => $args );
    $status = $GLOBALS['dcp_test_post_status'];
    return array(
        'response' => array( 'code' => $status ),
        'body'     => $status === 200
            ? json_encode( array( 'orderId' => 'PLAT-99', 'orderNumber' => 'PN-99' ) )
            : json_encode( array( 'message' => 'boom' ) ),
    );
}
function wp_remote_get( $url, $args = array() ) {
    return array( 'response' => array( 'code' => 200 ), 'body' => '{}' );
}
function wp_remote_retrieve_response_code( $r ) { return $r['response']['code'] ?? 0; }
function wp_remote_retrieve_body( $r )          { return $r['body'] ?? ''; }
function wp_remote_retrieve_headers( $r )       { return array(); }

function wp_json_encode( $data, $options = 0, $depth = 512 ) {
    return json_encode( $data, $options, $depth );
}

// ---------------------------------------------------------------------------
// 2. Fake order graph — same surface as build_fixtures.php PLUS the
//    mutation methods dcp_sync_order() uses: get_meta/update_meta_data/
//    save/add_order_note.
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
    public $id            = 50829;
    public $number        = '50829';
    public $status        = 'completed';
    public $total         = 300.0;
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
    public $notes         = array();   // add_order_note() capture
    public $saved         = 0;

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
    public function get_meta( $key, $single = true )   { return $this->meta[ $key ] ?? ''; }
    public function get_shipping_methods()              { return array(); }
    public function update_meta_data( $key, $value )   { $this->meta[ $key ] = $value; }
    public function save()                              { $this->saved++; return $this->id; }
    public function add_order_note( $note, $c = 0, $a = false ) { $this->notes[] = (string) $note; return count( $this->notes ); }
}

function wc_get_order( $order_id ) {
    $o = $GLOBALS['dcp_test_current_order'];
    return ( $o && $o->get_id() === $order_id ) ? $o : false;
}

// ---------------------------------------------------------------------------
// 3. Load the REAL plugin file.
// ---------------------------------------------------------------------------

require_once __DIR__ . '/../Default Plugin/delicate-courier-platform.php';

dcp_test_set_option( 'dcp_store_id',       '42' );
dcp_test_set_option( 'dcp_webhook_secret', 'test-webhook-secret-32-chars-min!!' );
// Kill the exponential-backoff retry loop's sleep()s: one attempt is enough
// to observe success/failure behaviour.
$GLOBALS['dcp_test_filter_overrides']['dcp_platform_post_max_attempts'] = 1;

// Simulate the plugin bootstrap having run its hook registrations. The real
// bootstrap is gated on class_exists('WooCommerce'); replicate the exact
// hook list here would be a parallel implementation, so instead grep the
// plugin source for the completed-status hook registration (test 1).

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

    $p = new DcpFakeProduct(); $p->weight = 1.0; $p->sku = 'SKU-W';
    $i = new DcpFakeItem();
    $i->id = 1; $i->product_id = 101; $i->name = 'Widget';
    $i->quantity = 2; $i->total = 200.0; $i->product = $p;
    $o->items = array( $i );
    return $o;
}

$failures = 0;
function dcp_assert( $cond, $label ) {
    global $failures;
    if ( $cond ) {
        echo "PASS  $label\n";
    } else {
        $failures++;
        fwrite( STDERR, "FAIL  $label\n" );
    }
}

function dcp_test_reset( $post_status ) {
    $GLOBALS['dcp_test_post_calls']  = array();
    $GLOBALS['dcp_test_post_status'] = $post_status;
}

// ---------------------------------------------------------------------------
// Test 1 — the completed-status transition is wired to dcp_sync_order in the
// plugin source. (The bootstrap that registers it is gated on a real
// WooCommerce class, so assert against the source registration line itself.)
// ---------------------------------------------------------------------------
$src = file_get_contents( __DIR__ . '/../Default Plugin/delicate-courier-platform.php' );
dcp_assert(
    preg_match( "/add_action\\(\\s*'woocommerce_order_status_completed',\\s*'dcp_sync_order'/", $src ) === 1,
    'plugin registers dcp_sync_order on woocommerce_order_status_completed'
);

// ---------------------------------------------------------------------------
// Test 2 — FIRST sync, success: POST sent, platform id persisted, exactly
// one "synced" note added.
// ---------------------------------------------------------------------------
dcp_test_reset( 200 );
$order = dcp_test_make_order();
$GLOBALS['dcp_test_current_order'] = $order;
dcp_sync_order( $order->get_id() );
dcp_assert( count( $GLOBALS['dcp_test_post_calls'] ) === 1, 'first sync posts to platform' );
dcp_assert( $order->meta['_dcp_platform_order_id'] === 'PLAT-99', 'first sync stores platform order id' );
dcp_assert( count( $order->notes ) === 1 && strpos( $order->notes[0], 'synced to platform' ) !== false,
    'first sync adds exactly one synced note' );

// ---------------------------------------------------------------------------
// Test 3 — RESYNC on the completed hook: an already-synced order (meta
// _dcp_platform_order_id present) MUST be re-sent. This is the behaviour
// that lets the platform re-check "completed but never booked" (#50829).
// No new order note may be added on the resync success path.
// ---------------------------------------------------------------------------
dcp_test_reset( 200 );
$notes_before = count( $order->notes );
dcp_sync_order( $order->get_id() ); // same order, meta already set → resync
dcp_assert( count( $GLOBALS['dcp_test_post_calls'] ) === 1, 'already-synced order re-sends on completed hook' );
dcp_assert( count( $order->notes ) === $notes_before, 'resync success adds no duplicate order note' );

// ---------------------------------------------------------------------------
// Test 4 — RESYNC failure: platform down (5xx). The order must NOT collect
// duplicate "sync failed" notes — failure bookkeeping belongs to the outbox.
// ---------------------------------------------------------------------------
dcp_test_reset( 500 );
$notes_before = count( $order->notes );
dcp_sync_order( $order->get_id() );
dcp_assert( count( $GLOBALS['dcp_test_post_calls'] ) === 1, 'resync failure still attempted the POST' );
dcp_assert( count( $order->notes ) === $notes_before, 'resync failure adds NO duplicate failure note' );

// Repeated failed resyncs (plugin fires on every later transition) stay silent.
dcp_test_reset( 500 );
dcp_sync_order( $order->get_id() );
dcp_assert( count( $order->notes ) === $notes_before, 'repeated resync failures stay note-silent' );

// ---------------------------------------------------------------------------
// Test 5 — FIRST sync failure: exactly one failure note (merchant must see
// it in the order timeline), and no platform id persisted.
// ---------------------------------------------------------------------------
dcp_test_reset( 500 );
$fresh = dcp_test_make_order();
$fresh->id = 50830; $fresh->number = '50830';
$GLOBALS['dcp_test_current_order'] = $fresh;
dcp_sync_order( $fresh->get_id() );
dcp_assert( count( $fresh->notes ) === 1 && strpos( $fresh->notes[0], 'sync failed' ) !== false,
    'first-sync failure adds exactly one failure note' );
dcp_assert( ! isset( $fresh->meta['_dcp_platform_order_id'] ), 'failed first sync stores no platform id' );

// ---------------------------------------------------------------------------
// Test 6 — unconfigured plugin on a resync: silent no-op (no note spam).
// ---------------------------------------------------------------------------
dcp_test_reset( 200 );
$GLOBALS['dcp_test_current_order'] = $order; // already-synced order
$saved_secret = get_option( 'dcp_webhook_secret' );
delete_option( 'dcp_webhook_secret' );
$notes_before = count( $order->notes );
dcp_sync_order( $order->get_id() );
dcp_assert( count( $GLOBALS['dcp_test_post_calls'] ) === 0, 'unconfigured plugin does not POST' );
dcp_assert( count( $order->notes ) === $notes_before, 'unconfigured resync adds no note' );
dcp_test_set_option( 'dcp_webhook_secret', $saved_secret );

if ( $failures > 0 ) {
    fwrite( STDERR, "\n$failures assertion(s) FAILED\n" );
    exit( 1 );
}
echo "\nAll resync behaviour assertions passed.\n";
exit( 0 );
