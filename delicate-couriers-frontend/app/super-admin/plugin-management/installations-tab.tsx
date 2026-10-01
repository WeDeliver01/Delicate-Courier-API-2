'use client'

import React, { useCallback, useEffect, useMemo, useState } from 'react'
import { RefreshCw, AlertCircle, ChevronDown, ChevronRight } from 'lucide-react'
import api from '@/lib/api'
import { Card, CardContent } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatDateTime, isoTitle } from '@/lib/datetime'
import { Installation, PluginRelease, compareVersions, heartbeatHealth } from './types'

const HEALTH_BADGES: Record<string, { label: string; cls: string }> = {
  healthy: { label: 'Healthy', cls: 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300' },
  stale: { label: 'Stale (>24h)', cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-300' },
  silent: { label: 'Silent (>72h)', cls: 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300' },
}

export function InstallationsTab({ pluginId, pluginSlug }: { pluginId: number; pluginSlug: string }) {
  const [installs, setInstalls] = useState<Installation[]>([])
  const [releases, setReleases] = useState<PluginRelease[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [search, setSearch] = useState('')
  const [onlyOutdated, setOnlyOutdated] = useState(false)
  const [onlyUnhealthy, setOnlyUnhealthy] = useState(false)
  const [expanded, setExpanded] = useState<Set<number>>(new Set())

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const [installsRes, releasesRes] = await Promise.all([
        api.get<Installation[]>('/admin/plugin-platform/installations'),
        api.get<PluginRelease[]>(`/admin/plugin-platform/plugins/${pluginId}/releases`),
      ])
      setInstalls(installsRes.data.filter((i) => i.pluginSlug === pluginSlug))
      setReleases(releasesRes.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load installations')
    } finally {
      setLoading(false)
    }
  }, [pluginId, pluginSlug])

  useEffect(() => { load() }, [load])

  // Latest published version = the reference point for "outdated".
  const latestVersion = useMemo(() => {
    const published = releases.filter((r) => r.status === 'published')
    if (published.length === 0) return null
    return published.reduce((best, r) => (compareVersions(r.version, best) > 0 ? r.version : best), published[0].version)
  }, [releases])

  // Min compatibility of the latest published release, for surfacing
  // sites that couldn't take the update even if offered.
  const latestRelease = useMemo(
    () => releases.find((r) => r.status === 'published' && r.version === latestVersion) ?? null,
    [releases, latestVersion],
  )

  const isOutdated = (i: Installation) =>
    latestVersion != null && i.pluginVersion != null && compareVersions(i.pluginVersion, latestVersion) < 0

  const compatIssues = (i: Installation): string[] => {
    if (!latestRelease) return []
    const issues: string[] = []
    if (latestRelease.minWpVersion && i.wpVersion && compareVersions(i.wpVersion, latestRelease.minWpVersion) < 0)
      issues.push(`WP ${i.wpVersion} < required ${latestRelease.minWpVersion}`)
    if (latestRelease.minWcVersion && i.wcVersion && compareVersions(i.wcVersion, latestRelease.minWcVersion) < 0)
      issues.push(`WC ${i.wcVersion} < required ${latestRelease.minWcVersion}`)
    if (latestRelease.minPhpVersion && i.phpVersion && compareVersions(i.phpVersion, latestRelease.minPhpVersion) < 0)
      issues.push(`PHP ${i.phpVersion} < required ${latestRelease.minPhpVersion}`)
    return issues
  }

  const filtered = installs.filter((i) => {
    if (onlyOutdated && !isOutdated(i)) return false
    if (onlyUnhealthy && heartbeatHealth(i.lastHeartbeatOn) === 'healthy') return false
    if (!search.trim()) return true
    const q = search.trim().toLowerCase()
    return (
      i.domain.toLowerCase().includes(q) ||
      i.license.licenseKey.toLowerCase().includes(q) ||
      (i.pluginVersion ?? '').toLowerCase().includes(q)
    )
  })

  const toggle = (id: number) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  const parseErrors = (json: string | null): unknown[] => {
    if (!json) return []
    try {
      const parsed = JSON.parse(json)
      return Array.isArray(parsed) ? parsed : [parsed]
    } catch {
      return [json]
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <Input
          placeholder="Search by domain, license key or version…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="max-w-sm"
        />
        <label className="flex items-center gap-1.5 text-sm cursor-pointer dark:text-gray-300">
          <input type="checkbox" checked={onlyOutdated} onChange={(e) => setOnlyOutdated(e.target.checked)} />
          Outdated only
        </label>
        <label className="flex items-center gap-1.5 text-sm cursor-pointer dark:text-gray-300">
          <input type="checkbox" checked={onlyUnhealthy} onChange={(e) => setOnlyUnhealthy(e.target.checked)} />
          Heartbeat issues only
        </label>
        <span className="ml-auto text-xs text-gray-500 dark:text-gray-400">
          {latestVersion ? `Latest published: v${latestVersion}` : 'No published release'}
        </span>
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
        <p className="text-sm text-gray-500 py-8 text-center">Loading installations…</p>
      ) : filtered.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            {installs.length === 0
              ? 'No installations yet. Sites appear here after they activate a license.'
              : 'No installations match the current filters.'}
          </CardContent>
        </Card>
      ) : (
        <div className="overflow-x-auto bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-md">
          <table className="min-w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-800/50 border-b border-gray-200 dark:border-gray-800">
              <tr>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Domain</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Plugin</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">WP / WC / PHP</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Heartbeat</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Latency</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">License</th>
                <th className="px-3 py-2"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-800">
              {filtered.map((i) => {
                const health = heartbeatHealth(i.lastHeartbeatOn)
                const badge = HEALTH_BADGES[health]
                const outdated = isOutdated(i)
                const issues = compatIssues(i)
                const errors = parseErrors(i.recentErrorsJson)
                const isOpen = expanded.has(i.installationId)
                return (
                  <React.Fragment key={i.installationId}>
                    <tr
                      className="hover:bg-gray-50 dark:hover:bg-gray-800/50 cursor-pointer"
                      onClick={() => toggle(i.installationId)}
                    >
                      <td className="px-3 py-2 whitespace-nowrap">
                        <div className="flex items-center gap-1.5">
                          {isOpen ? <ChevronDown className="h-3.5 w-3.5 text-gray-400" /> : <ChevronRight className="h-3.5 w-3.5 text-gray-400" />}
                          <span className="font-medium dark:text-white">{i.domain}</span>
                        </div>
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap">
                        <span className="dark:text-gray-300">{i.pluginVersion ? `v${i.pluginVersion}` : '—'}</span>
                        {outdated && (
                          <span className="ml-1.5 px-1.5 py-0.5 text-[10px] rounded bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-300">
                            outdated
                          </span>
                        )}
                        {issues.length > 0 && (
                          <span
                            className="ml-1.5 px-1.5 py-0.5 text-[10px] rounded bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300"
                            title={issues.join('; ')}
                          >
                            incompatible
                          </span>
                        )}
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap text-xs text-gray-600 dark:text-gray-400">
                        {i.wpVersion ?? '?'} / {i.wcVersion ?? '?'} / {i.phpVersion ?? '?'}
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap">
                        <span className={`px-2 py-0.5 text-xs rounded-full ${badge.cls}`}>{badge.label}</span>
                        {i.lastHeartbeatOn && (
                          <span className="ml-1.5 text-xs text-gray-500 dark:text-gray-400" title={isoTitle(i.lastHeartbeatOn)}>
                            {formatDateTime(i.lastHeartbeatOn)}
                          </span>
                        )}
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap text-xs text-gray-600 dark:text-gray-400">
                        {i.apiLatencyMs != null ? `${i.apiLatencyMs} ms` : '—'}
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap">
                        <code className="text-xs font-mono text-gray-600 dark:text-gray-400">{i.license.licenseKey}</code>
                        {i.license.status !== 'active' && (
                          <span className="ml-1.5 px-1.5 py-0.5 text-[10px] rounded bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300">
                            {i.license.status}
                          </span>
                        )}
                      </td>
                      <td className="px-3 py-2 text-right">
                        {errors.length > 0 && (
                          <span className="inline-flex items-center gap-1 text-xs text-red-600 dark:text-red-400">
                            <AlertCircle className="h-3.5 w-3.5" />
                            {errors.length}
                          </span>
                        )}
                      </td>
                    </tr>
                    {isOpen && (
                      <tr className="bg-gray-50 dark:bg-gray-800/30">
                        <td colSpan={7} className="px-4 py-3 text-xs text-gray-600 dark:text-gray-400 space-y-2">
                          <div>
                            First seen {formatDateTime(i.createdOn)} · Tenant {i.license.tenantId ?? '—'} · Store {i.license.storeId ?? '—'}
                          </div>
                          {issues.length > 0 && (
                            <div className="text-red-600 dark:text-red-400">
                              Cannot take v{latestVersion}: {issues.join('; ')}
                            </div>
                          )}
                          {errors.length > 0 ? (
                            <div>
                              <p className="font-medium text-gray-700 dark:text-gray-300 mb-1">Recent errors reported by the site:</p>
                              <pre className="bg-white dark:bg-gray-950 border border-gray-200 dark:border-gray-800 rounded p-2 overflow-x-auto whitespace-pre-wrap break-all">
                                {JSON.stringify(errors, null, 2)}
                              </pre>
                            </div>
                          ) : (
                            <div className="text-gray-500">No recent errors reported.</div>
                          )}
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
    </div>
  )
}
