'use client'

import { useRouter } from 'next/navigation'
import Image from 'next/image'
import { Button } from '@/components/ui/button'

export default function LandingPage() {
  const router = useRouter()

  return (
    <div className="flex min-h-screen flex-col items-center justify-center bg-[#212121] text-white">
      <div className="mx-auto max-w-2xl space-y-8 px-6 text-center">
        {/* Logo/Branding */}
        <div className="space-y-4">
          
          {/* Logo */}
          <div className="flex justify-center py-4">
            <Image
              src="/dc_logo.png"
              alt="Delicate Courier Logo"
              width={200}
              height={200}
              priority
            />
          </div>
          
          <h1 className="text-5xl font-bold tracking-tight">
            Delicate Courier
          </h1>
                    
          {/* <p className="text-xl text-gray-400">
            Logistics Platform
          </p> */}

        </div>

        {/* Description */}
        <p className="text-lg leading-relaxed text-gray-300">
          Streamline your shipping operations with our automated platform. 
          Connect WooCommerce stores, manage shipments, and track deliveries 
          all in one place.
        </p>

        {/* Action Buttons */}
        <div className="flex flex-col gap-4 sm:flex-row sm:justify-center">
          <Button
            onClick={() => router.push('/login')}
            className="h-14 w-full sm:w-40 text-lg font-semibold bg-white text-[#212121] hover:bg-gray-200 border-2 border-white"
            size="lg"
          >
            Login
          </Button>
          <Button
            onClick={() => router.push('/register')}
            className="h-14 w-full sm:w-40 text-lg font-semibold bg-transparent border-2 border-white text-white hover:bg-white hover:text-[#212121]"
            size="lg"
          >
            Register
          </Button>
        </div>

        {/* Footer Info */}
        <div className="pt-12 text-sm text-gray-500">
          <p>Internal platform for Delicate Courier team</p>
        </div>
      </div>
    </div>
  )
}