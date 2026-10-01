<?php
/**
 * DAA_Feature — abstract base for adapter features.
 *
 * Provides:
 *   - safe_hook(): wraps any WP hook callback in try/catch so that an
 *     unhandled exception in a feature CANNOT bubble up and crash the
 *     checkout request. The exception is logged and the callback returns
 *     a configurable "safe" value (defaults to the first arg unchanged
 *     for filters, or void for actions).
 *   - is_enabled(): consults the master kill switch AND the feature's own
 *     toggle in settings. Features short-circuit at the top of every
 *     callback by calling $this->is_enabled() first.
 *   - feature_slug(): unique string for log entries and settings keys.
 *
 * Subclasses must implement:
 *   - feature_slug(): string
 *   - register(): void  — call add_action/add_filter with $this->safe_hook(...)
 *
 * Subclasses MAY override:
 *   - is_enabled(): if the feature has more conditions than just the toggle.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

abstract class DAA_Feature {

    /**
     * Reference to the global settings array, fetched once per request via
     * DAA_Settings::get_all(). Each feature gets a read-only view.
     */
    protected $settings;

    public function __construct( $settings ) {
        $this->settings = is_array( $settings ) ? $settings : array();
    }

    /**
     * Stable identifier for this feature. Used as the "feature" column in
     * logs and as the prefix for per-feature settings keys.
     */
    abstract public function feature_slug();

    /**
     * Wire the feature's hooks. Always use $this->safe_hook() around any
     * callback that runs during a customer-facing request (checkout,
     * cart, AJAX) so an exception cannot break that flow.
     */
    abstract public function register();

    /**
     * Master kill switch AND per-feature toggle. If either is off, return false.
     *
     * Settings shape (set by DAA_Settings):
     *   $settings['master_enabled']         => '1' | '0'  (default '0' on install — opt-in)
     *   $settings[ $slug . '_enabled' ]     => '1' | '0'  (default '0' — opt-in per feature)
     */
    public function is_enabled() {
        if ( empty( $this->settings['master_enabled'] ) ) {
            return false;
        }
        $key = $this->feature_slug() . '_enabled';
        return ! empty( $this->settings[ $key ] );
    }

    /**
     * Wrap a callback so any \Throwable inside it is caught, logged, and
     * swallowed. Returns a closure suitable for add_action / add_filter.
     *
     * For filters: if the wrapped callback throws, we return $args[0]
     * (the value being filtered) unchanged — this is the only safe default;
     * returning null could break downstream filters.
     *
     * For actions: there's no return value, so we just suppress.
     *
     * @param callable $callback     The real callback.
     * @param bool     $is_filter    True if used with add_filter (we'll return
     *                               the first arg on exception). Default false.
     */
    protected function safe_hook( $callback, $is_filter = false ) {
        $feature = $this->feature_slug();
        return function () use ( $callback, $is_filter, $feature ) {
            $args = func_get_args();
            try {
                return call_user_func_array( $callback, $args );
            } catch ( \Throwable $e ) {
                // Log with as much context as we can pull from args. Be
                // defensive — args may be unserialisable objects.
                $ctx = array(
                    'exception_class' => get_class( $e ),
                    'message'         => $e->getMessage(),
                    'file'            => $e->getFile(),
                    'line'            => $e->getLine(),
                );
                // Try to extract order ID if the first arg is a WC_Order
                // (extremely common for the hooks we use).
                $order_id = null;
                if ( isset( $args[0] ) && is_object( $args[0] ) && method_exists( $args[0], 'get_id' ) ) {
                    try { $order_id = (int) $args[0]->get_id(); } catch ( \Throwable $ignored ) {}
                }

                DAA_Logger::error( $feature, 'Uncaught exception in hook callback', $ctx, $order_id );

                // For filters, return the unmodified input so downstream
                // filters and the original consumer behave as if we'd
                // never run. For actions, swallow.
                if ( $is_filter && isset( $args[0] ) ) {
                    return $args[0];
                }
                return null;
            }
        };
    }
}
