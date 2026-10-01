'use client'

import { useState } from 'react'
import { useRouter } from 'next/navigation'
import { 
  Pencil, 
  ExternalLink, 
  Webhook, 
  TestTube, 
  Package,
  ChevronLeft,
  ChevronRight
} from 'lucide-react'

interface QuickAction {
  id: string
  icon: React.ReactNode
  label: string
  onClick: () => void
  variant?: 'default' | 'success' | 'warning' | 'danger'
}

interface QuickActionsSidebarProps {
  actions: QuickAction[]
}

export function QuickActionsSidebar({ actions }: QuickActionsSidebarProps) {
  const [isExpanded, setIsExpanded] = useState(false)

  const getVariantStyles = (variant?: string) => {
    switch (variant) {
      case 'success':
        return 'bg-green-50 hover:bg-green-100 text-green-600 dark:bg-green-900/20 dark:hover:bg-green-900/40 dark:text-green-400'
      case 'warning':
        return 'bg-yellow-50 hover:bg-yellow-100 text-yellow-600 dark:bg-yellow-900/20 dark:hover:bg-yellow-900/40 dark:text-yellow-400'
      case 'danger':
        return 'bg-red-50 hover:bg-red-100 text-red-600 dark:bg-red-900/20 dark:hover:bg-red-900/40 dark:text-red-400'
      default:
        return 'bg-gray-50 hover:bg-gray-100 text-gray-600 dark:bg-gray-800 dark:hover:bg-gray-700 dark:text-gray-300'
    }
  }

  return (
    <div 
      className={`
        fixed right-4 top-1/2 -translate-y-1/2 z-40
        flex flex-col gap-3 p-2
        bg-white dark:bg-gray-900 
        border-l border-y border-gray-200 dark:border-gray-700
        rounded-l-lg shadow-lg
        transition-all duration-300 ease-in-out
        ${isExpanded ? 'w-48' : 'w-14'}
      `}
    >
      {/* Toggle Button */}
      <button
        onClick={() => setIsExpanded(!isExpanded)}
        className="absolute -left-3 top-1/2 -translate-y-1/2 
          w-6 h-12 flex items-center justify-center
          bg-white dark:bg-gray-900 
          border border-gray-200 dark:border-gray-700
          rounded-l-md shadow-md
          text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
      >
        {isExpanded ? <ChevronRight size={14} /> : <ChevronLeft size={14} />}
      </button>

      {/* Action Buttons */}
      {actions.map((action) => (
        <button
          key={action.id}
          onClick={action.onClick}
          className={`
            flex items-center gap-3 p-3 rounded-lg
            transition-all duration-200
            ${getVariantStyles(action.variant)}
            ${isExpanded ? 'justify-start' : 'justify-center'}
          `}
          title={!isExpanded ? action.label : undefined}
        >
          <span className="flex-shrink-0">{action.icon}</span>
          {isExpanded && (
            <span className="text-sm font-medium whitespace-nowrap overflow-hidden">
              {action.label}
            </span>
          )}
        </button>
      ))}
    </div>
  )
}