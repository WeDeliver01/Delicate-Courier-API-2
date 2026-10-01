'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { RefreshCw, Upload, AlertCircle, Ban, ChevronDown, ChevronRight } from 'lucide-react'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatDateTime, isoTitle } from '@/lib/datetime'
import {
  PluginRelease,
  ROLLOUT_STAGES,
  STAGE_LABELS,
  formatBytes,
  stageBadgeClass,
} from './types'

export function ReleasesTab({ pluginId }: { pluginId: number }) {
  const [releases, setReleases] = useState<PluginRelease[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [expanded, setExpanded] = useState<Set<number>>(new Set())

  // Upload form
  const [showUpload, setShowUpload] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const [version, setVersion] = useState('')
  const [changelog, setChangelog] = useState('')
  const [minWp, setMinWp] = useState('')
  const [minWc, setMinWc] = useState('')
  const [minPhp, setMinPhp] = useState('')
  const [uploadStage, setUploadStage] = useState('internal')
  const [uploading, setUploading] = useState(false)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const [busyReleaseId, setBusyReleaseId] = useState<number | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get<PluginRelease[]>(`/admin/plugin-platform/plugins/${pluginId}/releases`)
      setReleases(res.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load releases')
    } finally {
      setLoading(false)
    }
  }, [pluginId])

  useEffect(() => { load() }, [load])

  const resetUploadForm = () => {
    setFile(null)
    setVersion('')
    setChangelog('')
    setMinWp('')
    setMinWc('')
    setMinPhp('')
    setUploadStage('internal')
    if (fileInputRef.current) fileInputRef.current.value = ''
  }

  const handleUpload = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!file || !version.trim()) return
    setUploading(true)
    setError('')
    setSuccess('')
    try {
      const form = new FormData()
      form.append('file', file)
      form.append('version', version.trim())
      if (changelog.trim()) form.append('changelog', changelog.trim())
      if (minWp.trim()) form.append('minWpVersion', minWp.trim())
      if (minWc.trim()) form.append('minWcVersion', minWc.trim())
      if (minPhp.trim()) form.append('minPhpVersion', minPhp.trim())
      form.append('rolloutStage', uploadStage)
      const res = await api.post(`/admin/plugin-platform/plugins/${pluginId}/releases`, form, {
        headers: { 'Content-Type': 'multipart/form-data' },
      })
      setSuccess(`Release ${res.data.version} published at stage "${STAGE_LABELS[res.data.rolloutStage] ?? res.data.rolloutStage}" — SHA256 ${res.data.sha256}`)
      setShowUpload(false)
      resetUploadForm()
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Upload failed')
    } finally {
      setUploading(false)
    }
  }

  const setRollout = async (release: PluginRelease, stage: string) => {
    if (stage === release.rolloutStage) return
    const label = STAGE_LABELS[stage] ?? stage
    if (!confirm(`Move v${release.version} to rollout stage "${label}"?`)) return
    setBusyReleaseId(release.releaseId)
    setError('')
    setSuccess('')
    try {
      await api.post(`/admin/plugin-platform/releases/${release.releaseId}/rollout`, { stage })
      setSuccess(`v${release.version} moved to "${label}"`)
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to change rollout stage')
    } finally {
      setBusyReleaseId(null)
    }
  }

  const withdraw = async (release: PluginRelease) => {
    if (!confirm(
      `Withdraw v${release.version}? It will never be offered to any installation again. ` +
      `Republishing is not supported — you would need to upload a new version. Continue?`
    )) return
    setBusyReleaseId(release.releaseId)
    setError('')
    setSuccess('')
    try {
      await api.post(`/admin/plugin-platform/releases/${release.releaseId}/withdraw`)
      setSuccess(`v${release.version} withdrawn. Sites already updated stay on it; to roll the fleet back, promote an earlier good release to 100%.`)
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to withdraw release')
    } finally {
      setBusyReleaseId(null)
    }
  }

  const toggle = (id: number) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600 dark:text-gray-400">
          Upload a ZIP to publish a release, then stage the rollout: Internal → Beta → 5% → 25% → 100%.
          Withdraw is the kill-switch for a bad release.
        </p>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={load} disabled={loading}>
            <RefreshCw className={`h-4 w-4 mr-2 ${loading ? 'animate-spin' : ''}`} />
            Refresh
          </Button>
          <Button size="sm" onClick={() => setShowUpload((v) => !v)}>
            <Upload className="h-4 w-4 mr-2" />
            Upload release
          </Button>
        </div>
      </div>

      {error && (
        <div className="p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-600 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400">
          {error}
        </div>
      )}
      {success && (
        <div className="p-3 bg-green-50 border border-green-200 rounded-lg text-sm text-green-700 dark:bg-green-900/20 dark:border-green-800 dark:text-green-400 break-all">
          {success}
        </div>
      )}

      {showUpload && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">New release</CardTitle>
          </CardHeader>
          <CardContent>
            <form onSubmit={handleUpload} className="space-y-4">
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Plugin ZIP *</label>
                  <input
                    ref={fileInputRef}
                    type="file"
                    accept=".zip,application/zip"
                    onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                    className="w-full text-sm text-gray-700 dark:text-gray-300 file:mr-3 file:px-3 file:py-1.5 file:rounded file:border file:border-gray-300 dark:file:border-gray-600 file:bg-white dark:file:bg-gray-800 file:text-sm file:cursor-pointer"
                    required
                  />
                  {file && (
                    <p className="text-xs text-gray-500 mt-1">{file.name} · {formatBytes(file.size)}</p>
                  )}
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Version *</label>
                  <Input value={version} onChange={(e) => setVersion(e.target.value)} placeholder="e.g. 2.5.1" required />
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Changelog</label>
                <textarea
                  value={changelog}
                  onChange={(e) => setChangelog(e.target.value)}
                  rows={4}
                  placeholder="What changed in this release…"
                  className="w-full text-sm rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-3 py-2"
                />
              </div>
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Min WordPress</label>
                  <Input value={minWp} onChange={(e) => setMinWp(e.target.value)} placeholder="e.g. 6.0" />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Min WooCommerce</label>
                  <Input value={minWc} onChange={(e) => setMinWc(e.target.value)} placeholder="e.g. 8.0" />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Min PHP</label>
                  <Input value={minPhp} onChange={(e) => setMinPhp(e.target.value)} placeholder="e.g. 7.4" />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Initial stage</label>
                  <select
                    value={uploadStage}
                    onChange={(e) => setUploadStage(e.target.value)}
                    className="w-full text-sm rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-2 py-2"
                  >
                    {ROLLOUT_STAGES.map((s) => (
                      <option key={s} value={s}>{STAGE_LABELS[s]}</option>
                    ))}
                  </select>
                </div>
              </div>
              <div className="flex gap-3 justify-end">
                <Button type="button" variant="outline" onClick={() => { setShowUpload(false); resetUploadForm() }}>
                  Cancel
                </Button>
                <Button type="submit" disabled={uploading || !file || !version.trim()}>
                  {uploading ? (
                    <>
                      <RefreshCw className="h-4 w-4 mr-2 animate-spin" />
                      Uploading…
                    </>
                  ) : (
                    'Publish release'
                  )}
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 py-8 text-center">Loading releases…</p>
      ) : releases.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            No releases yet. Upload the first ZIP to get started.
          </CardContent>
        </Card>
      ) : (
        <div className="space-y-3">
          {releases.map((r) => {
            const isOpen = expanded.has(r.releaseId)
            const withdrawn = r.status === 'withdrawn'
            const busy = busyReleaseId === r.releaseId
            return (
              <Card key={r.releaseId} className={withdrawn ? 'opacity-70' : ''}>
                <CardContent className="pt-4 pb-4">
                  <div
                    className="flex flex-wrap items-center gap-3 cursor-pointer"
                    onClick={() => toggle(r.releaseId)}
                  >
                    {isOpen ? <ChevronDown className="h-4 w-4 text-gray-400" /> : <ChevronRight className="h-4 w-4 text-gray-400" />}
                    <span className="font-semibold dark:text-white">v{r.version}</span>
                    {withdrawn ? (
                      <span className="px-2 py-0.5 text-xs rounded-full bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300">
                        Withdrawn
                      </span>
                    ) : (
                      <span className={`px-2 py-0.5 text-xs rounded-full ${stageBadgeClass(r.rolloutStage)}`}>
                        {STAGE_LABELS[r.rolloutStage] ?? r.rolloutStage}
                      </span>
                    )}
                    <span className="text-xs text-gray-500 dark:text-gray-400" title={isoTitle(r.createdOn)}>
                      {formatDateTime(r.createdOn)}{r.createdBy ? ` · ${r.createdBy}` : ''}
                    </span>
                    <span className="text-xs text-gray-500 dark:text-gray-400">{formatBytes(r.fileSizeBytes)}</span>
                    <span className="ml-auto" />
                    {!withdrawn && (
                      <Button
                        size="sm"
                        variant="outline"
                        className="text-red-600 border-red-300 hover:bg-red-50 dark:text-red-400 dark:border-red-800 dark:hover:bg-red-900/20"
                        disabled={busy}
                        onClick={(e) => { e.stopPropagation(); withdraw(r) }}
                      >
                        <Ban className="h-4 w-4 mr-1" />
                        Withdraw
                      </Button>
                    )}
                  </div>

                  {!withdrawn && (
                    <div className="mt-3 flex flex-wrap items-center gap-1">
                      <span className="text-xs text-gray-500 dark:text-gray-400 mr-1">Rollout:</span>
                      {ROLLOUT_STAGES.map((s, idx) => (
                        <span key={s} className="flex items-center gap-1">
                          {idx > 0 && <span className="text-gray-300 dark:text-gray-600 text-xs">→</span>}
                          <button
                            type="button"
                            disabled={busy}
                            onClick={() => setRollout(r, s)}
                            className={`px-2 py-1 text-xs rounded border transition-colors ${
                              r.rolloutStage === s
                                ? 'bg-blue-600 text-white border-blue-600'
                                : 'bg-white dark:bg-gray-800 border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-700'
                            } disabled:opacity-50`}
                          >
                            {STAGE_LABELS[s]}
                          </button>
                        </span>
                      ))}
                    </div>
                  )}

                  {isOpen && (
                    <div className="mt-4 space-y-3 text-sm border-t border-gray-100 dark:border-gray-800 pt-3">
                      <div className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1 text-xs text-gray-600 dark:text-gray-400">
                        <div><span className="font-medium">File:</span> {r.fileName}</div>
                        <div className="break-all"><span className="font-medium">SHA256:</span> <code>{r.sha256}</code></div>
                        <div>
                          <span className="font-medium">Compatibility:</span>{' '}
                          WP ≥ {r.minWpVersion ?? 'any'} · WC ≥ {r.minWcVersion ?? 'any'} · PHP ≥ {r.minPhpVersion ?? 'any'}
                        </div>
                        {withdrawn && (
                          <div className="text-red-600 dark:text-red-400">
                            Withdrawn {r.withdrawnOn ? formatDateTime(r.withdrawnOn) : ''}{r.withdrawnBy ? ` by ${r.withdrawnBy}` : ''}
                          </div>
                        )}
                      </div>
                      {r.changelog && (
                        <div>
                          <p className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase mb-1">Changelog</p>
                          <pre className="text-xs whitespace-pre-wrap bg-gray-50 dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded p-2">{r.changelog}</pre>
                        </div>
                      )}
                      {r.rules.length > 0 && (
                        <div className="text-xs text-gray-600 dark:text-gray-400">
                          <span className="font-medium">Targeting rules:</span>{' '}
                          {r.rules.map((rule) => `${rule.ruleType === 'allow_domain' ? 'allow' : 'deny'} ${rule.value}`).join(', ')}
                          {' '}(managed in the Rollouts tab)
                        </div>
                      )}
                    </div>
                  )}
                </CardContent>
              </Card>
            )
          })}
        </div>
      )}

      <div className="flex gap-2 p-3 bg-amber-50 border border-amber-200 rounded-lg dark:bg-amber-900/20 dark:border-amber-800 text-xs text-amber-800 dark:text-amber-300">
        <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
        <span>
          <strong>Rolling back:</strong> withdrawing a release stops it being offered, but sites that already
          updated keep it. To roll the fleet back, withdraw the bad release and move the last good release to 100% —
          note WordPress only auto-offers upgrades, so downgrades on already-updated sites must be done manually.
        </span>
      </div>
    </div>
  )
}
