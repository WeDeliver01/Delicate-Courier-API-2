'use client'

import { useEffect, useState } from 'react'
import { useRouter, useParams } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { AppLayout } from '@/components/layout/app-layout'
import { Eye, EyeOff } from 'lucide-react'

interface StoreDetail {
  storeID: number
  tenantID: number
  tenantName: string
  storeName: string
  wooCommerceURL: string
  isActive: boolean
  createdOn: string
  hasWooConsumerKey: boolean
  hasWooConsumerSecret: boolean
  hasWebhookSecret: boolean
  wooConsumerKey?: string
  wooConsumerSecret?: string
  webhookSecret?: string
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
  // Special Trip fallback
  specialTripCostPerKm?: number | null
  specialTripMinFee?: number | null
  specialTripMaxKm?: number | null
  hasGoogleMapsApiKey: boolean
}

export default function StoreEditPage() {
  const [store, setStore] = useState<StoreDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')

  // Basic Info
  const [storeName, setStoreName] = useState('')
  const [wooCommerceURL, setWooCommerceURL] = useState('')
  const [isActive, setIsActive] = useState(true)

  // Credentials
  const [wooConsumerKey, setWooConsumerKey] = useState('')
  const [wooConsumerSecret, setWooConsumerSecret] = useState('')
  const [webhookSecret, setWebhookSecret] = useState('')

  // Collection Address
  const [collectionAddressLine1, setCollectionAddressLine1] = useState('')
  const [collectionAddressLine2, setCollectionAddressLine2] = useState('')
  const [collectionCity, setCollectionCity] = useState('')
  const [collectionProvince, setCollectionProvince] = useState('')
  const [collectionPostalCode, setCollectionPostalCode] = useState('')
  const [collectionCountry, setCollectionCountry] = useState('ZA')

  // Collection Contact
  const [collectionContactName, setCollectionContactName] = useState('')
  const [collectionContactPhone, setCollectionContactPhone] = useState('')
  const [collectionContactEmail, setCollectionContactEmail] = useState('')
  const [collectionCompanyName, setCollectionCompanyName] = useState('')

  // Shiplogic Configuration
  const [shiplogicProviderId, setShiplogicProviderId] = useState<number>(0)
  const [shiplogicAccountId, setShiplogicAccountId] = useState<number>(0)
  const [defaultServiceLevel, setDefaultServiceLevel] = useState('ECO')

  // Special Trip fallback
  const [specialTripCostPerKm, setSpecialTripCostPerKm] = useState('')
  const [specialTripMinFee, setSpecialTripMinFee] = useState('')
  const [specialTripMaxKm, setSpecialTripMaxKm] = useState('')
  const [googleMapsApiKey, setGoogleMapsApiKey] = useState('')
  const [hasGoogleMapsApiKey, setHasGoogleMapsApiKey] = useState(false)
  const [showGoogleMapsApiKey, setShowGoogleMapsApiKey] = useState(false)

  // Show/hide toggles
  const [showConsumerKey, setShowConsumerKey] = useState(false)
  const [showConsumerSecret, setShowConsumerSecret] = useState(false)
  const [showWebhookSecret, setShowWebhookSecret] = useState(false)

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
      const data = response.data

      setStore(data)

      // Basic Info
      setStoreName(data.storeName)
      setWooCommerceURL(data.wooCommerceURL)
      setIsActive(data.isActive)

      // Credentials
      setWooConsumerKey(data.wooConsumerKey || '')
      setWooConsumerSecret(data.wooConsumerSecret || '')
      setWebhookSecret(data.webhookSecret || '')

      // Collection Address
      setCollectionAddressLine1(data.collectionAddressLine1 || '')
      setCollectionAddressLine2(data.collectionAddressLine2 || '')
      setCollectionCity(data.collectionCity || '')
      setCollectionProvince(data.collectionProvince || '')
      setCollectionPostalCode(data.collectionPostalCode || '')
      setCollectionCountry(data.collectionCountry || 'ZA')

      // Collection Contact
      setCollectionContactName(data.collectionContactName || '')
      setCollectionContactPhone(data.collectionContactPhone || '')
      setCollectionContactEmail(data.collectionContactEmail || '')
      setCollectionCompanyName(data.collectionCompanyName || '')

      // Shiplogic Configuration
      setShiplogicProviderId(data.shiplogicProviderId || 0)
      setShiplogicAccountId(data.shiplogicAccountId || 0)
      setDefaultServiceLevel(data.defaultServiceLevel || 'ECO')

      // Special Trip fallback
      setSpecialTripCostPerKm(data.specialTripCostPerKm != null ? String(data.specialTripCostPerKm) : '')
      setSpecialTripMinFee(data.specialTripMinFee != null ? String(data.specialTripMinFee) : '')
      setSpecialTripMaxKm(data.specialTripMaxKm != null ? String(data.specialTripMaxKm) : '')
      setHasGoogleMapsApiKey(!!data.hasGoogleMapsApiKey)
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

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    setSuccess('')

    try {
      const updateData: any = {
        storeName,
        wooCommerceURL,
        isActive,
        // Collection Address
        collectionAddressLine1,
        collectionAddressLine2,
        collectionCity,
        collectionProvince,
        collectionPostalCode,
        collectionCountry,
        // Collection Contact
        collectionContactName,
        collectionContactPhone,
        collectionContactEmail,
        collectionCompanyName,
        // Shiplogic Configuration
        shiplogicProviderId,
        shiplogicAccountId,
        defaultServiceLevel
      }

      // Only include credentials if they were entered
      if (wooConsumerKey) updateData.wooConsumerKey = wooConsumerKey
      if (wooConsumerSecret) updateData.wooConsumerSecret = wooConsumerSecret
      if (webhookSecret) updateData.webhookSecret = webhookSecret

      // Special Trip fallback. Rate per km: empty field = 0 = disable.
      updateData.specialTripCostPerKm = specialTripCostPerKm ? parseFloat(specialTripCostPerKm) : 0
      if (specialTripMinFee) updateData.specialTripMinFee = parseFloat(specialTripMinFee)
      if (specialTripMaxKm) updateData.specialTripMaxKm = parseFloat(specialTripMaxKm)
      // Google key: only send when a new value was typed (blank = keep current)
      if (googleMapsApiKey) updateData.googleMapsApiKey = googleMapsApiKey

      await api.put(`/stores/${storeId}`, updateData)

      setSuccess('Store updated successfully!')

      setTimeout(() => {
        router.push(`/stores/${storeId}`)
      }, 1500)
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to update store'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading store...</p>
        </div>
      </AppLayout>
    )
  }

  if (error && !store) {
    return (
      <AppLayout>
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error}</p>
          <Button onClick={() => router.push('/stores')}>Back to Stores</Button>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-4xl">
          <div className="mb-6">
            <Button
              variant="outline"
              onClick={() => router.push(`/stores/${storeId}`)}
              className="mb-2"
            >
              ← Back to Store Detail
            </Button>
            <h1 className="text-3xl font-bold">Edit Store</h1>
            <p className="text-gray-600 dark:text-gray-400">
              {store?.storeName} • Store ID: {store?.storeID}
            </p>
          </div>

          {success && (
            <div className="mb-6 rounded-md bg-green-50 dark:bg-green-900/20 p-4 text-green-800 dark:text-green-200">
              {success}
            </div>
          )}

          {error && (
            <div className="mb-6 rounded-md bg-red-50 dark:bg-red-900/20 p-4 text-red-800 dark:text-red-200">
              {error}
            </div>
          )}

          <form onSubmit={handleSubmit}>
            {/* Store Information */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Store Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <Label htmlFor="storeId">Store ID</Label>
                    <Input
                      id="storeId"
                      type="text"
                      value={store?.storeID || ''}
                      disabled
                      className="font-mono bg-gray-100 dark:bg-gray-800"
                    />
                  </div>

                  <div>
                    <Label htmlFor="storeName">Store Name *</Label>
                    <Input
                      id="storeName"
                      type="text"
                      value={storeName}
                      onChange={(e) => setStoreName(e.target.value)}
                      required
                      disabled={saving}
                    />
                  </div>
                </div>

                <div>
                  <Label htmlFor="wooCommerceURL">WooCommerce URL *</Label>
                  <Input
                    id="wooCommerceURL"
                    type="url"
                    value={wooCommerceURL}
                    onChange={(e) => setWooCommerceURL(e.target.value)}
                    placeholder="https://example.com"
                    required
                    disabled={saving}
                  />
                </div>

                <div className="flex items-center gap-2">
                  <input
                    id="isActive"
                    type="checkbox"
                    checked={isActive}
                    onChange={(e) => setIsActive(e.target.checked)}
                    disabled={saving}
                    className="h-4 w-4"
                  />
                  <Label htmlFor="isActive" className="cursor-pointer">
                    Store is Active
                  </Label>
                </div>
              </CardContent>
            </Card>

            {/* WooCommerce API Credentials */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>WooCommerce API Credentials</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  Leave blank to keep existing credentials
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="wooConsumerKey">Consumer Key</Label>
                  <div className="relative">
                    <Input
                      id="wooConsumerKey"
                      type={showConsumerKey ? 'text' : 'password'}
                      value={wooConsumerKey}
                      onChange={(e) => setWooConsumerKey(e.target.value)}
                      placeholder={store?.hasWooConsumerKey ? '••••••••' : 'Enter consumer key'}
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowConsumerKey(!showConsumerKey)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700"
                    >
                      {showConsumerKey ? <EyeOff className="h-5 w-5" /> : <Eye className="h-5 w-5" />}
                    </button>
                  </div>
                  {store?.hasWooConsumerKey && (
                    <p className="mt-1 text-xs text-green-600">✓ Currently configured</p>
                  )}
                </div>

                <div>
                  <Label htmlFor="wooConsumerSecret">Consumer Secret</Label>
                  <div className="relative">
                    <Input
                      id="wooConsumerSecret"
                      type={showConsumerSecret ? 'text' : 'password'}
                      value={wooConsumerSecret}
                      onChange={(e) => setWooConsumerSecret(e.target.value)}
                      placeholder={store?.hasWooConsumerSecret ? '••••••••' : 'Enter consumer secret'}
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowConsumerSecret(!showConsumerSecret)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700"
                    >
                      {showConsumerSecret ? <EyeOff className="h-5 w-5" /> : <Eye className="h-5 w-5" />}
                    </button>
                  </div>
                  {store?.hasWooConsumerSecret && (
                    <p className="mt-1 text-xs text-green-600">✓ Currently configured</p>
                  )}
                </div>
              </CardContent>
            </Card>

            {/* Webhook Configuration */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Webhook Configuration</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  Secure your webhook endpoint
                </p>
              </CardHeader>
              <CardContent>
                <div>
                  <Label htmlFor="webhookSecret">Webhook Secret</Label>
                  <div className="relative">
                    <Input
                      id="webhookSecret"
                      type={showWebhookSecret ? 'text' : 'password'}
                      value={webhookSecret}
                      onChange={(e) => setWebhookSecret(e.target.value)}
                      placeholder={store?.hasWebhookSecret ? '••••••••' : 'Enter webhook secret'}
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowWebhookSecret(!showWebhookSecret)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700"
                    >
                      {showWebhookSecret ? <EyeOff className="h-5 w-5" /> : <Eye className="h-5 w-5" />}
                    </button>
                  </div>
                  {store?.hasWebhookSecret && (
                    <p className="mt-1 text-xs text-green-600">✓ Currently configured</p>
                  )}
                </div>
              </CardContent>
            </Card>

            {/* Collection Address */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Collection Address</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  Where shipments are picked up from (warehouse/store location)
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="collectionCompanyName">Company Name</Label>
                  <Input
                    id="collectionCompanyName"
                    type="text"
                    value={collectionCompanyName}
                    onChange={(e) => setCollectionCompanyName(e.target.value)}
                    placeholder="e.g., Honey Bee Bakery - Pretoria Branch"
                    disabled={saving}
                  />
                </div>

                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <Label htmlFor="collectionAddressLine1">Address Line 1 *</Label>
                    <Input
                      id="collectionAddressLine1"
                      type="text"
                      value={collectionAddressLine1}
                      onChange={(e) => setCollectionAddressLine1(e.target.value)}
                      placeholder="e.g., 123 Industrial Road"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionAddressLine2">Address Line 2</Label>
                    <Input
                      id="collectionAddressLine2"
                      type="text"
                      value={collectionAddressLine2}
                      onChange={(e) => setCollectionAddressLine2(e.target.value)}
                      placeholder="e.g., Unit 5B"
                      disabled={saving}
                    />
                  </div>
                </div>

                <div className="grid gap-4 sm:grid-cols-3">
                  <div>
                    <Label htmlFor="collectionCity">City *</Label>
                    <Input
                      id="collectionCity"
                      type="text"
                      value={collectionCity}
                      onChange={(e) => setCollectionCity(e.target.value)}
                      placeholder="e.g., Pretoria"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionProvince">Province *</Label>
                    <Input
                      id="collectionProvince"
                      type="text"
                      value={collectionProvince}
                      onChange={(e) => setCollectionProvince(e.target.value)}
                      placeholder="e.g., Gauteng"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionPostalCode">Postal Code *</Label>
                    <Input
                      id="collectionPostalCode"
                      type="text"
                      value={collectionPostalCode}
                      onChange={(e) => setCollectionPostalCode(e.target.value)}
                      placeholder="e.g., 0001"
                      disabled={saving}
                    />
                  </div>
                </div>

                <div>
                  <Label htmlFor="collectionCountry">Country Code</Label>
                  <Input
                    id="collectionCountry"
                    type="text"
                    value={collectionCountry}
                    onChange={(e) => setCollectionCountry(e.target.value)}
                    placeholder="e.g., ZA"
                    disabled={saving}
                  />
                </div>
              </CardContent>
            </Card>

            {/* Collection Contact */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Collection Contact</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  Who the courier should contact for pickup
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <Label htmlFor="collectionContactName">Contact Name *</Label>
                    <Input
                      id="collectionContactName"
                      type="text"
                      value={collectionContactName}
                      onChange={(e) => setCollectionContactName(e.target.value)}
                      placeholder="e.g., John Warehouse Manager"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionContactPhone">Contact Phone *</Label>
                    <Input
                      id="collectionContactPhone"
                      type="tel"
                      value={collectionContactPhone}
                      onChange={(e) => setCollectionContactPhone(e.target.value)}
                      placeholder="e.g., +27123456789"
                      disabled={saving}
                    />
                  </div>
                </div>

                <div>
                  <Label htmlFor="collectionContactEmail">Contact Email</Label>
                  <Input
                    id="collectionContactEmail"
                    type="email"
                    value={collectionContactEmail}
                    onChange={(e) => setCollectionContactEmail(e.target.value)}
                    placeholder="e.g., warehouse@honeybee.co.za"
                    disabled={saving}
                  />
                </div>
              </CardContent>
            </Card>

            {/* Shiplogic Configuration */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Shiplogic Configuration</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  Provider and account settings for Shiplogic API
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="grid gap-4 sm:grid-cols-3">
                  <div>
                    <Label htmlFor="shiplogicProviderId">Provider ID *</Label>
                    <Input
                      id="shiplogicProviderId"
                      type="number"
                      value={shiplogicProviderId}
                      onChange={(e) => setShiplogicProviderId(parseInt(e.target.value) || 0)}
                      placeholder="e.g., 35"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="shiplogicAccountId">Account ID *</Label>
                    <Input
                      id="shiplogicAccountId"
                      type="number"
                      value={shiplogicAccountId}
                      onChange={(e) => setShiplogicAccountId(parseInt(e.target.value) || 0)}
                      placeholder="e.g., 624164"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="defaultServiceLevel">Default Service Level</Label>
                    <select
                      id="defaultServiceLevel"
                      value={defaultServiceLevel}
                      onChange={(e) => setDefaultServiceLevel(e.target.value)}
                      disabled={saving}
                      className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    >
                      <option value="ECO">ECO - Economy</option>
                      <option value="STD">STD - Standard</option>
                      <option value="ONX">ONX - Overnight Express</option>
                      <option value="EXSM">EXSM - Express Same Day</option>
                    </select>
                  </div>
                </div>

                <div className="rounded-md bg-blue-50 dark:bg-blue-900/20 p-4 text-sm text-blue-800 dark:text-blue-200">
                  <p className="font-medium">ℹ️ About Shiplogic IDs</p>
                  <p className="mt-1">
                    Provider ID and Account ID are specific to your Shiplogic account.
                    Contact Shaun or check your Shiplogic dashboard for these values.
                  </p>
                </div>
              </CardContent>
            </Card>

            {/* Special Trip Fallback */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Special Trip Fallback</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  When Shiplogic has no rate for an address, offer a distance-priced
                  &quot;Special Trip&quot; option at checkout instead of nothing
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="grid gap-4 sm:grid-cols-3">
                  <div>
                    <Label htmlFor="specialTripCostPerKm">Rate per km (R)</Label>
                    <Input
                      id="specialTripCostPerKm"
                      type="number"
                      step="0.01"
                      min="0"
                      value={specialTripCostPerKm}
                      onChange={(e) => setSpecialTripCostPerKm(e.target.value)}
                      placeholder="e.g., 9.00 (empty = disabled)"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="specialTripMinFee">Minimum fee (R)</Label>
                    <Input
                      id="specialTripMinFee"
                      type="number"
                      step="0.01"
                      min="0"
                      value={specialTripMinFee}
                      onChange={(e) => setSpecialTripMinFee(e.target.value)}
                      placeholder="e.g., 150.00"
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="specialTripMaxKm">Max distance (km)</Label>
                    <Input
                      id="specialTripMaxKm"
                      type="number"
                      step="1"
                      min="0"
                      value={specialTripMaxKm}
                      onChange={(e) => setSpecialTripMaxKm(e.target.value)}
                      placeholder="e.g., 200"
                      disabled={saving}
                    />
                  </div>
                </div>

                <div>
                  <Label htmlFor="googleMapsApiKey">Store&apos;s Google Maps API Key</Label>
                  <div className="relative">
                    <Input
                      id="googleMapsApiKey"
                      type={showGoogleMapsApiKey ? 'text' : 'password'}
                      value={googleMapsApiKey}
                      onChange={(e) => setGoogleMapsApiKey(e.target.value)}
                      placeholder={hasGoogleMapsApiKey ? '••••••••' : 'AIza...'}
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowGoogleMapsApiKey(!showGoogleMapsApiKey)}
                      className="absolute right-3 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700"
                      tabIndex={-1}
                    >
                      {showGoogleMapsApiKey ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                    </button>
                  </div>
                  {hasGoogleMapsApiKey ? (
                    <p className="mt-1 text-sm text-green-600 dark:text-green-400">
                      ✓ Currently configured — leave blank to keep the existing key
                    </p>
                  ) : (
                    <p className="mt-1 text-sm text-amber-600 dark:text-amber-400">
                      Not configured — special trips stay off until a key is added
                    </p>
                  )}
                </div>

                <div className="rounded-md bg-blue-50 dark:bg-blue-900/20 p-4 text-sm text-blue-800 dark:text-blue-200">
                  <p className="font-medium">ℹ️ About the Google Maps API key</p>
                  <p className="mt-1">
                    This must be the store&apos;s own key with the <strong>Distance Matrix API</strong> enabled
                    in Google Cloud Console — driving-distance lookups bill to their Google account.
                    Price = max(minimum fee, one-way driving km × rate per km). Beyond the max
                    distance, no rate is offered.
                  </p>
                </div>
              </CardContent>
            </Card>

            {/* Action Buttons */}
            <div className="flex gap-3">
              <Button type="submit" disabled={saving} className="flex-1">
                {saving ? 'Saving...' : 'Save Changes'}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => router.push(`/stores/${storeId}`)}
                disabled={saving}
              >
                Cancel
              </Button>
            </div>
          </form>
        </div>
      </div>
    </AppLayout>
  )
}