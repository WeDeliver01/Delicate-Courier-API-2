'use client'

import { use, useEffect, useState } from 'react'
import Link from 'next/link'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'
import { MerchantForm, type MerchantFormValues } from '@/components/merchants/merchant-form'
import { merchants, type Merchant, type MerchantChange } from '@/lib/merchants'

/**
 * One merchant: edit, rotate, switch off, and see what has been done to them.
 *
 * The change history is on the same page as the controls deliberately. The question it answers
 * — "who turned this off on Friday" — is always asked while looking at the thing that is off.
 */
export default function MerchantPage({ params }: { params: Promise<{ storeId: string }> }) {
  const storeId = Number(use(params).storeId)

  const [merchant, setMerchant] = useState<Merchant | null>(null)
  const [changes, setChanges] = useState<MerchantChange[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [rotated, setRotated] = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    try {
      const [m, c] = await Promise.all([merchants.get(storeId), merchants.changes(storeId)])
      setMerchant(m)
      setChanges(c)
    } catch {
      setError('Could not load this merchant.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storeId])

  const fail = (err: unknown, fallback: string) =>
    setError(
      (err as { response?: { data?: { error?: string } } }).response?.data?.error ?? fallback
    )

  const save = async (values: MerchantFormValues) => {
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      setMerchant(await merchants.update(storeId, values))
      setChanges(await merchants.changes(storeId))
      setNotice('Saved. Live on the next request.')
    } catch (err) {
      fail(err, 'Could not save this merchant.')
    } finally {
      setSaving(false)
    }
  }

  const rotate = async () => {
    // The old secret dies the moment this returns, so the plugin is broken until the new one
    // is pasted in. That is a phone call with the merchant, not a quiet bit of housekeeping.
    if (
      !window.confirm(
        'Issue a new webhook secret? The current one stops working immediately, and their plugin will reject orders until the new one is in place.'
      )
    ) {
      return
    }
    setError(null)
    try {
      const result = await merchants.rotateSecret(storeId)
      setRotated(result.webhookSecret)
      setChanges(await merchants.changes(storeId))
    } catch (err) {
      fail(err, 'Could not rotate the secret.')
    }
  }

  const deactivate = async () => {
    if (!window.confirm('Switch this merchant off? New orders from them will not be booked.')) {
      return
    }
    setError(null)
    try {
      setMerchant(await merchants.deactivate(storeId))
      setChanges(await merchants.changes(storeId))
      setNotice('Merchant switched off.')
    } catch (err) {
      fail(err, 'Could not switch this merchant off.')
    }
  }

  if (loading) {
    return (
      <AppLayout>
        <Card>
          <CardContent className="py-8 text-sm text-gray-500">Loading…</CardContent>
        </Card>
      </AppLayout>
    )
  }

  if (!merchant) {
    return (
      <AppLayout>
        <Card>
          <CardContent className="py-8 text-sm text-gray-500">
            {error ?? 'Merchant not found.'}{' '}
            <Link href="/merchants" className="underline">
              Back to merchants
            </Link>
          </CardContent>
        </Card>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="mx-auto max-w-3xl space-y-6">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold">{merchant.name}</h1>
            <p className="text-sm text-gray-500">
              Store {merchant.storeId} · {merchant.isActive ? 'Live' : 'Switched off'}
            </p>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" onClick={rotate}>
              Rotate secret
            </Button>
            {merchant.isActive && (
              <Button variant="outline" onClick={deactivate}>
                Switch off
              </Button>
            )}
          </div>
        </div>

        {notice && (
          <Card>
            <CardContent className="py-3 text-sm text-green-700">{notice}</CardContent>
          </Card>
        )}
        {error && (
          <Card>
            <CardContent className="py-3 text-sm text-red-600">{error}</CardContent>
          </Card>
        )}

        {rotated && (
          <Card>
            <CardHeader>
              <CardTitle className="text-base">New webhook secret</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <p className="text-sm text-gray-600">
                Shown once. Paste it into the merchant&apos;s plugin now — their orders will not
                be accepted until you do.
              </p>
              <code className="block overflow-x-auto rounded bg-gray-100 p-3 font-mono text-sm">
                {rotated}
              </code>
              <div className="flex gap-3">
                <Button onClick={() => void navigator.clipboard?.writeText(rotated)}>Copy</Button>
                <Button variant="outline" onClick={() => setRotated(null)}>
                  I have saved it
                </Button>
              </div>
            </CardContent>
          </Card>
        )}

        <MerchantForm initial={merchant} isNew={false} saving={saving} onSave={save} />

        <Card>
          <CardHeader>
            <CardTitle className="text-base">History</CardTitle>
          </CardHeader>
          <CardContent>
            {changes.length === 0 ? (
              <p className="text-sm text-gray-500">Nothing recorded yet.</p>
            ) : (
              <ul className="divide-y text-sm">
                {changes.map((c) => (
                  <li key={c.id} className="flex items-baseline justify-between gap-4 py-2">
                    <span className="font-medium">{c.action}</span>
                    <span className="text-gray-500">
                      {c.actorLabel ?? 'unknown'} · {new Date(c.occurredAt).toLocaleString()}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>
    </AppLayout>
  )
}
