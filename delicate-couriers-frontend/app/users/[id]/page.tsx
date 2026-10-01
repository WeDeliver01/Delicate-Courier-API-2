'use client'

import { useEffect, useState } from 'react'
import { useRouter, useParams } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'

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
  lastLoginOn: string | null
  createdOn: string
  createdBy: string
  changedOn: string | null
  changedBy: string | null
}

export default function UserDetailPage() {
  const [user, setUser] = useState<UserDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [deactivating, setDeactivating] = useState(false)
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
      setUser(response.data)
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

  const handleDeactivate = async () => {
    if (!confirm('Are you sure you want to deactivate this user? They will no longer be able to log in.')) {
      return
    }

    setDeactivating(true)
    try {
      await api.delete(`/users/${userId}`)
      alert('User deactivated successfully!')
      fetchUserDetail()
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to deactivate user'
      alert(`Error: ${message}`)
    } finally {
      setDeactivating(false)
    }
  }

  const getRoleBadgeColor = (role: string) => {
    switch (role) {
      case 'SuperAdmin':
        return 'bg-purple-100 text-purple-800'
      case 'Admin':
        return 'bg-blue-100 text-blue-800'
      default:
        return 'bg-gray-100 text-gray-800'
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading user details...</p>
        </div>
      </AppLayout>
    )
  }

  if (error || !user) {
    return (
      <AppLayout>
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error || 'User not found'}</p>
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
        <div className="mx-auto max-w-5xl">
          <div className="mb-6 flex items-center justify-between">
            <div>
              <Button
                variant="outline"
                onClick={() => router.push('/users')}
                className="mb-2"
              >
                ← Back to Users
              </Button>
              <h1 className="text-3xl font-bold">{user.fullName}</h1>
              <p className="text-gray-600">{user.email}</p>
            </div>

            <div className="flex gap-2">
              <Button
                variant="outline"
                onClick={() => router.push(`/users/${userId}/edit`)}
              >
                Edit User
              </Button>

              {user.isActive && (
                <Button
                  variant="destructive"
                  onClick={handleDeactivate}
                  disabled={deactivating}
                >
                  {deactivating ? 'Deactivating...' : 'Deactivate User'}
                </Button>
              )}

              <span
                className={`flex items-center rounded-full px-4 py-2 text-sm font-medium ${
                  user.isActive
                    ? 'bg-green-100 text-green-800'
                    : 'bg-gray-100 text-gray-800'
                }`}
              >
                {user.isActive ? 'Active' : 'Inactive'}
              </span>
            </div>
          </div>

          <div className="grid gap-6 lg:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>User Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600">Full Name</p>
                  <p className="font-medium">{user.fullName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Email</p>
                  <p className="font-medium">{user.email}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">First Name</p>
                  <p>{user.firstName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Last Name</p>
                  <p>{user.lastName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Tenant</p>
                  <p className="font-medium">{user.tenantName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Role</p>
                  <span
                    className={`inline-block rounded px-2 py-1 text-sm font-medium ${getRoleBadgeColor(
                      user.role
                    )}`}
                  >
                    {user.role}
                  </span>
                </div>
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Activity & Audit</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600">Last Login</p>
                  <p className="font-medium">
                    {user.lastLoginOn
                      ? new Date(user.lastLoginOn).toLocaleString()
                      : 'Never logged in'}
                  </p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Created</p>
                  <p>{new Date(user.createdOn).toLocaleString()}</p>
                  <p className="text-xs text-gray-500">by {user.createdBy}</p>
                </div>

                {user.changedOn && (
                  <div>
                    <p className="text-sm text-gray-600">Last Modified</p>
                    <p>{new Date(user.changedOn).toLocaleString()}</p>
                    <p className="text-xs text-gray-500">by {user.changedBy}</p>
                  </div>
                )}

                <div>
                  <p className="text-sm text-gray-600">Status</p>
                  <p className="font-medium">
                    {user.isActive ? 'Active' : 'Inactive'}
                  </p>
                </div>
              </CardContent>
            </Card>

            <Card className="lg:col-span-2">
              <CardHeader>
                <CardTitle>Quick Actions</CardTitle>
              </CardHeader>
              <CardContent>
                <div className="grid gap-3 sm:grid-cols-2">
                  <Button
                    variant="outline"
                    onClick={() => router.push(`/users/${userId}/edit`)}
                    className="w-full"
                  >
                    Edit User Details
                  </Button>
                  <Button
                    variant="outline"
                    onClick={() => router.push(`/tenants/${user.tenantID}`)}
                    className="w-full"
                  >
                    View Tenant: {user.tenantName}
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