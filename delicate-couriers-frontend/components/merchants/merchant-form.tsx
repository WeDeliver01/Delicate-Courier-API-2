'use client'

import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { blankAddress, type Merchant, type MerchantAddress, type MerchantServiceLevel } from '@/lib/merchants'

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

  /** Both ends of a pasted pair, in one update: two setAddress calls would race. */
  const setAddress2 = (latitude: number, longitude: number) =>
    setV((prev) => ({
      ...prev,
      collection: { ...(prev.collection ?? blankAddress()), latitude, longitude },
    }))

  const address: MerchantAddress = v.collection ?? blankAddress()

  // Null Island is what an empty coordinate field becomes, and the engine prices it happily:
  // a Pretoria delivery collected from the Gulf of Guinea quotes six figures. Said on the
  // form rather than discovered at somebody's checkout.
  const collectionPinLooksReal =
    Math.abs(address.latitude) > 0.0001 || Math.abs(address.longitude) > 0.0001

  const levels: MerchantServiceLevel[] = v.serviceLevels ?? []

  // Replaced wholesale rather than mutated: the array is the record of what the shopper is
  // offered and in what order, and editing it in place makes that order accidental.
  const setLevels = (next: MerchantServiceLevel[]) =>
    setV((prev) => ({ ...prev, serviceLevels: next }))

  const setLevel = (index: number, patch: Partial<MerchantServiceLevel>) =>
    setLevels(levels.map((l, i) => (i === index ? { ...l, ...patch } : l)))

  const addLevel = () =>
    setLevels([
      ...levels,
      { checkoutCode: '', checkoutLabel: '', engineCode: 'standard', estimatedDeliveryDays: 1 },
    ])

  const removeLevel = (index: number) => setLevels(levels.filter((_, i) => i !== index))

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
          <CardTitle className="text-base">Delivery options</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-sm text-gray-600">
            Each one is a separate choice at the shopper&apos;s checkout, priced on its own.
            They appear in this order.
          </p>

          {levels.length === 0 && (
            <p className="rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
              With none of these, this merchant has no delivery option to offer and their
              checkout will show nothing. Add at least one.
            </p>
          )}

          {levels.map((level, i) => (
            <div key={i} className="grid items-end gap-3 rounded border p-3 sm:grid-cols-12">
              <div className="sm:col-span-4">
                <Label htmlFor={`label-${i}`}>What the shopper sees</Label>
                <Input
                  id={`label-${i}`}
                  value={level.checkoutLabel}
                  onChange={(e) => setLevel(i, { checkoutLabel: e.target.value })}
                />
              </div>
              <div className="sm:col-span-2">
                <Label htmlFor={`code-${i}`}>Checkout code</Label>
                <Input
                  id={`code-${i}`}
                  value={level.checkoutCode}
                  onChange={(e) => setLevel(i, { checkoutCode: e.target.value })}
                />
              </div>
              <div className="sm:col-span-3">
                <Label htmlFor={`engine-${i}`}>Engine service level</Label>
                <Input
                  id={`engine-${i}`}
                  value={level.engineCode}
                  onChange={(e) => setLevel(i, { engineCode: e.target.value })}
                />
              </div>
              <div className="sm:col-span-2">
                <Label htmlFor={`days-${i}`}>Days</Label>
                <Input
                  id={`days-${i}`}
                  type="number"
                  min={0}
                  value={level.estimatedDeliveryDays}
                  onChange={(e) =>
                    setLevel(i, { estimatedDeliveryDays: Number(e.target.value) })
                  }
                />
              </div>
              <div className="sm:col-span-1">
                <Button type="button" variant="outline" onClick={() => removeLevel(i)}>
                  Remove
                </Button>
              </div>
            </div>
          ))}

          <div className="space-y-2">
            <Button type="button" variant="outline" onClick={addLevel}>
              Add delivery option
            </Button>
            {/* The checkout code becomes the WooCommerce method id, which is what comes back
                on the order. Two options sharing one means the shopper's choice cannot be
                read back and the booking would be a guess. */}
            <p className="text-xs text-gray-500">
              Checkout codes must be distinct — they become the WooCommerce method id, and it
              is how we tell which option the shopper paid for.
            </p>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">At checkout</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
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
            <Coordinate
              id="lat"
              value={address.latitude}
              onChange={(v) => setAddress('latitude', v)}
              onPair={(lat, lng) => setAddress2(lat, lng)}
            />
          </div>
          <div>
            <Label htmlFor="lng">Longitude</Label>
            <Coordinate
              id="lng"
              value={address.longitude}
              onChange={(v) => setAddress('longitude', v)}
              onPair={(lat, lng) => setAddress2(lat, lng)}
            />
            {/* Every quote is a loop measured from here. A wrong pin is a wrong price on
                every order that merchant takes, which is why it is typed and not guessed. */}
            <p className="mt-1 text-xs text-gray-500">
              Every price is measured from this point. Paste both from Google Maps at once if
              you like — &ldquo;-25.7479, 28.2293&rdquo; fills in each.
            </p>
            {!collectionPinLooksReal && (
              <p className="mt-1 text-xs text-red-600">
                0,&nbsp;0 is in the Atlantic Ocean. Every quote would be priced from there.
              </p>
            )}
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
              The customer&apos;s account code on the Engine, e.g. KRU001.
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

/**
 * A latitude or longitude, typed by a person.
 *
 * This was an `<input type="number">` whose onChange did `Number(e.target.value)`, and the
 * combination made a South African coordinate impossible to enter. A number input reports
 * `value === ''` while what is typed is not yet a valid number — and "-" is not, on its own.
 * So the first keystroke of "-25.75" became `Number('') === 0`, React wrote that 0 back over
 * the "-" the browser was still holding, and the field sat at 0 refusing the minus sign.
 * Every latitude in this country is negative, so the only values reachable were the wrong
 * ones, and 0,0 is what every merchant ended up saved with.
 *
 * Hence a text input with a draft: what is typed stays exactly as typed, and the number is
 * handed up only once it parses. Intermediate states a person passes through on the way to a
 * real coordinate -- "-", "-25." -- are left alone instead of being corrected into nonsense.
 */
function Coordinate({
  id,
  value,
  onChange,
  onPair,
}: {
  id: string
  value: number
  onChange: (v: number) => void
  onPair: (lat: number, lng: number) => void
}) {
  const [draft, setDraft] = useState<string | null>(null)

  // The draft owns the field while it is being edited; otherwise the saved value shows, so a
  // pasted pair filling the *other* box still appears here.
  const shown = draft ?? String(value)

  const handle = (text: string) => {
    // "-25.7479, 28.2293" is what Google Maps puts on the clipboard, and splitting it by hand
    // into two boxes is a step at which somebody drops a digit.
    const pair = text.match(
      /^\s*(-?\d+(?:\.\d+)?)\s*[,/\s]\s*(-?\d+(?:\.\d+)?)\s*$/,
    )
    if (pair) {
      setDraft(null)
      onPair(Number(pair[1]), Number(pair[2]))
      return
    }

    setDraft(text)
    const n = Number(text)
    if (text.trim() !== '' && Number.isFinite(n)) onChange(n)
  }

  return (
    <Input
      id={id}
      type="text"
      inputMode="decimal"
      required
      value={shown}
      onChange={(e) => handle(e.target.value)}
      onBlur={() => setDraft(null)}
      placeholder="e.g. -25.7479"
    />
  )
}
