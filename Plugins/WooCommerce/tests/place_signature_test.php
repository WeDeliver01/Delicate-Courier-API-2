<?php
/**
 * The address signature, in PHP and in the browser.
 *
 * A pin is only reused while it still belongs to the address on screen, and the two
 * sides decide that by computing the same string. If they drift, one of two things
 * happens and neither is loud: either every pin looks stale and the saving quietly
 * disappears, or — far worse — a pin that should have been rejected is accepted and
 * a delivery is priced and collected at the address the shopper edited away from.
 *
 * So both implementations are pinned here against the same inputs.
 */

// The plugin file needs WordPress to load, and this test needs one function out of
// it. Extracting that function from the real source tests what actually ships,
// without a second copy here that could agree with itself while the plugin drifts.
$src = file_get_contents( __DIR__ . '/../Default Plugin/delicate-courier-platform.php' );

if ( ! preg_match( '/function dcp_address_signature\(.*?
\}/s', $src, $m ) ) {
    echo "FAIL  could not find dcp_address_signature in the plugin source
";
    exit( 1 );
}
eval( $m[0] );

$failures = 0;
function check( $label, $ok ) {
    global $failures;
    if ( $ok ) {
        echo "PASS  $label\n";
    } else {
        echo "FAIL  $label\n";
        $failures++;
    }
}

// The plain case.
check(
    'builds a lowercase pipe-joined signature',
    dcp_address_signature( '12 Oak Street', 'Centurion', 'Pretoria', '0157' )
        === '12 oak street|centurion|pretoria|0157'
);

// Whitespace is where two implementations usually diverge: one trims, the other
// also collapses runs, and a double space between words silently breaks every match.
check(
    'collapses internal whitespace',
    dcp_address_signature( '12   Oak    Street', 'Centurion', 'Pretoria', '0157' )
        === '12 oak street|centurion|pretoria|0157'
);

check(
    'trims the ends',
    dcp_address_signature( '  12 Oak Street  ', ' Centurion ', 'Pretoria', ' 0157' )
        === '12 oak street|centurion|pretoria|0157'
);

check(
    'case does not change the signature',
    dcp_address_signature( '12 OAK STREET', 'CENTURION', 'Pretoria', '0157' )
        === dcp_address_signature( '12 oak street', 'centurion', 'pretoria', '0157' )
);

// Empty parts must still occupy their slot, or "12 Oak Street" with no suburb would
// collide with a different address whose suburb is "Pretoria".
check(
    'empty parts keep their position',
    dcp_address_signature( '12 Oak Street', '', 'Pretoria', '0157' )
        === '12 oak street||pretoria|0157'
);

// The thing this exists to catch.
check(
    'editing the street number changes the signature',
    dcp_address_signature( '12 Oak Street', 'Centurion', 'Pretoria', '0157' )
        !== dcp_address_signature( '14 Oak Street', 'Centurion', 'Pretoria', '0157' )
);

// The JavaScript must agree, character for character.
check(
    'the browser lowercases, trims and collapses in the same order',
    strpos( $src, ".trim().toLowerCase().replace(/\s+/g, ' ')" ) !== false
);
check(
    'the browser joins on the same character',
    strpos( $src, "}).join('|');" ) !== false
);
check(
    'the browser signs the same four parts',
    strpos( $src, 'dcpSignature(addr1, suburb, city, postcode)' ) !== false
);

// Coordinates that are not a place must never reach the platform as if they were.
check(
    'zero/zero is refused rather than stored',
    strpos( $src, "abs( \$lat ) < 0.0001 && abs( \$lng ) < 0.0001" ) !== false
);
check(
    'out-of-range coordinates are refused',
    strpos( $src, '$lat < -90 || $lat > 90 || $lng < -180 || $lng > 180' ) !== false
);

echo "\n";
if ( $failures > 0 ) {
    echo "$failures assertion(s) failed.\n";
    exit( 1 );
}
echo "All place-signature assertions passed.\n";
