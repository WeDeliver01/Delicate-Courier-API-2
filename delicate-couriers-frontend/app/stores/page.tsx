'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { AppLayout } from '@/components/layout/app-layout'
import { RefreshButton } from '@/components/ui/refresh-button'

interface Store {
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
}

export default function StoresPage() {
  const [stores, setStores] = useState<Store[]>([])
  const [filteredStores, setFilteredStores] = useState<Store[]>([])
  const [searchQuery, setSearchQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const router = useRouter()

  useEffect(() => {
    fetchStores()
  }, [router])

  useEffect(() => {
    if (searchQuery.trim() === '') {
      setFilteredStores(stores)
    } else {
      const query = searchQuery.toLowerCase()
      const filtered = stores.filter(
        (store) =>
          store.storeName.toLowerCase().includes(query) ||
          store.tenantName.toLowerCase().includes(query) ||
          store.wooCommerceURL.toLowerCase().includes(query)
      )
      setFilteredStores(filtered)
    }
  }, [searchQuery, stores])

  const fetchStores = async () => {
    try {
      const response = await api.get('/stores')
      setStores(response.data)
      setFilteredStores(response.data)
    } catch (error) {
      console.error('Error fetching stores:', error)
    } finally {
      setLoading(false)
    }
  }

  const handleRowClick = (storeId: number) => {
    router.push(`/stores/${storeId}`)
  }

  const handleEditClick = (e: React.MouseEvent, storeId: number) => {
    e.stopPropagation()
    router.push(`/stores/${storeId}/edit`)
  }

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-gray-50">
        <p className="text-lg">Loading stores...</p>
      </div>
    )
  }

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-7xl">
          <div className="mb-6 flex items-center justify-between">
            <div>
              <h1 className="text-3xl font-bold">Stores</h1>
              <p className="text-gray-600">
                Manage WooCommerce stores across all tenants
              </p>
            </div>
            <div className="flex items-center gap-2">
              <RefreshButton onRefresh={fetchStores} />
              <Button onClick={() => router.push('/stores/create')}>
                + Add Store
              </Button>
            </div>
          </div>

          <Card className="mb-6">
            <CardContent className="pt-6">
              <Input
                type="text"
                placeholder="Search by store name, tenant, or URL..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="max-w-md"
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>All Stores ({filteredStores.length})</CardTitle>
            </CardHeader>
            <CardContent>
              {filteredStores.length === 0 ? (
                <p className="text-center text-gray-500">
                  {searchQuery ? 'No stores found matching your search' : 'No stores yet'}
                </p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead className="border-b">
                      <tr>
                        <th className="pb-3 font-medium text-gray-600">Store Name</th>
                        <th className="pb-3 font-medium text-gray-600">Tenant</th>
                        <th className="pb-3 font-medium text-gray-600">WooCommerce URL</th>
                        <th className="pb-3 font-medium text-gray-600">Configuration</th>
                        <th className="pb-3 font-medium text-gray-600">Status</th>
                        <th className="pb-3 font-medium text-gray-600">Created</th>
                        <th className="pb-3 font-medium text-gray-600">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {filteredStores.map((store) => (
                        <tr
                          key={store.storeID}
                          className="cursor-pointer border-b transition-colors hover:bg-gray-50 last:border-0"
                          onClick={() => handleRowClick(store.storeID)}
                        >
                          <td className="py-3 font-medium">{store.storeName}</td>
                          <td className="py-3">{store.tenantName}</td>
                          <td className="py-3">
                            <a
                              href={store.wooCommerceURL}
                              target="_blank"
                              rel="noopener noreferrer"
                              className="text-blue-600 hover:underline"
                              onClick={(e) => e.stopPropagation()}
                            >
                              {store.wooCommerceURL}
                            </a>
                          </td>
                          <td className="py-3">
                            <div className="flex gap-1">
                              <span
                                className={`rounded px-2 py-1 text-xs ${
                                  store.hasWooConsumerKey
                                    ? 'bg-green-100 text-green-800'
                                    : 'bg-red-100 text-red-800'
                                }`}
                              >
                                {store.hasWooConsumerKey ? '✓ Keys' : '✗ Keys'}
                              </span>
                              <span
                                className={`rounded px-2 py-1 text-xs ${
                                  store.hasWebhookSecret
                                    ? 'bg-green-100 text-green-800'
                                    : 'bg-red-100 text-red-800'
                                }`}
                              >
                                {store.hasWebhookSecret ? '✓ Webhook' : '✗ Webhook'}
                              </span>
                            </div>
                          </td>
                          <td className="py-3">
                            <span
                              className={`rounded-full px-2 py-1 text-xs ${
                                store.isActive
                                  ? 'bg-green-100 text-green-800'
                                  : 'bg-gray-100 text-gray-800'
                              }`}
                            >
                              {store.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </td>
                          <td className="py-3 text-gray-600">
                            {new Date(store.createdOn).toLocaleDateString()}
                          </td>
                          <td className="py-3">
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={(e) => handleEditClick(e, store.storeID)}
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