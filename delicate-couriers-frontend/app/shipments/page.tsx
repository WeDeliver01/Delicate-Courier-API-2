'use client';

import { useState, useEffect } from 'react';
import React from 'react';
import { useRouter } from 'next/navigation';
import api from '@/lib/api';
import { AppLayout } from '@/components/layout/app-layout';
import { RefreshButton } from '@/components/ui/refresh-button';
import { formatDateTime, formatDateMaybeTime, isoTitle } from '@/lib/datetime';

interface Shipment {
  shipmentId: number;
  orderId: number;
  trackingNumber: string;
  consignmentId: string;
  courierName: string;
  courierService: string;
  status: string | null;
  shippingCost: number;
  estimatedDeliveryDate?: string;
  actualDeliveryDate?: string;
  createdOn: string;
  orderNumber: string;
  customerName: string;
  storeName: string;
  tenantName: string;
}

export default function ShipmentsPage() {
  const [shipments, setShipments] = useState<Shipment[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [statusFilter, setStatusFilter] = useState('all');
  const [searchQuery, setSearchQuery] = useState('');
  const router = useRouter();

  useEffect(() => {
    fetchShipments();
  }, [router]);

  const fetchShipments = async (showLoader: boolean = true) => {
    try {
      if (showLoader) setLoading(true);
      const response = await api.get('/shipments?days=90');
      setShipments(response.data.shipments || []);
      setError('');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to fetch shipments');
      console.error('Error fetching shipments:', err);
    } finally {
      if (showLoader) setLoading(false);
    }
  };

  const refreshShipments = () => fetchShipments(false);

  const getStatusColor = (status: string | null | undefined) => {
    if (!status) return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300';

    switch (status.toLowerCase()) {
      case 'delivered':
        return 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200';
      case 'in transit':
      case 'intransit':
      case 'in-transit':
      case 'at-hub':
      case 'out-for-delivery':
        return 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200';
      case 'collected':
      case 'collection-assigned':
      case 'out-for-collection':
        return 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200';
      case 'failed':
      case 'failed-delivery':
      case 'failed-collection':
        return 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200';
      case 'created':
      case 'pending':
      case 'submitted':
        return 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200';
      case 'cancelled':
        return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300';
      default:
        return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300';
    }
  };

  const filteredShipments = shipments.filter((shipment) => {
    const matchesStatus = statusFilter === 'all' ||
      (shipment.status && shipment.status.toLowerCase() === statusFilter.toLowerCase());
    const matchesSearch = searchQuery === '' ||
      shipment.trackingNumber.toLowerCase().includes(searchQuery.toLowerCase()) ||
      shipment.orderNumber.toLowerCase().includes(searchQuery.toLowerCase()) ||
      shipment.customerName.toLowerCase().includes(searchQuery.toLowerCase());
    return matchesStatus && matchesSearch;
  });

  if (loading) {
    return (
      <AppLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-gray-600 dark:text-gray-400">Loading shipments...</div>
        </div>
      </AppLayout>
    );
  }

  return (
    <AppLayout>
      <div className="p-4 sm:p-6 lg:p-8 bg-gray-50 dark:bg-gray-950">
        <div className="mb-6 sm:mb-8 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 dark:text-white mb-1">Shipments</h1>
            <p className="text-sm sm:text-base text-gray-600 dark:text-gray-400">Track and manage all shipments created through the platform</p>
          </div>
          <RefreshButton onRefresh={refreshShipments} />
        </div>

        {error && (
          <div className="mb-4 sm:mb-6 p-3 sm:p-4 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg">
            <p className="text-sm text-red-800 dark:text-red-200">{error}</p>
          </div>
        )}

        {/* Summary Stats - 2 cols mobile, 4 cols desktop */}
        <div className="mb-4 sm:mb-6 grid grid-cols-2 sm:grid-cols-5 gap-3 sm:gap-4">
          <div className="bg-white dark:bg-gray-800 p-3 sm:p-4 rounded-lg shadow">
            <div className="text-xs sm:text-sm text-gray-600 dark:text-gray-400">Total Shipments</div>
            <div className="text-xl sm:text-2xl font-bold text-gray-900 dark:text-white">{shipments.length}</div>
          </div>
          <div className="bg-white dark:bg-gray-800 p-3 sm:p-4 rounded-lg shadow">
            <div className="text-xs sm:text-sm text-gray-600 dark:text-gray-400">In Transit</div>
            <div className="text-xl sm:text-2xl font-bold text-blue-600">
              {shipments.filter(s => s.status && (s.status.toLowerCase() === 'intransit' || s.status.toLowerCase() === 'in transit')).length}
            </div>
          </div>
          <div className="bg-white dark:bg-gray-800 p-3 sm:p-4 rounded-lg shadow">
            <div className="text-xs sm:text-sm text-gray-600 dark:text-gray-400">Delivered</div>
            <div className="text-xl sm:text-2xl font-bold text-green-600">
              {shipments.filter(s => s.status && s.status.toLowerCase() === 'delivered').length}
            </div>
          </div>
          <div className="bg-white dark:bg-gray-800 p-3 sm:p-4 rounded-lg shadow">
            <div className="text-xs sm:text-sm text-gray-600 dark:text-gray-400">Failed</div>
            <div className="text-xl sm:text-2xl font-bold text-red-600">
              {shipments.filter(s => s.status && s.status.toLowerCase() === 'failed').length}
            </div>
          </div>
          <div className="bg-white dark:bg-gray-800 p-3 sm:p-4 rounded-lg shadow">
            <div className="text-xs sm:text-sm text-gray-600 dark:text-gray-400">Cancelled</div>
            <div className="text-xl sm:text-2xl font-bold text-gray-500">
              {shipments.filter(s => s.status && s.status.toLowerCase() === 'cancelled').length}
            </div>
          </div>
        </div>

        {/* Filters - stack on mobile */}
        <div className="mb-4 sm:mb-6 flex flex-col sm:flex-row gap-3 sm:gap-4">
          <div className="flex-1">
            <input
              type="text"
              placeholder="Search tracking, order, or customer..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full px-3 sm:px-4 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-800 text-gray-900 dark:text-white text-sm focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <select
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value)}
            className="w-full sm:w-auto px-3 sm:px-4 py-2 border border-gray-300 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-800 text-gray-900 dark:text-white text-sm focus:ring-2 focus:ring-blue-500"
          >
            <option value="all">All Statuses</option>
            <option value="created">Created</option>
            <option value="pending">Pending</option>
            <option value="collection-assigned">Collection Assigned</option>
            <option value="intransit">In Transit</option>
            <option value="delivered">Delivered</option>
            <option value="failed">Failed</option>
            <option value="cancelled">Cancelled</option>
          </select>
        </div>

        <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
          {filteredShipments.length === 0 ? (
            <div className="p-8 text-center text-gray-500 dark:text-gray-400">
              {searchQuery || statusFilter !== 'all' ? 'No shipments match your filters' : 'No shipments found'}
            </div>
          ) : (
            <>
              {/* Desktop Table */}
              <div className="hidden lg:block overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                  <thead className="bg-gray-50 dark:bg-gray-900">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Tracking #</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Order #</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Store / Tenant</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Customer</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Courier</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Est. Delivery</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Created</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                    {filteredShipments.map((shipment) => (
                      <tr
                        key={`shipment-${shipment.shipmentId}`}
                        className="hover:bg-gray-50 dark:hover:bg-gray-700 cursor-pointer"
                        onClick={() => router.push(`/shipments/${shipment.shipmentId}`)}
                      >
                        <td className="px-6 py-4 whitespace-nowrap">
                          <div className="text-sm font-medium text-gray-900 dark:text-white font-mono">{shipment.trackingNumber}</div>
                          <div className="text-xs text-gray-500 dark:text-gray-400">{shipment.consignmentId}</div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <div className="text-sm text-gray-900 dark:text-white">#{shipment.orderNumber}</div>
                        </td>
                        <td className="px-6 py-4">
                          <div className="text-sm text-gray-900 dark:text-white">{shipment.storeName}</div>
                          <div className="text-xs text-gray-500 dark:text-gray-400">{shipment.tenantName}</div>
                        </td>
                        <td className="px-6 py-4">
                          <div className="text-sm text-gray-900 dark:text-white">{shipment.customerName}</div>
                        </td>
                        <td className="px-6 py-4">
                          <div className="text-sm text-gray-900 dark:text-white">{shipment.courierName}</div>
                          <div className="text-xs text-gray-500 dark:text-gray-400">{shipment.courierService}</div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`px-2 inline-flex text-xs leading-5 font-semibold rounded-full ${getStatusColor(shipment.status)}`}>
                            {shipment.status || 'Unknown'}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400" title={isoTitle(shipment.estimatedDeliveryDate)}>
                          {shipment.estimatedDeliveryDate ? formatDateMaybeTime(shipment.estimatedDeliveryDate) : 'N/A'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400" title={isoTitle(shipment.createdOn)}>
                          {formatDateTime(shipment.createdOn)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {/* Mobile Card Layout */}
              <div className="lg:hidden divide-y divide-gray-200 dark:divide-gray-700">
                {filteredShipments.map((shipment) => (
                  <div
                    key={`shipment-mobile-${shipment.shipmentId}`}
                    onClick={() => router.push(`/shipments/${shipment.shipmentId}`)}
                    className="p-4 cursor-pointer active:bg-gray-50 dark:active:bg-gray-700"
                  >
                    {/* Top row: tracking + status */}
                    <div className="flex items-center justify-between mb-2">
                      <span className="font-mono text-sm font-medium text-gray-900 dark:text-white">{shipment.trackingNumber}</span>
                      <span className={`px-2 text-xs leading-5 font-semibold rounded-full ${getStatusColor(shipment.status)}`}>
                        {shipment.status || 'Unknown'}
                      </span>
                    </div>

                    {/* Customer & order */}
                    <div className="flex items-center justify-between mb-1">
                      <span className="text-sm text-gray-900 dark:text-white">{shipment.customerName}</span>
                      <span className="text-sm text-gray-600 dark:text-gray-400">#{shipment.orderNumber}</span>
                    </div>

                    {/* Store & courier */}
                    <div className="flex items-center justify-between mb-1">
                      <span className="text-xs text-gray-500 dark:text-gray-400">{shipment.storeName}</span>
                      <span className="text-xs text-gray-500 dark:text-gray-400">{shipment.courierName} · {shipment.courierService}</span>
                    </div>

                    {/* Footer: dates */}
                    <div className="flex items-center justify-between text-xs text-gray-400 mt-2">
                      <span title={isoTitle(shipment.createdOn)}>Created: {formatDateTime(shipment.createdOn)}</span>
                      <span title={isoTitle(shipment.estimatedDeliveryDate)}>
                        {shipment.estimatedDeliveryDate ? `ETA: ${formatDateMaybeTime(shipment.estimatedDeliveryDate)}` : ''}
                      </span>
                    </div>
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