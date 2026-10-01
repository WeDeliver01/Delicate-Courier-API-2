<?php
/**
 * DAA_Remote_Push — push events from this WordPress install to the
 * Delicate Courier Platform.
 *
 * Defining property: the bakery's checkout must NEVER block on us. Every
 * outbound call uses wp_remote_post() with blocking => false and a 2s
 * timeout. Failures are silent (best-effort); the pull channel from the
 * platform's Hangfire sweep is the source of truth for completeness.
 *
 * Wired hooks:
 *   - update_option_daa_settings (3-arg action): diffs old vs new and
 *     emits master_switch.toggled / feature_toggle.toggled / settings.saved.
 *     Secrets are redacted in the emitted snapshot.
 *   - daa_log_written (custom action fired by DAA_Logger::log when level
 *     is 'error'): emits log.error.
 *   - shutdown: re-runs DAA_Dependency_Check and emits dependency.changed
 *     if the cached status differs from the last persisted snapshot.
 *
 * plugin.activated / plugin.deactivated are NOT fired via hooks here —
 * they're invoked directly from DAA_Plugin::activate()/deactivate() (via
 * the static send() method below) because the hook needs to fire even
 * when no init() has happened.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class DAA_Remote_Push extends DAA_Feature {

	const SCHEMA_VERSION = 1;

	/** wp_options key where we stash the last seen dependency snapshot. */
	const DEP_SNAPSHOT_OPTION = 'daa_remote_dep_snapshot';

	public function feature_slug() {
		return 'remote_push';
	}

	/**
	 * Remote monitoring is gated by its own switch, NOT by master_enabled —
	 * the merchant might leave master OFF while still wanting the platform
	 * to see activation/log events.
	 */
	public function is_enabled() {
		return ! empty( $this->settings['remote_monitoring_enabled'] )
			&& ! empty( $this->settings['merchant_id'] )
			&& ! empty( $this->settings['daa_secret'] )
			&& ! empty( $this->settings['platform_ingest_url'] );
	}

	public function register() {
		// Settings diff push. update_option_$key is a 3-arg action.
		add_action( 'update_option_' . DAA_OPTION_KEY, $this->safe_hook( array( $this, 'on_settings_updated' ) ), 20, 3 );

		// Log-write push (only fires for error level — see DAA_Logger).
		add_action( 'daa_log_written', $this->safe_hook( array( $this, 'on_log_written' ) ), 10, 5 );

		// Dependency drift check at end of every request. Cheap: detect()
		// caches per-request, the get_option compare is one autoloaded read.
		add_action( 'shutdown', $this->safe_hook( array( $this, 'check_dependency_drift' ) ), 99 );
	}

	/* ============================================================
	 * EVENT HOOKS
	 * ============================================================ */

	public function on_settings_updated( $old, $new, $option ) {
		if ( ! $this->is_enabled() ) {
			return;
		}
		if ( ! is_array( $old ) ) { $old = array(); }
		if ( ! is_array( $new ) ) { $new = array(); }

		$actor = $this->current_actor_login();

		// 1. master_switch.toggled — explicit single-key delta on master_enabled.
		$old_master = isset( $old['master_enabled'] ) ? (string) $old['master_enabled'] : '0';
		$new_master = isset( $new['master_enabled'] ) ? (string) $new['master_enabled'] : '0';
		if ( $old_master !== $new_master ) {
			self::send( 'master_switch.toggled', array(
				'from'              => $old_master,
				'to'                => $new_master,
				'actor_user_login'  => $actor,
			) );
		}

		// 2. feature_toggle.toggled — one event per changed *_enabled key
		// (excluding master, already handled above).
		foreach ( $new as $k => $v ) {
			if ( $k === 'master_enabled' ) { continue; }
			if ( substr( $k, -8 ) !== '_enabled' ) { continue; }
			$ov = isset( $old[ $k ] ) ? (string) $old[ $k ] : '';
			$nv = (string) $v;
			if ( $ov !== $nv ) {
				self::send( 'feature_toggle.toggled', array(
					'feature'          => $k,
					'from'             => $ov,
					'to'               => $nv,
					'actor_user_login' => $actor,
				) );
			}
		}

		// 3. settings.saved — fired once if ANY non-toggle key differs.
		// Snapshot is the new settings, with secrets redacted.
		$changed_keys = array();
		foreach ( $new as $k => $v ) {
			$ov = array_key_exists( $k, $old ) ? $old[ $k ] : null;
			if ( $ov !== $v ) { $changed_keys[] = $k; }
		}
		// Filter out toggle keys for the "saved" event — those are already
		// captured by the dedicated events above.
		$non_toggle = array_values( array_filter( $changed_keys, function ( $k ) {
			return $k !== 'master_enabled' && substr( $k, -8 ) !== '_enabled';
		} ) );
		if ( ! empty( $non_toggle ) ) {
			self::send( 'settings.saved', array(
				'changed_keys'     => $non_toggle,
				'snapshot'         => self::redact_settings( $new ),
				'actor_user_login' => $actor,
			) );
		}
	}

	/**
	 * Bound to the custom 'daa_log_written' action emitted by DAA_Logger
	 * after every successful insert. We only push errors — info/warning/debug
	 * are picked up by the platform's pull channel.
	 */
	public function on_log_written( $level, $feature, $message, $context, $order_id ) {
		if ( ! $this->is_enabled() ) { return; }
		if ( $level !== 'error' ) { return; }
		self::send( 'log.error', array(
			'feature'  => (string) $feature,
			'message'  => (string) $message,
			'context'  => is_array( $context ) ? $context : array(),
			'order_id' => $order_id ? (int) $order_id : null,
		) );
	}

	public function check_dependency_drift() {
		if ( ! $this->is_enabled() ) { return; }
		if ( ! class_exists( 'DAA_Dependency_Check' ) ) { return; }
		$current = DAA_Dependency_Check::get_status();
		$previous = get_option( self::DEP_SNAPSHOT_OPTION, null );
		if ( $previous === $current ) { return; }
		if ( $previous !== null ) {
			self::send( 'dependency.changed', array(
				'previous_status' => $previous,
				'current_status'  => $current,
			) );
		}
		update_option( self::DEP_SNAPSHOT_OPTION, $current, false );
	}

	/* ============================================================
	 * SHARED SENDER (used by instance hooks AND activation hook)
	 * ============================================================ */

	/**
	 * Fire-and-forget POST to the configured platform ingest URL. Safe to
	 * call from anywhere — silent on every failure mode. Returns true if
	 * the request was dispatched (not whether the platform accepted it),
	 * false if it was skipped because remote monitoring is not configured.
	 */
	public static function send( $event, $event_data ) {
		$settings = DAA_Settings::get_all();
		if ( empty( $settings['remote_monitoring_enabled'] ) ) { return false; }

		$merchant_id = isset( $settings['merchant_id'] ) ? (string) $settings['merchant_id'] : '';
		$secret      = isset( $settings['daa_secret'] ) ? (string) $settings['daa_secret'] : '';
		$url         = isset( $settings['platform_ingest_url'] ) ? (string) $settings['platform_ingest_url'] : '';
		if ( $merchant_id === '' || $secret === '' || $url === '' ) { return false; }

		$envelope = array(
			'merchant_id'    => $merchant_id,
			'schema_version' => self::SCHEMA_VERSION,
			'occurred_at'    => gmdate( 'Y-m-d\TH:i:s\Z' ),
			'request_id'     => self::uuid4(),
			'payload'        => array(
				'event'      => (string) $event,
				'event_data' => is_array( $event_data ) ? $event_data : array(),
			),
		);

		$body = wp_json_encode( $envelope, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE );
		if ( $body === false ) { return false; }

		$sig = DAA_Sig::sign( $body, $secret );
		if ( $sig === '' ) { return false; }

		// blocking => false is the load-bearing property: this MUST NOT
		// hold the calling request open (the brief calls this out as
		// non-negotiable #1). 2s is the upper bound for DNS+connect.
		wp_remote_post( $url, array(
			'method'      => 'POST',
			'timeout'     => 2,
			'blocking'    => false,
			'redirection' => 0,
			'httpversion' => '1.1',
			'headers'     => array(
				'Content-Type'      => 'application/json',
				'X-DAA-Signature'   => $sig,
				'User-Agent'        => 'DelicateApiAdapter/' . DAA_VERSION,
			),
			'body'        => $body,
		) );

		return true;
	}

	/**
	 * Redact secrets in a settings array before pushing it across the wire.
	 */
	public static function redact_settings( $settings ) {
		if ( ! is_array( $settings ) ) { return array(); }
		$out = $settings;
		foreach ( array( 'daa_secret', 'platform_api_token' ) as $k ) {
			if ( array_key_exists( $k, $out ) && $out[ $k ] !== '' ) {
				$out[ $k ] = '***';
			}
		}
		return $out;
	}

	private function current_actor_login() {
		if ( function_exists( 'wp_get_current_user' ) ) {
			$u = wp_get_current_user();
			if ( $u && ! empty( $u->user_login ) ) {
				return (string) $u->user_login;
			}
		}
		return '';
	}

	private static function uuid4() {
		// RFC 4122 v4 UUID — uses random_bytes() (CSPRNG) when available,
		// falls back to wp_generate_uuid4 otherwise.
		if ( function_exists( 'random_bytes' ) ) {
			try {
				$data = random_bytes( 16 );
				$data[6] = chr( ( ord( $data[6] ) & 0x0f ) | 0x40 );
				$data[8] = chr( ( ord( $data[8] ) & 0x3f ) | 0x80 );
				return vsprintf( '%s%s-%s-%s-%s-%s%s%s', str_split( bin2hex( $data ), 4 ) );
			} catch ( \Throwable $e ) {
				// fall through
			}
		}
		if ( function_exists( 'wp_generate_uuid4' ) ) {
			return wp_generate_uuid4();
		}
		return sprintf( '%04x%04x-%04x-%04x-%04x-%04x%04x%04x',
			mt_rand( 0, 0xffff ), mt_rand( 0, 0xffff ),
			mt_rand( 0, 0xffff ),
			mt_rand( 0, 0x0fff ) | 0x4000,
			mt_rand( 0, 0x3fff ) | 0x8000,
			mt_rand( 0, 0xffff ), mt_rand( 0, 0xffff ), mt_rand( 0, 0xffff )
		);
	}
}
