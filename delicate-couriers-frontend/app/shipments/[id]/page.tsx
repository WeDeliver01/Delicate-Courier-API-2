'use client'

import { useEffect, useState } from 'react'
import { useRouter, useParams } from 'next/navigation'
import api from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'
import { formatDateTime, formatDateMaybeTime, isoTitle } from '@/lib/datetime'

interface ShipmentDetail {
  shipmentId: number
  orderId: number
  trackingNumber: string
  consignmentId: string
  courierName: string
  courierService: string
  status: string
  shippingCost: number
  estimatedDeliveryDate: string | null
  actualDeliveryDate: string | null
  requestedDeliveryDate: string | null
  requestedDeliveryTime: string | null
  requestedCollectionDate: string | null
  requestedCollectionTime: string | null
  occasion: string | null
  createdOn: string
  orderNumber: string
  customerName: string
  customerEmail: string
  customerPhone: string
  deliveryAddress: {
    line1: string
    line2: string | null
    city: string
    province: string | null
    postalCode: string
    country: string
  }
  tenantName: string
  storeName: string
  hasLabel: boolean
  labelUrl: string | null
  trackingEvents: Array<{
    eventDate: string
    status: string
    location: string | null
    description: string | null
  }>
}

export default function ShipmentDetailPage() {
  const [shipment, setShipment] = useState<ShipmentDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [resyncing, setResyncing] = useState(false)
  const [resyncMsg, setResyncMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const router = useRouter()
  const params = useParams()
  const shipmentId = params.id as string

  useEffect(() => {
    fetchShipmentDetail()
  }, [shipmentId, router])

  const fetchShipmentDetail = async () => {
    try {
      const response = await api.get(`/shipments/${shipmentId}`)
      setShipment(response.data)
    } catch (error: any) {
      if (error.response?.status === 404) {
        setError('Shipment not found')
      } else {
        setError('Failed to load shipment details')
      }
    } finally {
      setLoading(false)
    }
  }

  const handleDownloadLabel = () => {
    if (shipment?.labelUrl) {
      // labelUrl is an absolute path served by the backend (e.g. /api/...).
      // Use NEXT_PUBLIC_API_URL when set (frontend on different origin) but
      // fall back to same-origin so the Next.js proxy handles routing.
      const base = process.env.NEXT_PUBLIC_API_URL ?? ''
      window.open(`${base}${shipment.labelUrl}`, '_blank')
    }
  }

  const handleResyncStorefront = async () => {
    if (!shipment) return
    setResyncing(true)
    setResyncMsg(null)
    try {
      const res = await api.post(`/shipments/${shipment.shipmentId}/resync-storefront`)
      const path = res.data?.path as string | undefined
      const keysSent = res.data?.keysSent ?? res.data?.keyCount ?? 0
      const keysUpdated = res.data?.keysUpdated as number | null | undefined
      const wooOrder = res.data?.wooOrderId ?? ''
      let text: string
      if (path === 'plugin') {
        // The plugin route returns a real per-key updated count plus
        // skipped / preserved arrays. Show enough to point at the
        // specific failure mode when the metabox stays empty:
        //   - skipped → the platform sent a key the plugin doesn't recognise
        //   - preserved → the platform sent an empty value that the plugin
        //     refused to clobber over an existing real value (e.g. tracking
        //     number not yet populated on platform side)
        const skipped = (res.data?.skippedKeys as string[] | null) ?? []
        const preserved = (res.data?.preservedKeys as string[] | null) ?? []
        const extras: string[] = []
        if (skipped.length) extras.push(`skipped ${skipped.length}: ${skipped.join(', ')}`)
        if (preserved.length) extras.push(`preserved ${preserved.length}: ${preserved.join(', ')}`)
        if (res.data?.noteAdded) extras.push('order note added')
        const extrasText = extras.length ? ` — ${extras.join('; ')}` : ''
        text = `Plugin route: sent ${keysSent}, WP saved ${keysUpdated ?? 0} on order ${wooOrder}${extrasText}.`
      } else if (path === 'wc-rest') {
        // WC REST returns 200 without confirming per-key save. Be honest
        // about that — it's the difference between "it worked" and "it
        // returned 200" when the metabox still looks empty.
        text = `WC REST: sent ${keysSent} to order ${wooOrder} (HTTP ${res.data?.httpStatus ?? '?'}). WC does not confirm per-key save — install the v2.4.0 plugin if the metabox stays empty.`
      } else {
        text = res.data?.message ?? `Pushed ${keysSent} key(s) to order ${wooOrder}.`
      }
      setResyncMsg({ kind: 'ok', text })
    } catch (err: any) {
      const text =
        err?.response?.data?.message ??
        err?.message ??
        'Failed to resync shipment meta to WooCommerce.'
      setResyncMsg({ kind: 'err', text })
    } finally {
      setResyncing(false)
    }
  }

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-gray-50">
        <p className="text-lg">Loading shipment details...</p>
      </div>
    )
  }

  if (error || !shipment) {
    return (
  <AppLayout>
    <div className="p-8">
        <div className="flex flex-col items-center justify-center p-8">
          <p className="mb-4 text-lg text-red-600">{error || 'Shipment not found'}</p>
          <Button onClick={() => router.push('/dashboard')}>
            Back to Dashboard
          </Button>
        </div>
      </div>
  </AppLayout>
    )
  }

  return (
  <AppLayout>
    <div className="p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mx-auto max-w-5xl">
          {/* Header */}
          <div className="mb-6 flex items-center justify-between">
            <div>
              <Button
                variant="outline"
                onClick={() => router.push('/dashboard')}
                className="mb-2"
              >
                ← Back to Dashboard
              </Button>
              <h1 className="text-3xl font-bold">Shipment Details</h1>
              <p className="text-gray-600">Tracking: {shipment.trackingNumber}</p>
            </div>
            
            <div className="flex flex-col items-end gap-2">
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  onClick={handleResyncStorefront}
                  disabled={resyncing}
                  title="Re-push the _dcp_* shipment meta (tracking, courier, status, courier rate) to the merchant's WooCommerce order. Useful if the storefront metabox is stuck on 'Awaiting waybill'."
                >
                  {resyncing ? 'Resyncing…' : 'Resync to WooCommerce'}
                </Button>
                {shipment.hasLabel && (
                  <Button onClick={handleDownloadLabel}>Download Label</Button>
                )}
              </div>
              {resyncMsg && (
                <p
                  className={`text-sm ${
                    resyncMsg.kind === 'ok' ? 'text-green-700' : 'text-red-600'
                  }`}
                >
                  {resyncMsg.text}
                </p>
              )}
            </div>
          </div>

          <div className="grid gap-6 lg:grid-cols-2">
            {/* Shipment Info Card */}
            <Card>
              <CardHeader>
                <CardTitle>Shipment Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600">Status</p>
                  <span className={`inline-block rounded-full px-3 py-1 text-sm font-medium ${
                    shipment.status === 'Failed' ? 'bg-red-100 text-red-800' :
                    shipment.status === 'Delivered' ? 'bg-green-100 text-green-800' :
                    shipment.status === 'InTransit' ? 'bg-blue-100 text-blue-800' :
                    'bg-yellow-100 text-yellow-800'
                  }`}>
                    {shipment.status}
                  </span>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Tracking Number</p>
                  <p className="font-mono font-medium">{shipment.trackingNumber}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Consignment ID</p>
                  <p className="font-mono text-sm">{shipment.consignmentId}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Courier</p>
                  <p className="font-medium">{shipment.courierName}</p>
                  <p className="text-sm text-gray-500">{shipment.courierService}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Shipping Cost</p>
                  <p className="font-medium">R {shipment.shippingCost.toFixed(2)}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Created</p>
                  <p title={isoTitle(shipment.createdOn)}>{formatDateTime(shipment.createdOn)}</p>
                </div>

                {shipment.estimatedDeliveryDate && (
                  <div>
                    <p className="text-sm text-gray-600">Est. Delivery</p>
                    <p title={isoTitle(shipment.estimatedDeliveryDate)}>{formatDateMaybeTime(shipment.estimatedDeliveryDate)}</p>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* Customer Info Card */}
            <Card>
              <CardHeader>
                <CardTitle>Customer Information</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div>
                  <p className="text-sm text-gray-600">Order Number</p>
                  <p className="font-medium">{shipment.orderNumber}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Customer Name</p>
                  <p className="font-medium">{shipment.customerName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Email</p>
                  <p className="text-sm">{shipment.customerEmail}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Phone</p>
                  <p>{shipment.customerPhone}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Tenant</p>
                  <p className="font-medium">{shipment.tenantName}</p>
                </div>

                <div>
                  <p className="text-sm text-gray-600">Store</p>
                  <p>{shipment.storeName}</p>
                </div>
              </CardContent>
            </Card>

            {/* Schedule Card */}
            <Card>
              <CardHeader>
                <CardTitle>Delivery & Collection Schedule</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                {shipment.requestedDeliveryDate && (
                  <div>
                    <p className="text-sm text-gray-600">Delivery Date</p>
                    <p className="font-medium" title={isoTitle(shipment.requestedDeliveryDate)}>{formatDateMaybeTime(shipment.requestedDeliveryDate)}</p>
                  </div>
                )}
            
                {shipment.requestedDeliveryTime && (
                  <div>
                    <p className="text-sm text-gray-600">Delivery Time Slot</p>
                    <p className="font-medium">{shipment.requestedDeliveryTime}</p>
                  </div>
                )}
            
                {shipment.requestedCollectionDate && (
                  <div>
                    <p className="text-sm text-gray-600">Collection Date</p>
                    <p className="font-medium" title={isoTitle(shipment.requestedCollectionDate)}>{formatDateMaybeTime(shipment.requestedCollectionDate)}</p>
                  </div>
                )}
            
                {shipment.requestedCollectionTime && (
                  <div>
                    <p className="text-sm text-gray-600">Collection Time</p>
                    <p className="font-medium">{shipment.requestedCollectionTime}</p>
                  </div>
                )}
            
                {shipment.occasion && (
                  <div>
                    <p className="text-sm text-gray-600">Occasion</p>
                    <p className="font-medium">{shipment.occasion}</p>
                  </div>
                )}
            
                {!shipment.requestedDeliveryDate && !shipment.requestedDeliveryTime && !shipment.requestedCollectionDate && (
                  <p className="text-sm text-gray-500">No schedule information available</p>
                )}
              </CardContent>
            </Card>

            {/* Delivery Address Card */}
            <Card>
              <CardHeader>
                <CardTitle>Delivery Address</CardTitle>
              </CardHeader>
              <CardContent>
                <p>{shipment.deliveryAddress.line1}</p>
                {shipment.deliveryAddress.line2 && (
                  <p>{shipment.deliveryAddress.line2}</p>
                )}
                <p>{shipment.deliveryAddress.city}</p>
                {shipment.deliveryAddress.province && (
                  <p>{shipment.deliveryAddress.province}</p>
                )}
                <p>{shipment.deliveryAddress.postalCode}</p>
                <p>{shipment.deliveryAddress.country}</p>
              </CardContent>
            </Card>

            {/* Tracking Events Card */}
            <Card>
              <CardHeader>
                <CardTitle>Tracking History</CardTitle>
              </CardHeader>
              <CardContent>
                {shipment.trackingEvents.length === 0 ? (
                  <p className="text-sm text-gray-500">No tracking events yet</p>
                ) : (
                  <div className="space-y-4">
                    {shipment.trackingEvents.map((event, index) => (
                      <div key={index} className="border-l-2 border-blue-500 pl-4">
                        <p className="font-medium">{event.status}</p>
                        <p className="text-sm text-gray-600" title={isoTitle(event.eventDate)}>
                          {formatDateTime(event.eventDate)}
                        </p>
                        {event.location && (
                          <p className="text-sm text-gray-500">{event.location}</p>
                        )}
                        {event.description && (
                          <p className="text-sm">{event.description}</p>
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </CardContent>
            </Card>
          </div>
        </div>
      </div>
    </AppLayout>
  )
}