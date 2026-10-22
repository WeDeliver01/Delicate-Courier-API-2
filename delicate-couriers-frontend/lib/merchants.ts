import axios from 'axios'
import { getSupabase } from './supabase'

/**
 * Client for the Delicate Integration service's merchant admin.
 *
 * Points at this app's own server, not at the integration service. The route handler behind
 * `/integration-api` holds the admin key and never lets it near a browser; all this sends is
 * the operator's Supabase token, exactly like the rest of the portal.
 *
 * Deliberately not under `/api`: this app rewrites everything there straight to the .NET
 * backend, which would swallow the route handler before it ever ran.
 */
const integration = axios.create({
  baseURL: '/integration-api',
  headers: { 'Content-Type': 'application/json' },
})

integration.interceptors.request.use(async (config) => {
  if (typeof window !== 'undefined') {
    try {
      const { data } = await getSupabase().auth.getSession()
      const token = data.session?.access_token
      if (token) config.headers.Authorization = `Bearer ${token}`
    } catch {
      // Fall through and let the proxy answer 401.
    }
  }
  return config
})

export interface MerchantAddress {
  formatted: string
  line1?: string | null
  suburb?: string | null
  city?: string | null
  postalCode?: string | null
  country: string
  latitude: number
  longitude: number
  contactName?: string | null
  contactPhone?: string | null
}

export interface Merchant {
  storeId: number
  name: string
  engineAccountRef: string
  engineAccountId?: string | null
  collection: MerchantAddress
  checkoutServiceLevelCode: string
  checkoutRateLabel: string
  engineServiceLevelCode: string
  defaultPackageTypeId: string
  estimatedDeliveryDays: number
  bookingTriggerStatuses: string[]
  wooCommerceUrl?: string | null
  /** Whether credentials are stored. Never what they are. */
  hasWooCredentials: boolean
  isActive: boolean
  updatedAt: string
}

export interface MerchantChange {
  id: string
  storeId: number
  action: string
  actorLabel?: string | null
  detail?: string | null
  occurredAt: string
}

export interface MerchantCreated {
  merchant: Merchant
  /** Shown once. The integration service keeps no readable copy. */
  webhookSecret: string
}

export const blankAddress = (): MerchantAddress => ({
  formatted: '',
  line1: '',
  suburb: '',
  city: '',
  postalCode: '',
  country: 'ZA',
  latitude: 0,
  longitude: 0,
  contactName: '',
  contactPhone: '',
})

export const emptyMerchant = (): Partial<Merchant> => ({
  storeId: 0,
  name: '',
  engineAccountRef: '',
  engineAccountId: '',
  checkoutServiceLevelCode: 'STD',
  checkoutRateLabel: 'Delicate Courier Delivery',
  engineServiceLevelCode: 'standard',
  defaultPackageTypeId: '',
  estimatedDeliveryDays: 1,
  bookingTriggerStatuses: [],
  wooCommerceUrl: '',
  isActive: true,
  collection: blankAddress(),
})

/**
 * Whether the Engine will actually take orders for a merchant.
 *
 * Separate from the merchant record because it is a live call to another system, and because
 * the two things it depends on are created in the Engine by a super_admin, not here.
 */
export interface EngineReadiness {
  state:
    | 'Ready'
    | 'NoAccountMapping'
    | 'NotGrantedToCredential'
    | 'AccountNotActive'
    | 'CredentialRejected'
    | 'Unknown'
  canBook: boolean
  detail: string
  remedy: string | null
}

export const merchants = {
  list: () => integration.get<Merchant[]>('').then((r) => r.data),
  get: (storeId: number) => integration.get<Merchant>(`${storeId}`).then((r) => r.data),
  create: (body: unknown) => integration.post<MerchantCreated>('', body).then((r) => r.data),
  update: (storeId: number, body: unknown) =>
    integration.put<Merchant>(`${storeId}`, body).then((r) => r.data),
  rotateSecret: (storeId: number) =>
    integration.post<MerchantCreated>(`${storeId}/rotate-secret`).then((r) => r.data),
  deactivate: (storeId: number) =>
    integration.post<Merchant>(`${storeId}/deactivate`).then((r) => r.data),
  changes: (storeId: number) =>
    integration.get<MerchantChange[]>(`${storeId}/changes`).then((r) => r.data),
  engineReadiness: (storeId: number) =>
    integration
      .get<EngineReadiness>(`${storeId}/engine-readiness`)
      .then((r) => r.data),
}

export default integration
