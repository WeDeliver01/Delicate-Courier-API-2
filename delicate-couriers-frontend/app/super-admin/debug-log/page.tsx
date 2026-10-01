'use client';

// SuperAdmin-only mirror of every WooCommerce plugin's debug log. Each row
// is one entry the plugin emitted on its WordPress site, fire-and-forget
// shipped to the platform via /api/webhooks/plugin/debug-log.
//
// Visual model intentionally mirrors the plugin's own "Debug log" panel so
// support staff already familiar with the WP admin view feel at home.

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, Trash2 } from 'lucide-react';
import api from '@/lib/api';
import { AppLayout } from '@/components/layout/app-layout';
import { useAuth } from '@/components/providers/auth-provider';
import { formatDateTime, isoTitle } from '@/lib/datetime';

interface DebugLogEntry {
  pluginDebugLogId: number;
  storeId: number;
  tenantId: number;
  occurredAt: string;
  receivedAt: string;
  level: string;
  context: string;
  source: string;
  detail: string;
  pluginVersion: string | null;
  data: unknown;
}

interface ListResponse {
  items: DebugLogEntry[];
  total: number;
  page: number;
  pageSize: number;
}

interface StoreOption {
  storeId: number;
  tenantId: number;
  storeName: string;
  entryCount: number;
}

const PAGE_SIZE = 100;

const LEVEL_STYLES: Record<string, string> = {
  info: 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300',
  warning: 'bg-amber-50 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300',
  error: 'bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300',
  debug: 'bg-gray-50 text-gray-700 dark:bg-gray-900/30 dark:text-gray-300',
};

const SOURCE_STYLES: Record<string, string> = {
  server: 'bg-purple-50 text-purple-700 dark:bg-purple-900/30 dark:text-purple-300',
  browser: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300',
  checkout: 'bg-orange-50 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300',
};

export default function DebugLogPage() {
  const router = useRouter();
  const { user, loading: authLoading } = useAuth();
  const isSuperAdmin = user?.role === 'SuperAdmin';

  const [entries, setEntries] = useState<DebugLogEntry[]>([]);
  const [stores, setStores] = useState<StoreOption[]>([]);
  const [contexts, setContexts] = useState<string[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [autoRefresh, setAutoRefresh] = useState(false);

  // Filter inputs
  const [storeFilter, setStoreFilter] = useState('');
  const [levelFilter, setLevelFilter] = useState('');
  const [sourceFilter, setSourceFilter] = useState('');
  const [contextFilter, setContextFilter] = useState('');
  const [searchFilter, setSearchFilter] = useState('');
  const [fromFilter, setFromFilter] = useState('');
  const [toFilter, setToFilter] = useState('');

  // Bounce non-SuperAdmin users away.
  useEffect(() => {
    if (!authLoading && user && !isSuperAdmin) {
      router.replace('/');
    }
  }, [authLoading, user, isSuperAdmin, router]);

  const loadEntries = useCallback(async () => {
    if (!isSuperAdmin) return;
    setLoading(true);
    setError(null);
    try {
      const params: Record<string, string | number> = { page, pageSize: PAGE_SIZE };
      if (storeFilter) params.storeId = Number.parseInt(storeFilter, 10);
      if (levelFilter) params.level = levelFilter;
      if (sourceFilter) params.source = sourceFilter;
      if (contextFilter.trim()) params.context = contextFilter.trim();
      if (searchFilter.trim()) params.search = searchFilter.trim();
      if (fromFilter) params.from = new Date(fromFilter).toISOString();
      if (toFilter) params.to = new Date(toFilter).toISOString();
      const res = await api.get<ListResponse>('/admin/plugin-debug-logs', { params });
      setEntries(res.data.items);
      setTotal(res.data.total);
    } catch (e: unknown) {
      const err = e as { response?: { status?: number; data?: { message?: string } }; message?: string };
      const status = err.response?.status;
      const msg = err.response?.data?.message ?? err.message ?? 'unknown error';
      setError(
        status
          ? `Failed to load debug log (HTTP ${status}): ${msg}. Click Refresh to retry.`
          : `Failed to load debug log: ${msg}. Click Refresh to retry.`,
      );
    } finally {
      setLoading(false);
    }
  }, [isSuperAdmin, page, storeFilter, levelFilter, sourceFilter, contextFilter, searchFilter, fromFilter, toFilter]);

  useEffect(() => { loadEntries(); }, [loadEntries]);

  // Stores + contexts dropdowns: load once on mount (they're cheap and the
  // values change slowly).
  useEffect(() => {
    if (!isSuperAdmin) return;
    api.get<StoreOption[]>('/admin/plugin-debug-logs/stores')
      .then((res) => setStores(res.data))
      .catch(() => { /* non-fatal */ });
    api.get<string[]>('/admin/plugin-debug-logs/contexts')
      .then((res) => setContexts(res.data))
      .catch(() => { /* non-fatal */ });
  }, [isSuperAdmin]);

  // Auto-refresh every 5s when enabled — mirrors the plugin's own panel.
  useEffect(() => {
    if (!autoRefresh) return;
    const id = window.setInterval(() => { loadEntries(); }, 5000);
    return () => window.clearInterval(id);
  }, [autoRefresh, loadEntries]);

  const totalPages = useMemo(() => Math.max(1, Math.ceil(total / PAGE_SIZE)), [total]);

  const toggle = (id: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  const applyFilters = (e: React.FormEvent) => {
    e.preventDefault();
    if (page !== 1) setPage(1); else loadEntries();
  };

  const resetFilters = () => {
    setStoreFilter('');
    setLevelFilter('');
    setSourceFilter('');
    setContextFilter('');
    setSearchFilter('');
    setFromFilter('');
    setToFilter('');
    setPage(1);
  };

  const clearStoreLogs = async () => {
    if (!storeFilter) return;
    const store = stores.find((s) => s.storeId === Number.parseInt(storeFilter, 10));
    const label = store ? `${store.storeName} (Store #${store.storeId})` : `Store #${storeFilter}`;
    if (!confirm(`Delete ALL debug log entries for ${label}? This cannot be undone.`)) return;
    try {
      await api.delete(`/admin/plugin-debug-logs/store/${storeFilter}`);
      await loadEntries();
    } catch (e) {
      const err = e as { message?: string };
      alert(`Clear failed: ${err.message ?? 'unknown error'}`);
    }
  };

  if (authLoading || !user) {
    return <AppLayout><div className="p-6">Loading…</div></AppLayout>;
  }
  if (!isSuperAdmin) {
    return <AppLayout><div className="p-6">Redirecting…</div></AppLayout>;
  }

  return (
    <AppLayout>
      <div className="p-6 space-y-4">
        <div className="flex items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold">Debug Log</h1>
            <p className="text-sm text-gray-500 dark:text-gray-400">
              Live mirror of every WooCommerce plugin&apos;s debug log, including browser-side checkout events.
              Capped at 10,000 entries per store.
            </p>
          </div>
          <div className="flex items-center gap-2">
            <label className="flex items-center gap-1.5 text-xs text-gray-600 dark:text-gray-300 cursor-pointer">
              <input
                type="checkbox"
                checked={autoRefresh}
                onChange={(e) => setAutoRefresh(e.target.checked)}
                className="h-3.5 w-3.5"
              />
              Auto-refresh every 5s
            </label>
            <button
              type="button"
              onClick={() => loadEntries()}
              disabled={loading}
              className="inline-flex items-center gap-2 px-3 py-2 text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50"
            >
              <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
              Refresh
            </button>
            {storeFilter && (
              <button
                type="button"
                onClick={clearStoreLogs}
                className="inline-flex items-center gap-2 px-3 py-2 text-sm rounded border border-red-300 dark:border-red-800 bg-white dark:bg-gray-900 text-red-700 dark:text-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
                title="Delete all log entries for the selected store"
              >
                <Trash2 className="h-4 w-4" />
                Clear store
              </button>
            )}
          </div>
        </div>

        <form
          onSubmit={applyFilters}
          className="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-md p-4 grid grid-cols-1 md:grid-cols-3 lg:grid-cols-4 gap-3"
        >
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Store</label>
            <select
              value={storeFilter}
              onChange={(e) => setStoreFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            >
              <option value="">All stores</option>
              {stores.map((s) => (
                <option key={s.storeId} value={s.storeId}>
                  {s.storeName} (#{s.storeId}) · {s.entryCount.toLocaleString()} entries
                </option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Level</label>
            <select
              value={levelFilter}
              onChange={(e) => setLevelFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            >
              <option value="">All</option>
              <option value="info">info</option>
              <option value="warning">warning</option>
              <option value="error">error</option>
              <option value="debug">debug</option>
            </select>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Source</label>
            <select
              value={sourceFilter}
              onChange={(e) => setSourceFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            >
              <option value="">All</option>
              <option value="server">server</option>
              <option value="browser">browser</option>
              <option value="checkout">checkout</option>
            </select>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Context contains</label>
            <input
              type="text"
              value={contextFilter}
              onChange={(e) => setContextFilter(e.target.value)}
              placeholder="e.g. platform.order"
              list="dcp-debug-contexts"
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            />
            <datalist id="dcp-debug-contexts">
              {contexts.map((c) => <option key={c} value={c} />)}
            </datalist>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">From</label>
            <input
              type="datetime-local"
              value={fromFilter}
              onChange={(e) => setFromFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">To</label>
            <input
              type="datetime-local"
              value={toFilter}
              onChange={(e) => setToFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            />
          </div>
          <div className="lg:col-span-2">
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Search detail / data</label>
            <input
              type="text"
              value={searchFilter}
              onChange={(e) => setSearchFilter(e.target.value)}
              placeholder="Free text…"
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            />
          </div>
          <div className="md:col-span-3 lg:col-span-4 flex gap-2 justify-end">
            <button
              type="button"
              onClick={resetFilters}
              className="px-3 py-1.5 text-sm rounded border border-gray-300 dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-800"
            >
              Reset
            </button>
            <button
              type="submit"
              className="px-3 py-1.5 text-sm rounded bg-blue-600 text-white hover:bg-blue-700"
            >
              Apply filters
            </button>
          </div>
        </form>

        {error && (
          <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 text-red-700 dark:text-red-300 rounded p-3 text-sm">
            {error}
          </div>
        )}

        <div className="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-md overflow-hidden">
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-800">
              <thead className="bg-gray-50 dark:bg-gray-800/50">
                <tr>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Time</th>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Lvl</th>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Src</th>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Store</th>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Context</th>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Detail</th>
                  <th className="px-3 py-2"></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 dark:divide-gray-800 bg-white dark:bg-gray-900">
                {loading && (
                  <tr><td colSpan={7} className="px-4 py-6 text-center text-sm text-gray-500">Loading…</td></tr>
                )}
                {!loading && entries.length === 0 && (
                  <tr><td colSpan={7} className="px-4 py-6 text-center text-sm text-gray-500">
                    No entries match the current filters. If you just enabled debug logging in a merchant&apos;s WooCommerce plugin, entries will start streaming in here within seconds of the next plugin event.
                  </td></tr>
                )}
                {!loading && entries.map((e) => {
                  const isOpen = expanded.has(e.pluginDebugLogId);
                  const store = stores.find((s) => s.storeId === e.storeId);
                  const lvlCls = LEVEL_STYLES[e.level] ?? LEVEL_STYLES.info;
                  const srcCls = SOURCE_STYLES[e.source] ?? SOURCE_STYLES.server;
                  return (
                    <React.Fragment key={e.pluginDebugLogId}>
                      <tr
                        className="hover:bg-gray-50 dark:hover:bg-gray-800/50 cursor-pointer"
                        onClick={() => toggle(e.pluginDebugLogId)}
                      >
                        <td className="px-3 py-1.5 text-xs whitespace-nowrap font-mono" title={isoTitle(e.occurredAt)}>
                          {formatDateTime(e.occurredAt)}
                        </td>
                        <td className="px-3 py-1.5 text-xs whitespace-nowrap">
                          <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium ${lvlCls}`}>
                            {e.level}
                          </span>
                        </td>
                        <td className="px-3 py-1.5 text-xs whitespace-nowrap">
                          <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium ${srcCls}`}>
                            {e.source}
                          </span>
                        </td>
                        <td className="px-3 py-1.5 text-xs whitespace-nowrap text-gray-600 dark:text-gray-400">
                          {store ? store.storeName : `#${e.storeId}`}
                        </td>
                        <td className="px-3 py-1.5 text-xs whitespace-nowrap font-mono text-gray-700 dark:text-gray-300">
                          {e.context}
                        </td>
                        <td className="px-3 py-1.5 text-xs max-w-2xl">
                          <code className="text-xs whitespace-pre-wrap break-all">{e.detail}</code>
                        </td>
                        <td className="px-3 py-1.5 text-right">
                          {e.data != null && (
                            <span className="text-xs text-blue-600 dark:text-blue-400">
                              {isOpen ? '▾' : '▸'}
                            </span>
                          )}
                        </td>
                      </tr>
                      {isOpen && e.data != null && (
                        <tr className="bg-gray-50 dark:bg-gray-800/30">
                          <td colSpan={7} className="px-4 py-3">
                            <DetailBlock data={e.data} pluginVersion={e.pluginVersion} />
                          </td>
                        </tr>
                      )}
                    </React.Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>

          <div className="flex items-center justify-between px-4 py-2 border-t border-gray-200 dark:border-gray-800 text-sm">
            <span className="text-gray-500">
              {total.toLocaleString()} entr{total === 1 ? 'y' : 'ies'} · page {page} of {totalPages}
            </span>
            <div className="flex gap-2">
              <button
                disabled={page <= 1 || loading}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                className="px-3 py-1 rounded border border-gray-300 dark:border-gray-700 disabled:opacity-50"
              >
                Previous
              </button>
              <button
                disabled={page >= totalPages || loading}
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                className="px-3 py-1 rounded border border-gray-300 dark:border-gray-700 disabled:opacity-50"
              >
                Next
              </button>
            </div>
          </div>
        </div>
      </div>
    </AppLayout>
  );
}

/**
 * Renders the structured `data` payload (HTTP request/response or browser
 * event). Mirrors the plugin admin panel's detail block: a small meta line,
 * then request headers / body / response headers / body sections when present.
 */
function DetailBlock({ data, pluginVersion }: { data: unknown; pluginVersion: string | null }) {
  const d = (data ?? {}) as Record<string, unknown>;
  const meta: string[] = [];
  if (d.method) meta.push(String(d.method));
  if (d.url) meta.push(String(d.url));
  if (d.response_status != null) meta.push(`HTTP ${d.response_status}`);
  if (d.duration_ms != null) meta.push(`${d.duration_ms} ms`);
  if (pluginVersion) meta.push(`plugin ${pluginVersion}`);

  const sections: Array<{ title: string; body: unknown }> = [];
  if (d.error) sections.push({ title: 'Transport error', body: d.error });
  if (d.request_headers) sections.push({ title: 'Request headers', body: d.request_headers });
  if (d.request_body != null && d.request_body !== '') sections.push({ title: 'Request body', body: d.request_body });
  if (d.response_headers) sections.push({ title: 'Response headers', body: d.response_headers });
  if (d.response_body != null && d.response_body !== '') sections.push({ title: 'Response body', body: d.response_body });

  // Fallback: dump the whole object if none of the well-known fields match.
  const isStructuredHttp = sections.length > 0;

  return (
    <div className="space-y-2 text-xs">
      {meta.length > 0 && (
        <div className="text-gray-600 dark:text-gray-400 font-mono">{meta.join('  ·  ')}</div>
      )}
      {isStructuredHttp ? (
        sections.map((s) => (
          <div key={s.title}>
            <div className="text-[10px] font-semibold uppercase text-gray-500 dark:text-gray-400 tracking-wide mt-2 mb-0.5">
              {s.title}
            </div>
            <pre className="bg-white dark:bg-gray-950 border border-gray-200 dark:border-gray-800 rounded p-2 overflow-x-auto whitespace-pre-wrap break-all">
              {typeof s.body === 'string' ? s.body : JSON.stringify(s.body, null, 2)}
            </pre>
          </div>
        ))
      ) : (
        <pre className="bg-white dark:bg-gray-950 border border-gray-200 dark:border-gray-800 rounded p-2 overflow-x-auto whitespace-pre-wrap break-all">
          {JSON.stringify(d, null, 2)}
        </pre>
      )}
    </div>
  );
}
