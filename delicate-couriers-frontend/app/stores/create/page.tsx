'use client'

import { useEffect, useState } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import { Suspense } from 'react'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { AppLayout } from '@/components/layout/app-layout'
import { Stepper } from '@/components/ui/stepper'
import { Eye, EyeOff, ArrowLeft, ArrowRight, Check, Loader2 } from 'lucide-react'

interface Tenant {
  tenantID: number
  tenantName: string
}

const buildSteps = (platform: string) => [
  { id: 1, name: 'Basic Info' },
  { id: 2, name: platform === 'shopify' ? 'Shopify' : 'WooCommerce' },
  { id: 3, name: 'Collection Address' },
  { id: 4, name: 'Contact Details' },
  { id: 5, name: 'Shiplogic' },
  { id: 6, name: 'Review' },
]

function StoreWizardForm() {
  const router = useRouter()
  const searchParams = useSearchParams()
  const preselectedTenantId = searchParams.get('tenantId')

  const [currentStep, setCurrentStep] = useState(1)
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  // Show/hide toggles for sensitive fields
  const [showConsumerKey, setShowConsumerKey] = useState(false)
  const [showConsumerSecret, setShowConsumerSecret] = useState(false)
  const [showWebhookSecret, setShowWebhookSecret] = useState(false)
  const [showShopifyAccessToken, setShowShopifyAccessToken] = useState(false)
  const [showShopifyWebhookSecret, setShowShopifyWebhookSecret] = useState(false)
  const [showBearerToken, setShowBearerToken] = useState(false)

  // Form data
  const [formData, setFormData] = useState({
    // Step 1: Basic Info
    tenantID: preselectedTenantId || '',
    storeName: '',
    platform: 'woocommerce',
    wooCommerceURL: '',
    shopifyStoreUrl: '',
    // Step 2: WooCommerce API
    wooConsumerKey: '',
    wooConsumerSecret: '',
    webhookSecret: '',
    // Step 2: Shopify API
    shopifyAccessToken: '',
    shopifyWebhookSecret: '',
    // Step 3: Collection Address
    collectionAddressLine1: '',
    collectionAddressLine2: '',
    collectionCity: '',
    collectionProvince: '',
    collectionPostalCode: '',
    collectionCountry: 'South Africa',
    collectionSuburb: '',
    collectionLatitude: '',
    collectionLongitude: '',
    // Step 4: Contact Details
    collectionCompanyName: '',
    collectionContactName: '',
    collectionContactPhone: '',
    collectionContactEmail: '',
    // Step 5: Shiplogic
    shiplogicProviderId: '',
    shiplogicAccountId: '',
    shiplogicBearerToken: '',
    defaultServiceLevel: 'STD',
    collectionTimeFrom: '08:00',
    collectionTimeTo: '16:00',
    deliveryTimeFrom: '08:00',
    deliveryTimeTo: '17:00',
  })

  useEffect(() => {
    fetchTenants()
  }, [])

  useEffect(() => {
    if (preselectedTenantId) {
      setFormData(prev => ({ ...prev, tenantID: preselectedTenantId }))
    }
  }, [preselectedTenantId])

  const fetchTenants = async () => {
    try {
      const response = await api.get('/tenants')
      setTenants(response.data)
    } catch (error) {
      console.error('Error fetching tenants:', error)
      setError('Failed to load tenants')
    } finally {
      setLoading(false)
    }
  }

  const updateFormData = (field: string, value: string) => {
    setFormData(prev => ({ ...prev, [field]: value }))
  }

  const STEPS = buildSteps(formData.platform)

  const validateStep = (step: number): boolean => {
    setError('')
    switch (step) {
      case 1:
        if (!formData.tenantID) {
          setError('Please select a tenant')
          return false
        }
        if (!formData.storeName.trim()) {
          setError('Store name is required')
          return false
        }
        if (formData.platform === 'woocommerce') {
          if (!formData.wooCommerceURL.trim()) {
            setError('WooCommerce URL is required')
            return false
          }
        } else if (formData.platform === 'shopify') {
          if (!formData.shopifyStoreUrl.trim()) {
            setError('Shopify Store URL is required')
            return false
          }
        }
        return true
      case 2:
        if (formData.platform === 'woocommerce') {
          if (!formData.wooConsumerKey.trim()) {
            setError('Consumer Key is required')
            return false
          }
          if (!formData.wooConsumerSecret.trim()) {
            setError('Consumer Secret is required')
            return false
          }
          if (!formData.webhookSecret.trim()) {
            setError('Webhook Secret is required')
            return false
          }
        } else if (formData.platform === 'shopify') {
          if (!formData.shopifyAccessToken.trim()) {
            setError('Shopify Access Token is required')
            return false
          }
          if (!formData.shopifyWebhookSecret.trim()) {
            setError('Shopify Webhook Secret is required')
            return false
          }
        }
        return true
      case 3:
        if (!formData.collectionAddressLine1.trim()) {
          setError('Street address is required')
          return false
        }
        if (!formData.collectionCity.trim()) {
          setError('City is required')
          return false
        }
        if (!formData.collectionProvince.trim()) {
          setError('Province is required')
          return false
        }
        if (!formData.collectionPostalCode.trim()) {
          setError('Postal code is required')
          return false
        }
        return true
      case 4:
        if (!formData.collectionContactName.trim()) {
          setError('Contact name is required')
          return false
        }
        if (!formData.collectionContactPhone.trim()) {
          setError('Contact phone is required')
          return false
        }
        return true
      case 5:
        if (!formData.shiplogicProviderId) {
          setError('Shiplogic Provider ID is required')
          return false
        }
        if (!formData.shiplogicAccountId) {
          setError('Shiplogic Account ID is required')
          return false
        }
        return true
      default:
        return true
    }
  }

  const nextStep = () => {
    if (validateStep(currentStep)) {
      setCurrentStep(prev => Math.min(prev + 1, STEPS.length))
    }
  }

  const prevStep = () => {
    setCurrentStep(prev => Math.max(prev - 1, 1))
  }

  const handleSubmit = async () => {
    setSaving(true)
    setError('')

    try {
      const isShopify = formData.platform === 'shopify'
      const storeData = {
        tenantID: parseInt(formData.tenantID),
        storeName: formData.storeName,
        platform: formData.platform,
        wooCommerceURL: isShopify ? null : formData.wooCommerceURL,
        wooConsumerKey: isShopify ? null : formData.wooConsumerKey,
        wooConsumerSecret: isShopify ? null : formData.wooConsumerSecret,
        webhookSecret: isShopify ? null : formData.webhookSecret,
        shopifyStoreUrl: isShopify ? formData.shopifyStoreUrl : null,
        shopifyAccessToken: isShopify ? formData.shopifyAccessToken : null,
        shopifyWebhookSecret: isShopify ? formData.shopifyWebhookSecret : null,
        collectionAddressLine1: formData.collectionAddressLine1,
        collectionAddressLine2: formData.collectionAddressLine2 || null,
        collectionCity: formData.collectionCity,
        collectionProvince: formData.collectionProvince,
        collectionPostalCode: formData.collectionPostalCode,
        collectionCountry: formData.collectionCountry,
        collectionSuburb: formData.collectionSuburb || null,
        collectionLatitude: formData.collectionLatitude ? parseFloat(formData.collectionLatitude) : null,
        collectionLongitude: formData.collectionLongitude ? parseFloat(formData.collectionLongitude) : null,
        collectionCompanyName: formData.collectionCompanyName || null,
        collectionContactName: formData.collectionContactName,
        collectionContactPhone: formData.collectionContactPhone,
        collectionContactEmail: formData.collectionContactEmail || null,
        shiplogicProviderId: parseInt(formData.shiplogicProviderId),
        shiplogicAccountId: parseInt(formData.shiplogicAccountId),
        defaultServiceLevel: formData.defaultServiceLevel,
        collectionTimeFrom: formData.collectionTimeFrom,
        collectionTimeTo: formData.collectionTimeTo,
        deliveryTimeFrom: formData.deliveryTimeFrom,
        deliveryTimeTo: formData.deliveryTimeTo,
      }

      const response = await api.post('/stores', storeData)
      const newStoreId = response.data.storeID

      // Redirect to package types setup
      router.push(`/stores/${newStoreId}/package-types?setup=true`)
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to create store'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-blue-600" />
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950 min-h-screen">
        <div className="mx-auto max-w-4xl">
          {/* Header */}
          <div className="mb-6">
            <Button
              variant="outline"
              onClick={() => router.push('/stores')}
              className="mb-4"
            >
              <ArrowLeft className="w-4 h-4 mr-2" />
              Back to Stores
            </Button>
            <h1 className="text-3xl font-bold">Create New Store</h1>
            <p className="text-gray-600 dark:text-gray-400">
              Follow the steps to connect a WooCommerce store
            </p>
          </div>

          {/* Stepper */}
          <Stepper
            steps={STEPS}
            currentStep={currentStep}
            onStepClick={(step) => {
              if (step < currentStep) setCurrentStep(step)
            }}
          />

          {/* Error Message */}
          {error && (
            <div className="mb-6 p-4 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg">
              <p className="text-red-800 dark:text-red-200">{error}</p>
            </div>
          )}

          {/* Step Content */}
          <Card>
            <CardHeader>
              <CardTitle>
                Step {currentStep}:{' '}
                {currentStep === 2
                  ? formData.platform === 'shopify'
                    ? 'Shopify'
                    : 'WooCommerce'
                  : STEPS[currentStep - 1].name}
              </CardTitle>
            </CardHeader>
            <CardContent>
              {/* Step 1: Basic Info */}
              {currentStep === 1 && (
                <div className="space-y-4">
                  <div>
                    <Label htmlFor="tenantID">Tenant *</Label>
                    <select
                      id="tenantID"
                      value={formData.tenantID}
                      onChange={(e) => updateFormData('tenantID', e.target.value)}
                      className="mt-1 w-full rounded-md border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 p-2"
                      disabled={!!preselectedTenantId}
                    >
                      <option value="">-- Select a tenant --</option>
                      {tenants.map((tenant) => (
                        <option key={tenant.tenantID} value={tenant.tenantID}>
                          {tenant.tenantName}
                        </option>
                      ))}
                    </select>
                  </div>

                  <div>
                    <Label htmlFor="storeName">Store Name *</Label>
                    <Input
                      id="storeName"
                      value={formData.storeName}
                      onChange={(e) => updateFormData('storeName', e.target.value)}
                      placeholder="e.g., Honey Bee Bakery - Pretoria"
                    />
                  </div>

                  <div>
                    <Label htmlFor="platform">Platform *</Label>
                    <select
                      id="platform"
                      value={formData.platform}
                      onChange={(e) => updateFormData('platform', e.target.value)}
                      className="mt-1 w-full rounded-md border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 p-2"
                    >
                      <option value="woocommerce">WooCommerce</option>
                      <option value="shopify">Shopify</option>
                    </select>
                  </div>

                  {formData.platform === 'woocommerce' && (
                    <div>
                      <Label htmlFor="wooCommerceURL">WooCommerce URL *</Label>
                      <Input
                        id="wooCommerceURL"
                        type="url"
                        value={formData.wooCommerceURL}
                        onChange={(e) => updateFormData('wooCommerceURL', e.target.value)}
                        placeholder="https://your-store.com"
                      />
                    </div>
                  )}

                  {formData.platform === 'shopify' && (
                    <div>
                      <Label htmlFor="shopifyStoreUrl">Shopify Store URL *</Label>
                      <Input
                        id="shopifyStoreUrl"
                        type="url"
                        value={formData.shopifyStoreUrl}
                        onChange={(e) => updateFormData('shopifyStoreUrl', e.target.value)}
                        placeholder="https://your-store.myshopify.com"
                      />
                    </div>
                  )}
                </div>
              )}

              {/* Step 2: WooCommerce API */}
              {currentStep === 2 && formData.platform === 'woocommerce' && (
                <div className="space-y-4">
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Enter the API credentials from your WooCommerce store. 
                    Go to WooCommerce → Settings → Advanced → REST API to generate keys.
                  </p>

                  <div>
                    <Label htmlFor="wooConsumerKey">Consumer Key *</Label>
                    <div className="relative">
                      <Input
                        id="wooConsumerKey"
                        type={showConsumerKey ? 'text' : 'password'}
                        value={formData.wooConsumerKey}
                        onChange={(e) => updateFormData('wooConsumerKey', e.target.value)}
                        placeholder="ck_..."
                        className="pr-10"
                      />
                      <button
                        type="button"
                        onClick={() => setShowConsumerKey(!showConsumerKey)}
                        className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500"
                      >
                        {showConsumerKey ? <EyeOff size={18} /> : <Eye size={18} />}
                      </button>
                    </div>
                  </div>

                  <div>
                    <Label htmlFor="wooConsumerSecret">Consumer Secret *</Label>
                    <div className="relative">
                      <Input
                        id="wooConsumerSecret"
                        type={showConsumerSecret ? 'text' : 'password'}
                        value={formData.wooConsumerSecret}
                        onChange={(e) => updateFormData('wooConsumerSecret', e.target.value)}
                        placeholder="cs_..."
                        className="pr-10"
                      />
                      <button
                        type="button"
                        onClick={() => setShowConsumerSecret(!showConsumerSecret)}
                        className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500"
                      >
                        {showConsumerSecret ? <EyeOff size={18} /> : <Eye size={18} />}
                      </button>
                    </div>
                  </div>

                  <div>
                    <Label htmlFor="webhookSecret">Webhook Secret *</Label>
                    <div className="relative">
                      <Input
                        id="webhookSecret"
                        type={showWebhookSecret ? 'text' : 'password'}
                        value={formData.webhookSecret}
                        onChange={(e) => updateFormData('webhookSecret', e.target.value)}
                        placeholder="Enter a secure secret for webhook validation"
                        className="pr-10"
                      />
                      <button
                        type="button"
                        onClick={() => setShowWebhookSecret(!showWebhookSecret)}
                        className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500"
                      >
                        {showWebhookSecret ? <EyeOff size={18} /> : <Eye size={18} />}
                      </button>
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      This will be used to validate incoming webhooks from WooCommerce
                    </p>
                  </div>
                </div>
              )}

              {/* Step 2: Shopify API */}
              {currentStep === 2 && formData.platform === 'shopify' && (
                <div className="space-y-4">
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Enter the API credentials from your Shopify store. Create a
                    custom app in your Shopify admin (Settings → Apps and sales
                    channels → Develop apps) to generate an Admin API access token.
                  </p>

                  <div>
                    <Label htmlFor="shopifyAccessToken">Shopify Access Token *</Label>
                    <div className="relative">
                      <Input
                        id="shopifyAccessToken"
                        type={showShopifyAccessToken ? 'text' : 'password'}
                        value={formData.shopifyAccessToken}
                        onChange={(e) => updateFormData('shopifyAccessToken', e.target.value)}
                        placeholder="shpat_..."
                        className="pr-10"
                      />
                      <button
                        type="button"
                        onClick={() => setShowShopifyAccessToken(!showShopifyAccessToken)}
                        className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500"
                      >
                        {showShopifyAccessToken ? <EyeOff size={18} /> : <Eye size={18} />}
                      </button>
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      Admin API access token from your Shopify custom app.
                    </p>
                  </div>

                  <div>
                    <Label htmlFor="shopifyWebhookSecret">Shopify Webhook Secret *</Label>
                    <div className="relative">
                      <Input
                        id="shopifyWebhookSecret"
                        type={showShopifyWebhookSecret ? 'text' : 'password'}
                        value={formData.shopifyWebhookSecret}
                        onChange={(e) => updateFormData('shopifyWebhookSecret', e.target.value)}
                        placeholder="Enter the webhook signing secret"
                        className="pr-10"
                      />
                      <button
                        type="button"
                        onClick={() => setShowShopifyWebhookSecret(!showShopifyWebhookSecret)}
                        className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500"
                      >
                        {showShopifyWebhookSecret ? <EyeOff size={18} /> : <Eye size={18} />}
                      </button>
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      Used to validate the X-Shopify-Hmac-Sha256 signature on incoming webhooks.
                    </p>
                  </div>
                </div>
              )}

              {/* Step 3: Collection Address */}
              {currentStep === 3 && (
                <div className="space-y-4">
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Enter the pickup address where parcels will be collected from.
                  </p>

                  <div>
                    <Label htmlFor="collectionAddressLine1">Street Address *</Label>
                    <Input
                      id="collectionAddressLine1"
                      value={formData.collectionAddressLine1}
                      onChange={(e) => updateFormData('collectionAddressLine1', e.target.value)}
                      placeholder="e.g., 123 Main Road"
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionAddressLine2">Address Line 2</Label>
                    <Input
                      id="collectionAddressLine2"
                      value={formData.collectionAddressLine2}
                      onChange={(e) => updateFormData('collectionAddressLine2', e.target.value)}
                      placeholder="e.g., Unit 5, Business Park"
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionSuburb">Suburb</Label>
                    <Input
                      id="collectionSuburb"
                      value={formData.collectionSuburb}
                      onChange={(e) => updateFormData('collectionSuburb', e.target.value)}
                      placeholder="e.g., Menlyn"
                    />
                  </div>

                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <Label htmlFor="collectionCity">City *</Label>
                      <Input
                        id="collectionCity"
                        value={formData.collectionCity}
                        onChange={(e) => updateFormData('collectionCity', e.target.value)}
                        placeholder="e.g., Pretoria"
                      />
                    </div>
                    <div>
                      <Label htmlFor="collectionProvince">Province *</Label>
                      <select
                        id="collectionProvince"
                        value={formData.collectionProvince}
                        onChange={(e) => updateFormData('collectionProvince', e.target.value)}
                        className="mt-1 w-full rounded-md border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 p-2"
                      >
                        <option value="">-- Select --</option>
                        <option value="Gauteng">Gauteng</option>
                        <option value="Western Cape">Western Cape</option>
                        <option value="KwaZulu-Natal">KwaZulu-Natal</option>
                        <option value="Eastern Cape">Eastern Cape</option>
                        <option value="Free State">Free State</option>
                        <option value="Limpopo">Limpopo</option>
                        <option value="Mpumalanga">Mpumalanga</option>
                        <option value="North West">North West</option>
                        <option value="Northern Cape">Northern Cape</option>
                      </select>
                    </div>
                  </div>

                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <Label htmlFor="collectionPostalCode">Postal Code *</Label>
                      <Input
                        id="collectionPostalCode"
                        value={formData.collectionPostalCode}
                        onChange={(e) => updateFormData('collectionPostalCode', e.target.value)}
                        placeholder="e.g., 0181"
                      />
                    </div>
                    <div>
                      <Label htmlFor="collectionCountry">Country</Label>
                      <Input
                        id="collectionCountry"
                        value={formData.collectionCountry}
                        onChange={(e) => updateFormData('collectionCountry', e.target.value)}
                        disabled
                      />
                    </div>
                  </div>

                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <Label htmlFor="collectionLatitude">Latitude (optional)</Label>
                      <Input
                        id="collectionLatitude"
                        type="number"
                        step="any"
                        value={formData.collectionLatitude}
                        onChange={(e) => updateFormData('collectionLatitude', e.target.value)}
                        placeholder="e.g., -25.7479"
                      />
                    </div>
                    <div>
                      <Label htmlFor="collectionLongitude">Longitude (optional)</Label>
                      <Input
                        id="collectionLongitude"
                        type="number"
                        step="any"
                        value={formData.collectionLongitude}
                        onChange={(e) => updateFormData('collectionLongitude', e.target.value)}
                        placeholder="e.g., 28.2293"
                      />
                    </div>
                  </div>
                </div>
              )}

              {/* Step 4: Contact Details */}
              {currentStep === 4 && (
                <div className="space-y-4">
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Enter the contact details for the collection point.
                  </p>

                  <div>
                    <Label htmlFor="collectionCompanyName">Company Name</Label>
                    <Input
                      id="collectionCompanyName"
                      value={formData.collectionCompanyName}
                      onChange={(e) => updateFormData('collectionCompanyName', e.target.value)}
                      placeholder="e.g., Honey Bee Bakery"
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionContactName">Contact Name *</Label>
                    <Input
                      id="collectionContactName"
                      value={formData.collectionContactName}
                      onChange={(e) => updateFormData('collectionContactName', e.target.value)}
                      placeholder="e.g., John Smith"
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionContactPhone">Contact Phone *</Label>
                    <Input
                      id="collectionContactPhone"
                      value={formData.collectionContactPhone}
                      onChange={(e) => updateFormData('collectionContactPhone', e.target.value)}
                      placeholder="e.g., +27 82 123 4567"
                    />
                  </div>

                  <div>
                    <Label htmlFor="collectionContactEmail">Contact Email</Label>
                    <Input
                      id="collectionContactEmail"
                      type="email"
                      value={formData.collectionContactEmail}
                      onChange={(e) => updateFormData('collectionContactEmail', e.target.value)}
                      placeholder="e.g., contact@example.com"
                    />
                  </div>
                </div>
              )}

              {/* Step 5: Shiplogic Configuration */}
              {currentStep === 5 && (
                <div className="space-y-4">
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Enter the Shiplogic account details. Get these from your Shiplogic dashboard.
                  </p>

                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <Label htmlFor="shiplogicProviderId">Provider ID *</Label>
                      <Input
                        id="shiplogicProviderId"
                        type="number"
                        value={formData.shiplogicProviderId}
                        onChange={(e) => updateFormData('shiplogicProviderId', e.target.value)}
                        placeholder="e.g., 35"
                      />
                    </div>
                    <div>
                      <Label htmlFor="shiplogicAccountId">Account ID *</Label>
                      <Input
                        id="shiplogicAccountId"
                        type="number"
                        value={formData.shiplogicAccountId}
                        onChange={(e) => updateFormData('shiplogicAccountId', e.target.value)}
                        placeholder="e.g., 449679"
                      />
                    </div>
                  </div>

                  <div>
                    <Label htmlFor="defaultServiceLevel">Default Service Level</Label>
                    <select
                      id="defaultServiceLevel"
                      value={formData.defaultServiceLevel}
                      onChange={(e) => updateFormData('defaultServiceLevel', e.target.value)}
                      className="mt-1 w-full rounded-md border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-800 p-2"
                    >
                      <option value="ECO">Economy (ECO)</option>
                      <option value="STD">Standard (STD)</option>
                      <option value="EXP">Express (EXP)</option>
                    </select>
                  </div>

                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <Label htmlFor="collectionTimeFrom">Collection Time From</Label>
                      <Input
                        id="collectionTimeFrom"
                        type="time"
                        value={formData.collectionTimeFrom}
                        onChange={(e) => updateFormData('collectionTimeFrom', e.target.value)}
                      />
                    </div>
                    <div>
                      <Label htmlFor="collectionTimeTo">Collection Time To</Label>
                      <Input
                        id="collectionTimeTo"
                        type="time"
                        value={formData.collectionTimeTo}
                        onChange={(e) => updateFormData('collectionTimeTo', e.target.value)}
                      />
                    </div>
                  </div>

                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <Label htmlFor="deliveryTimeFrom">Delivery Time From</Label>
                      <Input
                        id="deliveryTimeFrom"
                        type="time"
                        value={formData.deliveryTimeFrom}
                        onChange={(e) => updateFormData('deliveryTimeFrom', e.target.value)}
                      />
                    </div>
                    <div>
                      <Label htmlFor="deliveryTimeTo">Delivery Time To</Label>
                      <Input
                        id="deliveryTimeTo"
                        type="time"
                        value={formData.deliveryTimeTo}
                        onChange={(e) => updateFormData('deliveryTimeTo', e.target.value)}
                      />
                    </div>
                  </div>
                </div>
              )}

              {/* Step 6: Review */}
              {currentStep === 6 && (
                <div className="space-y-6">
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Review your store configuration before creating.
                  </p>

                  <div className="grid gap-4 md:grid-cols-2">
                    <div className="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
                      <h3 className="font-semibold mb-2">Basic Info</h3>
                      <p className="text-sm"><span className="text-gray-500">Tenant:</span> {tenants.find(t => t.tenantID === parseInt(formData.tenantID))?.tenantName}</p>
                      <p className="text-sm"><span className="text-gray-500">Store:</span> {formData.storeName}</p>
                      <p className="text-sm"><span className="text-gray-500">URL:</span> {formData.wooCommerceURL}</p>
                    </div>

                    <div className="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
                      <h3 className="font-semibold mb-2">WooCommerce API</h3>
                      <p className="text-sm"><span className="text-gray-500">Consumer Key:</span> {formData.wooConsumerKey.substring(0, 10)}...</p>
                      <p className="text-sm"><span className="text-gray-500">Consumer Secret:</span> ••••••••</p>
                      <p className="text-sm"><span className="text-gray-500">Webhook Secret:</span> ••••••••</p>
                    </div>

                    <div className="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
                      <h3 className="font-semibold mb-2">Collection Address</h3>
                      <p className="text-sm">{formData.collectionAddressLine1}</p>
                      {formData.collectionAddressLine2 && <p className="text-sm">{formData.collectionAddressLine2}</p>}
                      <p className="text-sm">{formData.collectionSuburb}</p>
                      <p className="text-sm">{formData.collectionCity}, {formData.collectionProvince}</p>
                      <p className="text-sm">{formData.collectionPostalCode}, {formData.collectionCountry}</p>
                    </div>

                    <div className="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
                      <h3 className="font-semibold mb-2">Contact Details</h3>
                      <p className="text-sm"><span className="text-gray-500">Company:</span> {formData.collectionCompanyName || 'N/A'}</p>
                      <p className="text-sm"><span className="text-gray-500">Contact:</span> {formData.collectionContactName}</p>
                      <p className="text-sm"><span className="text-gray-500">Phone:</span> {formData.collectionContactPhone}</p>
                      <p className="text-sm"><span className="text-gray-500">Email:</span> {formData.collectionContactEmail || 'N/A'}</p>
                    </div>

                    <div className="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg md:col-span-2">
                      <h3 className="font-semibold mb-2">Shiplogic Configuration</h3>
                      <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
                        <p className="text-sm"><span className="text-gray-500">Provider ID:</span> {formData.shiplogicProviderId}</p>
                        <p className="text-sm"><span className="text-gray-500">Account ID:</span> {formData.shiplogicAccountId}</p>
                        <p className="text-sm"><span className="text-gray-500">Service Level:</span> {formData.defaultServiceLevel}</p>
                        <p className="text-sm"><span className="text-gray-500">Collection:</span> {formData.collectionTimeFrom} - {formData.collectionTimeTo}</p>
                      </div>
                    </div>
                  </div>
                </div>
              )}

              {/* Navigation Buttons */}
              <div className="flex justify-between mt-8 pt-6 border-t">
                <Button
                  variant="outline"
                  onClick={prevStep}
                  disabled={currentStep === 1}
                >
                  <ArrowLeft className="w-4 h-4 mr-2" />
                  Previous
                </Button>

                {currentStep < STEPS.length ? (
                  <Button onClick={nextStep}>
                    Next
                    <ArrowRight className="w-4 h-4 ml-2" />
                  </Button>
                ) : (
                  <Button onClick={handleSubmit} disabled={saving}>
                    {saving ? (
                      <>
                        <Loader2 className="w-4 h-4 mr-2 animate-spin" />
                        Creating Store...
                      </>
                    ) : (
                      <>
                        <Check className="w-4 h-4 mr-2" />
                        Create Store
                      </>
                    )}
                  </Button>
                )}
              </div>
            </CardContent>
          </Card>
        </div>
      </div>
    </AppLayout>
  )
}

export default function CreateStorePage() {
  return (
    <Suspense fallback={
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-blue-600" />
        </div>
      </AppLayout>
    }>
      <StoreWizardForm />
    </Suspense>
  )
}