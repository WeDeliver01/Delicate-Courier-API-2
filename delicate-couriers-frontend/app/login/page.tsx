'use client'

import { useState, useEffect, Suspense } from 'react'
import { useRouter, useSearchParams } from 'next/navigation'
import Image from 'next/image'
import { getSupabase } from '@/lib/supabase'
import { useAuth } from '@/components/providers/auth-provider'
import styles from './login.module.css'

function LoginForm() {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const router = useRouter()
  const searchParams = useSearchParams()
  const { session, loading } = useAuth()

  // If we already have a Supabase session (e.g. user opened /login while
  // already signed in), bounce them to the dashboard.
  useEffect(() => {
    if (!loading && session) {
      router.replace('/dashboard')
    }
  }, [loading, session, router])

  useEffect(() => {
    if (searchParams.get('expired') === 'true') {
      setError('Your session has expired. Please log in again.')
    }
    if (searchParams.get('registered') === 'true') {
      setSuccess('Registration successful! Please log in.')
    }
  }, [searchParams])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    setIsLoading(true)

    try {
      const { error: authError } = await getSupabase().auth.signInWithPassword({
        email,
        password,
      })

      if (authError) {
        // Map common Supabase auth errors to friendly messages.
        const msg = authError.message || ''
        if (/invalid login credentials/i.test(msg)) {
          setError('Invalid email or password')
        } else if (/email not confirmed/i.test(msg)) {
          setError('Please confirm your email before logging in.')
        } else {
          setError(msg || 'An error occurred. Please try again.')
        }
        return
      }

      router.replace('/dashboard')
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'An error occurred. Please try again.'
      setError(msg)
    } finally {
      setIsLoading(false)
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-[#212121] text-white px-4">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-[340px] space-y-6"
      >
        {/* Logo + Title */}
        <div className="mb-8 text-center">
          <div className="flex justify-center mb-4 cursor-pointer" onClick={() => router.push('/')}>
            <Image
              src="/dc_logo.png"
              alt="Delicate Courier Logo"
              width={120}
              height={120}
              priority
            />
          </div>
          <h1 className="text-2xl font-bold tracking-wide">Sign In</h1>
          <p className="mt-1 text-sm text-gray-400">Delicate Courier Platform</p>
        </div>

        {/* Success Message */}
        {success && (
          <div className="rounded-lg bg-green-500/20 border border-green-500/30 p-3 text-sm text-green-300">
            {success}
          </div>
        )}

        {/* Error Message */}
        {error && (
          <div className="rounded-lg bg-red-500/20 border border-red-500/30 p-3 text-sm text-red-300">
            {error}
          </div>
        )}

        {/* Email Input */}
        <div className={`${styles.blockCube} ${styles.blockInput}`}>
          <input
            type="email"
            name="email"
            placeholder="Email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
            disabled={isLoading}
            className={styles.inputText}
          />
          <div className={styles.bgTop}>
            <div className={styles.bgInner}></div>
          </div>
          <div className={styles.bgRight}>
            <div className={styles.bgInner}></div>
          </div>
          <div className={styles.bg}>
            <div className={styles.bgInner}></div>
          </div>
        </div>

        {/* Password Input with Toggle */}
        <div className="relative">
          <div className={`${styles.blockCube} ${styles.blockInput}`}>
            <input
              type={showPassword ? 'text' : 'password'}
              name="password"
              placeholder="Password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              disabled={isLoading}
              className={`${styles.inputText} pr-12`}
              style={{ paddingRight: '48px' }}
            />
            <div className={styles.bgTop}>
              <div className={styles.bgInner}></div>
            </div>
            <div className={styles.bgRight}>
              <div className={styles.bgInner}></div>
            </div>
            <div className={styles.bg}>
              <div className={styles.bgInner}></div>
            </div>
          </div>

          {/* Eye Toggle */}
          <button
            type="button"
            onClick={() => setShowPassword(!showPassword)}
            className="absolute right-3 top-1/2 -translate-y-1/2 z-10 text-gray-400 hover:text-white transition-colors focus:outline-none"
            tabIndex={-1}
            aria-label={showPassword ? 'Hide password' : 'Show password'}
          >
            {showPassword ? (
              <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor" className="w-5 h-5">
                <path strokeLinecap="round" strokeLinejoin="round" d="M3.98 8.223A10.477 10.477 0 0 0 1.934 12C3.226 16.338 7.244 19.5 12 19.5c.993 0 1.953-.138 2.863-.395M6.228 6.228A10.451 10.451 0 0 1 12 4.5c4.756 0 8.773 3.162 10.065 7.498a10.522 10.522 0 0 1-4.293 5.774M6.228 6.228 3 3m3.228 3.228 3.65 3.65m7.894 7.894L21 21m-3.228-3.228-3.65-3.65m0 0a3 3 0 1 0-4.243-4.243m4.242 4.242L9.88 9.88" />
              </svg>
            ) : (
              <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor" className="w-5 h-5">
                <path strokeLinecap="round" strokeLinejoin="round" d="M2.036 12.322a1.012 1.012 0 0 1 0-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178Z" />
                <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z" />
              </svg>
            )}
          </button>
        </div>

        {/* Submit Button */}
        <button
          type="submit"
          disabled={isLoading}
          className={`${styles.blockCube} ${styles.blockButton} w-full`}
        >
          <div className={styles.bgTop}>
            <div className={styles.bgInner}></div>
          </div>
          <div className={styles.bgRight}>
            <div className={styles.bgInner}></div>
          </div>
          <div className={styles.bg}>
            <div className={styles.bgInner}></div>
          </div>
          <div className={styles.buttonText}>
            {isLoading ? 'Logging in...' : 'Log In'}
          </div>
        </button>

        {/* Link to Register */}
        <div className="pt-2 text-center text-sm text-gray-400">
          Don&apos;t have an account?{' '}
          <button
            type="button"
            onClick={() => router.push('/register')}
            className="text-cyan-400 hover:text-cyan-300 transition-colors"
          >
            Register here
          </button>
        </div>
      </form>
    </div>
  )
}

export default function LoginPage() {
  return (
    <Suspense
      fallback={
        <div className="flex min-h-screen items-center justify-center bg-[#212121] text-white">
          <p>Loading…</p>
        </div>
      }
    >
      <LoginForm />
    </Suspense>
  )
}
