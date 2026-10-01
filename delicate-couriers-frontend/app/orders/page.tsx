'use client';

import { useState, useEffect } from 'react';
import React from 'react';
import { useRouter } from 'next/navigation';
import toast from 'react-hot-toast';
import api from '@/lib/api';
import { AppLayout } from '@/components/layout/app-layout';
import { RefreshButton } from '@/components/ui/refresh-button';
import { formatDateTime, isoTitle } from '@/lib/datetime';

interface LineItem {
  orderLineItemID: number;
  productName: string;
  sku: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
}

interface Order {
  orderID: number;
  wooOrderID: string;
  wooOrderNumber: string;
  customerName: string;
  customerEmail: string;
  customerPhone: string;
  orderTotal: number;
  orderStatus: string;
  orderDate: string;
  shippingAddressLine1: string;
  shippingAddressLine2: string;
  shippingCity: string;
  shippingProvince: string;
  shippingPostalCode: string;
  shippingCountry: string;
  fulfillmentType?: string | null;
  specialTripDistanceKm?: number | null;
  specialTripQuotedAmount?: number | null;
  storeName?: string;
  tenantName?: string;
  shipmentID?: number | null;
  shipmentStatus?: string | null;
  trackingNumber?: string | null;
  lineItems?: LineItem[];
}

export default function OrdersPage() {
  const [orders, setOrders] = useState<Order[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [expandedOrder, setExpandedOrder] = useState<number | null>(null);
  const [bookingOrderId, setBookingOrderId] = useState<number | null>(null);
  const router = useRouter();

  useEffect(() => {
    fetchOrders();
  }, [router]);

  const fetchOrders = async (showLoader: boolean = true) => {
    try {
      if (showLoader) setLoading(true);
      const response = await api.get('/orders');
      setOrders(response.data);
      setError('');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to fetch orders');
      console.error('Error fetching orders:', err);
    } finally {
      if (showLoader) setLoading(false);
    }
  };

  const refreshOrders = () => fetchOrders(false);

  const bookShipment = async (order: Order) => {
    if (bookingOrderId !== null) return;
    setBookingOrderId(order.orderID);
    try {
      const response = await api.post(`/shipments/create/${order.orderID}`);
      const trackingNumber = response.data?.trackingNumber;
      toast.success(
        trackingNumber
          ? `Shipment booked for #${order.wooOrderNumber} — tracking ${trackingNumber}`
          : `Shipment booked for #${order.wooOrderNumber}`,
        { duration: 6000 }
      );
      await fetchOrders(false);
    } catch (err: any) {
      const message =
        err.response?.data?.message ||
        err.response?.data?.errorMessage ||
        'Failed to book shipment';
      toast.error(`#${order.wooOrderNumber}: ${message}`, { duration: 8000 });
    } finally {
      setBookingOrderId(null);
    }
  };

  const isSpecialTrip = (order: Order) =>
    (order.fulfillmentType || '').toLowerCase() === 'special_trip';

  const canBookShipment = (order: Order) =>
    !order.shipmentID &&
    !isSpecialTrip(order) &&
    (order.fulfillmentType || '').toLowerCase() !== 'collect';

  const specialTripLabel = (order: Order) => {
    const km = order.specialTripDistanceKm != null ? `${order.specialTripDistanceKm} km` : null;
    const amt = order.specialTripQuotedAmount != null ? `R ${order.specialTripQuotedAmount.toFixed(2)}` : null;
    const detail = [amt, km].filter(Boolean).join(' · ');
    return detail ? `Special trip — book manually (${detail})` : 'Special trip — book manually';
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'processing':
        return 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200';
      case 'completed':
        return 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200';
      default:
        return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300';
    }
  };

  if (loading) {
    return (
      <AppLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-gray-600 dark:text-gray-400">Loading orders...</div>
        </div>
      </AppLayout>
    );
  }

  return (
    <AppLayout>
      <div className="p-4 sm:p-6 lg:p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mb-6 sm:mb-8 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 dark:text-white mb-1">Orders</h1>
            <p className="text-sm sm:text-base text-gray-600 dark:text-gray-400">View all WooCommerce orders and their shipment status</p>
          </div>
          <RefreshButton onRefresh={refreshOrders} />
        </div>

        {error && (
          <div className="mb-4 sm:mb-6 p-3 sm:p-4 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg">
            <p className="text-sm text-red-800 dark:text-red-200">{error}</p>
          </div>
        )}

        <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
          {orders.length === 0 ? (
            <div className="p-8 text-center text-gray-500 dark:text-gray-400">
              No orders found
            </div>
          ) : (
            <>
              {/* Desktop Table */}
              <div className="hidden lg:block overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                  <thead className="bg-gray-50 dark:bg-gray-900">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Order #</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Store / Tenant</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Customer</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Shipping Address</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Total</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Date</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                    {orders.map((order) => (
                      <React.Fragment key={order.orderID}>
                        <tr className="hover:bg-gray-50 dark:hover:bg-gray-700">
                          <td className="px-6 py-4 whitespace-nowrap">
                            <div className="text-sm font-medium text-gray-900 dark:text-white">#{order.wooOrderNumber}</div>
                            <div className="text-xs text-gray-500 dark:text-gray-400">WooID: {order.wooOrderID}</div>
                          </td>
                          <td className="px-6 py-4">
                            <div className="text-sm text-gray-900 dark:text-white">{order.storeName || 'N/A'}</div>
                            <div className="text-xs text-gray-500 dark:text-gray-400">{order.tenantName || 'N/A'}</div>
                          </td>
                          <td className="px-6 py-4">
                            <div className="text-sm text-gray-900 dark:text-white">{order.customerName}</div>
                            <div className="text-xs text-gray-500 dark:text-gray-400">{order.customerEmail}</div>
                            <div className="text-xs text-gray-500 dark:text-gray-400">{order.customerPhone}</div>
                          </td>
                          <td className="px-6 py-4">
                            <div className="text-sm text-gray-900 dark:text-white">
                              {order.shippingAddressLine1}
                              {order.shippingAddressLine2 && `, ${order.shippingAddressLine2}`}
                            </div>
                            <div className="text-xs text-gray-500 dark:text-gray-400">
                              {order.shippingCity}, {order.shippingProvince} {order.shippingPostalCode}
                            </div>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <div className="text-sm font-medium text-gray-900 dark:text-white">R {order.orderTotal.toFixed(2)}</div>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <span className={`px-2 inline-flex text-xs leading-5 font-semibold rounded-full ${getStatusColor(order.orderStatus)}`}>
                              {order.orderStatus}
                            </span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400" title={isoTitle(order.orderDate)}>
                            {formatDateTime(order.orderDate)}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm">
                            <div className="flex flex-col gap-1.5">
                              <button
                                onClick={() => setExpandedOrder(expandedOrder === order.orderID ? null : order.orderID)}
                                className="text-left text-blue-600 hover:text-blue-800 dark:text-blue-400 dark:hover:text-blue-300"
                              >
                                {expandedOrder === order.orderID ? 'Hide Items' : 'View Items'}
                              </button>
                              {order.shipmentID ? (
                                <span className="text-xs text-green-700 dark:text-green-400">
                                  {order.shipmentStatus ? `Shipment: ${order.shipmentStatus}` : 'Shipment booked'}
                                  {order.trackingNumber ? ` · ${order.trackingNumber}` : ''}
                                </span>
                              ) : canBookShipment(order) ? (
                                <button
                                  onClick={() => bookShipment(order)}
                                  disabled={bookingOrderId !== null}
                                  className="inline-flex w-fit items-center px-2.5 py-1 rounded-md text-xs font-medium bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
                                >
                                  {bookingOrderId === order.orderID ? 'Booking…' : 'Book Shipment'}
                                </button>
                              ) : isSpecialTrip(order) ? (
                                <span className="inline-flex w-fit items-center px-2 py-0.5 rounded-md text-xs font-medium bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200">
                                  {specialTripLabel(order)}
                                </span>
                              ) : (
                                <span className="text-xs text-gray-400 dark:text-gray-500">Collection order</span>
                              )}
                            </div>
                          </td>
                        </tr>
                        {expandedOrder === order.orderID && order.lineItems && (
                          <tr>
                            <td colSpan={8} className="px-6 py-4 bg-gray-50 dark:bg-gray-900">
                              <div className="text-sm font-semibold mb-2 text-gray-900 dark:text-white">Order Items:</div>
                              <table className="min-w-full">
                                <thead>
                                  <tr className="text-xs text-gray-500 dark:text-gray-400">
                                    <th className="text-left pb-2">Product</th>
                                    <th className="text-left pb-2">SKU</th>
                                    <th className="text-right pb-2">Qty</th>
                                    <th className="text-right pb-2">Unit Price</th>
                                    <th className="text-right pb-2">Total</th>
                                  </tr>
                                </thead>
                                <tbody>
                                  {order.lineItems.map((item) => (
                                    <tr key={item.orderLineItemID} className="text-sm">
                                      <td className="py-1 text-gray-900 dark:text-white">{item.productName}</td>
                                      <td className="py-1 text-gray-600 dark:text-gray-400">{item.sku}</td>
                                      <td className="py-1 text-right text-gray-900 dark:text-white">{item.quantity}</td>
                                      <td className="py-1 text-right text-gray-900 dark:text-white">R {item.unitPrice.toFixed(2)}</td>
                                      <td className="py-1 text-right font-medium text-gray-900 dark:text-white">R {item.lineTotal.toFixed(2)}</td>
                                    </tr>
                                  ))}
                                </tbody>
                              </table>
                            </td>
                          </tr>
                        )}
                      </React.Fragment>
                    ))}
                  </tbody>
                </table>
              </div>

              {/* Mobile Card Layout */}
              <div className="lg:hidden divide-y divide-gray-200 dark:divide-gray-700">
                {orders.map((order) => (
                  <div key={order.orderID} className="p-4">
                    {/* Order header row */}
                    <div className="flex items-center justify-between mb-2">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-gray-900 dark:text-white">#{order.wooOrderNumber}</span>
                        <span className={`px-2 text-xs leading-5 font-semibold rounded-full ${getStatusColor(order.orderStatus)}`}>
                          {order.orderStatus}
                        </span>
                      </div>
                      <span className="text-sm font-medium text-gray-900 dark:text-white">R {order.orderTotal.toFixed(2)}</span>
                    </div>

                    {/* Customer & store */}
                    <div className="space-y-1 mb-2">
                      <p className="text-sm text-gray-900 dark:text-white">{order.customerName}</p>
                      <p className="text-xs text-gray-500 dark:text-gray-400">{order.customerEmail}</p>
                    </div>

                    {/* Address */}
                    <p className="text-xs text-gray-500 dark:text-gray-400 mb-2">
                      {order.shippingAddressLine1}, {order.shippingCity}, {order.shippingProvince} {order.shippingPostalCode}
                    </p>

                    {/* Shipment status / booking */}
                    <div className="mb-2">
                      {order.shipmentID ? (
                        <span className="text-xs text-green-700 dark:text-green-400">
                          {order.shipmentStatus ? `Shipment: ${order.shipmentStatus}` : 'Shipment booked'}
                          {order.trackingNumber ? ` · ${order.trackingNumber}` : ''}
                        </span>
                      ) : canBookShipment(order) ? (
                        <button
                          onClick={() => bookShipment(order)}
                          disabled={bookingOrderId !== null}
                          className="inline-flex items-center px-2.5 py-1 rounded-md text-xs font-medium bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
                        >
                          {bookingOrderId === order.orderID ? 'Booking…' : 'Book Shipment'}
                        </button>
                      ) : isSpecialTrip(order) ? (
                        <span className="inline-flex items-center px-2 py-0.5 rounded-md text-xs font-medium bg-amber-100 text-amber-800 dark:bg-amber-900 dark:text-amber-200">
                          {specialTripLabel(order)}
                        </span>
                      ) : (
                        <span className="text-xs text-gray-400 dark:text-gray-500">Collection order</span>
                      )}
                    </div>

                    {/* Footer row */}
                    <div className="flex items-center justify-between">
                      <span className="text-xs text-gray-400" title={isoTitle(order.orderDate)}>{order.storeName || 'N/A'} · {formatDateTime(order.orderDate)}</span>
                      <button
                        onClick={() => setExpandedOrder(expandedOrder === order.orderID ? null : order.orderID)}
                        className="text-xs text-blue-600 dark:text-blue-400 font-medium"
                      >
                        {expandedOrder === order.orderID ? 'Hide Items' : 'View Items'}
                      </button>
                    </div>

                    {/* Expanded line items */}
                    {expandedOrder === order.orderID && order.lineItems && (
                      <div className="mt-3 pt-3 border-t border-gray-200 dark:border-gray-700 space-y-2">
                        <p className="text-xs font-semibold text-gray-900 dark:text-white">Order Items:</p>
                        {order.lineItems.map((item) => (
                          <div key={item.orderLineItemID} className="flex items-center justify-between text-sm">
                            <div className="flex-1 min-w-0">
                              <p className="text-gray-900 dark:text-white truncate">{item.productName}</p>
                              <p className="text-xs text-gray-500">Qty: {item.quantity} × R {item.unitPrice.toFixed(2)}</p>
                            </div>
                            <span className="text-sm font-medium text-gray-900 dark:text-white ml-3">R {item.lineTotal.toFixed(2)}</span>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </>
          )}
        </div>
      </div>
    </AppLayout>
  );
}