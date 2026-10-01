"use client";

import { useEffect } from "react";

export default function StoreDetailError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    console.error("[StoreDetailError]", error);
  }, [error]);

  return (
    <div className="min-h-screen flex items-center justify-center p-6 bg-gray-50">
      <div className="max-w-lg w-full bg-white rounded-xl border border-red-200 p-6 shadow-sm">
        <h2 className="text-lg font-semibold text-red-700 mb-2">
          Store page failed to load
        </h2>
        <p className="text-sm text-gray-700 mb-3">
          Something threw while rendering this page. The details below help us
          fix it — please send a screenshot if it keeps happening.
        </p>
        <pre className="text-xs bg-gray-100 text-gray-800 p-3 rounded overflow-auto max-h-48 whitespace-pre-wrap break-words">
          {error?.message || "Unknown error"}
          {error?.digest ? `\n\ndigest: ${error.digest}` : ""}
        </pre>
        <div className="mt-4 flex gap-2">
          <button
            onClick={() => reset()}
            className="px-4 py-2 text-sm bg-blue-600 text-white rounded-lg hover:bg-blue-700"
          >
            Try again
          </button>
          <button
            onClick={() => (window.location.href = "/stores")}
            className="px-4 py-2 text-sm border border-gray-300 rounded-lg hover:bg-gray-100"
          >
            Back to Stores
          </button>
        </div>
      </div>
    </div>
  );
}
