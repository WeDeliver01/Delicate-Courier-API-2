import { NextRequest, NextResponse } from 'next/server';
import { createClient } from '@supabase/supabase-js';

/**
 * Server-side proxy to the Delicate Integration service.
 *
 * The integration admin API is opened by a shared key. That key can change where a parcel is
 * collected from and which account a delivery is billed to, so it must never reach a browser —
 * anything the browser holds is in devtools, in an extension, and in a screenshot. It lives in
 * this server's environment and goes no further.
 *
 * It lives outside `/api` on purpose: this app rewrites that whole prefix to the .NET backend,
 * so a route handler under it is never reached.
 *
 * The operator is still authenticated the way the rest of the portal does it, with their
 * Supabase session. This route verifies that session before it will use the key at all, so the
 * proxy is not simply an unauthenticated hole in front of a protected API. It then tells the
 * integration service who the person was, which is what turns its change log from "the admin
 * key did this" into a name.
 */

const INTEGRATION_URL = process.env.INTEGRATION_API_URL;
const ADMIN_KEY = process.env.INTEGRATION_ADMIN_KEY;

const SUPABASE_URL = process.env.NEXT_PUBLIC_SUPABASE_URL;
const SUPABASE_ANON_KEY = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;

/** The signed-in operator, or null when the request has no usable session. */
async function currentUser(request: NextRequest): Promise<{ email: string } | null> {
  if (!SUPABASE_URL || !SUPABASE_ANON_KEY) return null;

  const authorization = request.headers.get('authorization');
  const token = authorization?.toLowerCase().startsWith('bearer ')
    ? authorization.slice(7).trim()
    : null;
  if (!token) return null;

  // Ask Supabase rather than decoding the token here. A signature we do not verify is a
  // claim, not an identity, and this is the only thing standing between the open internet
  // and a key that can reconfigure merchants.
  const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);
  const { data, error } = await supabase.auth.getUser(token);
  if (error || !data.user?.email) return null;
  return { email: data.user.email };
}

async function forward(request: NextRequest, path: string[]): Promise<NextResponse> {
  if (!INTEGRATION_URL || !ADMIN_KEY) {
    // Closed by default. A missing key means the console cannot manage merchants, never that
    // it manages them without one.
    return NextResponse.json(
      { error: 'Merchant administration is not configured on this server.' },
      { status: 503 }
    );
  }

  const user = await currentUser(request);
  if (!user) {
    return NextResponse.json({ error: 'Not signed in.' }, { status: 401 });
  }

  const target = new URL(
    `${INTEGRATION_URL.replace(/\/$/, '')}/api/admin/merchants/${path.join('/')}`.replace(
      /\/+$/,
      ''
    )
  );
  target.search = request.nextUrl.search;

  const body = request.method === 'GET' || request.method === 'DELETE'
    ? undefined
    : await request.text();

  const response = await fetch(target, {
    method: request.method,
    headers: {
      'content-type': 'application/json',
      'x-admin-key': ADMIN_KEY,
      'x-admin-actor': user.email,
    },
    body: body && body.length > 0 ? body : undefined,
    cache: 'no-store',
  });

  const text = await response.text();
  return new NextResponse(text || null, {
    status: response.status,
    headers: { 'content-type': response.headers.get('content-type') ?? 'application/json' },
  });
}

export async function GET(request: NextRequest, context: { params: Promise<{ path?: string[] }> }) {
  return forward(request, (await context.params).path ?? []);
}

export async function POST(request: NextRequest, context: { params: Promise<{ path?: string[] }> }) {
  return forward(request, (await context.params).path ?? []);
}

export async function PUT(request: NextRequest, context: { params: Promise<{ path?: string[] }> }) {
  return forward(request, (await context.params).path ?? []);
}
