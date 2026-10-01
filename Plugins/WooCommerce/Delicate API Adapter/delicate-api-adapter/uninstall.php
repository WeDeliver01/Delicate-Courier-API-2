<?php
/**
 * Uninstall handler for Delicate API Adapter.
 *
 * Runs when the plugin is deleted (not just deactivated) via the WP admin.
 * Cleanup steps:
 *   1. Drop the {$wpdb->prefix}daa_logs table.
 *   2. Delete the daa_settings option.
 *
 * We do NOT touch any order meta — meta keys written by the adapter
 * (_dcp_fulfillment_type, _dtsc_delivery_date, _daa_*) belong to the
 * order record and may still be useful for reference. The merchant can
 * mass-delete via a CLI command if they really want a scorched-earth
 * removal; the runbook covers that.
 */

if ( ! defined( 'WP_UNINSTALL_PLUGIN' ) ) {
    exit;
}

global $wpdb;

// Drop the log table directly without loading the logger class — we may
// be running in a context where the plugin code is no longer loadable.
$table = $wpdb->prefix . 'daa_logs';
// phpcs:ignore WordPress.DB.PreparedSQL.InterpolatedNotPrepared
$wpdb->query( "DROP TABLE IF EXISTS {$table}" );

// Remove the single settings option. We do this with the bare API rather
// than loading the plugin's classes, for the same robustness reason.
delete_option( 'daa_settings' );
