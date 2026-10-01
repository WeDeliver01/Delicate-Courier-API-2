'use client';

import { useEffect, useState } from 'react';
import { useRouter, useParams } from 'next/navigation';
import { ArrowLeft, Play, Loader2, ChevronDown, ChevronUp } from 'lucide-react';
import { ResultModal } from '@/components/testing/ResultModal';

interface TestResult {
  success: boolean;
  endpoint: string;
  responseTime?: string;
  ratesCount?: number;
  rates?: any[];
  testData?: any;
  error?: string;
  innerError?: string;
  details?: string;
}

export default function TestEndpointsPage() {
  const router = useRouter();
  const params = useParams();
  const storeId = params?.id as string;
  const [loading, setLoading] = useState<{ [key: string]: boolean }>({});
  const [modalOpen, setModalOpen] = useState(false);
  const [currentResult, setCurrentResult] = useState<TestResult | null>(null);
  const [expandedPayload, setExpandedPayload] = useState<{ [key: string]: boolean }>({});

  

  const testEndpoint = async (endpointKey: string, apiPath: string) => {
    setLoading({ ...loading, [endpointKey]: true });

    try {
      // Route through axios so the Supabase access token is attached automatically.
      const { default: api } = await import('@/lib/api');
      const response = await api.post(apiPath, undefined, { validateStatus: () => true });

      const data = response.data;

      setCurrentResult(data);
      setModalOpen(true);
      setLoading({ ...loading, [endpointKey]: false });
    } catch (error: any) {
      const errorResult: TestResult = {
        success: false,
        endpoint: apiPath,
        error: 'Network error',
        details: error.message,
      };

      setCurrentResult(errorResult);
      setModalOpen(true);
      setLoading({ ...loading, [endpointKey]: false });
    }
  };

  const togglePayload = (key: string) => {
    setExpandedPayload({ ...expandedPayload, [key]: !expandedPayload[key] });
  };

  const endpoints = [
    {
      key: 'get-rates',
      name: 'Get Shipping Rates',
      method: 'POST',
      path: '/rates',
      description: 'Fetch real-time shipping rates from Shiplogic',
      apiPath: `/api/testing/shiplogic/test-rates/${storeId}`,
      samplePayload: {
        collectionAddress: {
          street: 'Store warehouse address',
          city: 'Pretoria',
          postalCode: '0157'
        },
        deliveryAddress: {
          street: '227 Lois Avenue',
          suburb: 'Mooikloof',
          city: 'Pretoria',
          postalCode: '0081'
        },
        parcel: {
          dimensions: '30x20x10cm',
          weight: '2.5kg'
        }
      }
    },
  ];

  return (
    <div className="min-h-screen bg-[#0a0a0a] text-white p-8">
      {/* Header */}
      <div className="max-w-6xl mx-auto mb-8">
        <button
          onClick={() => router.push(`/stores/${storeId}`)}
          className="flex items-center gap-2 text-gray-400 hover:text-white mb-4 transition-colors"
        >
          <ArrowLeft size={20} />
          Back to Store
        </button>

        <h1 className="text-3xl font-bold mb-2">Test Shiplogic Endpoints</h1>
        <p className="text-gray-400">
  Test real Shiplogic API endpoints with live data
</p>
      </div>

      {/* Endpoints List */}
      <div className="max-w-6xl mx-auto space-y-6">
        {endpoints.map((endpoint) => {
          const isLoading = loading[endpoint.key];
          const isExpanded = expandedPayload[endpoint.key];

          return (
            <div
              key={endpoint.key}
              className="bg-[#1a1a1a] border border-gray-800 rounded-lg p-6"
            >
              {/* Endpoint Header */}
              <div className="flex items-start justify-between mb-4">
                <div className="flex-1">
                  <div className="flex items-center gap-3 mb-2">
                    <h3 className="text-xl font-semibold">{endpoint.name}</h3>
                    <span className="px-2 py-1 bg-blue-500/20 text-blue-400 text-xs rounded font-mono">
                      {endpoint.method}
                    </span>
                  </div>
                  <p className="text-sm text-gray-400 mb-1 font-mono">{endpoint.path}</p>
                  <p className="text-sm text-gray-500">{endpoint.description}</p>
                </div>

                {/* Test Button */}
                <button
                  onClick={() => testEndpoint(endpoint.key, endpoint.apiPath)}
                  disabled={isLoading}
                  className="flex items-center gap-2 px-4 py-2 bg-green-600 hover:bg-green-700 disabled:bg-gray-700 disabled:cursor-not-allowed rounded-lg transition-colors whitespace-nowrap"
                >
                  {isLoading ? (
                    <>
                      <Loader2 size={16} className="animate-spin" />
                      Testing...
                    </>
                  ) : (
                    <>
                      <Play size={16} />
                      Test Endpoint
                    </>
                  )}
                </button>
              </div>

              {/* View Sample Payload */}
              <div className="mb-4">
                <button
                  onClick={() => togglePayload(endpoint.key)}
                  className="flex items-center gap-2 text-sm text-gray-400 hover:text-white transition-colors"
                >
                  {isExpanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
                  {isExpanded ? 'Hide' : 'View'} Sample Test Data
                </button>

                {isExpanded && (
                  <div className="mt-3 p-4 bg-[#0a0a0a] border border-gray-800 rounded-lg">
                    <p className="text-xs text-gray-500 mb-2">Sample payload sent to Shiplogic:</p>
                    <pre className="text-xs text-gray-300 overflow-x-auto">
                      {JSON.stringify(endpoint.samplePayload, null, 2)}
                    </pre>
                    <p className="text-xs text-gray-500 mt-3">
                      <strong>Note:</strong> This uses your store's actual collection address and Shiplogic credentials.
                    </p>
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {/* Result Modal */}
      {currentResult && (
        <ResultModal
          isOpen={modalOpen}
          onClose={() => setModalOpen(false)}
          result={currentResult}
        />
      )}
    </div>
  );
}