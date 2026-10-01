'use client'

import { useState, useEffect } from 'react'
import { useRouter } from 'next/navigation'
import { Shield, Users, Ticket, Key, UserCheck, UserX, RefreshCw, Truck, ShoppingBag, Package } from 'lucide-react'
import api from '@/lib/api'
import { AppLayout } from '@/components/layout/app-layout'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

interface User {
  userID: number
  email: string
  firstName: string
  lastName: string
  fullName: string
  role: string
  isActive: boolean
  tenantName: string
  lastLoginOn: string | null
  createdOn: string
}

interface Stats {
  totalUsers: number
  activeUsers: number
  inactiveUsers: number
  superAdmins: number
  admins: number
  operators: number
  inviteCodesTotal: number
  inviteCodesUsed: number
  inviteCodesActive: number
}

export default function SuperAdminPage() {
  const router = useRouter()
  const [users, setUsers] = useState<User[]>([])
  const [stats, setStats] = useState<Stats | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [searchQuery, setSearchQuery] = useState('')

  // Password reset modal state
  const [showResetModal, setShowResetModal] = useState(false)
  const [resetUserId, setResetUserId] = useState<number | null>(null)
  const [resetUserEmail, setResetUserEmail] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [isResetting, setIsResetting] = useState(false)

  // Role change state
  const [changingRoleId, setChangingRoleId] = useState<number | null>(null)

  useEffect(() => {
    // SuperAdmin gating is also enforced server-side; this is just to keep
    // the page from flashing data to non-SuperAdmins. The role comes from
    // the AuthProvider (Supabase app_metadata.app_role).
    fetchData()
  }, [router])

  const fetchData = async () => {
    try {
      setIsLoading(true)
      const [usersRes, statsRes] = await Promise.all([
        api.get('/users'),
        api.get('/superadmin/stats')
      ])
      setUsers(usersRes.data)
      setStats(statsRes.data)
    } catch (err: any) {
      if (err.response?.status === 401) {
        router.push('/login')
      } else if (err.response?.status === 403) {
        router.push('/dashboard')
      } else {
        setError('Failed to load data')
      }
    } finally {
      setIsLoading(false)
    }
  }

  const handleRoleChange = async (userId: number, newRole: string) => {
    setChangingRoleId(userId)
    setError('')
    setSuccess('')

    try {
      await api.put(`/superadmin/users/${userId}/role`, { role: newRole })
      setUsers(users.map(u => u.userID === userId ? { ...u, role: newRole } : u))
      setSuccess('Role updated successfully')
      fetchData() // Refresh stats
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to update role')
    } finally {
      setChangingRoleId(null)
    }
  }

  const handleToggleActive = async (userId: number, currentStatus: boolean) => {
    setError('')
    setSuccess('')

    try {
      await api.put(`/superadmin/users/${userId}/status`, { isActive: !currentStatus })
      setUsers(users.map(u => u.userID === userId ? { ...u, isActive: !currentStatus } : u))
      setSuccess(`User ${!currentStatus ? 'activated' : 'deactivated'} successfully`)
      fetchData() // Refresh stats
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to update status')
    }
  }

  const handleResetPassword = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!resetUserId || !newPassword) return

    setIsResetting(true)
    setError('')

    try {
      await api.put(`/superadmin/users/${resetUserId}/reset-password`, { newPassword })
      setSuccess(`Password reset successfully for ${resetUserEmail}`)
      setShowResetModal(false)
      setResetUserId(null)
      setResetUserEmail('')
      setNewPassword('')
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to reset password')
    } finally {
      setIsResetting(false)
    }
  }

  const openResetModal = (userId: number, email: string) => {
    setResetUserId(userId)
    setResetUserEmail(email)
    setNewPassword('')
    setShowResetModal(true)
  }

  const getRoleBadge = (role: string) => {
    switch (role) {
      case 'SuperAdmin':
        return <span className="px-2 py-1 text-xs rounded-full bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-300">{role}</span>
      case 'Admin':
        return <span className="px-2 py-1 text-xs rounded-full bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300">{role}</span>
      default:
        return <span className="px-2 py-1 text-xs rounded-full bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300">{role}</span>
    }
  }

  const filteredUsers = users.filter(user => {
    if (!searchQuery) return true
    const query = searchQuery.toLowerCase()
    return (
      user.fullName.toLowerCase().includes(query) ||
      user.email.toLowerCase().includes(query) ||
      user.role.toLowerCase().includes(query)
    )
  })

  if (isLoading) {
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
        <div className="mx-auto max-w-7xl">
          {/* Header */}
          <div className="mb-6 flex items-center gap-3">
            <Shield className="h-8 w-8 text-purple-600" />
            <div>
              <h1 className="text-3xl font-bold dark:text-white">Super Admin</h1>
              <p className="text-gray-600 dark:text-gray-400">System management and user control</p>
            </div>
          </div>

          {/* Messages */}
          {error && (
            <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg text-red-600 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400">
              {error}
            </div>
          )}
          {success && (
            <div className="mb-4 p-4 bg-green-50 border border-green-200 rounded-lg text-green-600 dark:bg-green-900/20 dark:border-green-800 dark:text-green-400">
              {success}
            </div>
          )}

          {/* Stats Cards */}
          {stats && (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
              <Card>
                <CardContent className="pt-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm text-gray-500 dark:text-gray-400">Total Users</p>
                      <p className="text-3xl font-bold dark:text-white">{stats.totalUsers}</p>
                    </div>
                    <Users className="h-10 w-10 text-blue-500 opacity-50" />
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    {stats.activeUsers} active, {stats.inactiveUsers} inactive
                  </div>
                </CardContent>
              </Card>

              <Card>
                <CardContent className="pt-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm text-gray-500 dark:text-gray-400">By Role</p>
                      <p className="text-3xl font-bold dark:text-white">{stats.superAdmins + stats.admins + stats.operators}</p>
                    </div>
                    <Shield className="h-10 w-10 text-purple-500 opacity-50" />
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    {stats.superAdmins} Super, {stats.admins} Admin, {stats.operators} Operator
                  </div>
                </CardContent>
              </Card>

              <Card>
                <CardContent className="pt-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm text-gray-500 dark:text-gray-400">Invite Codes</p>
                      <p className="text-3xl font-bold dark:text-white">{stats.inviteCodesTotal}</p>
                    </div>
                    <Ticket className="h-10 w-10 text-green-500 opacity-50" />
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    {stats.inviteCodesUsed} used, {stats.inviteCodesActive} active
                  </div>
                </CardContent>
              </Card>

              <Card className="cursor-pointer hover:shadow-md transition-shadow" onClick={() => router.push('/super-admin/shiplogic-webhook')}>
                <CardContent className="pt-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm text-gray-500 dark:text-gray-400">Integrations</p>
                      <p className="text-lg font-semibold text-indigo-600 dark:text-indigo-400">Shiplogic Webhook →</p>
                    </div>
                    <Truck className="h-10 w-10 text-indigo-500 opacity-50" />
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    URL + auth key for tracking updates
                  </div>
                </CardContent>
              </Card>

              <Card className="cursor-pointer hover:shadow-md transition-shadow" onClick={() => router.push('/super-admin/shopify-carrier')}>
                <CardContent className="pt-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm text-gray-500 dark:text-gray-400">Integrations</p>
                      <p className="text-lg font-semibold text-emerald-600 dark:text-emerald-400">Shopify Rates →</p>
                    </div>
                    <ShoppingBag className="h-10 w-10 text-emerald-500 opacity-50" />
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    Register checkout carrier service
                  </div>
                </CardContent>
              </Card>

              <Card className="cursor-pointer hover:shadow-md transition-shadow" onClick={() => router.push('/super-admin/plugin-management')}>
                <CardContent className="pt-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm text-gray-500 dark:text-gray-400">Plugins</p>
                      <p className="text-lg font-semibold text-blue-600 dark:text-blue-400">Plugin Management →</p>
                    </div>
                    <Package className="h-10 w-10 text-blue-500 opacity-50" />
                  </div>
                  <div className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                    Releases, licenses, rollouts &amp; analytics
                  </div>
                </CardContent>
              </Card>
            </div>
          )}

          {/* User Management */}
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <CardTitle className="flex items-center gap-2">
                  <Users className="h-5 w-5" />
                  User Management
                </CardTitle>
                <Button variant="outline" size="sm" onClick={fetchData}>
                  <RefreshCw className="h-4 w-4 mr-2" />
                  Refresh
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <div className="mb-4">
                <Input
                  type="text"
                  placeholder="Search users by name, email, or role..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="max-w-md"
                />
              </div>

              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead className="border-b dark:border-gray-700">
                    <tr>
                      <th className="pb-3 font-medium text-gray-600 dark:text-gray-400">User</th>
                      <th className="pb-3 font-medium text-gray-600 dark:text-gray-400">Role</th>
                      <th className="pb-3 font-medium text-gray-600 dark:text-gray-400">Status</th>
                      <th className="pb-3 font-medium text-gray-600 dark:text-gray-400">Last Login</th>
                      <th className="pb-3 font-medium text-gray-600 dark:text-gray-400">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredUsers.map((user) => (
                      <tr key={user.userID} className="border-b dark:border-gray-700 last:border-0">
                        <td className="py-3">
                          <div>
                            <p className="font-medium dark:text-white">{user.fullName}</p>
                            <p className="text-xs text-gray-500 dark:text-gray-400">{user.email}</p>
                          </div>
                        </td>
                        <td className="py-3">
                          <select
                            value={user.role}
                            onChange={(e) => handleRoleChange(user.userID, e.target.value)}
                            disabled={changingRoleId === user.userID}
                            className="text-sm border border-gray-300 dark:border-gray-600 rounded px-2 py-1 bg-white dark:bg-gray-800 dark:text-white"
                          >
                            <option value="Operator">Operator</option>
                            <option value="Admin">Admin</option>
                            <option value="SuperAdmin">SuperAdmin</option>
                          </select>
                        </td>
                        <td className="py-3">
                          <span className={`px-2 py-1 text-xs rounded-full ${
                            user.isActive 
                              ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300' 
                              : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300'
                          }`}>
                            {user.isActive ? 'Active' : 'Inactive'}
                          </span>
                        </td>
                        <td className="py-3 text-gray-600 dark:text-gray-400">
                          {user.lastLoginOn 
                            ? new Date(user.lastLoginOn).toLocaleDateString() 
                            : 'Never'}
                        </td>
                        <td className="py-3">
                          <div className="flex items-center gap-2">
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => handleToggleActive(user.userID, user.isActive)}
                              title={user.isActive ? 'Deactivate' : 'Activate'}
                            >
                              {user.isActive ? (
                                <UserX className="h-4 w-4 text-red-500" />
                              ) : (
                                <UserCheck className="h-4 w-4 text-green-500" />
                              )}
                            </Button>
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => openResetModal(user.userID, user.email)}
                              title="Reset Password"
                            >
                              <Key className="h-4 w-4" />
                            </Button>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </CardContent>
          </Card>

          {/* Password Reset Modal */}
          {showResetModal && (
            <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
              <Card className="w-full max-w-md mx-4">
                <CardHeader>
                  <CardTitle>Reset Password</CardTitle>
                </CardHeader>
                <CardContent>
                  <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
                    Reset password for: <strong>{resetUserEmail}</strong>
                  </p>
                  <form onSubmit={handleResetPassword}>
                    <div className="mb-4">
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                        New Password
                      </label>
                      <Input
                        type="password"
                        value={newPassword}
                        onChange={(e) => setNewPassword(e.target.value)}
                        placeholder="Enter new password"
                        required
                        minLength={8}
                      />
                      <p className="text-xs text-gray-500 mt-1">Minimum 8 characters</p>
                    </div>
                    <div className="flex gap-3">
                      <Button
                        type="button"
                        variant="outline"
                        onClick={() => setShowResetModal(false)}
                        className="flex-1"
                      >
                        Cancel
                      </Button>
                      <Button
                        type="submit"
                        disabled={isResetting || newPassword.length < 8}
                        className="flex-1"
                      >
                        {isResetting ? 'Resetting...' : 'Reset Password'}
                      </Button>
                    </div>
                  </form>
                </CardContent>
              </Card>
            </div>
          )}
        </div>
      </div>
    </AppLayout>
  )
}