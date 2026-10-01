<?php
/**
 * DAA Logger
 *
 * Writes log entries to a custom table {$wpdb->prefix}daa_logs.
 *
 * Why a custom table (not wp_options, not error_log):
 *   - wp_options entries are autoloaded and slow to query by sub-key. DCP
 *     stores its debug log in a single option which it has to read+rewrite
 *     on every entry. That's fine for low traffic; we want something more
 *     robust for checkout flows that fire multiple log writes per request.
 *   - PHP error_log is at the mercy of the host's php.ini. Many shared
 *     hosts disable it or aggressively rotate it. Merchant cannot read it.
 *   - A custom table lets the admin UI show the last N entries with a
 *     simple "ORDER BY id DESC LIMIT N", and lets us hard-cap row count
 *     so we never balloon the DB.
 *
 * Ring buffer behaviour:
 *   On every insert, if the row count exceeds DAA_LOG_RING_SIZE, the
 *   oldest rows are deleted in a single DELETE statement. We compute the
 *   threshold id from the current MAX(id) minus DAA_LOG_RING_SIZE so we
 *   don't need to lock or transaction. Slight over/under-shoot under
 *   concurrency is acceptable for a log table.
 *
 * Levels: 'debug' | 'info' | 'warning' | 'error'
 *
 * Context is a free-form associative array — stored as JSON.
 */

if ( ! defined( 'ABSPATH' ) ) {
    exit;
}

class DAA_Logger {

    /** Cached resolved table name, per-request. */
    private static $table = null;

    /**
     * Fully qualified log table name including the WordPress table prefix.
     */
    public static function table_name() {
        if ( self::$table === null ) {
            global $wpdb;
            self::$table = $wpdb->prefix . DAA_LOG_TABLE_SUFFIX;
        }
        return self::$table;
    }

    /**
     * Create the log table. Called from the activation hook.
     *
     * Uses dbDelta() so future schema changes can be applied by re-running
     * the same statement on update. Returns the dbDelta result for logging.
     */
    public static function create_table() {
        global $wpdb;
        $table = self::table_name();
        $charset_collate = $wpdb->get_charset_collate();

        // dbDelta is strict about formatting — two spaces after PRIMARY KEY,
        // no backticks around column names, KEY index_name (col) on own lines.
        $sql = "CREATE TABLE {$table} (
    id bigint(20) unsigned NOT NULL AUTO_INCREMENT,
    created_at datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
    level varchar(16) NOT NULL DEFAULT 'info',
    feature varchar(64) NOT NULL DEFAULT '',
    message text NOT NULL,
    context longtext NULL,
    order_id bigint(20) unsigned NULL,
    PRIMARY KEY  (id),
    KEY created_at (created_at),
    KEY level (level),
    KEY order_id (order_id)
) {$charset_collate};";

        if ( ! function_exists( 'dbDelta' ) ) {
            require_once ABSPATH . 'wp-admin/includes/upgrade.php';
        }
        return dbDelta( $sql );
    }

    /**
     * Drop the log table. Called from uninstall.php.
     */
    public static function drop_table() {
        global $wpdb;
        $table = self::table_name();
        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
        $wpdb->query( "DROP TABLE IF EXISTS {$table}" );
    }

    /**
     * Write a log entry.
     *
     * Silent-fail: if logging itself errors, we MUST NOT raise — the
     * adapter's whole purpose is to be invisible at checkout. A logger
     * exception would propagate up into the order-creation flow.
     */
    public static function log( $level, $feature, $message, $context = array(), $order_id = null ) {
        try {
            global $wpdb;

            // Only allow whitelisted levels — keeps log queries tidy.
            $level = in_array( $level, array( 'debug', 'info', 'warning', 'error' ), true ) ? $level : 'info';

            // Encode context for storage. Anything not JSON-encodable gets
            // dropped to an explanatory note rather than failing the insert.
            $ctx_json = '';
            if ( ! empty( $context ) ) {
                $encoded = wp_json_encode( $context, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
                if ( $encoded === false ) {
                    $encoded = wp_json_encode( array( 'note' => 'context unencodable: ' . json_last_error_msg() ) );
                }
                $ctx_json = (string) $encoded;
            }

            $data = array(
                'level'    => $level,
                'feature'  => (string) $feature,
                'message'  => (string) $message,
                'context'  => $ctx_json,
                'order_id' => $order_id ? (int) $order_id : null,
            );
            $formats = array( '%s', '%s', '%s', '%s', '%d' );
            if ( $order_id === null ) {
                // Allow NULL in order_id column when no order is in scope.
                unset( $data['order_id'] );
                array_pop( $formats );
            }

            $wpdb->insert( self::table_name(), $data, $formats );

            // 0.2.0: Notify in-process listeners (DAA_Remote_Push hooks this
            // to forward errors to the platform). Fire-and-forget; any
            // listener that throws is swallowed by the outer try/catch.
            if ( function_exists( 'do_action' ) ) {
                do_action( 'daa_log_written', $level, (string) $feature, (string) $message, is_array( $context ) ? $context : array(), $order_id ? (int) $order_id : null );
            }

            // Probabilistic ring-buffer prune: only run on ~1 in 20 inserts
            // to avoid hammering the DB. We still hard-cap with a hefty
            // safety margin so over-shoot under concurrency is bounded.
            if ( wp_rand( 1, 20 ) === 1 ) {
                self::prune();
            }
        } catch ( \Throwable $e ) {
            // Last resort — try error_log. Never throw out of this method.
            if ( function_exists( 'error_log' ) ) {
                error_log( '[DAA Logger] write failed: ' . $e->getMessage() );
            }
        }
    }

    /** Convenience wrappers. */
    public static function debug(   $feature, $msg, $ctx = array(), $order_id = null ) { self::log( 'debug',   $feature, $msg, $ctx, $order_id ); }
    public static function info(    $feature, $msg, $ctx = array(), $order_id = null ) { self::log( 'info',    $feature, $msg, $ctx, $order_id ); }
    public static function warning( $feature, $msg, $ctx = array(), $order_id = null ) { self::log( 'warning', $feature, $msg, $ctx, $order_id ); }
    public static function error(   $feature, $msg, $ctx = array(), $order_id = null ) { self::log( 'error',   $feature, $msg, $ctx, $order_id ); }

    /**
     * Hard-cap the log table at DAA_LOG_RING_SIZE rows.
     *
     * Computes the cutoff id from MAX(id) - ring_size and deletes everything
     * at or below it. This is more concurrency-tolerant than ORDER BY ... LIMIT
     * (which some MySQL configs reject) and it deletes in a single statement.
     */
    public static function prune() {
        global $wpdb;
        $table = self::table_name();
        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
        $max_id = (int) $wpdb->get_var( "SELECT MAX(id) FROM {$table}" );
        if ( $max_id <= DAA_LOG_RING_SIZE ) {
            return; // Not yet at capacity.
        }
        $cutoff = $max_id - DAA_LOG_RING_SIZE;
        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
        $wpdb->query( $wpdb->prepare( "DELETE FROM {$table} WHERE id <= %d", $cutoff ) );
    }

    /**
     * Fetch the most recent N entries for the admin viewer.
     *
     * @param int    $limit    Max rows to return (default 100, cap 1000).
     * @param string $level    Optional level filter ('debug'/'info'/'warning'/'error').
     * @return array           Array of row objects, newest first.
     */
    public static function fetch( $limit = 100, $level = '' ) {
        global $wpdb;
        $table = self::table_name();
        $limit = max( 1, min( (int) $limit, 1000 ) );

        if ( $level !== '' && in_array( $level, array( 'debug', 'info', 'warning', 'error' ), true ) ) {
            // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
            return $wpdb->get_results(
                $wpdb->prepare( "SELECT * FROM {$table} WHERE level = %s ORDER BY id DESC LIMIT %d", $level, $limit )
            );
        }
        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
        return $wpdb->get_results( $wpdb->prepare( "SELECT * FROM {$table} ORDER BY id DESC LIMIT %d", $limit ) );
    }

    /** Empty the table — used by the "Clear logs" admin button. */
    public static function clear() {
        global $wpdb;
        $table = self::table_name();
        // phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
        $wpdb->query( "TRUNCATE TABLE {$table}" );
    }
}
