'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { ShoppingBag, RefreshCw, AlertCircle, CheckCircle2, Copy, Check, ExternalLink } from 'lucide-react'
import api from '@/lib/api'
import { useAuth } from '@/components/providers/auth-provider'
import { AppLayout } from '@/components/layout/app-layout'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'

interface ShopifyStore {
  storeID: number
  storeName: string
  tenantID: number
  tenantName: string
  shopifyStoreUrl: string | null
  hasAccessToken: boolean
  isActive: boolean
}

interface CarrierServiceResult {
  success: boolean
  message: string
  callbackUrl?: string | null
}

interface WebhookSubscriptionResult {
  success: boolean
  message: string
  callbackUrl?: string | null
  created?: string[]
  alreadyPresent?: string[]
  updated?: string[]
  failed?: string[]
  webhookSecretConfigured?: boolean
}

export default function ShopifyCarrierSetupPage() {
  const router = useRouter()
  const { user, loading: authLoading } = useAuth()
  const isSuperAdmin = user?.role === 'SuperAdmin'

  const [stores, setStores] = useState<ShopifyStore[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState('')
  const [registeringId, setRegisteringId] = useState<number | null>(null)
  const [results, setResults] = useState<Record<number, CarrierServiceResult>>({})
  const [subscribingId, setSubscribingId] = useState<number | null>(null)
  const [webhookResults, setWebhookResults] = useState<Record<number, WebhookSubscriptionResult>>({})
  const [copied, setCopied] = useState<number | null>(null)

  useEffect(() => {
    if (!authLoading && user && !isSuperAdmin) {
      router.push('/dashboard')
    }
  }, [authLoading, user, isSuperAdmin, router])

  const loadStores = async () => {
    try {
      setIsLoading(true)
      setError('')
      const res = await api.get<ShopifyStore[]>('/shopify/stores')
      setStores(res.data)
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load Shopify stores')
    } finally {
      setIsLoading(false)
    }
  }

  useEffect(() => {
    if (!isSuperAdmin) return
    loadStores()
  }, [isSuperAdmin])

  const registerCarrierService = async (storeId: number) => {
    setRegisteringId(storeId)
    setResults((prev) => {
      const next = { ...prev }
      delete next[storeId]
      return next
    })
    try {
      const res = await api.post<CarrierServiceResult>(`/shopify/register-carrier-service/${storeId}`)
      setResults((prev) => ({ ...prev, [storeId]: res.data }))
    } catch (err: any) {
      const data = err.response?.data
      setResults((prev) => ({
        ...prev,
        [storeId]: {
          success: false,
          message: data?.message || data?.error || 'Registration failed',
          callbackUrl: data?.callbackUrl ?? null,
        },
      }))
    } finally {
      setRegisteringId(null)
    }
  }

  const subscribeWebhooks = async (storeId: number) => {
    setSubscribingId(storeId)
    setWebhookResults((prev) => {
      const next = { ...prev }
      delete next[storeId]
      return next
    })
    try {
      const res = await api.post<WebhookSubscriptionResult>(`/shopify/register-webhooks/${storeId}`)
      setWebhookResults((prev) => ({ ...prev, [storeId]: res.data }))
    } catch (err: any) {
      const data = err.response?.data
      setWebhookResults((prev) => ({
        ...prev,
        [storeId]: {
          success: false,
          message: data?.message || data?.error || 'Webhook subscription failed',
          callbackUrl: data?.callbackUrl ?? null,
        },
      }))
    } finally {
      setSubscribingId(null)
    }
  }

  const copyUrl = async (storeId: number, value: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(storeId)
      setTimeout(() => setCopied(null), 1500)
    } catch {
      setError('Clipboard copy failed — select the value manually.')
    }
  }

  if (authLoading || isLoading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading...</p>
        </div>
      </AppLayout>
    )
  }

  if (!isSuperAdmin) return null

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950 min-h-screen">
        <div className="mx-auto max-w-4xl">
          <div className="mb-6 flex items-center gap-3">
            <ShoppingBag className="h-8 w-8 text-emerald-600" />
            <div className="flex-1">
              <h1 className="text-3xl font-bold dark:text-white">Shopify Store Setup</h1>
              <p className="text-gray-600 dark:text-gray-400">
                Register the Delicate Couriers carrier service (live checkout rates) and subscribe the
                store to order webhooks (so new orders flow in automatically) for a connected Shopify store.
              </p>
            </div>
            <Button variant="outline" size="sm" onClick={loadStores}>
              <RefreshCw className="h-4 w-4 mr-2" />
              Refresh
            </Button>
          </div>

          {error && (
            <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg text-red-600 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400">
              {error}
            </div>
          )}

          <Card className="mb-4 border-amber-200 dark:border-amber-800">
            <CardContent className="pt-6">
              <div className="flex gap-2">
                <AlertCircle className="h-5 w-5 text-amber-600 dark:text-amber-400 flex-shrink-0 mt-0.5" />
                <div className="text-sm text-gray-700 dark:text-gray-300 space-y-1">
                  <p>
                    Registering creates (or updates) a <strong>Carrier Service</strong> named{' '}
                    <strong>Delicate Couriers</strong> on the store. Shopify will then call our platform
                    for live rates during checkout.
                  </p>
                  <p>
                    If registration is rejected, the store&apos;s access token most likely lacks the{' '}
                    <code className="bg-gray-100 dark:bg-gray-800 px-1 rounded">write_shipping</code>{' '}
                    (Carrier Service) scope, or the store&apos;s Shopify plan doesn&apos;t support
                    third-party carrier-calculated rates (this requires the Advanced plan, the annual
                    plan, or the Carrier Calculated Shipping add-on).
                  </p>
                  <p>
                    <strong>Subscribe order webhooks</strong> registers{' '}
                    <code className="bg-gray-100 dark:bg-gray-800 px-1 rounded">orders/create</code>,{' '}
                    <code className="bg-gray-100 dark:bg-gray-800 px-1 rounded">orders/updated</code> and{' '}
                    <code className="bg-gray-100 dark:bg-gray-800 px-1 rounded">orders/cancelled</code> so new
                    orders flow into the platform automatically. It is idempotent — re-running won&apos;t create
                    duplicates. This needs the{' '}
                    <code className="bg-gray-100 dark:bg-gray-800 px-1 rounded">read_orders</code> scope, and the
                    store&apos;s webhook secret must match the app&apos;s API secret for the webhooks to verify.
                  </p>
                </div>
              </div>
            </CardContent>
          </Card>

          {stores.length === 0 ? (
            <Card>
              <CardContent className="py-12 text-center text-gray-500 dark:text-gray-400">
                No Shopify-platform stores found. Connect a store on the{' '}
                <button className="text-emerald-600 hover:underline" onClick={() => router.push('/stores')}>
                  Stores
                </button>{' '}
                page first (set its platform to Shopify and add the store URL + access token).
              </CardContent>
            </Card>
          ) : (
            <div className="space-y-4">
              {stores.map((store) => {
                const result = results[store.storeID]
                const busy = registeringId === store.storeID
                return (
                  <Card key={store.storeID}>
                    <CardHeader>
                      <CardTitle className="flex items-center justify-between gap-3">
                        <span className="truncate">{store.storeName}</span>
                        <span className="text-xs font-normal text-gray-500 dark:text-gray-400">
                          {store.tenantName} · #{store.storeID}
                        </span>
                      </CardTitle>
                    </CardHeader>
                    <CardContent className="space-y-3">
                      <div className="text-sm text-gray-600 dark:text-gray-400">
                        <span className="font-medium">Store URL:</span>{' '}
                        {store.shopifyStoreUrl || <span className="italic text-gray-400">not set</span>}
                      </div>

                      {!store.hasAccessToken && (
                        <div className="flex gap-2 p-3 bg-yellow-50 border border-yellow-200 rounded-lg dark:bg-yellow-900/20 dark:border-yellow-800 text-sm text-yellow-800 dark:text-yellow-300">
                          <AlertCircle className="h-5 w-5 flex-shrink-0 mt-0.5" />
                          <span>
                            No Shopify access token on file for this store. Add one on the Stores page before registering.
                          </span>
                        </div>
                      )}

                      <div className="flex flex-wrap items-center gap-3">
                        <Button
                          onClick={() => registerCarrierService(store.storeID)}
                          disabled={busy || !store.hasAccessToken || !store.shopifyStoreUrl}
                        >
                          {busy ? (
                            <>
                              <RefreshCw className="h-4 w-4 mr-2 animate-spin" />
                              Registering...
                            </>
                          ) : (
                            'Register / re-register carrier service'
                          )}
                        </Button>
                        <Button
                          variant="outline"
                          onClick={() => subscribeWebhooks(store.storeID)}
                          disabled={subscribingId === store.storeID || !store.hasAccessToken || !store.shopifyStoreUrl}
                        >
                          {subscribingId === store.storeID ? (
                            <>
                              <RefreshCw className="h-4 w-4 mr-2 animate-spin" />
                              Subscribing...
                            </>
                          ) : (
                            'Subscribe order webhooks'
                          )}
                        </Button>
                      </div>

                      {result && (
                        <div
                          className={`p-3 rounded-lg border text-sm ${
                            result.success
                              ? 'bg-green-50 border-green-200 text-green-700 dark:bg-green-900/20 dark:border-green-800 dark:text-green-400'
                              : 'bg-red-50 border-red-200 text-red-700 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400'
                          }`}
                        >
                          <div className="flex gap-2">
                            {result.success ? (
                              <CheckCircle2 className="h-5 w-5 flex-shrink-0 mt-0.5" />
                            ) : (
                              <AlertCircle className="h-5 w-5 flex-shrink-0 mt-0.5" />
                            )}
                            <span>{result.message}</span>
                          </div>
                          {result.callbackUrl && (
                            <div className="mt-2 flex items-center gap-2">
                              <span className="text-xs font-medium opacity-80">Callback URL:</span>
                              <code className="flex-1 truncate text-xs bg-white/60 dark:bg-black/30 px-2 py-1 rounded font-mono">
                                {result.callbackUrl}
                              </code>
                              <Button
                                variant="outline"
                                size="sm"
                                onClick={() => copyUrl(store.storeID, result.callbackUrl!)}
                              >
                                {copied === store.storeID ? (
                                  <Check className="h-4 w-4 text-green-600" />
                                ) : (
                                  <Copy className="h-4 w-4" />
                                )}
                              </Button>
                            </div>
                          )}
                        </div>
                      )}

                      {webhookResults[store.storeID] && (() => {
                        const wr = webhookResults[store.storeID]
                        return (
                          <div
                            className={`p-3 rounded-lg border text-sm ${
                              wr.success
                                ? 'bg-green-50 border-green-200 text-green-700 dark:bg-green-900/20 dark:border-green-800 dark:text-green-400'
                                : 'bg-red-50 border-red-200 text-red-700 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400'
                            }`}
                          >
                            <div className="flex gap-2">
                              {wr.success ? (
                                <CheckCircle2 className="h-5 w-5 flex-shrink-0 mt-0.5" />
                              ) : (
                                <AlertCircle className="h-5 w-5 flex-shrink-0 mt-0.5" />
                              )}
                              <span>{wr.message}</span>
                            </div>
                            {wr.webhookSecretConfigured === false && (
                              <div className="mt-2 flex gap-2 text-amber-700 dark:text-amber-400">
                                <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                                <span className="text-xs">
                                  This store has no webhook secret on file. Shopify-signed webhooks will be
                                  rejected until the app&apos;s API secret is saved as the store&apos;s webhook
                                  secret on the Stores page.
                                </span>
                              </div>
                            )}
                            {wr.callbackUrl && (
                              <div className="mt-2 text-xs opacity-80">
                                <span className="font-medium">Webhook URL:</span>{' '}
                                <code className="font-mono">{wr.callbackUrl}</code>
                              </div>
                            )}
                          </div>
                        )
                      })()}
                    </CardContent>
                  </Card>
                )
              })}
            </div>
          )}

          <Card className="mt-4">
            <CardHeader>
              <CardTitle>Verify in Shopify</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2 text-sm text-gray-700 dark:text-gray-300">
              <p>
                After a successful registration, open the store&apos;s Shopify admin →{' '}
                <strong>Settings → Shipping and delivery</strong>. The{' '}
                <strong>Delicate Couriers</strong> carrier service should appear under the relevant
                shipping profile, and live rates will be quoted at checkout.
              </p>
              <Button variant="outline" onClick={() => router.push('/stores')}>
                <ExternalLink className="h-4 w-4 mr-2" />
                Manage stores
              </Button>
            </CardContent>
          </Card>
        </div>
      </div>
    </AppLayout>
  )
}
