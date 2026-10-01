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

interface TenantDetail {
  tenantID: number
  tenantName: string
  contactEmail: string
  contactPhone: string
  tenantAPIKey: string
  shiplogicBearerToken: string | null
  isActive: boolean
  createdOn: string
  storeCount: number
  hasShiplogicToken: boolean
}

export default function TenantEditPage() {
  const [tenant, setTenant] = useState<TenantDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')

  // Form fields
  const [tenantName, setTenantName] = useState('')
  const [contactEmail, setContactEmail] = useState('')
  const [contactPhone, setContactPhone] = useState('')
  const [shiplogicBearerToken, setShiplogicBearerToken] = useState('')
  const [isActive, setIsActive] = useState(true)

  // Show/hide toggle
  const [showToken, setShowToken] = useState(false)

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
      const data = response.data

      setTenant(data)
      setTenantName(data.tenantName)
      setContactEmail(data.contactEmail)
      setContactPhone(data.contactPhone)
      setShiplogicBearerToken(data.shiplogicBearerToken || '')
      setIsActive(data.isActive)
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

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    setSuccess('')

    try {
      const updateData: any = {
        tenantName,
        contactEmail,
        contactPhone,
        isActive,
      }

      // Only include token if it was entered
      if (shiplogicBearerToken) {
        updateData.shiplogicBearerToken = shiplogicBearerToken
      }

      await api.put(`/tenants/${tenantId}`, updateData)

      setSuccess('Tenant updated successfully!')

      setTimeout(() => {
        router.push(`/tenants/${tenantId}`)
      }, 1500)
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to update tenant'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading tenant...</p>
        </div>
      </AppLayout>
    )
  }

  if (error && !tenant) {
    return (
      <AppLayout>
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error}</p>
          <Button onClick={() => router.push('/tenants')}>Back to Tenants</Button>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-3xl">
          <div className="mb-6">
            <Button
              variant="outline"
              onClick={() => router.push(`/tenants/${tenantId}`)}
              className="mb-2"
            >
              ← Back to Tenant Detail
            </Button>
            <h1 className="text-3xl font-bold">Edit Tenant</h1>
            <p className="text-gray-600 dark:text-gray-400">
              {tenant?.tenantName} • Tenant ID: {tenant?.tenantID}
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
            {/* Tenant Information */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Tenant Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <Label htmlFor="tenantId">Tenant ID</Label>
                    <Input
                      id="tenantId"
                      type="text"
                      value={tenant?.tenantID || ''}
                      disabled
                      className="font-mono bg-gray-100 dark:bg-gray-800"
                    />
                  </div>

                  <div>
                    <Label htmlFor="tenantName">Tenant Name *</Label>
                    <Input
                      id="tenantName"
                      type="text"
                      value={tenantName}
                      onChange={(e) => setTenantName(e.target.value)}
                      required
                      disabled={saving}
                    />
                  </div>
                </div>

                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <Label htmlFor="contactEmail">Contact Email *</Label>
                    <Input
                      id="contactEmail"
                      type="email"
                      value={contactEmail}
                      onChange={(e) => setContactEmail(e.target.value)}
                      required
                      disabled={saving}
                    />
                  </div>

                  <div>
                    <Label htmlFor="contactPhone">Contact Phone *</Label>
                    <Input
                      id="contactPhone"
                      type="tel"
                      value={contactPhone}
                      onChange={(e) => setContactPhone(e.target.value)}
                      required
                      disabled={saving}
                    />
                  </div>
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
                    Tenant is Active
                  </Label>
                </div>
              </CardContent>
            </Card>

            {/* Shiplogic API Configuration */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Shiplogic API Configuration</CardTitle>
                <p className="text-sm text-gray-600 dark:text-gray-400">
                  Leave blank to keep existing token
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="shiplogicBearerToken">Shiplogic Bearer Token</Label>
                  <div className="relative">
                    <Input
                      id="shiplogicBearerToken"
                      type={showToken ? 'text' : 'password'}
                      value={shiplogicBearerToken}
                      onChange={(e) => setShiplogicBearerToken(e.target.value)}
                      placeholder={tenant?.hasShiplogicToken ? '••••••••' : 'Enter bearer token'}
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowToken(!showToken)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700 dark:hover:text-gray-300"
                    >
                      {showToken ? <EyeOff className="h-5 w-5" /> : <Eye className="h-5 w-5" />}
                    </button>
                  </div>
                  {tenant?.hasShiplogicToken && (
                    <p className="mt-1 text-xs text-green-600 dark:text-green-400">✓ Currently configured</p>
                  )}
                </div>

                <div className="rounded-md bg-blue-50 dark:bg-blue-900/20 p-4 text-sm text-blue-800 dark:text-blue-200">
                  <p className="font-medium">ℹ️ About Shiplogic Token</p>
                  <p className="mt-1">
                    This token is used to create shipments via the Shiplogic API.
                    Each tenant should have their own Shiplogic sub-account token.
                  </p>
                </div>
              </CardContent>
            </Card>

            {/* API Key (Read-Only) */}
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>API Key (Read-Only)</CardTitle>
              </CardHeader>
              <CardContent>
                <div>
                  <Label>Tenant API Key</Label>
                  <div className="flex gap-2">
                    <Input
                      type="text"
                      value={tenant?.tenantAPIKey || ''}
                      disabled
                      className="font-mono text-sm bg-gray-100 dark:bg-gray-800"
                    />
                    <Button
                      type="button"
                      variant="outline"
                      onClick={() => {
                        navigator.clipboard.writeText(tenant?.tenantAPIKey || '')
                        alert('API Key copied to clipboard!')
                      }}
                    >
                      Copy
                    </Button>
                  </div>
                  <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                    This API key is auto-generated and cannot be changed
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
                onClick={() => router.push(`/tenants/${tenantId}`)}
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