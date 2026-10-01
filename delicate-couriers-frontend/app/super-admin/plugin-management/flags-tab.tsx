'use client'

import { useCallback, useEffect, useState } from 'react'
import { RefreshCw, Plus, Trash2, ToggleLeft, ToggleRight } from 'lucide-react'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatDateTime, isoTitle } from '@/lib/datetime'
import { FeatureFlag } from './types'

export function FlagsTab({ pluginId }: { pluginId: number }) {
  const [flags, setFlags] = useState<FeatureFlag[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [busyId, setBusyId] = useState<number | null>(null)

  // Create form
  const [showCreate, setShowCreate] = useState(false)
  const [newKey, setNewKey] = useState('')
  const [newEnabled, setNewEnabled] = useState(true)
  const [newValueJson, setNewValueJson] = useState('')
  const [creating, setCreating] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get<FeatureFlag[]>(`/admin/plugin-platform/plugins/${pluginId}/flags`)
      setFlags(res.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load feature flags')
    } finally {
      setLoading(false)
    }
  }, [pluginId])

  useEffect(() => { load() }, [load])

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newKey.trim()) return
    setCreating(true)
    setError('')
    setSuccess('')
    try {
      await api.post(`/admin/plugin-platform/plugins/${pluginId}/flags`, {
        key: newKey.trim(),
        enabled: newEnabled,
        valueJson: newValueJson.trim() || null,
      })
      setSuccess(`Flag "${newKey.trim()}" saved.`)
      setShowCreate(false)
      setNewKey(''); setNewEnabled(true); setNewValueJson('')
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to save flag')
    } finally {
      setCreating(false)
    }
  }

  const toggleFlag = async (flag: FeatureFlag) => {
    setBusyId(flag.featureFlagId)
    setError('')
    setSuccess('')
    try {
      await api.post(`/admin/plugin-platform/plugins/${pluginId}/flags`, {
        key: flag.key,
        enabled: !flag.enabled,
        valueJson: flag.valueJson,
      })
      setSuccess(`Flag "${flag.key}" ${!flag.enabled ? 'enabled' : 'disabled'}.`)
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to toggle flag')
    } finally {
      setBusyId(null)
    }
  }

  const deleteFlag = async (flag: FeatureFlag) => {
    if (!confirm(`Delete flag "${flag.key}"? Sites will stop receiving it on their next config fetch.`)) return
    setBusyId(flag.featureFlagId)
    setError('')
    setSuccess('')
    try {
      await api.delete(`/admin/plugin-platform/flags/${flag.featureFlagId}`)
      setSuccess(`Flag "${flag.key}" deleted.`)
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to delete flag')
    } finally {
      setBusyId(null)
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600 dark:text-gray-400">
          Feature flags are delivered to every installation of this plugin on its next update check.
          The optional JSON value lets you ship configuration alongside the on/off switch.
        </p>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={load} disabled={loading}>
            <RefreshCw className={`h-4 w-4 mr-2 ${loading ? 'animate-spin' : ''}`} />
            Refresh
          </Button>
          <Button size="sm" onClick={() => setShowCreate((v) => !v)}>
            <Plus className="h-4 w-4 mr-2" />
            New flag
          </Button>
        </div>
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

      {showCreate && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">New feature flag</CardTitle>
          </CardHeader>
          <CardContent>
            <form onSubmit={create} className="space-y-4">
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Key *</label>
                  <Input value={newKey} onChange={(e) => setNewKey(e.target.value)} placeholder="e.g. enable_new_checkout_flow" required />
                </div>
                <div className="flex items-end pb-1">
                  <label className="flex items-center gap-2 text-sm cursor-pointer dark:text-gray-300">
                    <input type="checkbox" checked={newEnabled} onChange={(e) => setNewEnabled(e.target.checked)} />
                    Enabled
                  </label>
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Value (optional JSON)</label>
                <textarea
                  value={newValueJson}
                  onChange={(e) => setNewValueJson(e.target.value)}
                  rows={3}
                  placeholder='e.g. {"max_retries": 3}'
                  className="w-full text-sm font-mono rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-3 py-2"
                />
              </div>
              <div className="flex gap-3 justify-end">
                <Button type="button" variant="outline" onClick={() => setShowCreate(false)}>Cancel</Button>
                <Button type="submit" disabled={creating || !newKey.trim()}>
                  {creating ? 'Saving…' : 'Save flag'}
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 py-8 text-center">Loading flags…</p>
      ) : flags.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            No feature flags defined for this plugin yet.
          </CardContent>
        </Card>
      ) : (
        <div className="space-y-2">
          {flags.map((f) => (
            <Card key={f.featureFlagId}>
              <CardContent className="py-3 flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={() => toggleFlag(f)}
                  disabled={busyId === f.featureFlagId}
                  title={f.enabled ? 'Disable' : 'Enable'}
                  className="disabled:opacity-50"
                >
                  {f.enabled ? (
                    <ToggleRight className="h-7 w-7 text-green-600" />
                  ) : (
                    <ToggleLeft className="h-7 w-7 text-gray-400" />
                  )}
                </button>
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <code className="text-sm font-mono font-medium dark:text-white">{f.key}</code>
                    <span className={`px-1.5 py-0.5 text-[10px] rounded ${
                      f.enabled
                        ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300'
                        : 'bg-gray-100 text-gray-600 dark:bg-gray-700 dark:text-gray-400'
                    }`}>
                      {f.enabled ? 'enabled' : 'disabled'}
                    </span>
                  </div>
                  {f.valueJson && (
                    <code className="block text-xs text-gray-500 dark:text-gray-400 truncate mt-0.5">{f.valueJson}</code>
                  )}
                  {f.changedOn && (
                    <p className="text-xs text-gray-400 mt-0.5" title={isoTitle(f.changedOn)}>
                      Changed {formatDateTime(f.changedOn)}{f.changedBy ? ` by ${f.changedBy}` : ''}
                    </p>
                  )}
                </div>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={busyId === f.featureFlagId}
                  onClick={() => deleteFlag(f)}
                  className="text-red-600 border-red-300 hover:bg-red-50 dark:text-red-400 dark:border-red-800 dark:hover:bg-red-900/20"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  )
}
