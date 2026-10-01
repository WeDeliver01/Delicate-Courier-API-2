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

interface UserDetail {
  userID: number
  tenantID: number
  tenantName: string
  email: string
  firstName: string
  lastName: string
  fullName: string
  role: string
  isActive: boolean
}

export default function UserEditPage() {
  const [user, setUser] = useState<UserDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')

  // Form fields
  const [email, setEmail] = useState('')
  const [firstName, setFirstName] = useState('')
  const [lastName, setLastName] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState('User')
  const [isActive, setIsActive] = useState(true)

  // Show/hide toggle
  const [showPassword, setShowPassword] = useState(false)

  const router = useRouter()
  const params = useParams()
  const userId = params.id as string

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchUserDetail()
  }, [userId, router])

  const fetchUserDetail = async () => {
    try {
      const response = await api.get(`/users/${userId}`)
      const userData = response.data

      setUser(userData)
      setEmail(userData.email)
      setFirstName(userData.firstName)
      setLastName(userData.lastName)
      setRole(userData.role)
      setIsActive(userData.isActive)
    } catch (error: any) {
      if (error.response?.status === 404) {
        setError('User not found')
      } else {
        setError('Failed to load user details')
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
        email,
        firstName,
        lastName,
        role,
        isActive,
      }

      // Only include password if it was entered
      if (password) {
        if (password.length < 6) {
          setError('Password must be at least 6 characters')
          setSaving(false)
          return
        }
        updateData.password = password
      }

      await api.put(`/users/${userId}`, updateData)

      setSuccess('User updated successfully!')

      setTimeout(() => {
        router.push(`/users/${userId}`)
      }, 1500)
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to update user'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading user...</p>
        </div>
      </AppLayout>
    )
  }

  if (error && !user) {
    return (
      <AppLayout>
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error}</p>
          <Button onClick={() => router.push('/users')}>
            Back to Users
          </Button>
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
              onClick={() => router.push(`/users/${userId}`)}
              className="mb-2"
            >
              ← Back to User Detail
            </Button>
            <h1 className="text-3xl font-bold">Edit User</h1>
            <p className="text-gray-600">{user?.fullName}</p>
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
                  <Label htmlFor="email">Email *</Label>
                  <Input
                    id="email"
                    type="email"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
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
                    User is Active
                  </Label>
                </div>
              </CardContent>
            </Card>

            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Change Password</CardTitle>
                <p className="text-sm text-gray-600">
                  Leave blank to keep current password
                </p>
              </CardHeader>
              <CardContent className="space-y-4">
                <div>
                  <Label htmlFor="password">New Password</Label>
                  <div className="relative">
                    <Input
                      id="password"
                      type={showPassword ? 'text' : 'password'}
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      placeholder="Enter new password (optional)"
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
                    Minimum 6 characters
                  </p>
                </div>
              </CardContent>
            </Card>

            <Card className="mb-6">
              <CardHeader>
                <CardTitle>Tenant Assignment (Read-Only)</CardTitle>
              </CardHeader>
              <CardContent>
                <div>
                  <Label>Tenant</Label>
                  <Input
                    type="text"
                    value={user?.tenantName || ''}
                    disabled
                  />
                  <p className="mt-1 text-xs text-gray-500">
                    Users cannot be moved between tenants
                  </p>
                </div>
              </CardContent>
            </Card>

            <div className="flex gap-3">
              <Button type="submit" disabled={saving} className="flex-1">
                {saving ? 'Saving...' : 'Save Changes'}
              </Button>
              <Button
                type="button"
                variant="outline"
                onClick={() => router.push(`/users/${userId}`)}
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