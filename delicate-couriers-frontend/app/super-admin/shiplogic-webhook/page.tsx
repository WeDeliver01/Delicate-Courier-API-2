'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Truck, Copy, Check, AlertCircle, ExternalLink } from 'lucide-react'
import api from '@/lib/api'
import { useAuth } from '@/components/providers/auth-provider'
import { AppLayout } from '@/components/layout/app-layout'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'

interface ShiplogicWebhookInfo {
  deliveryUrl: string
  topic: string
  authKey: string
  authKeyConfigured: boolean
  authHeaderName: string
  alternateHeaderName: string
  notes: string
}

export default function ShiplogicWebhookSetupPage() {
  const router = useRouter()
  const { user, loading: authLoading } = useAuth()
  const isSuperAdmin = user?.role === 'SuperAdmin'

  const [info, setInfo] = useState<ShiplogicWebhookInfo | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState<string>('')

  useEffect(() => {
    if (!authLoading && user && !isSuperAdmin) {
      router.push('/dashboard')
    }
  }, [authLoading, user, isSuperAdmin, router])

  useEffect(() => {
    if (!isSuperAdmin) return
    const load = async () => {
      try {
        setIsLoading(true)
        const res = await api.get<ShiplogicWebhookInfo>('/admin/integrations/shiplogic-webhook')
        setInfo(res.data)
      } catch (err: any) {
        setError(err.response?.data?.message || 'Failed to load webhook configuration')
      } finally {
        setIsLoading(false)
      }
    }
    load()
  }, [isSuperAdmin])

  const copy = async (label: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(label)
      setTimeout(() => setCopied(''), 1500)
    } catch {
      setError('Clipboard copy failed — select the value manually.')
    }
  }

  if (authLoading || isLoading) {
    return (
      <AppLayout>
        <div className="flex min-h-screen items-center justify-center">
          <p className="text-lg">Loading...</p>
        </div>
      </AppLayout>
    )
  }

  if (!isSuperAdmin) return null

  return (
    <AppLayout>
      <div className="p-8 bg-gray-50 dark:bg-gray-950 min-h-screen">
        <div className="mx-auto max-w-4xl">
          <div className="mb-6 flex items-center gap-3">
            <Truck className="h-8 w-8 text-indigo-600" />
            <div>
              <h1 className="text-3xl font-bold dark:text-white">Shiplogic Webhook Setup</h1>
              <p className="text-gray-600 dark:text-gray-400">
                Paste these values into Shiplogic so tracking updates flow back into the platform automatically.
              </p>
            </div>
          </div>

          {error && (
            <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg text-red-600 dark:bg-red-900/20 dark:border-red-800 dark:text-red-400">
              {error}
            </div>
          )}

          {info && !info.authKeyConfigured && (
            <div className="mb-4 p-4 bg-yellow-50 border border-yellow-200 rounded-lg dark:bg-yellow-900/20 dark:border-yellow-800">
              <div className="flex gap-2">
                <AlertCircle className="h-5 w-5 text-yellow-700 dark:text-yellow-400 flex-shrink-0 mt-0.5" />
                <div className="text-sm text-yellow-800 dark:text-yellow-300">
                  <strong>Auth key not configured.</strong> Set the{' '}
                  <code className="bg-yellow-100 dark:bg-yellow-900 px-1 rounded">Shiplogic__WebhookSecret</code>{' '}
                  environment variable to a long random string, redeploy, then paste the same value into Shiplogic below.
                  Until then, the platform accepts Shiplogic webhooks <em>without</em> authentication.
                </div>
              </div>
            </div>
          )}

          {info && (
            <>
              <Card className="mb-4">
                <CardHeader>
                  <CardTitle>1. Webhook values</CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  <Field
                    label="Topic"
                    value={info.topic}
                    copied={copied === 'topic'}
                    onCopy={() => copy('topic', info.topic)}
                  />
                  <Field
                    label="Delivery URL"
                    value={info.deliveryUrl}
                    copied={copied === 'url'}
                    onCopy={() => copy('url', info.deliveryUrl)}
                    mono
                  />
                  <Field
                    label="Auth key (optional)"
                    value={info.authKeyConfigured ? info.authKey : '(not configured)'}
                    copied={copied === 'auth'}
                    onCopy={() => info.authKeyConfigured && copy('auth', info.authKey)}
                    disabled={!info.authKeyConfigured}
                    mono
                  />
                  <Field
                    label='Notify when'
                    value="(leave blank — all tracking events)"
                    disabled
                  />
                </CardContent>
              </Card>

              <Card className="mb-4">
                <CardHeader>
                  <CardTitle>2. Steps in Shiplogic</CardTitle>
                </CardHeader>
                <CardContent>
                  <ol className="list-decimal list-inside space-y-2 text-sm text-gray-700 dark:text-gray-300">
                    <li>
                      Open the Shiplogic merchant portal and go to{' '}
                      <strong>Settings → Webhooks</strong>.
                    </li>
                    <li>Click <strong>+ Add webhook subscription</strong>.</li>
                    <li>Set <strong>Topic</strong> to <em>Shipment tracking event</em>.</li>
                    <li>Paste the <strong>Delivery URL</strong> from above.</li>
                    <li>
                      Paste the <strong>Auth key</strong> from above. Shiplogic sends this verbatim
                      on the <code>{info.authHeaderName}</code> header (we also accept{' '}
                      <code>{info.alternateHeaderName}</code> for compatibility).
                    </li>
                    <li>Leave <strong>Notify when</strong> and <strong>Meta</strong> blank.</li>
                    <li>Save the subscription, then trigger a test event from Shiplogic.</li>
                  </ol>
                  <p className="mt-4 text-xs text-gray-500 dark:text-gray-400">{info.notes}</p>
                </CardContent>
              </Card>

              <Card>
                <CardHeader>
                  <CardTitle>3. Verify</CardTitle>
                </CardHeader>
                <CardContent className="space-y-2 text-sm text-gray-700 dark:text-gray-300">
                  <p>
                    Every accepted webhook writes a <strong>tracking.event_ingested</strong> row to
                    System Events. Use the link below to confirm yours landed.
                  </p>
                  <Button variant="outline" onClick={() => router.push('/system-events?eventType=tracking.event_ingested')}>
                    <ExternalLink className="h-4 w-4 mr-2" />
                    Open System Events (tracking)
                  </Button>
                </CardContent>
              </Card>
            </>
          )}
        </div>
      </div>
    </AppLayout>
  )
}

function Field({
  label,
  value,
  copied,
  onCopy,
  disabled,
  mono,
}: {
  label: string
  value: string
  copied?: boolean
  onCopy?: () => void
  disabled?: boolean
  mono?: boolean
}) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">{label}</label>
      <div className="flex gap-2">
        <input
          readOnly
          value={value}
          className={`flex-1 px-3 py-2 border border-gray-300 dark:border-gray-700 rounded bg-gray-50 dark:bg-gray-900 text-sm dark:text-white ${
            mono ? 'font-mono' : ''
          } ${disabled ? 'text-gray-400 dark:text-gray-600' : ''}`}
          onFocus={(e) => !disabled && e.target.select()}
        />
        {onCopy && (
          <Button variant="outline" size="sm" onClick={onCopy} disabled={disabled}>
            {copied ? <Check className="h-4 w-4 text-green-600" /> : <Copy className="h-4 w-4" />}
          </Button>
        )}
      </div>
    </div>
  )
}
