'use client'

import { useEffect, useState } from 'react'
import { useRouter, useParams } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'

interface TenantDetail {
  tenantID: number
  tenantName: string
  contactEmail: string
  contactPhone: string
  tenantAPIKey: string
  shiplogicBearerToken: string | null
  isActive: boolean
  createdOn: string
  createdBy: string
  changedOn: string | null
  changedBy: string | null
  storeCount: number
  hasShiplogicToken: boolean
}

export default function TenantDetailPage() {
  const [tenant, setTenant] = useState<TenantDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [deactivating, setDeactivating] = useState(false)
  const router = useRouter()
  const params = useParams()
  const tenantId = params.id as string

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchTenantDetail()
  }, [tenantId, router])

  const fetchTenantDetail = async () => {
    try {
      const response = await api.get(`/tenants/${tenantId}`)
      setTenant(response.data)
    } catch (error: any) {
      if (error.response?.status === 404) {
        setError('Tenant not found')
      } else {
        setError('Failed to load tenant details')
      }
    } finally {
      setLoading(false)
    }
  }

  const handleDeactivate = async () => {
    if (!confirm('Are you sure you want to deactivate this tenant? All associated stores will stop processing orders.')) {
      return
    }

    setDeactivating(true)
    try {
      await api.delete(`/tenants/${tenantId}`)
      alert('Tenant deactivated successfully!')
      fetchTenantDetail()
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to deactivate tenant'
      alert(`Error: ${message}`)
    } finally {
      setDeactivating(false)
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading tenant details...</p>
        </div>
      </AppLayout>
    )
  }

  if (error || !tenant) {
    return (
      <AppLayout>
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error || 'Tenant not found'}</p>
          <Button onClick={() => router.push('/tenants')}>Back to Tenants</Button>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-5xl">
          {/* Header */}
          <div className="mb-6 flex items-center justify-between">
            <div>
              <Button
                variant="outline"
                onClick={() => router.push('/tenants')}
                className="mb-2"
              >
                ← Back to Tenants
              </Button>
              <h1 className="text-3xl font-bold">{tenant.tenantName}</h1>
              <p className="text-gray-600 dark:text-gray-400">
                Tenant ID: {tenant.tenantID}
              </p>
            </div>

            <div className="flex gap-2">
              <Button
                variant="outline"
                onClick={() => router.push(`/tenants/${tenantId}/edit`)}
              >
                Edit Tenant
              </Button>

              {tenant.isActive && (
                <Button
                  variant="destructive"
                  onClick={handleDeactivate}
                  disabled={deactivating}
                >
                  {deactivating ? 'Deactivating...' : 'Deactivate Tenant'}
                </Button>
              )}

              <span
                className={`flex items-center rounded-full px-4 py-2 text-sm font-medium ${
                  tenant.isActive
                    ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                    : 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200'
                }`}
              >
                {tenant.isActive ? 'Active' : 'Inactive'}
              </span>
            </div>
          </div>

          <div className="grid gap-6 lg:grid-cols-2">
            {/* Tenant Information Card */}
            <Card>
              <CardHeader>
                <CardTitle>Tenant Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Tenant ID</p>
                  <p className="font-medium font-mono">{tenant.tenantID}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Tenant Name</p>
                  <p className="font-medium">{tenant.tenantName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Contact Email</p>
                  <p className="font-medium">{tenant.contactEmail}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Contact Phone</p>
                  <p className="font-medium">{tenant.contactPhone}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Number of Stores</p>
                  <p className="font-medium">{tenant.storeCount}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Status</p>
                  <p className="font-medium">{tenant.isActive ? 'Active' : 'Inactive'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Created</p>
                  <p>{new Date(tenant.createdOn).toLocaleString()} by {tenant.createdBy}</p>
                </div>

                {tenant.changedOn && (
                  <div>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Last Modified</p>
                    <p>{new Date(tenant.changedOn).toLocaleString()} by {tenant.changedBy}</p>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* API Configuration Card */}
            <Card>
              <CardHeader>
                <CardTitle>API Configuration</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Tenant API Key</p>
                  <p className="break-all font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded">
                    {tenant.tenantAPIKey}
                  </p>
                </div>

                <div className="flex items-center justify-between rounded-lg border dark:border-gray-700 p-3">
                  <div>
                    <p className="font-medium">Shiplogic Bearer Token</p>
                    <p className="text-sm text-gray-600 dark:text-gray-400">
                      For courier API integration
                    </p>
                  </div>
                  <span
                    className={`rounded-full px-3 py-1 text-sm font-medium ${
                      tenant.hasShiplogicToken
                        ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                        : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'
                    }`}
                  >
                    {tenant.hasShiplogicToken ? '✓ Configured' : '✗ Missing'}
                  </span>
                </div>

                {!tenant.hasShiplogicToken && (
                  <div className="rounded-md bg-yellow-50 dark:bg-yellow-900/20 p-4 text-sm text-yellow-800 dark:text-yellow-200">
                    <p className="font-medium">⚠️ Configuration Incomplete</p>
                    <p className="mt-1">
                      This tenant needs a Shiplogic bearer token configured before stores can process shipments.
                      Click "Edit Tenant" to add it.
                    </p>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* Quick Actions Card */}
            <Card className="lg:col-span-2">
              <CardHeader>
                <CardTitle>Quick Actions</CardTitle>
              </CardHeader>
              <CardContent>
                <div className="grid gap-3 sm:grid-cols-4">
                  <Button
                    variant="outline"
                    onClick={() => router.push(`/tenants/${tenantId}/edit`)}
                    className="w-full"
                  >
                    Edit Configuration
                  </Button>
                  <Button
                    variant="outline"
                    onClick={() => router.push(`/stores?tenant=${tenantId}`)}
                    className="w-full"
                  >
                    View Stores ({tenant.storeCount})
                  </Button>
                  <Button
                    variant="outline"
                    onClick={() => router.push('/stores/create')}
                    className="w-full"
                  >
                    Add New Store
                  </Button>
                  <Button
                    variant="outline"
                    onClick={() => {
                      navigator.clipboard.writeText(tenant.tenantAPIKey)
                      alert('API Key copied to clipboard!')
                    }}
                    className="w-full"
                  >
                    Copy API Key
                  </Button>
                </div>
              </CardContent>
            </Card>
          </div>
        </div>
      </div>
    </AppLayout>
  )
}