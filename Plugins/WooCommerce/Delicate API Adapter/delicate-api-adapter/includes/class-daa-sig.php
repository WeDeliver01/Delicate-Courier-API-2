<?php
/**
 * DAA_Sig — tiny HMAC-SHA256 signing helper used by both directions of
 * the remote monitoring channel.
 *
 *   sign( $body, $secret )   -> base64(HMAC-SHA256(secret, body))
 *   verify( $body, $sig, $secret ) -> bool (constant-time)
 *
 * Why a dedicated class instead of inlining the two-liner: it gives us a
 * single place to change algorithm/encoding if we ever rotate, and a
 * single place for the constant-time comparison rule that the .NET side
 * mirrors with CryptographicOperations.FixedTimeEquals.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class DAA_Sig {

	/**
	 * Compute the canonical signature of $body under $secret.
	 * Returns an empty string if either input is empty — callers should
	 * treat empty-string signatures as a signal not to send.
	 */
	public static function sign( $body, $secret ) {
		if ( ! is_string( $body ) || $body === '' ) {
			return '';
		}
		if ( ! is_string( $secret ) || $secret === '' ) {
			return '';
		}
		return base64_encode( hash_hmac( 'sha256', $body, $secret, true ) );
	}

	/**
	 * Verify a supplied signature against $body and $secret, in constant
	 * time. False on any input problem so callers can fail-closed.
	 */
	public static function verify( $body, $signature, $secret ) {
		if ( ! is_string( $body ) || ! is_string( $signature ) || ! is_string( $secret ) ) {
			return false;
		}
		if ( $body === '' || $signature === '' || $secret === '' ) {
			return false;
		}
		$expected = self::sign( $body, $secret );
		if ( $expected === '' ) {
			return false;
		}
		return hash_equals( $expected, $signature );
	}
}
