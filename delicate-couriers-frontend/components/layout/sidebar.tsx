'use client'

import { usePathname, useRouter } from 'next/navigation'
import Image from 'next/image'
import { useAuth } from '@/components/providers/auth-provider'
import {
  LayoutDashboard,
  Package,
  Store,
  ShoppingCartIcon,
  Building2,
  Users,
  BarChart3,
  Shield,
  LogOut,
  User,
  Activity,
  Truck,
  Bug,
  ShoppingBag
} from 'lucide-react'

interface NavItem {
  name: string
  href: string
  icon: any
  roles?: string[]
}

const navigation: NavItem[] = [
  { name: 'Dashboard', href: '/dashboard', icon: LayoutDashboard },
  { name: 'Shipments', href: '/shipments', icon: Package },
  { name: 'Stores', href: '/stores', icon: Store },
  { name: 'Orders', href: '/orders', icon: ShoppingCartIcon },
  { name: 'Tenants', href: '/tenants', icon: Building2, roles: ['Admin', 'SuperAdmin'] },
  { name: 'Users', href: '/users', icon: Users, roles: ['Admin', 'SuperAdmin'] },
  { name: 'Reports', href: '/reports', icon: BarChart3 },
  { name: 'System Events', href: '/system-events', icon: Activity, roles: ['SuperAdmin'] },
  { name: 'Debug Log', href: '/super-admin/debug-log', icon: Bug, roles: ['SuperAdmin'] },
  { name: 'Shiplogic Webhook', href: '/super-admin/shiplogic-webhook', icon: Truck, roles: ['SuperAdmin'] },
  { name: 'Shopify Rates', href: '/super-admin/shopify-carrier', icon: ShoppingBag, roles: ['SuperAdmin'] },
]

export function Sidebar() {
  const pathname = usePathname()
  const router = useRouter()
  const { user, signOut } = useAuth()
  const userRole = user?.role ?? ''

  const isActive = (href: string) => {
    if (href === '/dashboard') return pathname === '/dashboard'
    return pathname.startsWith(href)
  }

  const canAccess = (item: NavItem) => {
    if (!item.roles) return true
    return item.roles.includes(userRole)
  }

  const handleLogout = async () => { await signOut() }

  return (
    <div className="flex h-full w-64 flex-col border-r bg-white dark:bg-gray-900 dark:border-gray-700">
      {/* Logo/Brand */}
      <div
        onClick={() => router.push('/dashboard')}
        className="flex h-16 items-center border-b px-4 dark:border-gray-700 gap-3 cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
      >
        <Image
          src="/dc_logo.png"
          alt="Delicate Courier Logo"
          width={36}
          height={36}
          className="rounded-full"
        />
        <h2 className="text-lg font-bold text-gray-900 dark:text-white">Delicate Courier</h2>
      </div>

      {/* Navigation */}
      <nav className="flex-1 space-y-1 px-3 py-4">
        {navigation.filter(canAccess).map((item) => {
          const Icon = item.icon
          const active = isActive(item.href)
          
          return (
            <button
              key={item.name}
              onClick={() => router.push(item.href)}
              className={`flex w-full items-center gap-3 rounded-lg px-4 py-3 text-sm font-medium transition-colors ${
                active
                  ? 'bg-blue-50 text-blue-600 dark:bg-blue-900 dark:text-blue-300'
                  : 'text-gray-700 hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-800'
              }`}
            >
              <Icon className="h-5 w-5" />
              {item.name}
            </button>
          )
        })}
      </nav>

      {/* Footer */}
      <div className="border-t p-3 space-y-1 dark:border-gray-700">
        {/* Super Admin */}
        {userRole === 'SuperAdmin' && (
          <button
            onClick={() => router.push('/super-admin')}
            className={`flex w-full items-center gap-3 rounded-lg px-4 py-2 text-sm font-medium transition-colors ${
              pathname.startsWith('/super-admin')
                ? 'bg-purple-50 text-purple-600 dark:bg-purple-900 dark:text-purple-300'
                : 'text-purple-600 hover:bg-purple-50 dark:text-purple-400 dark:hover:bg-purple-900/50'
            }`}
          >
            <Shield className="h-5 w-5" />
            Super Admin
          </button>
        )}

        {/* Profile */}
        <button
          onClick={() => router.push('/profile')}
          className={`flex w-full items-center gap-3 rounded-lg px-4 py-2 text-sm font-medium transition-colors ${
            pathname.startsWith('/profile')
              ? 'bg-blue-50 text-blue-600 dark:bg-blue-900 dark:text-blue-300'
              : 'text-gray-700 hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-800'
          }`}
        >
          <User className="h-5 w-5" />
          Profile
        </button>

        {/* Logout */}
        <button
          onClick={handleLogout}
          className="flex w-full items-center gap-3 rounded-lg px-4 py-2 text-sm font-medium text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/50 transition-colors"
        >
          <LogOut className="h-5 w-5" />
          Logout
        </button>

        {/* Version */}
        <p className="text-xs text-gray-500 dark:text-gray-400 px-4 pt-2">Platform v1.0</p>
      </div>
    </div>
  )
}