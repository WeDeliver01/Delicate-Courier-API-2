'use client'

import { useState, useEffect } from 'react'
import { useRouter, usePathname } from 'next/navigation'
import { Button } from '@/components/ui/button'
import { Sidebar } from './sidebar'
import { Menu, X } from 'lucide-react'
import { Moon, Sun } from 'lucide-react'
import { useTheme } from '@/components/providers/theme-provider'
import { useAuth } from '@/components/providers/auth-provider'

export function AppLayout({ children }: { children: React.ReactNode }) {
  const [sidebarOpen, setSidebarOpen] = useState(true)
  const router = useRouter()
  const pathname = usePathname()
  const { theme, toggleTheme } = useTheme()
  const { user } = useAuth()

  useEffect(() => {
    setSidebarOpen(false)
  }, [pathname])

  const getUserName = () => user?.name || user?.email || 'User'

  return (
    <div className="flex h-screen bg-gray-50">
      {/* Sidebar */}
      {sidebarOpen && <Sidebar />}

      {/* Main Content Area */}
      <div className="flex flex-1 flex-col overflow-hidden">
        {/* Top Header */}
        <header className="flex h-16 items-center justify-between border-b bg-white px-8 dark:bg-gray-900 dark:border-gray-700">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setSidebarOpen(!sidebarOpen)}
            className="flex items-center gap-2"
          >
            {sidebarOpen ? (
              <>
                <X className="h-5 w-5" />
                <span className="text-sm">Close Menu</span>
              </>
            ) : (
              <>
                <Menu className="h-5 w-5" />
                <span className="text-sm">Open Menu</span>
              </>
            )}
          </Button>
          
          <div className="flex items-center gap-4">
            {/* Dark Mode Toggle */}
            <Button
              variant="ghost"
              size="sm"
              onClick={toggleTheme}
              className="flex items-center gap-2"
            >
              {theme === 'dark' ? (
                <>
                  <Sun className="h-5 w-5" />
                  <span className="text-sm">Light</span>
                </>
              ) : (
                <>
                  <Moon className="h-5 w-5" />
                  <span className="text-sm">Dark</span>
                </>
              )}
            </Button>
            
            <span className="text-sm text-gray-600 dark:text-gray-300">
              Welcome, <span className="font-medium">{getUserName()}</span>
            </span>
          </div>
        </header>

        {/* Page Content */}
        <main className="flex-1 overflow-y-auto bg-gray-50 dark:bg-gray-950">
          {children}
        </main>
      </div>
    </div>
  )
}