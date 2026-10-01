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

interface Tenant {
  tenantID: number
  tenantName: string
}

export default function CreateUserPage() {
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')

  // Form fields
  const [tenantID, setTenantID] = useState<string>('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [firstName, setFirstName] = useState('')
  const [lastName, setLastName] = useState('')
  const [role, setRole] = useState('User')

  // Show/hide toggle
  const [showPassword, setShowPassword] = useState(false)

  const router = useRouter()

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchTenants()
  }, [router])

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

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError('')
    setSuccess('')

    if (!tenantID) {
      setError('Please select a tenant')
      setSaving(false)
      return
    }

    if (password.length < 6) {
      setError('Password must be at least 6 characters')
      setSaving(false)
      return
    }

    try {
      const userData = {
        tenantId: parseInt(tenantID),
        email,
        password,
        firstName,
        lastName,
        role,
      }

      // /api/admin/users provisions the user in Supabase Auth AND writes the
      // local profile row in one shot (with rollback on failure).
      const response = await api.post('/admin/users', userData)

      setSuccess('User created successfully!')

      setTimeout(() => {
        router.push(`/users/${response.data.userID}`)
      }, 1500)
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to create user'
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
              onClick={() => router.push('/users')}
              className="mb-2"
            >
              ← Back to Users
            </Button>
            <h1 className="text-3xl font-bold">Add New User</h1>
            <p className="text-gray-600">Create a new platform user</p>
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
                <CardTitle>User Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="tenantID">Tenant *</Label>
                  <select
                    id="tenantID"
                    value={tenantID}
                    onChange={(e) => setTenantID(e.target.value)}
                    required
                    disabled={saving}
                    className="mt-1 w-full rounded-md border border-gray-300 p-2"
                  >
                    <option value="">-- Select a tenant --</option>
                    {tenants.map((tenant) => (
                      <option key={tenant.tenantID} value={tenant.tenantID}>
                        {tenant.tenantName}
                      </option>
                    ))}
                  </select>
                  {tenants.length === 0 && (
                    <p className="mt-1 text-xs text-red-600">
                      No tenants available. Please create a tenant first.
                    </p>
                  )}
                </div>

                <div>
                  <Label htmlFor="email">Email *</Label>
                  <Input
                    id="email"
                    type="email"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    placeholder="user@example.com"
                    required
                    disabled={saving}
                  />
                </div>

                <div>
                  <Label htmlFor="firstName">First Name *</Label>
                  <Input
                    id="firstName"
                    type="text"
                    value={firstName}
                    onChange={(e) => setFirstName(e.target.value)}
                    placeholder="John"
                    required
                    disabled={saving}
                  />
                </div>

                <div>
                  <Label htmlFor="lastName">Last Name *</Label>
                  <Input
                    id="lastName"
                    type="text"
                    value={lastName}
                    onChange={(e) => setLastName(e.target.value)}
                    placeholder="Doe"
                    required
                    disabled={saving}
                  />
                </div>

                <div>
                  <Label htmlFor="role">Role *</Label>
                  <select
                    id="role"
                    value={role}
                    onChange={(e) => setRole(e.target.value)}
                    required
                    disabled={saving}
                    className="mt-1 w-full rounded-md border border-gray-300 p-2"
                  >
                    <option value="User">User</option>
                    <option value="Admin">Admin</option>
                    <option value="SuperAdmin">SuperAdmin</option>
                  </select>
                  <p className="mt-1 text-xs text-gray-500">
                    User = Basic access | Admin = Full tenant access | SuperAdmin = Platform access
                  </p>
                </div>
              </CardContent>
            </Card>

            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Password</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="password">Password *</Label>
                  <div className="relative">
                    <Input
                      id="password"
                      type={showPassword ? 'text' : 'password'}
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      placeholder="Minimum 6 characters"
                      required
                      disabled={saving}
                      className="pr-10"
                    />
                    <button
                      type="button"
                      onClick={() => setShowPassword(!showPassword)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-700"
                    >
                      {showPassword ? (
                        <EyeOff className="h-5 w-5" />
                      ) : (
                        <Eye className="h-5 w-5" />
                      )}
                    </button>
                  </div>
                  <p className="mt-1 text-xs text-gray-500">
                    Password must be at least 6 characters
                  </p>
                </div>
              </CardContent>
            </Card>

            <div className="rounded-md bg-gray-50 p-4 text-sm text-gray-700 mb-6">
              <p className="font-medium">What happens next?</p>
              <ul className="mt-2 list-inside list-disc space-y-1">
                <li>The user is created in Supabase Auth (handles the password securely).</li>
                <li>A local profile row is created with the selected role and tenant.</li>
                <li>The user can sign in immediately with the email + password.</li>
                <li>They can also reset their password from the sign-in screen if needed.</li>
              </ul>
            </div>

            <div className="flex gap-3">
              <Button type="submit" disabled={saving} className="flex-1">
                {saving ? 'Creating User...' : 'Create User'}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => router.push('/users')}
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