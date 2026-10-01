'use client';

import { CheckCircle2, XCircle, X, Copy } from 'lucide-react';
import { useState } from 'react';

interface ResultModalProps {
  isOpen: boolean;
  onClose: () => void;
  result: {
    success: boolean;
    endpoint: string;
    responseTime?: string;
    ratesCount?: number;
    rates?: any[];
    testData?: any;
    error?: string;
    innerError?: string;
    details?: string;
  };
}

export function ResultModal({ isOpen, onClose, result }: ResultModalProps) {
  const [copied, setCopied] = useState(false);

  if (!isOpen) return null;

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80">
      <div className="bg-[#1a1a1a] border border-gray-800 rounded-lg w-full max-w-3xl max-h-[90vh] overflow-y-auto m-4">
        {/* Header */}
        <div className="sticky top-0 bg-[#1a1a1a] border-b border-gray-800 p-4 flex items-center justify-between">
          <div className="flex items-center gap-3">
            {result.success ? (
              <>
                <CheckCircle2 size={24} className="text-green-500" />
                <h2 className="text-xl font-bold text-green-500">Test Passed</h2>
              </>
            ) : (
              <>
                <XCircle size={24} className="text-red-500" />
                <h2 className="text-xl font-bold text-red-500">Test Failed</h2>
              </>
            )}
          </div>
          <button
            onClick={onClose}
            className="p-2 hover:bg-gray-800 rounded-lg transition-colors"
          >
            <X size={20} />
          </button>
        </div>

        {/* Content */}
        <div className="p-6 space-y-4">
          {/* Endpoint Info */}
          <div>
            <p className="text-sm text-gray-400">Endpoint</p>
            <p className="font-mono text-white">{result.endpoint}</p>
          </div>

          {result.responseTime && (
            <div>
              <p className="text-sm text-gray-400">Response Time</p>
              <p className="text-white">{result.responseTime}</p>
            </div>
          )}

          {/* Success Content */}
          {result.success && (
            <>
              {result.ratesCount !== undefined && (
                <div>
                  <p className="text-sm text-gray-400 mb-2">Rates Found</p>
                  <p className="text-2xl font-bold text-green-500">{result.ratesCount}</p>
                </div>
              )}

              {result.rates && result.rates.length > 0 && (
                <div>
                  <p className="text-sm text-gray-400 mb-3">Available Shipping Rates:</p>
                  <div className="space-y-3">
                    {result.rates.map((rate, idx) => (
                      <div
                        key={idx}
                        className="bg-[#0a0a0a] border border-gray-800 rounded-lg p-4"
                      >
                        <div className="flex justify-between items-start mb-2">
                          <div>
                            <p className="font-semibold text-white text-lg">{rate.serviceLevelName}</p>
                            <p className="text-gray-500 text-sm">{rate.serviceLevelCode}</p>
                          </div>
                          <div className="text-right">
                            <p className="font-bold text-green-400 text-xl">R{rate.cost}</p>
                            {rate.estimatedDeliveryDays && (
                              <p className="text-sm text-gray-500">
                                {rate.estimatedDeliveryDays} day{rate.estimatedDeliveryDays !== 1 ? 's' : ''}
                              </p>
                            )}
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {result.testData && (
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <p className="text-sm text-gray-400">Test Data Used</p>
                    <button
                      onClick={() => copyToClipboard(JSON.stringify(result.testData, null, 2))}
                      className="flex items-center gap-1 text-xs text-gray-400 hover:text-white transition-colors"
                    >
                      <Copy size={14} />
                      {copied ? 'Copied!' : 'Copy JSON'}
                    </button>
                  </div>
                  <pre className="p-4 bg-[#0a0a0a] border border-gray-800 rounded-lg overflow-x-auto text-xs text-gray-300">
                    {JSON.stringify(result.testData, null, 2)}
                  </pre>
                </div>
              )}
            </>
          )}

          {/* Error Content */}
          {!result.success && (
            <>
              <div className="bg-red-900/20 border border-red-800 rounded-lg p-4">
                <p className="text-sm text-red-400 font-semibold mb-2">Error Message:</p>
                <p className="text-red-300">{result.error}</p>
              </div>

              {result.innerError && (
                <div className="bg-red-900/20 border border-red-800 rounded-lg p-4">
                  <p className="text-sm text-red-400 font-semibold mb-2">Details:</p>
                  <p className="text-red-300">{result.innerError}</p>
                </div>
              )}

              {result.details && (
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <p className="text-sm text-gray-400">Technical Details</p>
                    <button
                      onClick={() => copyToClipboard(result.details || '')}
                      className="flex items-center gap-1 text-xs text-gray-400 hover:text-white transition-colors"
                    >
                      <Copy size={14} />
                      {copied ? 'Copied!' : 'Copy'}
                    </button>
                  </div>
                  <pre className="p-4 bg-[#0a0a0a] border border-red-900/50 rounded-lg overflow-x-auto text-xs text-red-400">
                    {result.details}
                  </pre>
                </div>
              )}
            </>
          )}
        </div>

        {/* Footer */}
        <div className="sticky bottom-0 bg-[#1a1a1a] border-t border-gray-800 p-4">
          <button
            onClick={onClose}
            className="w-full px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}