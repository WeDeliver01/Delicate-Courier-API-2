'use client'

import { Check } from 'lucide-react'

interface Step {
  id: number
  name: string
  description?: string
}

interface StepperProps {
  steps: Step[]
  currentStep: number
  onStepClick?: (step: number) => void
}

export function Stepper({ steps, currentStep, onStepClick }: StepperProps) {
  return (
    <nav aria-label="Progress" className="mb-8">
      <ol className="flex items-center justify-between">
        {steps.map((step, index) => {
          const isCompleted = currentStep > step.id
          const isCurrent = currentStep === step.id
          const isClickable = onStepClick && (isCompleted || isCurrent)

          return (
            <li key={step.id} className="relative flex-1">
              {/* Connector Line */}
              {index !== steps.length - 1 && (
                <div
                  className={`absolute top-5 left-1/2 w-full h-0.5 ${
                    isCompleted ? 'bg-blue-600' : 'bg-gray-200 dark:bg-gray-700'
                  }`}
                  aria-hidden="true"
                />
              )}

              {/* Step Circle and Label */}
              <div
                className={`relative flex flex-col items-center group ${
                  isClickable ? 'cursor-pointer' : ''
                }`}
                onClick={() => isClickable && onStepClick(step.id)}
              >
                {/* Circle */}
                <span
                  className={`
                    w-10 h-10 flex items-center justify-center rounded-full 
                    text-sm font-semibold transition-all duration-200
                    ${
                      isCompleted
                        ? 'bg-blue-600 text-white'
                        : isCurrent
                        ? 'bg-blue-600 text-white ring-4 ring-blue-100 dark:ring-blue-900'
                        : 'bg-gray-200 dark:bg-gray-700 text-gray-500 dark:text-gray-400'
                    }
                    ${isClickable ? 'group-hover:ring-4 group-hover:ring-blue-100 dark:group-hover:ring-blue-900' : ''}
                  `}
                >
                  {isCompleted ? <Check size={20} /> : step.id}
                </span>

                {/* Label */}
                <span
                  className={`
                    mt-2 text-xs font-medium text-center max-w-[80px]
                    ${
                      isCurrent
                        ? 'text-blue-600 dark:text-blue-400'
                        : isCompleted
                        ? 'text-gray-900 dark:text-gray-100'
                        : 'text-gray-500 dark:text-gray-400'
                    }
                  `}
                >
                  {step.name}
                </span>

                {/* Description (optional) */}
                {step.description && isCurrent && (
                  <span className="mt-1 text-xs text-gray-500 dark:text-gray-400 text-center max-w-[100px]">
                    {step.description}
                  </span>
                )}
              </div>
            </li>
          )
        })}
      </ol>
    </nav>
  )
}