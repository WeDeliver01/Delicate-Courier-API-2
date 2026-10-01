import { createClient, SupabaseClient } from '@supabase/supabase-js';

// Browser-side Supabase client. Owns the auth session (stored in localStorage
// via supabase-js itself) and refreshes access tokens automatically. The
// rest of the app reads the current access token through this client when
// making API calls (see `lib/api.ts`).
//
// We use a module-level singleton so HMR / multiple imports don't end up with
// rival auth listeners fighting over the same session.

const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
const anonKey = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY;

if (!url || !anonKey) {
  // Don't crash render — surface a clear console error and let the auth
  // provider show a sensible message. Throwing here would take the whole app
  // down in environments where NEXT_PUBLIC_* hasn't been wired yet.
  // eslint-disable-next-line no-console
  console.error(
    '[supabase] NEXT_PUBLIC_SUPABASE_URL or NEXT_PUBLIC_SUPABASE_ANON_KEY is not set. Auth will not work.'
  );
}

let _client: SupabaseClient | null = null;

export function getSupabase(): SupabaseClient {
  if (_client) return _client;
  _client = createClient(url ?? '', anonKey ?? '', {
    auth: {
      persistSession: true,
      autoRefreshToken: true,
      detectSessionInUrl: true,
      flowType: 'pkce',
    },
  });
  return _client;
}

// Convenience default export — most callers just want `supabase.*`.
export const supabase = typeof window !== 'undefined' ? getSupabase() : (null as unknown as SupabaseClient);
