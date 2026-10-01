'use client';

// SuperAdmin-only audit log of every meaningful platform activity. The
// backend gates this at the controller with [Authorize(Roles="SuperAdmin")],
// so this page is purely a UI for that data — but we also hide the page
// behind a client-side role check so non-SuperAdmin users don't see an
// awkward 403 if they hit the URL directly.

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { RefreshCw } from 'lucide-react';
import api from '@/lib/api';
import { AppLayout } from '@/components/layout/app-layout';
import { useAuth } from '@/components/providers/auth-provider';
import { formatDateTime, isoTitle } from '@/lib/datetime';

interface SystemEvent {
  systemEventId: number;
  occurredAt: string;
  eventType: string;
  tenantId: number | null;
  actorUserId: number | null;
  actorKind: string;
  actorLabel: string | null;
  entityType: string | null;
  entityRef: string | null;
  message: string;
  details: unknown;
}

interface ListResponse {
  items: SystemEvent[];
  total: number;
  page: number;
  pageSize: number;
}

const PAGE_SIZE = 50;

export default function SystemEventsPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { user, loading: authLoading } = useAuth();
  const isSuperAdmin = user?.role === 'SuperAdmin';

  const [events, setEvents] = useState<SystemEvent[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [eventTypes, setEventTypes] = useState<string[]>([]);

  // Filter inputs (uncommitted) vs applied filters — keeps the table from
  // re-querying on every keystroke. Seed from URL query params so a link
  // like /system-events?eventType=tracking.event_ingested arrives pre-filtered.
  const [eventTypeFilter, setEventTypeFilter] = useState(() => searchParams?.get('eventType') ?? '');
  const [actorKindFilter, setActorKindFilter] = useState(() => searchParams?.get('actorKind') ?? '');
  const [tenantIdFilter, setTenantIdFilter] = useState(() => searchParams?.get('tenantId') ?? '');
  const [fromFilter, setFromFilter] = useState(() => searchParams?.get('from') ?? '');
  const [toFilter, setToFilter] = useState(() => searchParams?.get('to') ?? '');
  const [searchFilter, setSearchFilter] = useState(() => searchParams?.get('search') ?? '');

  // Bounce non-SuperAdmin users to the dashboard rather than letting them
  // sit on a page they cannot use.
  useEffect(() => {
    if (!authLoading && user && !isSuperAdmin) {
      router.replace('/');
    }
  }, [authLoading, user, isSuperAdmin, router]);

  const loadEvents = useCallback(async () => {
    if (!isSuperAdmin) return;
    setLoading(true);
    setError(null);
    try {
      const params: Record<string, string | number> = {
        page,
        pageSize: PAGE_SIZE,
      };
      if (eventTypeFilter) params.eventType = eventTypeFilter;
      if (actorKindFilter) params.actorKind = actorKindFilter;
      if (tenantIdFilter) params.tenantId = Number.parseInt(tenantIdFilter, 10);
      if (fromFilter) params.from = new Date(fromFilter).toISOString();
      if (toFilter) params.to = new Date(toFilter).toISOString();
      if (searchFilter.trim()) params.search = searchFilter.trim();

      const res = await api.get<ListResponse>('/admin/system-events', { params });
      setEvents(res.data.items);
      setTotal(res.data.total);
    } catch (e: unknown) {
      const err = e as {
        response?: { status?: number; data?: { message?: string; title?: string; detail?: string } };
        message?: string;
      };
      const status = err.response?.status;
      const serverMsg =
        err.response?.data?.message ?? err.response?.data?.detail ?? err.response?.data?.title;
      const detail = serverMsg ?? err.message ?? 'unknown error';
      setError(
        status
          ? `Failed to load system events (HTTP ${status}): ${detail}. Click Refresh to retry — this is often a transient database connection limit.`
          : `Failed to load system events: ${detail}. Click Refresh to retry.`,
      );
    } finally {
      setLoading(false);
    }
  }, [isSuperAdmin, page, eventTypeFilter, actorKindFilter, tenantIdFilter, fromFilter, toFilter, searchFilter]);

  useEffect(() => {
    loadEvents();
  }, [loadEvents]);

  // Pull the catalogue once so the dropdown reflects whatever the system
  // has actually emitted, rather than a hard-coded list that goes stale.
  useEffect(() => {
    if (!isSuperAdmin) return;
    api.get<string[]>('/admin/system-events/event-types')
      .then((res) => setEventTypes(res.data))
      .catch(() => { /* non-fatal */ });
  }, [isSuperAdmin]);

  const totalPages = useMemo(() => Math.max(1, Math.ceil(total / PAGE_SIZE)), [total]);

  const toggleExpanded = (id: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  const applyFilters = (e: React.FormEvent) => {
    e.preventDefault();
    // Setting page to 1 triggers loadEvents via the effect; no need to
    // call it again here (would otherwise cause a stale-page double fetch).
    if (page !== 1) setPage(1);
    else loadEvents();
  };

  const resetFilters = () => {
    setEventTypeFilter('');
    setActorKindFilter('');
    setTenantIdFilter('');
    setFromFilter('');
    setToFilter('');
    setSearchFilter('');
    setPage(1);
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
            <h1 className="text-2xl font-semibold">System Events</h1>
            <p className="text-sm text-gray-500 dark:text-gray-400">
              Audit log of every webhook, shipment, user and job activity across all tenants.
            </p>
          </div>
          <button
            type="button"
            onClick={() => loadEvents()}
            disabled={loading}
            className="inline-flex items-center gap-2 px-3 py-2 text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50"
            title="Reload the current page of events"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Refresh
          </button>
        </div>

        <form
          onSubmit={applyFilters}
          className="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-md p-4 grid grid-cols-1 md:grid-cols-3 lg:grid-cols-6 gap-3"
        >
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
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Event type</label>
            <select
              value={eventTypeFilter}
              onChange={(e) => setEventTypeFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            >
              <option value="">All</option>
              {eventTypes.map((t) => (
                <option key={t} value={t}>{t}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Actor kind</label>
            <select
              value={actorKindFilter}
              onChange={(e) => setActorKindFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
            >
              <option value="">All</option>
              <option value="User">User</option>
              <option value="System">System</option>
              <option value="Webhook">Webhook</option>
              <option value="Job">Job</option>
            </select>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Tenant ID</label>
            <input
              type="number"
              value={tenantIdFilter}
              onChange={(e) => setTenantIdFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
              placeholder="Any"
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 dark:text-gray-300 mb-1">Search</label>
            <input
              type="text"
              value={searchFilter}
              onChange={(e) => setSearchFilter(e.target.value)}
              className="w-full text-sm rounded border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-2 py-1"
              placeholder="Message, entity, actor…"
            />
          </div>
          <div className="md:col-span-3 lg:col-span-6 flex gap-2 justify-end">
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
                  <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">When</th>
                  <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Event</th>
                  <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Tenant</th>
                  <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Actor</th>
                  <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Entity</th>
                  <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Message</th>
                  <th className="px-4 py-2"></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 dark:divide-gray-800 bg-white dark:bg-gray-900">
                {loading && (
                  <tr><td colSpan={7} className="px-4 py-6 text-center text-sm text-gray-500">Loading…</td></tr>
                )}
                {!loading && events.length === 0 && (
                  <tr><td colSpan={7} className="px-4 py-6 text-center text-sm text-gray-500">No events match the current filters.</td></tr>
                )}
                {!loading && events.map((ev) => {
                  const isOpen = expanded.has(ev.systemEventId);
                  return (
                    <React.Fragment key={ev.systemEventId}>
                      <tr className="hover:bg-gray-50 dark:hover:bg-gray-800/50">
                        <td className="px-4 py-2 text-sm whitespace-nowrap" title={isoTitle(ev.occurredAt)}>
                          {formatDateTime(ev.occurredAt)}
                        </td>
                        <td className="px-4 py-2 text-sm whitespace-nowrap">
                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-300">
                            {ev.eventType}
                          </span>
                        </td>
                        <td className="px-4 py-2 text-sm">{ev.tenantId ?? '—'}</td>
                        <td className="px-4 py-2 text-sm whitespace-nowrap">
                          <span className="text-xs text-gray-500">{ev.actorKind}</span>
                          {ev.actorLabel && <div className="text-sm">{ev.actorLabel}</div>}
                        </td>
                        <td className="px-4 py-2 text-sm whitespace-nowrap">
                          {ev.entityType ? <div className="text-xs text-gray-500">{ev.entityType}</div> : null}
                          {ev.entityRef ?? '—'}
                        </td>
                        <td className="px-4 py-2 text-sm max-w-xl">
                          <div className="truncate" title={ev.message}>{ev.message}</div>
                        </td>
                        <td className="px-4 py-2 text-right">
                          {ev.details != null && (
                            <button
                              onClick={() => toggleExpanded(ev.systemEventId)}
                              className="text-xs text-blue-600 dark:text-blue-400 hover:underline"
                            >
                              {isOpen ? 'Hide' : 'Details'}
                            </button>
                          )}
                        </td>
                      </tr>
                      {isOpen && (
                        <tr className="bg-gray-50 dark:bg-gray-800/30">
                          <td colSpan={7} className="px-4 py-3">
                            <pre className="text-xs overflow-x-auto whitespace-pre-wrap">
                              {JSON.stringify(ev.details, null, 2)}
                            </pre>
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
              {total.toLocaleString()} event{total === 1 ? '' : 's'} · page {page} of {totalPages}
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
