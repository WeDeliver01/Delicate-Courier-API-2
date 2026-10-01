'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'
import { RefreshButton } from '@/components/ui/refresh-button'
import {
  LineChart,
  Line,
  BarChart,
  Bar,
  PieChart,
  Pie,
  Cell,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from 'recharts'

interface ShipmentTimePoint {
  date: string
  count: number
}

interface TenantPerformance {
  tenantID: number
  tenantName: string
  totalShipments: number
  deliveredShipments: number
  failedShipments: number
  totalRevenue: number
}

interface StatusCount {
  status: string
  count: number
  percentage: number
}

interface SuccessRate {
  totalShipments: number
  successfulShipments: number
  failedShipments: number
  successRate: number
  failureRate: number
}

interface OrdersSummary {
  totalOrders: number
  totalOrderRevenue: number
  bookedShipments: number
  shippingRevenue: number
}

interface BookingHourPoint {
  hour: number
  count: number
}

const formatRand = (value: number) =>
  `R ${(value ?? 0).toLocaleString('en-ZA', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`

const formatHour = (hour: number) => `${hour.toString().padStart(2, '0')}:00`

export default function ReportsPage() {
  const [loading, setLoading] = useState(true)
  const [dateRange, setDateRange] = useState('30')

  const [shipmentsOverTime, setShipmentsOverTime] = useState<ShipmentTimePoint[]>([])
  const [tenantPerformance, setTenantPerformance] = useState<TenantPerformance[]>([])
  const [statusBreakdown, setStatusBreakdown] = useState<StatusCount[]>([])
  const [successRate, setSuccessRate] = useState<SuccessRate | null>(null)
  const [ordersSummary, setOrdersSummary] = useState<OrdersSummary | null>(null)
  const [bookingsByHour, setBookingsByHour] = useState<BookingHourPoint[]>([])

  const router = useRouter()

  useEffect(() => {
    if (typeof window === 'undefined') return
    fetchReports()
  }, [dateRange, router])

  const fetchReports = async (showLoader: boolean = true) => {
    if (showLoader) setLoading(true)
    try {
      const days = parseInt(dateRange)
      const to = new Date()
      const from = new Date()
      from.setDate(from.getDate() - days)

      const fromStr = from.toISOString().split('T')[0]
      const toStr = to.toISOString().split('T')[0]

      const [timeData, tenantData, statusData, successData, ordersData, bookingsData] = await Promise.all([
        api.get(`/reports/shipments-over-time?from=${fromStr}&to=${toStr}`),
        api.get(`/reports/tenant-performance?from=${fromStr}&to=${toStr}`),
        api.get(`/reports/status-breakdown?from=${fromStr}&to=${toStr}`),
        api.get(`/reports/success-rate?from=${fromStr}&to=${toStr}`),
        api.get(`/reports/orders-summary?from=${fromStr}&to=${toStr}`),
        api.get(`/reports/bookings-by-hour?from=${fromStr}&to=${toStr}`),
      ])

      setShipmentsOverTime(
        timeData.data.dataPoints.map((point: any) => ({
          date: new Date(point.date).toLocaleDateString(),
          count: point.count,
        }))
      )
      setTenantPerformance(tenantData.data.tenants)
      setStatusBreakdown(statusData.data.statuses)
      setSuccessRate(successData.data)
      setOrdersSummary(ordersData.data)
      setBookingsByHour(bookingsData.data.hours)
    } catch (error) {
      console.error('Error fetching reports:', error)
    } finally {
      if (showLoader) setLoading(false)
    }
  }

  const refreshReports = () => fetchReports(false)

  const COLORS = ['#10b981', '#3b82f6', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899']

  if (loading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading reports...</p>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="p-4 sm:p-6 lg:p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-7xl">
          {/* Header - stacks on mobile */}
          <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h1 className="text-2xl sm:text-3xl font-bold">Reports & Analytics</h1>
              <p className="text-sm sm:text-base text-gray-600">Performance metrics and insights</p>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <Button
                variant={dateRange === '7' ? 'default' : 'outline'}
                onClick={() => setDateRange('7')}
                size="sm"
              >
                7 Days
              </Button>
              <Button
                variant={dateRange === '30' ? 'default' : 'outline'}
                onClick={() => setDateRange('30')}
                size="sm"
              >
                30 Days
              </Button>
              <Button
                variant={dateRange === '90' ? 'default' : 'outline'}
                onClick={() => setDateRange('90')}
                size="sm"
              >
                90 Days
              </Button>
              <RefreshButton onRefresh={refreshReports} />
            </div>
          </div>

          {/* Orders & Revenue Cards - 2 cols mobile, 4 cols desktop */}
          {ordersSummary && (
            <div className="mb-6">
              <h2 className="mb-3 text-lg sm:text-xl font-semibold">Orders & Revenue</h2>
              <div className="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-4">
                <Card>
                  <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                    <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                      Total Orders
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                    <p className="text-2xl sm:text-3xl font-bold">{ordersSummary.totalOrders}</p>
                  </CardContent>
                </Card>

                <Card>
                  <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                    <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                      Total Order Revenue
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                    <p className="text-2xl sm:text-3xl font-bold text-green-600">
                      {formatRand(ordersSummary.totalOrderRevenue)}
                    </p>
                  </CardContent>
                </Card>

                <Card>
                  <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                    <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                      Booked Shipments
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                    <p className="text-2xl sm:text-3xl font-bold">{ordersSummary.bookedShipments}</p>
                  </CardContent>
                </Card>

                <Card>
                  <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                    <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                      Shipping Revenue
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                    <p className="text-2xl sm:text-3xl font-bold text-green-600">
                      {formatRand(ordersSummary.shippingRevenue)}
                    </p>
                  </CardContent>
                </Card>
              </div>
            </div>
          )}

          {/* Success Rate Cards - 2 cols mobile, 4 cols desktop */}
          {successRate && (
            <div className="mb-6 grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-4">
              <Card>
                <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                  <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                    Total Shipments
                  </CardTitle>
                </CardHeader>
                <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                  <p className="text-2xl sm:text-3xl font-bold">{successRate.totalShipments}</p>
                </CardContent>
              </Card>

              <Card>
                <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                  <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                    Successful Deliveries
                  </CardTitle>
                </CardHeader>
                <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                  <p className="text-2xl sm:text-3xl font-bold text-green-600">
                    {successRate.successfulShipments}
                  </p>
                </CardContent>
              </Card>

              <Card>
                <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                  <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                    Success Rate
                  </CardTitle>
                </CardHeader>
                <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                  <p className="text-2xl sm:text-3xl font-bold text-green-600">
                    {successRate.successRate}%
                  </p>
                </CardContent>
              </Card>

              <Card>
                <CardHeader className="pb-1 sm:pb-2 p-3 sm:p-6">
                  <CardTitle className="text-xs sm:text-sm font-medium text-gray-600">
                    Failed Shipments
                  </CardTitle>
                </CardHeader>
                <CardContent className="p-3 pt-0 sm:p-6 sm:pt-0">
                  <p className="text-2xl sm:text-3xl font-bold text-red-600">
                    {successRate.failedShipments}
                  </p>
                </CardContent>
              </Card>
            </div>
          )}

          {/* Charts - single col mobile, 2 col desktop */}
          <div className="grid gap-4 sm:gap-6 lg:grid-cols-2">
            {/* Shipments Over Time */}
            <Card>
              <CardHeader className="p-4 sm:p-6">
                <CardTitle className="text-base sm:text-lg">Shipments Over Time</CardTitle>
              </CardHeader>
              <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
                {shipmentsOverTime.length > 0 ? (
                  <ResponsiveContainer width="100%" height={250}>
                    <LineChart data={shipmentsOverTime}>
                      <CartesianGrid strokeDasharray="3 3" />
                      <XAxis dataKey="date" tick={{ fontSize: 11 }} />
                      <YAxis tick={{ fontSize: 11 }} />
                      <Tooltip />
                      <Legend wrapperStyle={{ fontSize: 12 }} />
                      <Line
                        type="monotone"
                        dataKey="count"
                        stroke="#3b82f6"
                        strokeWidth={2}
                        name="Shipments"
                      />
                    </LineChart>
                  </ResponsiveContainer>
                ) : (
                  <p className="text-center text-gray-500 py-8">No data available</p>
                )}
              </CardContent>
            </Card>

            {/* Status Breakdown */}
            <Card>
              <CardHeader className="p-4 sm:p-6">
                <CardTitle className="text-base sm:text-lg">Status Breakdown</CardTitle>
              </CardHeader>
              <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
                {statusBreakdown.length > 0 ? (
                  <>
                    <ResponsiveContainer width="100%" height={200}>
                      <PieChart>
                        <Pie
                          data={statusBreakdown as any}
                          dataKey="count"
                          nameKey="status"
                          cx="50%"
                          cy="50%"
                          outerRadius={70}
                          label={false}
                        >
                          {statusBreakdown.map((entry, index) => (
                            <Cell key={`cell-${index}`} fill={COLORS[index % COLORS.length]} />
                          ))}
                        </Pie>
                        <Tooltip />
                      </PieChart>
                    </ResponsiveContainer>
                    {/* Legend as list below chart for mobile readability */}
                    <div className="mt-3 space-y-1">
                      {statusBreakdown.map((entry, index) => (
                        <div key={entry.status} className="flex items-center justify-between text-sm">
                          <div className="flex items-center gap-2">
                            <span
                              className="inline-block h-3 w-3 rounded-full"
                              style={{ backgroundColor: COLORS[index % COLORS.length] }}
                            />
                            <span className="text-gray-700 dark:text-gray-300">{entry.status}</span>
                          </div>
                          <span className="text-gray-500 dark:text-gray-400">
                            {entry.count} ({entry.percentage.toFixed(1)}%)
                          </span>
                        </div>
                      ))}
                    </div>
                  </>
                ) : (
                  <p className="text-center text-gray-500 py-8">No data available</p>
                )}
              </CardContent>
            </Card>

            {/* Tenant Performance Chart */}
            <Card className="lg:col-span-2">
              <CardHeader className="p-4 sm:p-6">
                <CardTitle className="text-base sm:text-lg">Performance by Tenant</CardTitle>
              </CardHeader>
              <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
                {tenantPerformance.length > 0 ? (
                  <ResponsiveContainer width="100%" height={250}>
                    <BarChart data={tenantPerformance}>
                      <CartesianGrid strokeDasharray="3 3" />
                      <XAxis dataKey="tenantName" tick={{ fontSize: 11 }} />
                      <YAxis tick={{ fontSize: 11 }} />
                      <Tooltip />
                      <Legend wrapperStyle={{ fontSize: 12 }} />
                      <Bar dataKey="totalShipments" fill="#3b82f6" name="Total" />
                      <Bar dataKey="deliveredShipments" fill="#10b981" name="Delivered" />
                      <Bar dataKey="failedShipments" fill="#ef4444" name="Failed" />
                    </BarChart>
                  </ResponsiveContainer>
                ) : (
                  <p className="text-center text-gray-500 py-8">No data available</p>
                )}
              </CardContent>
            </Card>

            {/* Peak Booking Times */}
            <Card className="lg:col-span-2">
              <CardHeader className="p-4 sm:p-6">
                <CardTitle className="text-base sm:text-lg">Peak Booking Times</CardTitle>
                <p className="text-xs sm:text-sm text-gray-500">Shipment bookings by hour of day (SAST)</p>
              </CardHeader>
              <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
                {bookingsByHour.some((b) => b.count > 0) ? (
                  <ResponsiveContainer width="100%" height={250}>
                    <BarChart data={bookingsByHour}>
                      <CartesianGrid strokeDasharray="3 3" />
                      <XAxis
                        dataKey="hour"
                        tick={{ fontSize: 11 }}
                        tickFormatter={(h) => formatHour(h)}
                        interval={1}
                      />
                      <YAxis tick={{ fontSize: 11 }} allowDecimals={false} />
                      <Tooltip
                        labelFormatter={(h) => `${formatHour(h as number)} SAST`}
                        formatter={(value: any) => [value, 'Bookings']}
                      />
                      <Bar dataKey="count" fill="#8b5cf6" name="Bookings" />
                    </BarChart>
                  </ResponsiveContainer>
                ) : (
                  <p className="text-center text-gray-500 py-8">No data available</p>
                )}
              </CardContent>
            </Card>
          </div>

          {/* Tenant Performance Table */}
          {tenantPerformance.length > 0 && (
            <Card className="mt-4 sm:mt-6">
              <CardHeader className="p-4 sm:p-6">
                <CardTitle className="text-base sm:text-lg">Detailed Tenant Metrics</CardTitle>
              </CardHeader>
              <CardContent className="p-4 pt-0 sm:p-6 sm:pt-0">
                {/* Desktop table */}
                <div className="hidden sm:block overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead className="border-b">
                      <tr>
                        <th className="pb-3 font-medium text-gray-600">Tenant</th>
                        <th className="pb-3 font-medium text-gray-600">Total</th>
                        <th className="pb-3 font-medium text-gray-600">Delivered</th>
                        <th className="pb-3 font-medium text-gray-600">Failed</th>
                        <th className="pb-3 font-medium text-gray-600">Revenue</th>
                      </tr>
                    </thead>
                    <tbody>
                      {tenantPerformance.map((tenant) => (
                        <tr key={tenant.tenantID} className="border-b last:border-0">
                          <td className="py-3 font-medium">{tenant.tenantName}</td>
                          <td className="py-3">{tenant.totalShipments}</td>
                          <td className="py-3 text-green-600">{tenant.deliveredShipments}</td>
                          <td className="py-3 text-red-600">{tenant.failedShipments}</td>
                          <td className="py-3">R {tenant.totalRevenue.toFixed(2)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>

                {/* Mobile cards */}
                <div className="sm:hidden space-y-3">
                  {tenantPerformance.map((tenant) => (
                    <div
                      key={tenant.tenantID}
                      className="rounded-lg border border-gray-200 dark:border-gray-700 p-3"
                    >
                      <p className="font-medium text-sm text-gray-900 dark:text-white mb-2">{tenant.tenantName}</p>
                      <div className="grid grid-cols-2 gap-2 text-sm">
                        <div>
                          <span className="text-xs text-gray-500">Total</span>
                          <p className="font-medium">{tenant.totalShipments}</p>
                        </div>
                        <div>
                          <span className="text-xs text-gray-500">Revenue</span>
                          <p className="font-medium">R {tenant.totalRevenue.toFixed(2)}</p>
                        </div>
                        <div>
                          <span className="text-xs text-gray-500">Delivered</span>
                          <p className="font-medium text-green-600">{tenant.deliveredShipments}</p>
                        </div>
                        <div>
                          <span className="text-xs text-gray-500">Failed</span>
                          <p className="font-medium text-red-600">{tenant.failedShipments}</p>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </AppLayout>
  )
}