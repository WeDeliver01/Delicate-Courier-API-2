'use client'

import { useCallback, useEffect, useState } from 'react'
import { RefreshCw, Plus, Trash2 } from 'lucide-react'
import api from '@/lib/api'
import { Card, CardContent } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  PluginRelease,
  ROLLOUT_STAGES,
  STAGE_COVERAGE,
  STAGE_LABELS,
  stageBadgeClass,
} from './types'

/**
 * Rollouts overview: current stage + coverage per published release, with
 * per-release domain allow/deny targeting rules. Stage changes live in the
 * Releases tab; this view is about who is covered right now.
 */
export function RolloutsTab({ pluginId }: { pluginId: number }) {
  const [releases, setReleases] = useState<PluginRelease[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [busy, setBusy] = useState(false)

  // Add-rule form state, keyed by releaseId
  const [ruleDomain, setRuleDomain] = useState<Record<number, string>>({})
  const [ruleType, setRuleType] = useState<Record<number, string>>({})

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get<PluginRelease[]>(`/admin/plugin-platform/plugins/${pluginId}/releases`)
      setReleases(res.data.filter((r) => r.status === 'published'))
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load rollouts')
    } finally {
      setLoading(false)
    }
  }, [pluginId])

  useEffect(() => { load() }, [load])

  const addRule = async (release: PluginRelease) => {
    const domain = (ruleDomain[release.releaseId] ?? '').trim()
    const type = ruleType[release.releaseId] ?? 'allow_domain'
    if (!domain) return
    setBusy(true)
    setError('')
    setSuccess('')
    try {
      await api.post(`/admin/plugin-platform/releases/${release.releaseId}/rules`, {
        ruleType: type,
        value: domain,
      })
      setSuccess(`${type === 'allow_domain' ? 'Allow' : 'Deny'} rule for ${domain} added to v${release.version}.`)
      setRuleDomain((prev) => ({ ...prev, [release.releaseId]: '' }))
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to add rule')
    } finally {
      setBusy(false)
    }
  }

  const deleteRule = async (release: PluginRelease, ruleId: number, label: string) => {
    if (!confirm(`Remove rule "${label}" from v${release.version}?`)) return
    setBusy(true)
    setError('')
    setSuccess('')
    try {
      await api.delete(`/admin/plugin-platform/rules/${ruleId}`)
      setSuccess(`Rule removed from v${release.version}.`)
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to remove rule')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600 dark:text-gray-400">
          Current rollout stage and coverage per published release. Domain rules override the stage:
          <strong> allow</strong> always offers the release to that domain, <strong>deny</strong> never offers it.
          Change stages from the Releases tab.
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
      {success && (
        <div className="p-3 bg-green-50 border border-green-200 rounded-lg text-sm text-green-700 dark:bg-green-900/20 dark:border-green-800 dark:text-green-400">
          {success}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 py-8 text-center">Loading…</p>
      ) : releases.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            No published releases — upload one from the Releases tab.
          </CardContent>
        </Card>
      ) : (
        <div className="space-y-3">
          {releases.map((r) => {
            const coverage = STAGE_COVERAGE[r.rolloutStage] ?? 0
            const stageIdx = ROLLOUT_STAGES.indexOf(r.rolloutStage as (typeof ROLLOUT_STAGES)[number])
            return (
              <Card key={r.releaseId}>
                <CardContent className="pt-4 pb-4 space-y-3">
                  <div className="flex flex-wrap items-center gap-3">
                    <span className="font-semibold dark:text-white">v{r.version}</span>
                    <span className={`px-2 py-0.5 text-xs rounded-full ${stageBadgeClass(r.rolloutStage)}`}>
                      {STAGE_LABELS[r.rolloutStage] ?? r.rolloutStage}
                    </span>
                    <span className="text-xs text-gray-500 dark:text-gray-400">
                      {r.rolloutStage === 'internal' && 'Internal licenses only'}
                      {r.rolloutStage === 'beta' && 'Internal + beta licenses'}
                      {coverage > 0 && `~${coverage}% of the general fleet (+ internal & beta)`}
                    </span>
                  </div>

                  {/* Stage progress bar */}
                  <div className="flex items-center gap-1">
                    {ROLLOUT_STAGES.map((s, idx) => (
                      <div
                        key={s}
                        title={STAGE_LABELS[s]}
                        className={`h-2 flex-1 rounded-full ${
                          idx <= stageIdx ? 'bg-blue-600' : 'bg-gray-200 dark:bg-gray-700'
                        }`}
                      />
                    ))}
                  </div>
                  <div className="flex justify-between text-[10px] text-gray-400">
                    {ROLLOUT_STAGES.map((s) => <span key={s}>{STAGE_LABELS[s]}</span>)}
                  </div>

                  {/* Rules */}
                  <div className="border-t border-gray-100 dark:border-gray-800 pt-3 space-y-2">
                    <p className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase">Domain targeting rules</p>
                    {r.rules.length === 0 ? (
                      <p className="text-xs text-gray-400">No rules — stage-based rollout only.</p>
                    ) : (
                      <div className="flex flex-wrap gap-2">
                        {r.rules.map((rule) => (
                          <span
                            key={rule.rolloutRuleId}
                            className={`inline-flex items-center gap-1.5 px-2 py-1 text-xs rounded-full border ${
                              rule.ruleType === 'allow_domain'
                                ? 'bg-green-50 border-green-200 text-green-800 dark:bg-green-900/20 dark:border-green-800 dark:text-green-300'
                                : 'bg-red-50 border-red-200 text-red-800 dark:bg-red-900/20 dark:border-red-800 dark:text-red-300'
                            }`}
                          >
                            {rule.ruleType === 'allow_domain' ? 'allow' : 'deny'} · {rule.value}
                            <button
                              type="button"
                              disabled={busy}
                              onClick={() => deleteRule(r, rule.rolloutRuleId, `${rule.ruleType} ${rule.value}`)}
                              className="opacity-60 hover:opacity-100 disabled:opacity-30"
                              title="Remove rule"
                            >
                              <Trash2 className="h-3 w-3" />
                            </button>
                          </span>
                        ))}
                      </div>
                    )}
                    <div className="flex flex-wrap items-center gap-2 pt-1">
                      <select
                        value={ruleType[r.releaseId] ?? 'allow_domain'}
                        onChange={(e) => setRuleType((prev) => ({ ...prev, [r.releaseId]: e.target.value }))}
                        className="text-xs rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-2 py-1.5"
                      >
                        <option value="allow_domain">Allow domain</option>
                        <option value="deny_domain">Deny domain</option>
                      </select>
                      <Input
                        value={ruleDomain[r.releaseId] ?? ''}
                        onChange={(e) => setRuleDomain((prev) => ({ ...prev, [r.releaseId]: e.target.value }))}
                        placeholder="example.co.za"
                        className="max-w-xs h-8 text-xs"
                      />
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={busy || !(ruleDomain[r.releaseId] ?? '').trim()}
                        onClick={() => addRule(r)}
                      >
                        <Plus className="h-3.5 w-3.5 mr-1" />
                        Add rule
                      </Button>
                    </div>
                  </div>
                </CardContent>
              </Card>
            )
          })}
        </div>
      )}
    </div>
  )
}
