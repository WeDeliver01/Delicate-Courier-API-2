// Shared types for the Plugin Management dashboard. Shapes mirror the
// camelCase JSON emitted by PluginPlatformAdminController.

export interface PluginProduct {
  pluginId: number
  slug: string
  name: string
  description: string | null
  isActive: boolean
  createdOn: string
  releaseCount: number
  latestVersion: string | null
  licenseCount: number
}

export interface RolloutRule {
  rolloutRuleId: number
  ruleType: string // allow_domain | deny_domain
  value: string
}

export interface PluginRelease {
  releaseId: number
  version: string
  changelog: string
  minWpVersion: string | null
  minWcVersion: string | null
  minPhpVersion: string | null
  sha256: string
  fileSizeBytes: number
  fileName: string
  rolloutStage: string
  status: string // published | withdrawn
  createdOn: string
  createdBy: string | null
  withdrawnOn: string | null
  withdrawnBy: string | null
  rules: RolloutRule[]
}

export interface LicenseInstallation {
  installationId: number
  domain: string
  pluginVersion: string | null
  wpVersion: string | null
  wcVersion: string | null
  phpVersion: string | null
  lastHeartbeatOn: string | null
  apiLatencyMs: number | null
}

export interface PluginLicense {
  licenseId: number
  licenseKey: string
  tenantId: number | null
  storeId: number | null
  domain: string | null
  status: string // inactive | active | revoked
  isInternal: boolean
  isBeta: boolean
  activatedOn: string | null
  deactivatedOn: string | null
  expiresOn: string | null
  createdOn: string
  createdBy: string | null
  installations: LicenseInstallation[]
}

export interface Installation {
  installationId: number
  domain: string
  pluginVersion: string | null
  wpVersion: string | null
  wcVersion: string | null
  phpVersion: string | null
  lastHeartbeatOn: string | null
  apiLatencyMs: number | null
  recentErrorsJson: string | null
  createdOn: string
  license: {
    licenseId: number
    licenseKey: string
    status: string
    storeId: number | null
    tenantId: number | null
  }
  pluginSlug: string
}

export interface FeatureFlag {
  featureFlagId: number
  key: string
  enabled: boolean
  valueJson: string | null
  changedOn: string | null
  changedBy: string | null
}

export interface TelemetryEvent {
  telemetryEventId: number
  installationId: number | null
  pluginId: number | null
  eventType: string
  payloadJson: string | null
  occurredOn: string
  receivedOn: string
}

export interface AuditLog {
  auditLogId: number
  action: string
  actor: string
  entityType: string | null
  entityRef: string | null
  detailsJson: string | null
  ipAddress: string | null
  createdOn: string
}

export const ROLLOUT_STAGES = ['internal', 'beta', 'percent5', 'percent25', 'percent100'] as const

export const STAGE_LABELS: Record<string, string> = {
  internal: 'Internal',
  beta: 'Beta',
  percent5: '5%',
  percent25: '25%',
  percent100: '100%',
}

export const STAGE_COVERAGE: Record<string, number> = {
  internal: 0,
  beta: 0,
  percent5: 5,
  percent25: 25,
  percent100: 100,
}

export function stageBadgeClass(stage: string): string {
  switch (stage) {
    case 'internal':
      return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300'
    case 'beta':
      return 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-300'
    case 'percent5':
      return 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300'
    case 'percent25':
      return 'bg-indigo-100 text-indigo-800 dark:bg-indigo-900 dark:text-indigo-300'
    case 'percent100':
      return 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300'
    default:
      return 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300'
  }
}

export function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${bytes} B`
}

/** Heartbeat freshness bucket used for badges and analytics. */
export function heartbeatHealth(lastHeartbeatOn: string | null): 'healthy' | 'stale' | 'silent' {
  if (!lastHeartbeatOn) return 'silent'
  const ageMs = Date.now() - new Date(lastHeartbeatOn).getTime()
  if (ageMs <= 24 * 60 * 60 * 1000) return 'healthy'
  if (ageMs <= 72 * 60 * 60 * 1000) return 'stale'
  return 'silent'
}

/** Compare dotted version strings; returns -1/0/1. */
export function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map((n) => Number.parseInt(n, 10) || 0)
  const pb = b.split('.').map((n) => Number.parseInt(n, 10) || 0)
  const len = Math.max(pa.length, pb.length)
  for (let i = 0; i < len; i++) {
    const da = pa[i] ?? 0
    const db = pb[i] ?? 0
    if (da !== db) return da < db ? -1 : 1
  }
  return 0
}
