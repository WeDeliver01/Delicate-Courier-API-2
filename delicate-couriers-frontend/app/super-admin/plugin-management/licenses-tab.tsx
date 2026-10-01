'use client'

import { useCallback, useEffect, useState } from 'react'
import { RefreshCw, Plus, Copy, Check, Ban } from 'lucide-react'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatDateTime, isoTitle } from '@/lib/datetime'
import { PluginLicense } from './types'

function licenseStatusBadge(status: string) {
  switch (status) {
    case 'active':
      return 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300'
    case 'inactive':
      return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300'
    case 'revoked':
      return 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300'
    default:
      return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300'
  }
}

export function LicensesTab({ pluginId }: { pluginId: number }) {
  const [licenses, setLicenses] = useState<PluginLicense[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [copiedId, setCopiedId] = useState<number | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)

  // Issue form
  const [showIssue, setShowIssue] = useState(false)
  const [tenantId, setTenantId] = useState('')
  const [storeId, setStoreId] = useState('')
  const [isInternal, setIsInternal] = useState(false)
  const [isBeta, setIsBeta] = useState(false)
  const [expiresOn, setExpiresOn] = useState('')
  const [issuing, setIssuing] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get<PluginLicense[]>(`/admin/plugin-platform/plugins/${pluginId}/licenses`)
      setLicenses(res.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load licenses')
    } finally {
      setLoading(false)
    }
  }, [pluginId])

  useEffect(() => { load() }, [load])

  const issue = async (e: React.FormEvent) => {
    e.preventDefault()
    setIssuing(true)
    setError('')
    setSuccess('')
    try {
      const body: Record<string, unknown> = { isInternal, isBeta }
      if (tenantId.trim()) body.tenantId = Number.parseInt(tenantId, 10)
      if (storeId.trim()) body.storeId = Number.parseInt(storeId, 10)
      if (expiresOn) body.expiresOn = new Date(expiresOn).toISOString()
      const res = await api.post(`/admin/plugin-platform/plugins/${pluginId}/licenses`, body)
      setSuccess(`License issued: ${res.data.licenseKey} — copy it now and send it to the merchant.`)
      setShowIssue(false)
      setTenantId(''); setStoreId(''); setIsInternal(false); setIsBeta(false); setExpiresOn('')
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to issue license')
    } finally {
      setIssuing(false)
    }
  }

  const revoke = async (license: PluginLicense) => {
    if (!confirm(
      `Revoke license ${license.licenseKey}? Sites using it lose update access immediately. This cannot be undone.`
    )) return
    setBusyId(license.licenseId)
    setError('')
    setSuccess('')
    try {
      await api.post(`/admin/plugin-platform/licenses/${license.licenseId}/revoke`)
      setSuccess(`License ${license.licenseKey} revoked.`)
      await load()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to revoke license')
    } finally {
      setBusyId(null)
    }
  }

  const copyKey = async (id: number, key: string) => {
    try {
      await navigator.clipboard.writeText(key)
      setCopiedId(id)
      setTimeout(() => setCopiedId(null), 1500)
    } catch {
      setError('Clipboard copy failed — select the key manually.')
    }
  }

  const filtered = licenses.filter((l) => {
    if (statusFilter && l.status !== statusFilter) return false
    if (!search.trim()) return true
    const q = search.trim().toLowerCase()
    return (
      l.licenseKey.toLowerCase().includes(q) ||
      (l.domain ?? '').toLowerCase().includes(q) ||
      l.installations.some((i) => i.domain.toLowerCase().includes(q)) ||
      String(l.tenantId ?? '').includes(q) ||
      String(l.storeId ?? '').includes(q)
    )
  })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <Input
          placeholder="Search by key, domain, tenant or store…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="max-w-sm"
        />
        <select
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value)}
          className="text-sm rounded border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 dark:text-white px-2 py-2"
        >
          <option value="">All statuses</option>
          <option value="active">Active</option>
          <option value="inactive">Inactive</option>
          <option value="revoked">Revoked</option>
        </select>
        <span className="ml-auto" />
        <Button variant="outline" size="sm" onClick={load} disabled={loading}>
          <RefreshCw className={`h-4 w-4 mr-2 ${loading ? 'animate-spin' : ''}`} />
          Refresh
        </Button>
        <Button size="sm" onClick={() => setShowIssue((v) => !v)}>
          <Plus className="h-4 w-4 mr-2" />
          Issue license
        </Button>
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

      {showIssue && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Issue a new license</CardTitle>
          </CardHeader>
          <CardContent>
            <form onSubmit={issue} className="space-y-4">
              <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Tenant ID (optional)</label>
                  <Input type="number" value={tenantId} onChange={(e) => setTenantId(e.target.value)} placeholder="e.g. 2" />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Store ID (optional)</label>
                  <Input type="number" value={storeId} onChange={(e) => setStoreId(e.target.value)} placeholder="e.g. 5" />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Expires (optional)</label>
                  <Input type="date" value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
                </div>
              </div>
              <div className="flex flex-wrap gap-6 text-sm">
                <label className="flex items-center gap-2 cursor-pointer dark:text-gray-300">
                  <input type="checkbox" checked={isInternal} onChange={(e) => setIsInternal(e.target.checked)} />
                  Internal (receives internal-stage releases)
                </label>
                <label className="flex items-center gap-2 cursor-pointer dark:text-gray-300">
                  <input type="checkbox" checked={isBeta} onChange={(e) => setIsBeta(e.target.checked)} />
                  Beta (receives beta-stage releases)
                </label>
              </div>
              <div className="flex gap-3 justify-end">
                <Button type="button" variant="outline" onClick={() => setShowIssue(false)}>Cancel</Button>
                <Button type="submit" disabled={issuing}>
                  {issuing ? 'Issuing…' : 'Issue license'}
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <p className="text-sm text-gray-500 py-8 text-center">Loading licenses…</p>
      ) : filtered.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-gray-500 dark:text-gray-400">
            {licenses.length === 0 ? 'No licenses issued yet.' : 'No licenses match the current filters.'}
          </CardContent>
        </Card>
      ) : (
        <div className="overflow-x-auto bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-md">
          <table className="min-w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-800/50 border-b border-gray-200 dark:border-gray-800">
              <tr>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">License key</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Bound to</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Tenant / Store</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Channels</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">Dates</th>
                <th className="px-3 py-2"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-800">
              {filtered.map((l) => (
                <tr key={l.licenseId} className="hover:bg-gray-50 dark:hover:bg-gray-800/50">
                  <td className="px-3 py-2 whitespace-nowrap">
                    <div className="flex items-center gap-1.5">
                      <code className="text-xs font-mono dark:text-gray-300">{l.licenseKey}</code>
                      <button
                        type="button"
                        onClick={() => copyKey(l.licenseId, l.licenseKey)}
                        className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                        title="Copy key"
                      >
                        {copiedId === l.licenseId ? <Check className="h-3.5 w-3.5 text-green-600" /> : <Copy className="h-3.5 w-3.5" />}
                      </button>
                    </div>
                  </td>
                  <td className="px-3 py-2">
                    <span className={`px-2 py-0.5 text-xs rounded-full ${licenseStatusBadge(l.status)}`}>
                      {l.status}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-xs text-gray-600 dark:text-gray-400">
                    {l.installations.length > 0 ? (
                      <div className="space-y-0.5">
                        {l.installations.map((i) => (
                          <div key={i.installationId}>
                            <span className="font-medium dark:text-gray-300">{i.domain}</span>
                            {i.pluginVersion && <span className="text-gray-400"> · v{i.pluginVersion}</span>}
                          </div>
                        ))}
                      </div>
                    ) : l.domain ? (
                      l.domain
                    ) : (
                      <span className="italic text-gray-400">not activated</span>
                    )}
                  </td>
                  <td className="px-3 py-2 text-xs text-gray-600 dark:text-gray-400 whitespace-nowrap">
                    {l.tenantId != null ? `Tenant ${l.tenantId}` : '—'}{l.storeId != null ? ` / Store ${l.storeId}` : ''}
                  </td>
                  <td className="px-3 py-2 text-xs whitespace-nowrap">
                    {l.isInternal && (
                      <span className="px-1.5 py-0.5 mr-1 rounded bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300">internal</span>
                    )}
                    {l.isBeta && (
                      <span className="px-1.5 py-0.5 rounded bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300">beta</span>
                    )}
                    {!l.isInternal && !l.isBeta && <span className="text-gray-400">stable</span>}
                  </td>
                  <td className="px-3 py-2 text-xs text-gray-600 dark:text-gray-400 whitespace-nowrap">
                    <div title={isoTitle(l.createdOn)}>Issued {formatDateTime(l.createdOn)}</div>
                    {l.activatedOn && <div title={isoTitle(l.activatedOn)}>Activated {formatDateTime(l.activatedOn)}</div>}
                    {l.expiresOn && <div title={isoTitle(l.expiresOn)}>Expires {formatDateTime(l.expiresOn)}</div>}
                  </td>
                  <td className="px-3 py-2 text-right whitespace-nowrap">
                    {l.status !== 'revoked' && (
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={busyId === l.licenseId}
                        onClick={() => revoke(l)}
                        className="text-red-600 border-red-300 hover:bg-red-50 dark:text-red-400 dark:border-red-800 dark:hover:bg-red-900/20"
                      >
                        <Ban className="h-3.5 w-3.5 mr-1" />
                        Revoke
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
