'use client'

import { useRouter } from 'next/navigation'
import Image from 'next/image'

export default function RegisterInfoPage() {
  const router = useRouter()

  return (
    <div className="flex min-h-screen items-center justify-center bg-[#212121] text-white px-4">
      <div className="w-full max-w-[420px] text-center space-y-6">
        <div className="flex justify-center cursor-pointer" onClick={() => router.push('/')}>
          <Image
            src="/dc_logo.png"
            alt="Delicate Courier Logo"
            width={120}
            height={120}
            priority
          />
        </div>

        <div className="space-y-2">
          <h1 className="text-2xl font-bold tracking-wide">Account Required</h1>
          <p className="text-sm text-gray-400">Delicate Courier Platform</p>
        </div>

        <div className="rounded-lg bg-cyan-500/10 border border-cyan-500/30 p-4 text-sm text-cyan-100">
          Accounts are provisioned by your administrator. Please contact your
          administrator to request access to the Delicate Courier Platform.
        </div>

        <button
          type="button"
          onClick={() => router.push('/login')}
          className="w-full rounded-md bg-cyan-500 py-2.5 font-semibold text-black hover:bg-cyan-400 transition-colors"
        >
          Back to Login
        </button>
      </div>
    </div>
  )
}
