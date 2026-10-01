'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useRouter, usePathname } from 'next/navigation';
import type { Session, User } from '@supabase/supabase-js';
import { getSupabase } from '@/lib/supabase';

export interface AuthUser {
  userId: string;            // Supabase auth.users.id (uuid)
  email: string;
  name: string;
  role: string;              // SuperAdmin | Admin | User (from app_metadata.app_role)
  tenantId: number | null;   // from app_metadata.tenant_id
}

interface AuthContextValue {
  session: Session | null;
  user: AuthUser | null;
  loading: boolean;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

// Pages that should be reachable without a session.
const PUBLIC_PATHS = ['/login', '/auth/callback', '/'];

function deriveAuthUser(session: Session | null): AuthUser | null {
  if (!session?.user) return null;
  const u: User = session.user;
  const meta = (u.app_metadata ?? {}) as Record<string, unknown>;
  const userMeta = (u.user_metadata ?? {}) as Record<string, unknown>;
  // Prefer top-level claim from the Supabase auth hook, fall back to app_metadata.
  const role = (meta.app_role as string)
    ?? (meta.role as string)
    ?? 'User';
  const tenantRaw = meta.tenant_id;
  const tenantId = typeof tenantRaw === 'number'
    ? tenantRaw
    : typeof tenantRaw === 'string' && tenantRaw.length > 0
      ? Number.parseInt(tenantRaw, 10)
      : null;
  const name = (userMeta.full_name as string)
    ?? (userMeta.name as string)
    ?? u.email
    ?? 'User';
  return {
    userId: u.id,
    email: u.email ?? '',
    name,
    role,
    tenantId: Number.isFinite(tenantId as number) ? (tenantId as number) : null,
  };
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [loading, setLoading] = useState(true);
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    const supabase = getSupabase();
    let mounted = true;

    supabase.auth.getSession().then(({ data }) => {
      if (!mounted) return;
      setSession(data.session ?? null);
      setLoading(false);
    });

    const { data: sub } = supabase.auth.onAuthStateChange((_event, newSession) => {
      setSession(newSession);
    });

    return () => {
      mounted = false;
      sub.subscription.unsubscribe();
    };
  }, []);

  // Route guard: bounce unauthenticated users to /login (except on public paths).
  useEffect(() => {
    if (loading) return;
    if (!session && !PUBLIC_PATHS.some((p) => pathname === p || pathname.startsWith(`${p}/`))) {
      router.replace('/login');
    }
  }, [loading, session, pathname, router]);

  const signOut = useCallback(async () => {
    await getSupabase().auth.signOut();
    router.replace('/login');
  }, [router]);

  const user = useMemo(() => deriveAuthUser(session), [session]);

  const value = useMemo<AuthContextValue>(() => ({ session, user, loading, signOut }), [session, user, loading, signOut]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within an AuthProvider');
  return ctx;
}
