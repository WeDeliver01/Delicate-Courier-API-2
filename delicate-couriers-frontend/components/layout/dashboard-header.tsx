'use client'

import { Button } from '@/components/ui/button'
import { useAuth } from '@/components/providers/auth-provider'

export function DashboardHeader() {
  const { user, signOut } = useAuth()
  const name = user?.name || user?.email || 'User'

  return (
    <header className="border-b bg-white">
      <div className="flex h-16 items-center justify-between px-8">
        <div className="flex items-center space-x-4">
          <h2 className="text-xl font-bold">Delicate Courier</h2>
          <span className="text-sm text-gray-500">Platform</span>
        </div>

        <div className="flex items-center space-x-4">
          <span className="text-sm text-gray-600">
            Welcome, <span className="font-medium">{name}</span>
          </span>
          <Button variant="outline" size="sm" onClick={() => signOut()}>
            Logout
          </Button>
        </div>
      </div>
    </header>
  )
}
