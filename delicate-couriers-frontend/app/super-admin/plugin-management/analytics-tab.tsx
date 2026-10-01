'use client'

import React, { useCallback, useEffect, useMemo, useState } from 'react'
import { RefreshCw, Download, HeartPulse, AlertTriangle, TrendingUp } from 'lucide-react'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { formatDateTime, isoTitle } from '@/lib/datetime'
import { AuditLog, Installation, TelemetryEvent, compareVersions, heartbeatHealth } from './types'

function Bar({ value, max, color }: { value: number; max: number; color: string }) {
  const pct = max > 0 ? Math.max(2, Math.round((value / max) * 100)) : 0
  return (
    <div className="flex-1 h-4 bg-gray-100 dark:bg-gray-800 rounded overflow-hidden">
      <div className={`h-full ${color}`} style={{ width: `${pct}%` }} />
    </div>
  )
}

export function AnalyticsTab({ pluginId, pluginSlug }: { pluginId: number; pluginSlug: string }) {
  const [installs, setInstalls] = useState<Installation[]>([])
  const [telemetry, setTelemetry] = useState<TelemetryEvent[]>([])
  const [audit, setAudit] = useState<AuditLog[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [auditActionFilter, setAuditActionFilter] = useState('')
  const [expandedAudit, setExpandedAudit] = useState<Set<number>>(new Set())

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const [installsRes, telemetryRes, auditRes] = await Promise.all([
        api.get<Installation[]>('/admin/plugin-platform/installations'),
        api.get<TelemetryEvent[]>('/admin/plugin-platform/telemetry', { params: { limit: 500, pluginId } }),
        api.get<AuditLog[]>('/admin/plugin-platform/audit-logs', {
          params: { limit: 500, pluginId, ...(auditActionFilter ? { action: auditActionFilter } : {}) },
        }),
      ])
      setInstalls(installsRes.data.filter((i) => i.pluginSlug === pluginSlug))
      setTelemetry(telemetryRes.data)
      setAudit(auditRes.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load analytics')
    } finally {
      setLoading(false)
    }
  }, [pluginId, pluginSlug, auditActionFilter])

  useEffect(() => { load() }, [load])

  // Version adoption across the fleet.
  const versionDistribution = useMemo(() => {
    const counts = new Map<string, number>()
    for (const i of installs) {
      const v = i.pluginVersion ?? 'unknown'
      counts.set(v, (counts.get(v) ?? 0) + 1)
    }
    return [...counts.entries()].sort((a, b) =>
      a[0] === 'unknown' ? 1 : b[0] === 'unknown' ? -1 : compareVersions(b[0], a[0]),
    )
  }, [installs])

  // Heartbeat health buckets.
  const health = useMemo(() => {
    const buckets = { healthy: 0, stale: 0, silent: 0 }
    for (const i of installs) buckets[heartbeatHealth(i.lastHeartbeatOn)]++
    return buckets
  }, [installs])

  // Download counts from the audit trail (download.served actions).
  const downloadsByDay = useMemo(() => {
    const perDay = new Map<string, number>()
    for (const a of audit) {
      if (a.action !== 'download.served') continue
      const day = a.createdOn.slice(0, 10)
      perDay.set(day, (perDay.get(day) ?? 0) + 1)
    }
    return [...perDay.entries()].sort((a, b) => a[0].localeCompare(b[0])).slice(-14)
  }, [audit])

  // Telemetry event-type breakdown + error rate.
  const telemetryByType = useMemo(() => {
    const counts = new Map<string, number>()
    for (const e of telemetry) counts.set(e.eventType, (counts.get(e.eventType) ?? 0) + 1)
    return [...counts.entries()].sort((a, b) => b[1] - a[1])
  }, [telemetry])

  const errorEventCount = useMemo(
    () => telemetry.filter((e) => e.eventType.toLowerCase().includes('error')).length,
    [telemetry],
  )
  const errorRate = telemetry.length > 0 ? Math.round((errorEventCount / telemetry.length) * 100) : 0
  const sitesWithErrors = useMemo(
    () => installs.filter((i) => {
      if (!i.recentErrorsJson) return false
      try {
        const parsed = JSON.parse(i.recentErrorsJson)
        return Array.isArray(parsed) ? parsed.length > 0 : true
      } catch { return true }
    }).length,
    [installs],
  )

  const totalDownloads = useMemo(
    () => audit.filter((a) => a.action === 'download.served').length,
    [audit],
  )

  const auditActions = useMemo(() => [...new Set(audit.map((a) => a.action))].sort(), [audit])

  const maxVersionCount = Math.max(1, ...versionDistribution.map(([, c]) => c))
  const maxDayDownloads = Math.max(1, ...downloadsByDay.map(([, c]) => c))
  const maxTypeCount = Math.max(1, ...telemetryByType.map(([, c]) => c))

  const toggleAudit = (id: number) => {
    setExpandedAudit((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600 dark:text-gray-400">
          Fleet analytics from heartbeats, telemetry and the audit trail (most recent 500 telemetry
          events / 500 audit entries, scoped to this plugin).
        </p>
        <Button variant="outline" size="sm" onClick={load} disabled={loading}>
          <RefreshCw className={`h-4 w-4 mr-2 ${loading ? 'animate-spin' : ''}`} />
          Refresh
        </Button>
      </div>

      {error && (
        <div className="p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-600 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400">
          {error}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 py-8 text-center">Loading analytics…</p>
      ) : (
        <>
          {/* Summary cards */}
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
            <Card>
              <CardContent className="pt-5 pb-4">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs text-gray-500 dark:text-gray-400">Installations</p>
                    <p className="text-2xl font-bold dark:text-white">{installs.length}</p>
                  </div>
                  <TrendingUp className="h-8 w-8 text-blue-500 opacity-50" />
                </div>
              </CardContent>
            </Card>
            <Card>
              <CardContent className="pt-5 pb-4">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs text-gray-500 dark:text-gray-400">Heartbeat healthy</p>
                    <p className="text-2xl font-bold dark:text-white">
                      {health.healthy}<span className="text-sm text-gray-400 font-normal"> / {installs.length}</span>
                    </p>
                  </div>
                  <HeartPulse className="h-8 w-8 text-green-500 opacity-50" />
                </div>
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  {health.stale} stale · {health.silent} silent
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardContent className="pt-5 pb-4">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs text-gray-500 dark:text-gray-400">Downloads served</p>
                    <p className="text-2xl font-bold dark:text-white">{totalDownloads}</p>
                  </div>
                  <Download className="h-8 w-8 text-indigo-500 opacity-50" />
                </div>
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">in recent audit window</p>
              </CardContent>
            </Card>
            <Card>
              <CardContent className="pt-5 pb-4">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs text-gray-500 dark:text-gray-400">Error rate</p>
                    <p className="text-2xl font-bold dark:text-white">{errorRate}%</p>
                  </div>
                  <AlertTriangle className={`h-8 w-8 opacity-50 ${errorRate > 10 ? 'text-red-500' : 'text-amber-500'}`} />
                </div>
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  {errorEventCount} error events · {sitesWithErrors} site{sitesWithErrors === 1 ? '' : 's'} reporting errors
                </p>
              </CardContent>
            </Card>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            {/* Version adoption */}
            <Card>
              <CardHeader>
                <CardTitle className="text-base">Version adoption</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2">
                {versionDistribution.length === 0 ? (
                  <p className="text-sm text-gray-500">No installations yet.</p>
                ) : (
                  versionDistribution.map(([version, count]) => (
                    <div key={version} className="flex items-center gap-3 text-sm">
                      <span className="w-20 font-mono text-xs dark:text-gray-300">{version === 'unknown' ? 'unknown' : `v${version}`}</span>
                      <Bar value={count} max={maxVersionCount} color="bg-blue-600" />
                      <span className="w-8 text-right text-xs text-gray-500">{count}</span>
                    </div>
                  ))
                )}
              </CardContent>
            </Card>

            {/* Downloads per day */}
            <Card>
              <CardHeader>
                <CardTitle className="text-base">Update downloads (last 14 active days)</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2">
                {downloadsByDay.length === 0 ? (
                  <p className="text-sm text-gray-500">No downloads recorded in the recent audit window.</p>
                ) : (
                  downloadsByDay.map(([day, count]) => (
                    <div key={day} className="flex items-center gap-3 text-sm">
                      <span className="w-24 font-mono text-xs dark:text-gray-300">{day}</span>
                      <Bar value={count} max={maxDayDownloads} color="bg-indigo-600" />
                      <span className="w-8 text-right text-xs text-gray-500">{count}</span>
                    </div>
                  ))
                )}
              </CardContent>
            </Card>
          </div>

          {/* Telemetry breakdown */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Telemetry events by type (recent {telemetry.length})</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2">
              {telemetryByType.length === 0 ? (
                <p className="text-sm text-gray-500">No telemetry received yet.</p>
              ) : (
                telemetryByType.map(([type, count]) => (
                  <div key={type} className="flex items-center gap-3 text-sm">
                    <span className="w-48 truncate font-mono text-xs dark:text-gray-300" title={type}>{type}</span>
                    <Bar
                      value={count}
                      max={maxTypeCount}
                      color={type.toLowerCase().includes('error') ? 'bg-red-500' : 'bg-emerald-600'}
                    />
                    <span className="w-8 text-right text-xs text-gray-500">{count}</span>
                  </div>
                ))
              )}
            </CardContent>
          </Card>

          {/* Audit log */}
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between gap-3">
                <CardTitle className="text-base">Audit log</CardTitle>
                <select
                  value={auditActionFilter}
                  onChange={(e) => setAuditActionFilter(e.target.value)}
                  className="text-xs rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-2 py-1.5"
                >
                  <option value="">All actions</option>
                  {auditActions.map((a) => <option key={a} value={a}>{a}</option>)}
                </select>
              </div>
            </CardHeader>
            <CardContent>
              {audit.length === 0 ? (
                <p className="text-sm text-gray-500">No audit entries.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="min-w-full text-sm">
                    <thead className="border-b border-gray-200 dark:border-gray-800">
                      <tr>
                        <th className="px-2 py-1.5 text-left text-xs font-medium text-gray-500 uppercase">Time</th>
                        <th className="px-2 py-1.5 text-left text-xs font-medium text-gray-500 uppercase">Action</th>
                        <th className="px-2 py-1.5 text-left text-xs font-medium text-gray-500 uppercase">Actor</th>
                        <th className="px-2 py-1.5 text-left text-xs font-medium text-gray-500 uppercase">Entity</th>
                        <th className="px-2 py-1.5"></th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-100 dark:divide-gray-800">
                      {audit.map((a) => {
                        const isOpen = expandedAudit.has(a.auditLogId)
                        return (
                          <React.Fragment key={a.auditLogId}>
                            <tr
                              className="hover:bg-gray-50 dark:hover:bg-gray-800/50 cursor-pointer"
                              onClick={() => toggleAudit(a.auditLogId)}
                            >
                              <td className="px-2 py-1.5 text-xs whitespace-nowrap font-mono text-gray-600 dark:text-gray-400" title={isoTitle(a.createdOn)}>
                                {formatDateTime(a.createdOn)}
                              </td>
                              <td className="px-2 py-1.5 text-xs whitespace-nowrap">
                                <code className="dark:text-gray-300">{a.action}</code>
                              </td>
                              <td className="px-2 py-1.5 text-xs whitespace-nowrap text-gray-600 dark:text-gray-400">{a.actor}</td>
                              <td className="px-2 py-1.5 text-xs text-gray-600 dark:text-gray-400">
                                {a.entityType && <span>{a.entityType} · </span>}{a.entityRef}
                              </td>
                              <td className="px-2 py-1.5 text-right text-xs text-blue-600 dark:text-blue-400">
                                {a.detailsJson ? (isOpen ? '▾' : '▸') : ''}
                              </td>
                            </tr>
                            {isOpen && a.detailsJson && (
                              <tr className="bg-gray-50 dark:bg-gray-800/30">
                                <td colSpan={5} className="px-3 py-2">
                                  <pre className="text-xs whitespace-pre-wrap break-all bg-white dark:bg-gray-950 border border-gray-200 dark:border-gray-800 rounded p-2">
                                    {(() => { try { return JSON.stringify(JSON.parse(a.detailsJson!), null, 2) } catch { return a.detailsJson } })()}
                                  </pre>
                                  {a.ipAddress && <p className="text-[10px] text-gray-400 mt-1">IP: {a.ipAddress}</p>}
                                </td>
                              </tr>
                            )}
                          </React.Fragment>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </CardContent>
          </Card>
        </>
      )}
    </div>
  )
}
