'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { AppLayout } from '@/components/layout/app-layout'
import { Eye, EyeOff } from 'lucide-react'

export default function CreateTenantPage() {
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')

  // Form fields
  const [tenantName, setTenantName] = useState('')
  const [contactEmail, setContactEmail] = useState('')
  const [contactPhone, setContactPhone] = useState('')
  const [shiplogicBearerToken, setShiplogicBearerToken] = useState('')

  // Show/hide toggle
  const [showToken, setShowToken] = useState(false)

  const router = useRouter()

  useEffect(() => {
    if (typeof window === 'undefined') return
    setLoading(false)
  }, [router])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    setSuccess('')

    try {
      const tenantData = {
        tenantName,
        contactEmail,
        contactPhone,
        shiplogicBearerToken: shiplogicBearerToken || null,
      }

      const response = await api.post('/tenants', tenantData)

      setSuccess('Tenant created successfully!')

      // Redirect to the new tenant detail page after 1.5 seconds
      setTimeout(() => {
        router.push(`/tenants/${response.data.tenantID}`)
      }, 1500)
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to create tenant'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading...</p>
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
              onClick={() => router.push('/tenants')}
              className="mb-2"
            >
              ← Back to Tenants
            </Button>
            <h1 className="text-3xl font-bold">Add New Tenant</h1>
            <p className="text-gray-600">Create a new client company</p>
          </div>

          {success && (
            <div className="mb-6 rounded-md bg-green-50 p-4 text-green-800">
              {success}
            </div>
          )}

          {error && (
            <div className="mb-6 rounded-md bg-red-50 p-4 text-red-800">
              {error}
            </div>
          )}

          <form onSubmit={handleSubmit}>
            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Tenant Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="tenantName">Tenant Name *</Label>
                  <Input
                    id="tenantName"
                    type="text"
                    value={tenantName}
                    onChange={(e) => setTenantName(e.target.value)}
                    placeholder="e.g., Honey Bee Bakery"
                    required
                    disabled={saving}
                  />
                </div>

                <div>
                  <Label htmlFor="contactEmail">Contact Email *</Label>
                  <Input
                    id="contactEmail"
                    type="email"
                    value={contactEmail}
                    onChange={(e) => setContactEmail(e.target.value)}
                    placeholder="contact@example.com"
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
                    placeholder="+27123456789"
                    required
                    disabled={saving}
                  />
                </div>
              </CardContent>
            </Card>

            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Shiplogic API Configuration</CardTitle>
                <p className="text-sm text-gray-600">
                  Optional - can be added later
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
                      placeholder="Enter bearer token (optional)"
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowToken(!showToken)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700"
                    >
                      {showToken ? (
                        <EyeOff className="h-5 w-5" />
                      ) : (
                        <Eye className="h-5 w-5" />
                      )}
                    </button>
                  </div>
                </div>

                <div className="rounded-md bg-blue-50 p-4 text-sm text-blue-800">
                  <p className="font-medium">ℹ️ About Shiplogic Token</p>
                  <p className="mt-1">
                    This token is used to create shipments via the Shiplogic API. 
                    Each tenant should have their own Shiplogic sub-account token.
                    You can add this later if not available now.
                  </p>
                </div>
              </CardContent>
            </Card>

            <div className="rounded-md bg-gray-50 p-4 text-sm text-gray-700 mb-6">
              <p className="font-medium">📋 What happens next?</p>
              <ul className="mt-2 list-inside list-disc space-y-1">
                <li>A unique API key will be auto-generated for this tenant</li>
                <li>The tenant will be set to Active by default</li>
                <li>You can add stores for this tenant after creation</li>
                <li>You can update the Shiplogic token anytime</li>
              </ul>
            </div>

            <div className="flex gap-3">
              <Button type="submit" disabled={saving} className="flex-1">
                {saving ? 'Creating Tenant...' : 'Create Tenant'}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => router.push('/tenants')}
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