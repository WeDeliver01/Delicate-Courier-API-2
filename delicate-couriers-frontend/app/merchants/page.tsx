'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { AppLayout } from '@/components/layout/app-layout'
import { RefreshButton } from '@/components/ui/refresh-button'
import { merchants, type Merchant } from '@/lib/merchants'

/**
 * Merchants on the Delicate Engine.
 *
 * These used to live in a settings file, which meant onboarding a bakery was a developer and
 * a deploy. The list shows the things an operator actually needs at a glance: whether a
 * merchant is live, whether we can read their shop back, and which Engine account their
 * deliveries are billed to.
 */
export default function MerchantsPage() {
  const [all, setAll] = useState<Merchant[]>([])
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      setAll(await merchants.list())
    } catch (err) {
      const message =
        (err as { response?: { data?: { error?: string } } }).response?.data?.error ??
        'Could not load merchants.'
      setError(message)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
  }, [])

  const term = query.trim().toLowerCase()
  const shown = term
    ? all.filter(
        (m) =>
          m.name.toLowerCase().includes(term) ||
          m.engineAccountRef.toLowerCase().includes(term) ||
          String(m.storeId).includes(term)
      )
    : all

  return (
    <AppLayout>
      <div className="space-y-6">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold">Merchants</h1>
            <p className="text-sm text-gray-500">
              Stores booking deliveries through the Delicate Engine.
            </p>
          </div>
          <div className="flex items-center gap-2">
            <RefreshButton onRefresh={load} />
            <Link href="/merchants/new">
              <Button>Add merchant</Button>
            </Link>
          </div>
        </div>

        <Input
          placeholder="Search by name, store id or account reference"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          className="max-w-md"
        />

        {error && (
          <Card>
            <CardContent className="py-4 text-sm text-red-600">{error}</CardContent>
          </Card>
        )}

        {loading ? (
          <Card>
            <CardContent className="py-8 text-sm text-gray-500">Loading…</CardContent>
          </Card>
        ) : shown.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-sm text-gray-500">
              {all.length === 0
                ? 'No merchants yet. Add one to start booking through the Engine.'
                : 'No merchant matches that search.'}
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-4">
            {shown.map((m) => (
              <Card key={m.storeId}>
                <CardHeader className="pb-2">
                  <div className="flex items-center justify-between gap-3">
                    <CardTitle className="text-base">
                      <Link href={`/merchants/${m.storeId}`} className="hover:underline">
                        {m.name}
                      </Link>
                    </CardTitle>
                    <span
                      className={`rounded-full px-2 py-0.5 text-xs ${
                        m.isActive
                          ? 'bg-green-100 text-green-800'
                          : 'bg-gray-200 text-gray-600'
                      }`}
                    >
                      {m.isActive ? 'Live' : 'Off'}
                    </span>
                  </div>
                </CardHeader>
                <CardContent className="grid gap-1 text-sm text-gray-600 sm:grid-cols-3">
                  <div>
                    <span className="text-gray-400">Store </span>
                    {m.storeId}
                  </div>
                  <div>
                    <span className="text-gray-400">Engine account </span>
                    {m.engineAccountRef}
                  </div>
                  <div>
                    <span className="text-gray-400">Shop readable </span>
                    {/* Without these we cannot check what the shopper chose at checkout, so a
                        collection order can book as a delivery. Worth seeing in the list. */}
                    {m.hasWooCredentials ? 'yes' : 'no'}
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>
    </AppLayout>
  )
}
