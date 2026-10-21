'use client'

import { useState } from 'react'
import { useRouter } from 'next/navigation'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { AppLayout } from '@/components/layout/app-layout'
import { MerchantForm, type MerchantFormValues } from '@/components/merchants/merchant-form'
import { emptyMerchant, merchants } from '@/lib/merchants'

/**
 * Onboarding a merchant.
 *
 * Ends on the webhook secret, which is the one thing that cannot be looked up again: it is
 * shown once, and the only way back from losing it is to rotate and reconfigure the plugin.
 * So the page stops there and makes the operator acknowledge it rather than navigating away
 * with it still on screen.
 */
export default function NewMerchantPage() {
  const router = useRouter()
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [secret, setSecret] = useState<{ storeId: number; value: string } | null>(null)

  const save = async (values: MerchantFormValues) => {
    setSaving(true)
    setError(null)
    try {
      const created = await merchants.create(values)
      setSecret({ storeId: created.merchant.storeId, value: created.webhookSecret })
    } catch (err) {
      const message =
        (err as { response?: { data?: { error?: string } } }).response?.data?.error ??
        'Could not save this merchant.'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  if (secret) {
    return (
      <AppLayout>
        <div className="mx-auto max-w-2xl space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>Merchant added</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <p className="text-sm text-gray-600">
                Paste this webhook secret into the merchant&apos;s plugin settings. It is shown
                once — we keep no readable copy, so if it is lost it has to be rotated and the
                plugin reconfigured.
              </p>
              <code className="block overflow-x-auto rounded bg-gray-100 p-3 font-mono text-sm">
                {secret.value}
              </code>
              <div className="flex gap-3">
                <Button
                  onClick={() => {
                    void navigator.clipboard?.writeText(secret.value)
                  }}
                >
                  Copy
                </Button>
                <Button
                  variant="outline"
                  onClick={() => router.push(`/merchants/${secret.storeId}`)}
                >
                  I have saved it
                </Button>
              </div>
            </CardContent>
          </Card>
        </div>
      </AppLayout>
    )
  }

  return (
    <AppLayout>
      <div className="mx-auto max-w-3xl space-y-6">
        <div>
          <h1 className="text-2xl font-semibold">Add merchant</h1>
          <p className="text-sm text-gray-500">
            A store that will book deliveries through the Delicate Engine.
          </p>
        </div>

        {error && (
          <Card>
            <CardContent className="py-4 text-sm text-red-600">{error}</CardContent>
          </Card>
        )}

        <MerchantForm initial={emptyMerchant()} isNew saving={saving} onSave={save} />
      </div>
    </AppLayout>
  )
}
