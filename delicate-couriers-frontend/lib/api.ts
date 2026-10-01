import axios from 'axios';
import { getSupabase } from './supabase';

const api = axios.create({
  baseURL: process.env.NEXT_PUBLIC_API_URL ?? '/api',
  headers: { 'Content-Type': 'application/json' },
});

// Attach the current Supabase access token to every request. We pull it
// from supabase-js (rather than localStorage directly) so we always get the
// freshest one — supabase-js silently rotates the token in the background.
api.interceptors.request.use(async (config) => {
  if (typeof window !== 'undefined') {
    try {
      const { data } = await getSupabase().auth.getSession();
      const token = data.session?.access_token;
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
    } catch {
      // If the session lookup fails, fall through and let the backend 401.
    }
  }
  return config;
});

// On 401, sign out of Supabase and bounce to /login. The provider's onAuthStateChange
// will also notice the SIGNED_OUT and update React state.
api.interceptors.response.use(
  (response) => response,
  async (error) => {
    if (typeof window !== 'undefined' && error.response?.status === 401) {
      try {
        await getSupabase().auth.signOut();
      } catch {}
      if (!window.location.pathname.startsWith('/login')) {
        window.location.href = '/login?expired=true';
      }
    }
    return Promise.reject(error);
  }
);

export default api;
