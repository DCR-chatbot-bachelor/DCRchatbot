export type ExecutionMode = 'Baseline' | 'NeuroSymbolic'

export interface DcrGraph {
  graphId: string
  title: string
  language: string
}

export interface DcrEvent {
  id: string
  included: boolean
  isProductive: boolean
  sequence: number
  label: string
  value?: string | null
  displayValue?: string | null
  description?: string | null
  explanation?: string | null
  dataType: string
  isEnabled: boolean
  isExecuted?: boolean | null
  isPending: boolean
  allowedValues: string[]
}

export interface PendingAnswer {
  id: string
  eventId: string
  question?: string | null
  proposedValue: string
  explanation?: string | null
  isConfirmed: boolean
  isAutoInferred: boolean
}

export interface ChatRequest {
  sessionId?: string | null
  message: string
  mode: ExecutionMode
  action?: 'confirm' | 'reject' | 'revise' | null
  targetPendingAnswerId?: string | null
}

export interface ChatResponse {
  sessionId: string
  message: string
  mode: ExecutionMode
  pendingDraft?: PendingAnswer | null
  requiresConfirmation: boolean
  availableEvents: DcrEvent[]
  executedEvents: DcrEvent[]
  isEnded: boolean
}
