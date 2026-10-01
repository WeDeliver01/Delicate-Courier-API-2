'use client'

import * as React from 'react'
import { RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

type ButtonProps = React.ComponentProps<typeof Button>

export interface RefreshButtonProps extends Omit<ButtonProps, 'onClick' | 'children'> {
  onRefresh: () => void | Promise<void>
  label?: string
  refreshingLabel?: string
  showLabel?: boolean
}

export function RefreshButton({
  onRefresh,
  label = 'Refresh',
  refreshingLabel = 'Refreshing...',
  showLabel = true,
  variant = 'outline',
  size = 'sm',
  className,
  disabled,
  ...rest
}: RefreshButtonProps) {
  const [isRefreshing, setIsRefreshing] = React.useState(false)
  const [lastRefreshedAt, setLastRefreshedAt] = React.useState<Date | null>(null)

  const handleClick = React.useCallback(async () => {
    if (isRefreshing) return
    setIsRefreshing(true)
    try {
      await onRefresh()
      setLastRefreshedAt(new Date())
    } finally {
      setIsRefreshing(false)
    }
  }, [isRefreshing, onRefresh])

  const title = lastRefreshedAt
    ? `Last refreshed at ${lastRefreshedAt.toLocaleTimeString()}`
    : 'Pull the latest data'

  return (
    <Button
      type="button"
      variant={variant}
      size={size}
      onClick={handleClick}
      disabled={disabled || isRefreshing}
      title={title}
      aria-label={isRefreshing ? refreshingLabel : label}
      className={cn(className)}
      {...rest}
    >
      <RefreshCw className={cn(isRefreshing && 'animate-spin')} />
      {showLabel && (isRefreshing ? refreshingLabel : label)}
    </Button>
  )
}
