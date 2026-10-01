'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { AppLayout } from '@/components/layout/app-layout'

interface User {
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
}

export default function UsersPage() {
  const [users, setUsers] = useState<User[]>([])
  const [filteredUsers, setFilteredUsers] = useState<User[]>([])
  const [searchQuery, setSearchQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const router = useRouter()

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchUsers()
  }, [router])

  useEffect(() => {
    if (searchQuery.trim() === '') {
      setFilteredUsers(users)
    } else {
      const query = searchQuery.toLowerCase()
      const filtered = users.filter(
        (user) =>
          user.fullName.toLowerCase().includes(query) ||
          user.email.toLowerCase().includes(query) ||
          user.tenantName.toLowerCase().includes(query) ||
          user.role.toLowerCase().includes(query)
      )
      setFilteredUsers(filtered)
    }
  }, [searchQuery, users])

  const fetchUsers = async () => {
    try {
      const response = await api.get('/users')
      setUsers(response.data)
      setFilteredUsers(response.data)
    } catch (error) {
      console.error('Error fetching users:', error)
    } finally {
      setLoading(false)
    }
  }

  const handleRowClick = (userId: number) => {
    router.push(`/users/${userId}`)
  }

  const handleEditClick = (e: React.MouseEvent, userId: number) => {
    e.stopPropagation()
    router.push(`/users/${userId}/edit`)
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
          <p className="text-lg">Loading users...</p>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-7xl">
          <div className="mb-6 flex items-center justify-between">
            <div>
              <h1 className="text-3xl font-bold">Users</h1>
              <p className="text-gray-600">
                Manage platform users and permissions
              </p>
            </div>
            <Button onClick={() => router.push('/users/create')}>
              + Add User
            </Button>
          </div>

          <Card className="mb-6">
            <CardContent className="pt-6">
              <Input
                type="text"
                placeholder="Search by name, email, tenant, or role..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="max-w-md"
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>All Users ({filteredUsers.length})</CardTitle>
            </CardHeader>
            <CardContent>
              {filteredUsers.length === 0 ? (
                <p className="text-center text-gray-500">
                  {searchQuery ? 'No users found matching your search' : 'No users yet'}
                </p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead className="border-b">
                      <tr>
                        <th className="pb-3 font-medium text-gray-600">Name</th>
                        <th className="pb-3 font-medium text-gray-600">Email</th>
                        <th className="pb-3 font-medium text-gray-600">Tenant</th>
                        <th className="pb-3 font-medium text-gray-600">Role</th>
                        <th className="pb-3 font-medium text-gray-600">Status</th>
                        <th className="pb-3 font-medium text-gray-600">Last Login</th>
                        <th className="pb-3 font-medium text-gray-600">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {filteredUsers.map((user) => (
                        <tr
                          key={user.userID}
                          className="cursor-pointer border-b transition-colors hover:bg-gray-50 last:border-0"
                          onClick={() => handleRowClick(user.userID)}
                        >
                          <td className="py-3 font-medium">{user.fullName}</td>
                          <td className="py-3">{user.email}</td>
                          <td className="py-3">{user.tenantName}</td>
                          <td className="py-3">
                            <span
                              className={`rounded px-2 py-1 text-xs font-medium ${getRoleBadgeColor(
                                user.role
                              )}`}
                            >
                              {user.role}
                            </span>
                          </td>
                          <td className="py-3">
                            <span
                              className={`rounded-full px-2 py-1 text-xs ${
                                user.isActive
                                  ? 'bg-green-100 text-green-800'
                                  : 'bg-gray-100 text-gray-800'
                              }`}
                            >
                              {user.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </td>
                          <td className="py-3 text-gray-600">
                            {user.lastLoginOn
                              ? new Date(user.lastLoginOn).toLocaleDateString()
                              : 'Never'}
                          </td>
                          <td className="py-3">
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={(e) => handleEditClick(e, user.userID)}
                            >
                              Edit
                            </Button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
    </AppLayout>
  )
}