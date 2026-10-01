'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { AppLayout } from '@/components/layout/app-layout'

interface UnshippedOrder {
  orderId: number | string
  orderNumber: string
  customerName: string
  tenantName: string
  storeName: string
  orderTotal: number
  createdOn: string
}

export default function CreateShipmentPage() {
  const [orders, setOrders] = useState<UnshippedOrder[]>([])
  const [selectedOrderId, setSelectedOrderId] = useState<string>('')
  const [manualOrderId, setManualOrderId] = useState<string>('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const router = useRouter()

  useEffect(() => {
    fetchUnshippedOrders()
  }, [router])

  const fetchUnshippedOrders = async () => {
    try {
      const response = await api.get('/shipments/orders/unshipped?limit=100')
      setOrders(response.data)
    } catch (error) {
      console.error('Error fetching orders:', error)
    }
  }

  const handleCreateFromDropdown = async () => {
    if (!selectedOrderId) {
      setError('Please select an order')
      return
    }

    await createShipment(selectedOrderId)
  }

  const handleCreateFromManualId = async () => {
    if (!manualOrderId.trim()) {
      setError('Please enter an order number')
      return
    }

    // Send the raw reference as the user typed it. The backend accepts
    // EITHER the internal OrderID, the WooCommerce order id, OR the
    // WooCommerce order number — so whichever one the user copied from
    // their store admin will work.
    await createShipment(manualOrderId.trim())
  }

  const createShipment = async (orderRef: string) => {
    setLoading(true)
    setError('')
    setSuccess('')

    try {
      const response = await api.post(`/shipments/create/${encodeURIComponent(orderRef)}`)

      if (response.data.success) {
        setSuccess(`Shipment created successfully! Tracking: ${response.data.trackingNumber}`)
        
        // Redirect to shipment detail after 2 seconds
        setTimeout(() => {
          router.push(`/shipments/${response.data.shipmentId}`)
        }, 2000)
      }
    } catch (error: any) {
      const message = error.response?.data?.message || 'Failed to create shipment'
      setError(message)
    } finally {
      setLoading(false)
    }
  }

  return (
  <AppLayout>
    <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-3xl">
          <div className="mb-6">
            <Button
              variant="outline"
              onClick={() => router.push('/dashboard')}
              className="mb-2"
            >
              ← Back to Dashboard
            </Button>
            <h1 className="text-3xl font-bold">Create Manual Shipment</h1>
            <p className="text-gray-600">Create a shipment for an existing order</p>
          </div>

          {/* Success Message */}
          {success && (
            <div className="mb-6 rounded-md bg-green-50 p-4 text-green-800">
              {success}
            </div>
          )}

          {/* Error Message */}
          {error && (
            <div className="mb-6 rounded-md bg-red-50 p-4 text-red-800">
              {error}
            </div>
          )}

          {/* Option 1: Select from Dropdown */}
          <Card className="mb-6">
            <CardHeader>
              <CardTitle>Option 1: Select from Available Orders</CardTitle>
              <p className="text-sm text-gray-600">
                Choose an order that doesn't have a shipment yet
              </p>
            </CardHeader>
            <CardContent className="space-y-4">
              {orders.length === 0 ? (
                <p className="text-sm text-gray-500">
                  No orders available. All orders already have shipments.
                </p>
              ) : (
                <>
                  <div>
                    <Label htmlFor="order-select">Select Order</Label>
                    <select
                      id="order-select"
                      value={selectedOrderId}
                      onChange={(e) => setSelectedOrderId(e.target.value)}
                      className="mt-1 w-full rounded-md border border-gray-300 p-2"
                      disabled={loading}
                    >
                      <option value="">-- Select an order --</option>
                      {orders.map((order) => (
                        <option key={order.orderId} value={order.orderId}>
                          Order #{order.orderNumber} - {order.customerName} - {order.tenantName} ({order.storeName}) - R{order.orderTotal.toFixed(2)}
                        </option>
                      ))}
                    </select>
                  </div>

                  <Button
                    onClick={handleCreateFromDropdown}
                    disabled={loading || !selectedOrderId}
                    className="w-full"
                  >
                    {loading ? 'Creating Shipment...' : 'Create Shipment from Selected Order'}
                  </Button>
                </>
              )}
            </CardContent>
          </Card>

          {/* Divider */}
          <div className="relative mb-6">
            <div className="absolute inset-0 flex items-center">
              <div className="w-full border-t border-gray-300"></div>
            </div>
            <div className="relative flex justify-center text-sm">
              <span className="bg-gray-50 px-2 text-gray-500">OR</span>
            </div>
          </div>

          {/* Option 2: Enter Order Number Manually */}
          <Card>
            <CardHeader>
              <CardTitle>Option 2: Enter Order Number Manually</CardTitle>
              <p className="text-sm text-gray-600">
                Type the WooCommerce order number you see in your store (e.g.{' '}
                <span className="font-mono">11980</span>). The internal database
                order id also works if you happen to have it.
              </p>
            </CardHeader>
            <CardContent className="space-y-4">
              <div>
                <Label htmlFor="manual-order-id">Order Number</Label>
                <Input
                  id="manual-order-id"
                  type="text"
                  inputMode="text"
                  placeholder="e.g. 11980"
                  value={manualOrderId}
                  onChange={(e) => setManualOrderId(e.target.value)}
                  disabled={loading}
                />
              </div>

              <Button
                onClick={handleCreateFromManualId}
                disabled={loading || !manualOrderId.trim()}
                className="w-full"
              >
                {loading ? 'Creating Shipment...' : 'Create Shipment from Order Number'}
              </Button>
            </CardContent>
          </Card>

          {/* Info Box */}
          <div className="mt-6 rounded-md bg-blue-50 p-4 text-sm text-blue-800">
            <p className="font-medium">Note:</p>
            <ul className="mt-2 list-inside list-disc space-y-1">
              <li>The order must exist in the system</li>
              <li>The order must not already have a shipment</li>
              <li>The store must have collection address configured</li>
              <li>The tenant must have Shiplogic token configured</li>
            </ul>
          </div>
        </div>
      </div>
    </AppLayout>
  )
}