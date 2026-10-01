'use client'

import { useEffect, useState } from 'react'
import { useRouter, useParams } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'
import { QuickActionsSidebar } from '@/components/ui/quick-actions-sidebar'
import { Pencil, ExternalLink, Webhook, TestTube, Package, KeyRound } from 'lucide-react'

interface StoreDetail {
  storeID: number
  tenantID: number
  tenantName: string
  storeName: string
  wooCommerceURL: string
  isActive: boolean
  createdOn: string
  createdBy: string
  changedOn?: string
  changedBy?: string
  hasWooConsumerKey: boolean
  hasWooConsumerSecret: boolean
  hasWebhookSecret: boolean
  // Collection Address
  collectionAddressLine1?: string
  collectionAddressLine2?: string
  collectionCity?: string
  collectionProvince?: string
  collectionPostalCode?: string
  collectionCountry: string
  // Collection Contact
  collectionContactName?: string
  collectionContactPhone?: string
  collectionContactEmail?: string
  collectionCompanyName?: string
  // Shiplogic Configuration
  shiplogicProviderId: number
  shiplogicAccountId: number
  defaultServiceLevel: string
}

export default function StoreDetailPage() {
  const [store, setStore] = useState<StoreDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [deactivating, setDeactivating] = useState(false)
  const [testingWebhook, setTestingWebhook] = useState(false)
  const [webhookTestResult, setWebhookTestResult] = useState<{
      success: boolean;
      message: string;
  } | null>(null)
  const [testingWooApi, setTestingWooApi] = useState(false)
  const [wooApiTestResult, setWooApiTestResult] = useState<{
      success: boolean;
      message: string;
  } | null>(null)

  const router = useRouter()
  const params = useParams()
  const storeId = params.id as string

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchStoreDetail()
  }, [storeId, router])

  const fetchStoreDetail = async () => {
    try {
      const response = await api.get(`/stores/${storeId}`)
      setStore(response.data)
    } catch (error: any) {
      if (error.response?.status === 404) {
        setError('Store not found')
      } else {
        setError('Failed to load store details')
      }
    } finally {
      setLoading(false)
    }
  }

  const handleDeactivate = async () => {
    if (!confirm('Are you sure you want to deactivate this store? It will stop processing orders.')) {
      return
    }

    setDeactivating(true)
    try {
      await api.delete(`/stores/${storeId}`)
      alert('Store deactivated successfully!')
      fetchStoreDetail()
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to deactivate store'
      alert(`Error: ${message}`)
    } finally {
      setDeactivating(false)
    }
  }

  const handleTestWooApi = async () => {
    setTestingWooApi(true)
    setWooApiTestResult(null)

    try {
      const response = await api.get(`/woocommerce/test-connection/${storeId}`)
      const data = response.data || {}
      if (data.success) {
        const env = data.environment ? ` (WC ${data.environment})` : ''
        setWooApiTestResult({
          success: true,
          message: `WooCommerce REST API credentials work${env}. Tracking pushback will succeed on the next shipment.`,
        })
      } else {
        setWooApiTestResult({
          success: false,
          message: data.message || 'WooCommerce rejected the credentials.',
        })
      }
      setTimeout(() => setWooApiTestResult(null), 8000)
    } catch (err: any) {
      const status = err.response?.status
      const detail = err.response?.data?.message || err.response?.data?.error || err.message
      const hint =
        status === 401
          ? 'WooCommerce returned 401 — Consumer Key/Secret are wrong or revoked, or the key was created Read-only (it needs Read/Write).'
          : status === 404
          ? 'WooCommerce returned 404 — check the store URL.'
          : ''
      setWooApiTestResult({
        success: false,
        message: `${detail || 'Test failed.'} ${hint}`.trim(),
      })
      setTimeout(() => setWooApiTestResult(null), 10000)
    } finally {
      setTestingWooApi(false)
    }
  }

  const handleTestWebhook = async () => {
    setTestingWebhook(true)
    setWebhookTestResult(null)

    try {
      const response = await api.post(`/stores/${storeId}/test-webhook`)
      setWebhookTestResult({
        success: true,
        message: response.data.message || 'Webhook test successful!'
      })
      setTimeout(() => setWebhookTestResult(null), 5000)
    } catch (err: any) {
      setWebhookTestResult({
        success: false,
        message: err.response?.data?.message || 'Webhook test failed. Please check your configuration.'
      })
      setTimeout(() => setWebhookTestResult(null), 8000)
    } finally {
      setTestingWebhook(false)
    }
  }

  // Helper to format address
  const formatCollectionAddress = () => {
    if (!store) return 'Not configured'
    const parts = [
      store.collectionAddressLine1,
      store.collectionAddressLine2,
      store.collectionCity,
      store.collectionProvince,
      store.collectionPostalCode,
      store.collectionCountry
    ].filter(Boolean)
    return parts.length > 0 ? parts.join(', ') : 'Not configured'
  }

  // Quick Actions for sidebar
  const quickActions = store ? [
    {
      id: 'edit',
      icon: <Pencil size={20} />,
      label: 'Edit Store',
      onClick: () => router.push(`/stores/${storeId}/edit`),
    },
    {
      id: 'woocommerce',
      icon: <ExternalLink size={20} />,
      label: 'View WooCommerce',
      onClick: () => window.open(store.wooCommerceURL, '_blank'),
    },
    {
      id: 'webhook',
      icon: <Webhook size={20} />,
      label: testingWebhook ? 'Testing...' : 'Test Plugin Webhook',
      onClick: handleTestWebhook,
      variant: 'warning' as const,
    },
    {
      id: 'woo-api',
      icon: <KeyRound size={20} />,
      label: testingWooApi ? 'Testing...' : 'Test WooCommerce API',
      onClick: handleTestWooApi,
    },
    {
      id: 'shiplogic',
      icon: <TestTube size={20} />,
      label: 'Test Shiplogic',
      onClick: () => router.push(`/stores/${storeId}/test-endpoints`),
    },
    {
      id: 'packages',
      icon: <Package size={20} />,
      label: 'Package Types',
      onClick: () => router.push(`/stores/${storeId}/package-types`),
      variant: 'success' as const,
    },
  ] : []

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading store details...</p>
        </div>
      </AppLayout>
    )
  }

  if (error || !store) {
    return (
      <AppLayout>
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error || 'Store not found'}</p>
          <Button onClick={() => router.push('/stores')}>Back to Stores</Button>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950 min-h-screen">
        <div className="mx-auto max-w-5xl pr-16">
          {/* Header */}
          <div className="mb-6 flex items-center justify-between">
            <div>
              <Button
                variant="outline"
                onClick={() => router.push('/stores')}
                className="mb-2"
              >
                ← Back to Stores
              </Button>
              <h1 className="text-3xl font-bold">{store.storeName}</h1>
              <p className="text-gray-600 dark:text-gray-400">
                {store.tenantName} • Store ID: {store.storeID}
              </p>
            </div>

            <div className="flex gap-2 items-center">
              {store.isActive && (
                <Button
                  variant="destructive"
                  onClick={handleDeactivate}
                  disabled={deactivating}
                >
                  {deactivating ? 'Deactivating...' : 'Deactivate Store'}
                </Button>
              )}

              <span
                className={`flex items-center rounded-full px-4 py-2 text-sm font-medium ${
                  store.isActive
                    ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                    : 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200'
                }`}
              >
                {store.isActive ? 'Active' : 'Inactive'}
              </span>
            </div>
          </div>

          {/* WooCommerce REST API Test Result */}
          {wooApiTestResult && (
            <div
              className={`mb-6 p-4 rounded-lg ${
                wooApiTestResult.success
                  ? 'bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800'
                  : 'bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800'
              }`}
            >
              <p
                className={
                  wooApiTestResult.success
                    ? 'text-green-800 dark:text-green-200'
                    : 'text-red-800 dark:text-red-200'
                }
              >
                {wooApiTestResult.message}
              </p>
            </div>
          )}

          {/* Webhook Test Result */}
          {webhookTestResult && (
            <div
              className={`mb-6 p-4 rounded-lg ${
                webhookTestResult.success
                  ? 'bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800'
                  : 'bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800'
              }`}
            >
              <p
                className={
                  webhookTestResult.success
                    ? 'text-green-800 dark:text-green-200'
                    : 'text-red-800 dark:text-red-200'
                }
              >
                {webhookTestResult.message}
              </p>
            </div>
          )}

          <div className="grid gap-6 lg:grid-cols-2">
            {/* Store Information Card */}
            <Card>
              <CardHeader>
                <CardTitle>Store Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Store ID</p>
                  <p className="font-medium font-mono">{store.storeID}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Store Name</p>
                  <p className="font-medium">{store.storeName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Tenant</p>
                  <p className="font-medium">{store.tenantName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">WooCommerce URL</p>
                  <a
                    href={store.wooCommerceURL}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-blue-600 hover:underline"
                  >
                    {store.wooCommerceURL}
                  </a>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Status</p>
                  <p className="font-medium">{store.isActive ? 'Active' : 'Inactive'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Created</p>
                  <p>{new Date(store.createdOn).toLocaleString()} by {store.createdBy}</p>
                </div>

                {store.changedOn && (
                  <div>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Last Modified</p>
                    <p>{new Date(store.changedOn).toLocaleString()} by {store.changedBy}</p>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* Configuration Status Card */}
            <Card>
              <CardHeader>
                <CardTitle>Configuration Status</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="flex items-center justify-between rounded-lg border p-3 dark:border-gray-700">
                  <div>
                    <p className="font-medium">WooCommerce API Keys</p>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Consumer Key & Secret</p>
                  </div>
                  <span
                    className={`rounded-full px-3 py-1 text-sm font-medium ${
                      store.hasWooConsumerKey && store.hasWooConsumerSecret
                        ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                        : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'
                    }`}
                  >
                    {store.hasWooConsumerKey && store.hasWooConsumerSecret ? '✓ Configured' : '✗ Missing'}
                  </span>
                </div>

                <div className="flex items-center justify-between rounded-lg border p-3 dark:border-gray-700">
                  <div>
                    <p className="font-medium">Webhook Secret</p>
                    <p className="text-sm text-gray-600 dark:text-gray-400">For secure webhooks</p>
                  </div>
                  <span
                    className={`rounded-full px-3 py-1 text-sm font-medium ${
                      store.hasWebhookSecret
                        ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                        : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'
                    }`}
                  >
                    {store.hasWebhookSecret ? '✓ Configured' : '✗ Missing'}
                  </span>
                </div>

                <div className="flex items-center justify-between rounded-lg border p-3 dark:border-gray-700">
                  <div>
                    <p className="font-medium">Collection Address</p>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Pickup location</p>
                  </div>
                  <span
                    className={`rounded-full px-3 py-1 text-sm font-medium ${
                      store.collectionAddressLine1
                        ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                        : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'
                    }`}
                  >
                    {store.collectionAddressLine1 ? '✓ Configured' : '✗ Missing'}
                  </span>
                </div>

                <div className="flex items-center justify-between rounded-lg border p-3 dark:border-gray-700">
                  <div>
                    <p className="font-medium">Shiplogic Account</p>
                    <p className="text-sm text-gray-600 dark:text-gray-400">Provider & Account IDs</p>
                  </div>
                  <span
                    className={`rounded-full px-3 py-1 text-sm font-medium ${
                      store.shiplogicProviderId > 0 && store.shiplogicAccountId > 0
                        ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200'
                        : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'
                    }`}
                  >
                    {store.shiplogicProviderId > 0 && store.shiplogicAccountId > 0 ? '✓ Configured' : '✗ Missing'}
                  </span>
                </div>

                {(!store.hasWooConsumerKey || !store.hasWebhookSecret || !store.collectionAddressLine1) && (
                  <div className="rounded-md bg-yellow-50 dark:bg-yellow-900/20 p-4 text-sm text-yellow-800 dark:text-yellow-200">
                    <p className="font-medium">⚠️ Configuration Incomplete</p>
                    <p className="mt-1">
                      This store needs additional configuration before it can process orders.
                      Click the Edit button on the right to complete the setup.
                    </p>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* Collection Address Card */}
            <Card>
              <CardHeader>
                <CardTitle>Collection Address</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Company Name</p>
                  <p className="font-medium">{store.collectionCompanyName || 'Not set'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Address</p>
                  <p className="font-medium">{formatCollectionAddress()}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Contact Name</p>
                  <p className="font-medium">{store.collectionContactName || 'Not set'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Contact Phone</p>
                  <p className="font-medium">{store.collectionContactPhone || 'Not set'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Contact Email</p>
                  <p className="font-medium">{store.collectionContactEmail || 'Not set'}</p>
                </div>
              </CardContent>
            </Card>

            {/* Shiplogic Configuration Card */}
            <Card>
              <CardHeader>
                <CardTitle>Shiplogic Configuration</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Provider ID</p>
                  <p className="font-medium font-mono">{store.shiplogicProviderId || 'Not set'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Account ID</p>
                  <p className="font-medium font-mono">{store.shiplogicAccountId || 'Not set'}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600 dark:text-gray-400">Default Service Level</p>
                  <p className="font-medium">{store.defaultServiceLevel || 'ECO'}</p>
                </div>
              </CardContent>
            </Card>
          </div>
        </div>

        {/* Quick Actions Sidebar */}
        <QuickActionsSidebar actions={quickActions} />
      </div>
    </AppLayout>
  )
}