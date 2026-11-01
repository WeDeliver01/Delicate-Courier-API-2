<?php
/**
 * The plugin does not call Shiplogic, and does not keep credentials for it.
 *
 * It used to. When the platform could not be reached the plugin fell back to calling
 * Shiplogic directly, from the merchant's own site, on credentials the merchant had
 * pasted into a settings field. That fallback outlived the decision to stop using
 * that courier, which made it worse than dead code: it fired exactly when the
 * platform was already struggling, and it would have quoted a shopper a price from
 * a company that was never going to collect the parcel. Nothing would have looked
 * wrong — that is the whole problem with a fallback to the wrong answer.
 *
 * Asserted against the shipped source rather than against behaviour, because the
 * guarantee being made is about what this file can reach at all.
 */

$path = __DIR__ . '/../Default Plugin/delicate-courier-platform.php';
$src  = file_get_contents( $path );

$failures = 0;
function check( $label, $ok, $detail = '' ) {
    global $failures;
    if ( $ok ) {
        echo "PASS  $label\n";
    } else {
        echo "FAIL  $label" . ( $detail === '' ? '' : " — $detail" ) . "\n";
        $failures++;
    }
}

// ── Nothing left to call ────────────────────────────────────────────────────

check(
    'no Shiplogic host anywhere in the plugin',
    stripos( $src, 'shiplogic.com' ) === false
);

preg_match_all( '/^function\s+(\w*shiplogic\w*)\s*\(/mi', $src, $fns );
check(
    'no function defined for talking to Shiplogic',
    empty( $fns[1] ),
    implode( ', ', $fns[1] )
);

preg_match_all( '/\bDCP_[A-Z_]*SHIPLOGIC[A-Z_]*\b/', $src, $consts );
check(
    'no Shiplogic constants',
    empty( $consts[0] ),
    implode( ', ', array_unique( $consts[0] ) )
);

check(
    'the rates path has no direct-courier tier',
    stripos( $src, 'dcp_fetch_rates_direct_shiplogic' ) === false
);

check(
    'no Shiplogic diagnostic is registered',
    stripos( $src, 'dcp_test_shiplogic_rates' ) === false
);

// ── The credentials are taken out, not just stopped being read ──────────────
//
// A bearer token nobody uses is still a bearer token in somebody's database. The
// activation hook deletes these, so this asserts the names are still in that list
// — removing them from it would be a tidy-up that silently leaves every merchant's
// stored credentials behind forever.

// Scoped to the $legacy array that is actually passed to delete_option(), not to the
// whole function: the autoload-off list a few lines below names the same options and
// would satisfy a looser match while deleting nothing.
if ( ! preg_match( '/\$legacy\s*=\s*array\((.*?)\);/s', $src, $legacy ) ) {
    check( 'the legacy-option delete list is present', false );
} else {
    foreach ( array( 'dcp_shiplogic_bearer', 'dcp_shiplogic_api_key',
                     'dcp_shiplogic_account_id', 'dcp_shiplogic_provider_id' ) as $opt ) {
        check( "upgrade deletes the stored $opt", strpos( $legacy[1], "'$opt'" ) !== false );
    }
}

// Nothing should still be reading them.
check(
    'no settings field writes a Shiplogic option',
    ! preg_match( '/name="dcp_shiplogic/', $src )
);

// ── Whatever is left is only the removal itself ─────────────────────────────

$mentions = array();
foreach ( explode( "\n", $src ) as $i => $line ) {
    if ( stripos( $line, 'shiplogic' ) !== false ) {
        $mentions[ $i + 1 ] = trim( $line );
    }
}
$unexpected = array();
foreach ( $mentions as $line_no => $text ) {
    $is_delete_list = preg_match( "/^'dcp_shiplogic_\w+',$/", $text );
    $is_explanation = stripos( $text, '*' ) === 0 || stripos( $text, '//' ) === 0;
    if ( ! $is_delete_list && ! $is_explanation ) {
        $unexpected[] = "$line_no: $text";
    }
}
check(
    'every remaining mention is either the delete list or a comment explaining it',
    empty( $unexpected ),
    implode( ' | ', $unexpected )
);

echo "\n";
if ( $failures > 0 ) {
    echo "$failures assertion(s) failed.\n";
    exit( 1 );
}
echo "Shiplogic is gone and its credentials are deleted on upgrade.\n";
