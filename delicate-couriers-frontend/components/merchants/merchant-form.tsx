'use client'

import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { blankAddress, type Merchant, type MerchantAddress } from '@/lib/merchants'

export interface MerchantFormValues extends Partial<Merchant> {
  /** Only sent when being set or replaced; blank means "leave what is stored". */
  wooConsumerKey?: string
  wooConsumerSecret?: string
}

/**
 * The merchant form.
 *
 * Grouped by who cares about each part rather than by how it is stored: what the shopper sees
 * at checkout, where we collect from, and how we reach the merchant's shop. The Engine fields
 * are last because they are set once at onboarding and then left alone.
 */
export function MerchantForm({
  initial,
  isNew,
  saving,
  onSave,
}: {
  initial: MerchantFormValues
  isNew: boolean
  saving: boolean
  onSave: (values: MerchantFormValues) => void
}) {
  const [v, setV] = useState<MerchantFormValues>(initial)

  const set = <K extends keyof MerchantFormValues>(key: K, value: MerchantFormValues[K]) =>
    setV((prev) => ({ ...prev, [key]: value }))

  // Typed rather than cast: the address fields are a known shape, and a cast through
  // Record<string, unknown> would happily accept a misspelled key forever.
  const setAddress = <K extends keyof MerchantAddress>(key: K, value: MerchantAddress[K]) =>
    setV((prev) => ({
      ...prev,
      collection: { ...(prev.collection ?? blankAddress()), [key]: value },
    }))

  const address: MerchantAddress = v.collection ?? blankAddress()

  return (
    <form
      className="space-y-6"
      onSubmit={(e) => {
        e.preventDefault()
        onSave(v)
      }}
    >
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Merchant</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <div>
            <Label htmlFor="storeId">Store ID</Label>
            <Input
              id="storeId"
              type="number"
              required
              disabled={!isNew}
              value={v.storeId ?? 0}
              onChange={(e) => set('storeId', Number(e.target.value))}
            />
            {/* It is the number in their plugin's settings, not one we hand out, and it is
                what every webhook arrives with — so it cannot change later. */}
            <p className="mt-1 text-xs text-gray-500">
              {isNew ? "From the merchant's plugin settings." : 'Fixed once set.'}
            </p>
          </div>
          <div>
            <Label htmlFor="name">Name</Label>
            <Input
              id="name"
              required
              value={v.name ?? ''}
              onChange={(e) => set('name', e.target.value)}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">At checkout</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <div>
            <Label htmlFor="rateLabel">What the shopper sees</Label>
            <Input
              id="rateLabel"
              value={v.checkoutRateLabel ?? ''}
              onChange={(e) => set('checkoutRateLabel', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="serviceCode">Service code</Label>
            <Input
              id="serviceCode"
              value={v.checkoutServiceLevelCode ?? 'STD'}
              onChange={(e) => set('checkoutServiceLevelCode', e.target.value)}
            />
            {/* Get this wrong and the plugin filters our rate out: the customer simply sees
                no delivery option, with nothing to say why. */}
            <p className="mt-1 text-xs text-gray-500">
              Must match the plugin&apos;s configured service level, usually STD.
            </p>
          </div>
          <div>
            <Label htmlFor="days">Estimated days</Label>
            <Input
              id="days"
              type="number"
              min={0}
              value={v.estimatedDeliveryDays ?? 1}
              onChange={(e) => set('estimatedDeliveryDays', Number(e.target.value))}
            />
          </div>
          <div>
            <Label htmlFor="triggers">Book only on these statuses</Label>
            <Input
              id="triggers"
              placeholder="leave blank to book on any"
              value={(v.bookingTriggerStatuses ?? []).join(', ')}
              onChange={(e) =>
                set(
                  'bookingTriggerStatuses',
                  e.target.value
                    .split(',')
                    .map((s) => s.trim())
                    .filter(Boolean)
                )
              }
            />
            <p className="mt-1 text-xs text-gray-500">
              Comma separated, e.g. completed. Blank books on any non-cancelled status.
            </p>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Collection address</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <div className="sm:col-span-2">
            <Label htmlFor="formatted">Address</Label>
            <Input
              id="formatted"
              required
              value={address.formatted}
              onChange={(e) => setAddress('formatted', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="line1">Street</Label>
            <Input
              id="line1"
              value={address.line1 ?? ''}
              onChange={(e) => setAddress('line1', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="suburb">Suburb</Label>
            <Input
              id="suburb"
              value={address.suburb ?? ''}
              onChange={(e) => setAddress('suburb', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="city">City</Label>
            <Input
              id="city"
              value={address.city ?? ''}
              onChange={(e) => setAddress('city', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="postcode">Postal code</Label>
            <Input
              id="postcode"
              value={address.postalCode ?? ''}
              onChange={(e) => setAddress('postalCode', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="lat">Latitude</Label>
            <Input
              id="lat"
              type="number"
              step="any"
              required
              value={address.latitude}
              onChange={(e) => setAddress('latitude', Number(e.target.value))}
            />
          </div>
          <div>
            <Label htmlFor="lng">Longitude</Label>
            <Input
              id="lng"
              type="number"
              step="any"
              required
              value={address.longitude}
              onChange={(e) => setAddress('longitude', Number(e.target.value))}
            />
            {/* Every quote is a loop measured from here. A wrong pin is a wrong price on
                every order that merchant takes, which is why it is typed and not guessed. */}
            <p className="mt-1 text-xs text-gray-500">
              Every price is measured from this point.
            </p>
          </div>
          <div>
            <Label htmlFor="contactName">Contact name</Label>
            <Input
              id="contactName"
              value={address.contactName ?? ''}
              onChange={(e) => setAddress('contactName', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="contactPhone">Contact phone</Label>
            <Input
              id="contactPhone"
              value={address.contactPhone ?? ''}
              onChange={(e) => setAddress('contactPhone', e.target.value)}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Their shop</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <div className="sm:col-span-2">
            <Label htmlFor="wooUrl">WooCommerce URL</Label>
            <Input
              id="wooUrl"
              placeholder="https://example.co.za"
              value={v.wooCommerceUrl ?? ''}
              onChange={(e) => set('wooCommerceUrl', e.target.value)}
            />
            {/* Without these we cannot read back what the shopper chose, so an order whose
                only collection signal is the merchant's own label books as a delivery. */}
            <p className="mt-1 text-xs text-gray-500">
              Needed to check whether a shopper chose collection instead of delivery.
            </p>
          </div>
          <div>
            <Label htmlFor="ck">Consumer key</Label>
            <Input
              id="ck"
              placeholder={v.hasWooCredentials ? 'stored — leave blank to keep' : 'ck_…'}
              value={v.wooConsumerKey ?? ''}
              onChange={(e) => set('wooConsumerKey', e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="cs">Consumer secret</Label>
            <Input
              id="cs"
              type="password"
              placeholder={v.hasWooCredentials ? 'stored — leave blank to keep' : 'cs_…'}
              value={v.wooConsumerSecret ?? ''}
              onChange={(e) => set('wooConsumerSecret', e.target.value)}
            />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">On the Engine</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <div>
            <Label htmlFor="accountRef">Account reference</Label>
            <Input
              id="accountRef"
              required
              value={v.engineAccountRef ?? ''}
              onChange={(e) => set('engineAccountRef', e.target.value)}
            />
            <p className="mt-1 text-xs text-gray-500">
              As mapped on the Engine, e.g. store-42.
            </p>
          </div>
          <div>
            <Label htmlFor="accountId">Account ID</Label>
            <Input
              id="accountId"
              value={v.engineAccountId ?? ''}
              onChange={(e) => set('engineAccountId', e.target.value)}
            />
            {/* Delivery callbacks arrive naming this and nothing else, so without it a
                delivered parcel never reaches the merchant's order. */}
            <p className="mt-1 text-xs text-gray-500">
              Required for delivery updates to reach their shop.
            </p>
          </div>
          <div>
            <Label htmlFor="packageType">Default package type</Label>
            <Input
              id="packageType"
              required
              value={v.defaultPackageTypeId ?? ''}
              onChange={(e) => set('defaultPackageTypeId', e.target.value)}
            />
            <p className="mt-1 text-xs text-gray-500">
              What their parcels are priced as in the Engine catalogue.
            </p>
          </div>
          <div>
            <Label htmlFor="engineService">Engine service level</Label>
            <Input
              id="engineService"
              value={v.engineServiceLevelCode ?? 'standard'}
              onChange={(e) => set('engineServiceLevelCode', e.target.value)}
            />
          </div>
        </CardContent>
      </Card>

      <div className="flex items-center gap-3">
        <Button type="submit" disabled={saving}>
          {saving ? 'Saving…' : isNew ? 'Add merchant' : 'Save changes'}
        </Button>
      </div>
    </form>
  )
}
