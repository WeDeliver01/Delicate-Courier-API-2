'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { AppLayout } from '@/components/layout/app-layout'
import { RefreshButton } from '@/components/ui/refresh-button'

interface Tenant {
  tenantID: number
  tenantName: string
  contactEmail: string
  contactPhone: string
  isActive: boolean
  createdOn: string
  hasShiplogicToken: boolean
}

export default function TenantsPage() {
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [filteredTenants, setFilteredTenants] = useState<Tenant[]>([])
  const [searchQuery, setSearchQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const router = useRouter()

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchTenants()
  }, [router])

  useEffect(() => {
    if (searchQuery.trim() === '') {
      setFilteredTenants(tenants)
    } else {
      const query = searchQuery.toLowerCase()
      const filtered = tenants.filter(
        (tenant) =>
          tenant.tenantName.toLowerCase().includes(query) ||
          tenant.contactEmail.toLowerCase().includes(query) ||
          tenant.contactPhone.toLowerCase().includes(query)
      )
      setFilteredTenants(filtered)
    }
  }, [searchQuery, tenants])

  const fetchTenants = async () => {
    try {
      const response = await api.get('/tenants')
      setTenants(response.data)
      setFilteredTenants(response.data)
    } catch (error) {
      console.error('Error fetching tenants:', error)
    } finally {
      setLoading(false)
    }
  }

  const handleRowClick = (tenantId: number) => {
    router.push(`/tenants/${tenantId}`)
  }

  const handleEditClick = (e: React.MouseEvent, tenantId: number) => {
    e.stopPropagation()
    router.push(`/tenants/${tenantId}/edit`)
  }

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading tenants...</p>
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
              <h1 className="text-3xl font-bold">Tenants</h1>
              <p className="text-gray-600">
                Manage client companies using the platform
              </p>
            </div>
            <div className="flex items-center gap-2">
              <RefreshButton onRefresh={fetchTenants} />
              <Button onClick={() => router.push('/tenants/create')}>
                + Add Tenant
              </Button>
            </div>
          </div>

          <Card className="mb-6">
            <CardContent className="pt-6">
              <Input
                type="text"
                placeholder="Search by name, email, or phone..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="max-w-md"
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>All Tenants ({filteredTenants.length})</CardTitle>
            </CardHeader>
            <CardContent>
              {filteredTenants.length === 0 ? (
                <p className="text-center text-gray-500">
                  {searchQuery ? 'No tenants found matching your search' : 'No tenants yet'}
                </p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead className="border-b">
                      <tr>
                        <th className="pb-3 font-medium text-gray-600">Tenant Name</th>
                        <th className="pb-3 font-medium text-gray-600">Contact Email</th>
                        <th className="pb-3 font-medium text-gray-600">Contact Phone</th>
                        <th className="pb-3 font-medium text-gray-600">Shiplogic Token</th>
                        <th className="pb-3 font-medium text-gray-600">Status</th>
                        <th className="pb-3 font-medium text-gray-600">Created</th>
                        <th className="pb-3 font-medium text-gray-600">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {filteredTenants.map((tenant) => (
                        <tr
                          key={tenant.tenantID}
                          className="cursor-pointer border-b transition-colors hover:bg-gray-50 last:border-0"
                          onClick={() => handleRowClick(tenant.tenantID)}
                        >
                          <td className="py-3 font-medium">{tenant.tenantName}</td>
                          <td className="py-3">{tenant.contactEmail}</td>
                          <td className="py-3">{tenant.contactPhone}</td>
                          <td className="py-3">
                            <span
                              className={`rounded px-2 py-1 text-xs ${
                                tenant.hasShiplogicToken
                                  ? 'bg-green-100 text-green-800'
                                  : 'bg-red-100 text-red-800'
                              }`}
                            >
                              {tenant.hasShiplogicToken ? '✓ Configured' : '✗ Missing'}
                            </span>
                          </td>
                          <td className="py-3">
                            <span
                              className={`rounded-full px-2 py-1 text-xs ${
                                tenant.isActive
                                  ? 'bg-green-100 text-green-800'
                                  : 'bg-gray-100 text-gray-800'
                              }`}
                            >
                              {tenant.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </td>
                          <td className="py-3 text-gray-600">
                            {new Date(tenant.createdOn).toLocaleDateString()}
                          </td>
                          <td className="py-3">
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={(e) => handleEditClick(e, tenant.tenantID)}
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