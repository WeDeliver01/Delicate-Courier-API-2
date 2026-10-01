'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { getSupabase } from '@/lib/supabase';

/**
 * OAuth + magic-link + password-reset landing page.
 *
 * Supabase (PKCE flow) appends `?code=...` to this URL. We swap the code for
 * a session, then bounce the user to /dashboard. Errors get surfaced inline
 * so they don't end up on a silent blank screen.
 */
export default function AuthCallbackPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const run = async () => {
      try {
        const supabase = getSupabase();
        const url = new URL(window.location.href);
        const code = url.searchParams.get('code');
        if (code) {
          const { error } = await supabase.auth.exchangeCodeForSession(code);
          if (error) throw error;
        }
        // exchangeCodeForSession may have just stored a session — go home.
        router.replace('/dashboard');
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Sign in failed');
      }
    };
    run();
  }, [router]);

  return (
    <div className="flex min-h-screen items-center justify-center bg-[#212121] text-white">
      {error ? (
        <div className="max-w-md rounded-lg bg-red-500/15 border border-red-500/30 p-4 text-sm text-red-200">
          <p className="font-medium">Sign in failed</p>
          <p className="mt-1 break-words">{error}</p>
          <button
            type="button"
            onClick={() => router.replace('/login')}
            className="mt-3 rounded bg-cyan-500 px-3 py-1.5 text-sm text-black hover:bg-cyan-400"
          >
            Back to sign in
          </button>
        </div>
      ) : (
        <p>Signing you in…</p>
      )}
    </div>
  );
}
