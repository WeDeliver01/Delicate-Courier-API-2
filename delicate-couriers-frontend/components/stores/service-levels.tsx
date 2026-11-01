'use client'

import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

/** One delivery option this store offers at checkout. */
export interface StoreServiceLevel {
  checkoutCode: string
  checkoutLabel: string
  engineCode: string
  estimatedDeliveryDays: number
}

/** The Engine's own service levels. Anything else is refused when a rate is asked for. */
const ENGINE_LEVELS: Array<[string, string]> = [
  ['standard', 'Standard — booked a day ahead, 4-hour window'],
  ['express', 'Express — booked a day ahead, 2-hour window'],
  ['on_demand', 'On demand — collected now'],
]

export const blankServiceLevel = (): StoreServiceLevel => ({
  checkoutCode: '',
  checkoutLabel: '',
  engineCode: 'standard',
  estimatedDeliveryDays: 1,
})

/**
 * What this store offers at checkout, and in what order a shopper sees it.
 *
 * Three names for one thing, because three parties disagree about what to call it: the
 * shopper reads the label, WooCommerce identifies the chosen method by the checkout code, and
 * the Engine prices the Engine level. The merchant's own words are the ones that should not
 * have to change, so all three are editable rather than derived from each other.
 */
export function ServiceLevelsEditor({
  levels,
  onChange,
  disabled,
}: {
  levels: StoreServiceLevel[]
  onChange: (next: StoreServiceLevel[]) => void
  disabled?: boolean
}) {
  // Replaced wholesale rather than mutated: the array is the record of what the shopper is
  // offered and in what order, and editing it in place makes that order accidental.
  const setLevel = (index: number, patch: Partial<StoreServiceLevel>) =>
    onChange(levels.map((l, i) => (i === index ? { ...l, ...patch } : l)))

  const duplicate = (code: string, index: number) =>
    code.trim() !== '' &&
    levels.some((l, i) => i !== index && l.checkoutCode.trim().toLowerCase() === code.trim().toLowerCase())

  return (
    <div className="space-y-3">
      {levels.length === 0 && (
        <p className="text-sm text-gray-500">
          No options yet. Until one is added this store offers the single default below.
        </p>
      )}

      {levels.map((level, i) => (
        <div key={i} className="grid items-end gap-3 rounded border p-3 sm:grid-cols-12">
          <div className="sm:col-span-4">
            <Label htmlFor={`sl-label-${i}`}>What the shopper sees</Label>
            <Input
              id={`sl-label-${i}`}
              value={level.checkoutLabel}
              disabled={disabled}
              placeholder="Delicate Courier Delivery"
              onChange={(e) => setLevel(i, { checkoutLabel: e.target.value })}
            />
          </div>
          <div className="sm:col-span-2">
            <Label htmlFor={`sl-code-${i}`}>Checkout code</Label>
            <Input
              id={`sl-code-${i}`}
              value={level.checkoutCode}
              disabled={disabled}
              placeholder="STD"
              onChange={(e) => setLevel(i, { checkoutCode: e.target.value })}
            />
            {duplicate(level.checkoutCode, i) && (
              <p className="mt-1 text-xs text-red-600">
                Already used. Two options sharing a code cannot be told apart on the order.
              </p>
            )}
          </div>
          <div className="sm:col-span-3">
            <Label htmlFor={`sl-engine-${i}`}>Engine service level</Label>
            <select
              id={`sl-engine-${i}`}
              value={level.engineCode}
              disabled={disabled}
              onChange={(e) => setLevel(i, { engineCode: e.target.value })}
              className="mt-1 w-full rounded-md border border-gray-300 bg-white p-2 dark:border-gray-600 dark:bg-gray-800"
            >
              {ENGINE_LEVELS.map(([code, label]) => (
                <option key={code} value={code}>
                  {label}
                </option>
              ))}
            </select>
          </div>
          <div className="sm:col-span-2">
            <Label htmlFor={`sl-days-${i}`}>Days</Label>
            <Input
              id={`sl-days-${i}`}
              type="number"
              min={0}
              value={level.estimatedDeliveryDays}
              disabled={disabled}
              onChange={(e) => setLevel(i, { estimatedDeliveryDays: Number(e.target.value) })}
            />
          </div>
          <div className="sm:col-span-1">
            <Button
              type="button"
              variant="outline"
              disabled={disabled}
              onClick={() => onChange(levels.filter((_, idx) => idx !== i))}
            >
              Remove
            </Button>
          </div>
        </div>
      ))}

      <div className="space-y-2">
        <Button
          type="button"
          variant="outline"
          disabled={disabled}
          onClick={() => onChange([...levels, blankServiceLevel()])}
        >
          Add delivery option
        </Button>
        {/* A free-text Engine code was how "Standard" got saved where "standard" was meant,
            and the Engine answers that with "service level not found" at somebody's
            checkout. There are three; they are a list. */}
        <p className="text-xs text-gray-500">
          Checkout codes must be distinct — each becomes the WooCommerce method id, and that is
          how we tell which option the shopper paid for.
        </p>
      </div>
    </div>
  )
}
