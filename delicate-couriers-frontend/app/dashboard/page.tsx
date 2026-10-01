'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { AppLayout } from '@/components/layout/app-layout'
import { Button } from '@/components/ui/button'
import { RefreshButton } from '@/components/ui/refresh-button'

interface ShipmentStats {
  total: number
  pending: number
  inTransit: number
  delivered: number
  failed: number
}

interface Shipment {
  shipmentId: number
  orderId: number
  trackingNumber: string
  tenantName: string
  storeName: string
  courierName: string
  status: string
  createdOn: string
  orderNumber: string
}

export default function DashboardPage() {
  const [stats, setStats] = useState<ShipmentStats | null>(null)
  const [shipments, setShipments] = useState<Shipment[]>([])
  const [loading, setLoading] = useState(true)
  const [statusFilter, setStatusFilter] = useState<string>('all')
  const [searchQuery, setSearchQuery] = useState('')
  const router = useRouter()

  useEffect(() => {
    fetchDashboardData()

    // Auto-refresh every 5 minutes (300000ms)
    const interval = setInterval(() => {
      fetchDashboardData()
    }, 300000)

    return () => clearInterval(interval)
  }, [router])

  const fetchDashboardData = async () => {
    try {
      const statsResponse = await api.get('/shipments/stats?days=30')
      setStats(statsResponse.data)

      const shipmentsResponse = await api.get('/shipments?days=30')
      setShipments(shipmentsResponse.data.shipments)
    } catch (error) {
      console.error('Error fetching dashboard data:', error)
    } finally {
      setLoading(false)
    }
  }

  const handleRetryShipment = async (orderId: number) => {
    try {
      const response = await api.post(`/shipments/create/${orderId}`)

      if (response.data.success) {
        alert('Shipment created successfully!')
        fetchDashboardData()
      }
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to retry shipment'
      alert(`Error: ${message}`)
    }
  }

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'Failed':
        return 'bg-red-100 text-red-800'
      case 'Delivered':
        return 'bg-green-100 text-green-800'
      case 'InTransit':
        return 'bg-blue-100 text-blue-800'
      default:
        return 'bg-yellow-100 text-yellow-800'
    }
  }

  const filteredShipments = shipments.filter((shipment) => {
    if (statusFilter !== 'all' && shipment.status !== statusFilter) return false
    if (searchQuery && !shipment.trackingNumber.toLowerCase().includes(searchQuery.toLowerCase())) return false
    return true
  })

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
      <div className="p-4 sm:p-6 lg:p-8 bg-gray-50 dark:bg-gray-950">
        {/* Header - stacks on mobile */}
        <div className="mb-6 flex flex-col gap-3 sm:mb-8 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold sm:text-3xl">Dashboard</h1>
          <div className="flex items-center gap-2 w-full sm:w-auto">
            <RefreshButton onRefresh={fetchDashboardData} />
            <Button
              onClick={() => router.push('/shipments/create')}
              className="bg-blue-600 hover:bg-blue-700 flex-1 sm:flex-none"
            >
              + Create Manual Shipment
            </Button>
          </div>
        </div>

        {/* Stats Cards - 2 cols on mobile, 4 on desktop */}
        {stats && (
          <div className="mb-6 grid grid-cols-2 gap-3 sm:mb-8 sm:gap-4 lg:grid-cols-4">
            <Card>
              <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                  Total Shipments
                </CardTitle>
              </CardHeader>
              <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                <p className="text-2xl sm:text-3xl font-bold">{stats.total}</p>
                <p className="text-xs text-gray-500">Last 30 days</p>
              </CardContent>
            </Card>

            <Card>
              <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                  Pending Pickup
                </CardTitle>
              </CardHeader>
              <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                <p className="text-2xl sm:text-3xl font-bold text-yellow-600">{stats.pending}</p>
                <p className="text-xs text-gray-500">Awaiting collection</p>
              </CardContent>
            </Card>

            <Card>
              <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                  In Transit
                </CardTitle>
              </CardHeader>
              <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                <p className="text-2xl sm:text-3xl font-bold text-blue-600">{stats.inTransit}</p>
                <p className="text-xs text-gray-500">On the way</p>
              </CardContent>
            </Card>

            <Card className={stats.failed > 0 ? 'border-red-300 bg-red-50' : ''}>
              <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                  Failed
                </CardTitle>
              </CardHeader>
              <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                <p className={`text-2xl sm:text-3xl font-bold ${stats.failed > 0 ? 'text-red-600' : 'text-gray-900'}`}>
                  {stats.failed}
                </p>
                <p className="text-xs text-gray-500">
                  {stats.failed > 0 ? '⚠️ Needs attention' : 'All good!'}
                </p>
              </CardContent>
            </Card>
          </div>
        )}

        {/* Failed Shipments Alert */}
        {stats && stats.failed > 0 && (
          <Card className="mb-6 sm:mb-8 border-red-300 bg-red-50">
            <CardHeader className="p-4 sm:p-6">
              <CardTitle className="flex items-center text-red-800 text-base sm:text-lg">
                <span className="mr-2 text-xl sm:text-2xl">⚠️</span>
                Failed Shipments ({stats.failed})
              </CardTitle>
            </CardHeader>
            <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
              <p className="mb-4 text-sm text-red-700">
                These shipments failed to create in Shiplogic. Review and retry or create manually.
              </p>

              <div className="space-y-2">
                {shipments
                  .filter((s) => s.status === 'Failed')
                  .slice(0, 5)
                  .map((shipment) => (
                    <div
                      key={shipment.shipmentId}
                      className="flex flex-col gap-2 rounded-md bg-white p-3 sm:flex-row sm:items-center sm:justify-between"
                    >
                      <div className="flex-1">
                        <p className="font-medium">Order #{shipment.orderNumber}</p>
                        <p className="text-sm text-gray-600">
                          {shipment.tenantName} - {shipment.storeName}
                        </p>
                      </div>
                      <div className="flex gap-2">
                        <Button
                          size="sm"
                          variant="outline"
                          className="flex-1 sm:flex-none"
                          onClick={() => handleRetryShipment(shipment.orderId)}
                        >
                          Retry
                        </Button>
                        <Button
                          size="sm"
                          className="flex-1 sm:flex-none"
                          onClick={() => router.push('/shipments/create')}
                        >
                          Manual Create
                        </Button>
                      </div>
                    </div>
                  ))}
              </div>

              {stats.failed > 5 && (
                <Button
                  variant="link"
                  className="mt-4 text-red-800"
                  onClick={() => setStatusFilter('Failed')}
                >
                  View all {stats.failed} failed shipments →
                </Button>
              )}
            </CardContent>
          </Card>
        )}

        {/* Recent Shipments */}
        <Card>
          <CardHeader className="p-4 sm:p-6">
            <div className="flex flex-col gap-3">
              <CardTitle className="text-base sm:text-lg">Recent Shipments</CardTitle>

              <div className="flex flex-col gap-2 sm:flex-row">
                <select
                  value={statusFilter}
                  onChange={(e) => setStatusFilter(e.target.value)}
                  className="rounded-md border border-gray-300 px-3 py-2 text-sm w-full sm:w-auto"
                >
                  <option value="all">All Status</option>
                  <option value="Created">Created</option>
                  <option value="InTransit">In Transit</option>
                  <option value="Delivered">Delivered</option>
                  <option value="Failed">Failed</option>
                </select>

                <input
                  type="text"
                  placeholder="Search tracking..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="rounded-md border border-gray-300 px-3 py-2 text-sm w-full sm:w-auto"
                />
              </div>
            </div>
          </CardHeader>
          <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
            {filteredShipments.length === 0 ? (
              <p className="text-center text-gray-500 py-8">No shipments found</p>
            ) : (
              <>
                {/* Desktop Table - hidden on mobile */}
                <div className="hidden md:block overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead className="border-b">
                      <tr>
                        <th className="pb-3 font-medium text-gray-600">Tracking</th>
                        <th className="pb-3 font-medium text-gray-600">Tenant</th>
                        <th className="pb-3 font-medium text-gray-600">Store</th>
                        <th className="pb-3 font-medium text-gray-600">Courier</th>
                        <th className="pb-3 font-medium text-gray-600">Status</th>
                        <th className="pb-3 font-medium text-gray-600">Created</th>
                      </tr>
                    </thead>
                    <tbody>
                      {filteredShipments.map((shipment) => (
                        <tr
                          key={shipment.shipmentId}
                          onClick={() => router.push(`/shipments/${shipment.shipmentId}`)}
                          className="cursor-pointer border-b transition-colors hover:bg-gray-50 last:border-0"
                        >
                          <td className="py-3 font-mono text-xs">{shipment.trackingNumber}</td>
                          <td className="py-3">{shipment.tenantName}</td>
                          <td className="py-3">{shipment.storeName}</td>
                          <td className="py-3">{shipment.courierName}</td>
                          <td className="py-3">
                            <span className={`rounded-full px-2 py-1 text-xs ${getStatusColor(shipment.status)}`}>
                              {shipment.status}
                            </span>
                          </td>
                          <td className="py-3 text-gray-600">
                            {new Date(shipment.createdOn).toLocaleDateString()}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>

                {/* Mobile Card Layout */}
                <div className="md:hidden space-y-3">
                  {filteredShipments.map((shipment) => (
                    <div
                      key={shipment.shipmentId}
                      onClick={() => router.push(`/shipments/${shipment.shipmentId}`)}
                      className="cursor-pointer rounded-lg border bg-white p-3 transition-colors active:bg-gray-50 dark:bg-gray-900 dark:border-gray-700"
                    >
                      <div className="flex items-center justify-between mb-2">
                        <span className="font-mono text-sm font-medium">{shipment.trackingNumber}</span>
                        <span className={`rounded-full px-2 py-0.5 text-xs ${getStatusColor(shipment.status)}`}>
                          {shipment.status}
                        </span>
                      </div>
                      <div className="space-y-1 text-sm text-gray-600">
                        <div className="flex justify-between">
                          <span>{shipment.storeName}</span>
                          <span>{shipment.courierName}</span>
                        </div>
                        <div className="flex justify-between text-xs text-gray-400">
                          <span>{shipment.tenantName}</span>
                          <span>{new Date(shipment.createdOn).toLocaleDateString()}</span>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </>
            )}
          </CardContent>
        </Card>
      </div>
    </AppLayout>
  )
}