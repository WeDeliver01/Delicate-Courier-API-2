'use client'

import { useCallback, useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Package, RefreshCw, Plus } from 'lucide-react'
import api from '@/lib/api'
import { useAuth } from '@/components/providers/auth-provider'
import { AppLayout } from '@/components/layout/app-layout'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { PluginProduct } from './types'
import { ReleasesTab } from './releases-tab'
import { LicensesTab } from './licenses-tab'
import { InstallationsTab } from './installations-tab'
import { FlagsTab } from './flags-tab'
import { RolloutsTab } from './rollouts-tab'
import { AnalyticsTab } from './analytics-tab'

const TABS = [
  { id: 'releases', label: 'Releases' },
  { id: 'licenses', label: 'Licenses' },
  { id: 'installations', label: 'Installations' },
  { id: 'flags', label: 'Feature Flags' },
  { id: 'rollouts', label: 'Rollouts' },
  { id: 'analytics', label: 'Analytics' },
] as const

type TabId = (typeof TABS)[number]['id']

export default function PluginManagementPage() {
  const router = useRouter()
  const { user, loading: authLoading } = useAuth()
  const isSuperAdmin = user?.role === 'SuperAdmin'

  const [plugins, setPlugins] = useState<PluginProduct[]>([])
  const [selectedPluginId, setSelectedPluginId] = useState<number | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [tab, setTab] = useState<TabId>('releases')

  // New-plugin form
  const [showCreate, setShowCreate] = useState(false)
  const [newSlug, setNewSlug] = useState('')
  const [newName, setNewName] = useState('')
  const [newDescription, setNewDescription] = useState('')
  const [creating, setCreating] = useState(false)

  useEffect(() => {
    if (!authLoading && user && !isSuperAdmin) {
      router.push('/dashboard')
    }
  }, [authLoading, user, isSuperAdmin, router])

  const loadPlugins = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get<PluginProduct[]>('/admin/plugin-platform/plugins')
      setPlugins(res.data)
      setSelectedPluginId((prev) => {
        if (prev != null && res.data.some((p) => p.pluginId === prev)) return prev
        return res.data[0]?.pluginId ?? null
      })
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load plugins')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    if (!isSuperAdmin) return
    loadPlugins()
  }, [isSuperAdmin, loadPlugins])

  const createPlugin = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newSlug.trim() || !newName.trim()) return
    setCreating(true)
    setError('')
    try {
      const res = await api.post('/admin/plugin-platform/plugins', {
        slug: newSlug.trim(),
        name: newName.trim(),
        description: newDescription.trim() || null,
      })
      setShowCreate(false)
      setNewSlug(''); setNewName(''); setNewDescription('')
      await loadPlugins()
      setSelectedPluginId(res.data.pluginId)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to create plugin')
    } finally {
      setCreating(false)
    }
  }

  if (authLoading || (isSuperAdmin && loading)) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading...</p>
        </div>
      </AppLayout>
    )
  }

  if (!isSuperAdmin) return null

  const selected = plugins.find((p) => p.pluginId === selectedPluginId) ?? null

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950 min-h-screen">
        <div className="mx-auto max-w-7xl">
          {/* Header */}
          <div className="mb-6 flex flex-wrap items-center gap-3">
            <Package className="h-8 w-8 text-blue-600" />
            <div className="flex-1 min-w-0">
              <h1 className="text-3xl font-bold dark:text-white">Plugin Management</h1>
              <p className="text-gray-600 dark:text-gray-400">
                Releases, licenses, installations, feature flags, rollouts and fleet analytics.
              </p>
            </div>
            <Button variant="outline" size="sm" onClick={loadPlugins}>
              <RefreshCw className="h-4 w-4 mr-2" />
              Refresh
            </Button>
            <Button size="sm" onClick={() => setShowCreate((v) => !v)}>
              <Plus className="h-4 w-4 mr-2" />
              New plugin
            </Button>
          </div>

          {error && (
            <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg text-red-600 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400">
              {error}
            </div>
          )}

          {showCreate && (
            <Card className="mb-4">
              <CardHeader>
                <CardTitle className="text-base">Register a new plugin product</CardTitle>
              </CardHeader>
              <CardContent>
                <form onSubmit={createPlugin} className="space-y-4">
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Slug *</label>
                      <Input
                        value={newSlug}
                        onChange={(e) => setNewSlug(e.target.value)}
                        placeholder="e.g. we-deliver-platform"
                        required
                      />
                      <p className="text-xs text-gray-500 mt-1">
                        Must match the update slug baked into the plugin build.
                      </p>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Name *</label>
                      <Input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="e.g. We Deliver Platform" required />
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Description</label>
                    <Input value={newDescription} onChange={(e) => setNewDescription(e.target.value)} placeholder="Optional" />
                  </div>
                  <div className="flex gap-3 justify-end">
                    <Button type="button" variant="outline" onClick={() => setShowCreate(false)}>Cancel</Button>
                    <Button type="submit" disabled={creating || !newSlug.trim() || !newName.trim()}>
                      {creating ? 'Creating…' : 'Create plugin'}
                    </Button>
                  </div>
                </form>
              </CardContent>
            </Card>
          )}

          {plugins.length === 0 ? (
            <Card>
              <CardContent className="py-12 text-center text-gray-500 dark:text-gray-400">
                No plugin products registered yet. Create one to start publishing releases.
              </CardContent>
            </Card>
          ) : (
            <>
              {/* Plugin selector */}
              <div className="mb-4 flex flex-wrap items-center gap-3">
                <label className="text-sm font-medium text-gray-700 dark:text-gray-300">Plugin:</label>
                <select
                  value={selectedPluginId ?? ''}
                  onChange={(e) => setSelectedPluginId(Number.parseInt(e.target.value, 10))}
                  className="text-sm rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-3 py-2"
                >
                  {plugins.map((p) => (
                    <option key={p.pluginId} value={p.pluginId}>
                      {p.name} ({p.slug})
                    </option>
                  ))}
                </select>
                {selected && (
                  <span className="text-xs text-gray-500 dark:text-gray-400">
                    {selected.releaseCount} release{selected.releaseCount === 1 ? '' : 's'}
                    {selected.latestVersion ? ` · latest v${selected.latestVersion}` : ''} ·{' '}
                    {selected.licenseCount} license{selected.licenseCount === 1 ? '' : 's'}
                  </span>
                )}
              </div>

              {/* Sub-navigation */}
              <div className="mb-6 border-b border-gray-200 dark:border-gray-800 flex flex-wrap gap-1">
                {TABS.map((t) => (
                  <button
                    key={t.id}
                    type="button"
                    onClick={() => setTab(t.id)}
                    className={`px-4 py-2 text-sm font-medium border-b-2 -mb-px transition-colors ${
                      tab === t.id
                        ? 'border-blue-600 text-blue-600 dark:text-blue-400'
                        : 'border-transparent text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200'
                    }`}
                  >
                    {t.label}
                  </button>
                ))}
              </div>

              {selected && (
                <>
                  {tab === 'releases' && <ReleasesTab pluginId={selected.pluginId} />}
                  {tab === 'licenses' && <LicensesTab pluginId={selected.pluginId} />}
                  {tab === 'installations' && <InstallationsTab pluginId={selected.pluginId} pluginSlug={selected.slug} />}
                  {tab === 'flags' && <FlagsTab pluginId={selected.pluginId} />}
                  {tab === 'rollouts' && <RolloutsTab pluginId={selected.pluginId} />}
                  {tab === 'analytics' && <AnalyticsTab pluginId={selected.pluginId} pluginSlug={selected.slug} />}
                </>
              )}
            </>
          )}
        </div>
      </div>
    </AppLayout>
  )
}
