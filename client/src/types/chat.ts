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

export interface AnsweredQuestion {
  eventId: string
  question: string
  value: string
}

export interface ChatMessage {
  id: string
  sender: 'user' | 'bot'
  content: string
  timestamp: string
  /** De bekræftede svar, beskeden gav; hvert kan rettes (FR-HITL-2). */
  answers?: MessageAnswer[]
  /** Knapper under beskeden: grafens emner eller svar på et choice-spørgsmål. */
  quickReplies?: QuickReply[]
}

/** En knap, hvis tekst sendes som borgerens besked, når der trykkes på den. */
export interface QuickReply {
  key: string
  label: string
}

export interface Suggestion {
  eventId: string
  label: string
}

export interface AnswerOption {
  label: string
  value: string
}

export interface MessageAnswer {
  answer: AnsweredQuestion
  /** Svaret er siden rettet og gælder ikke længere. */
  isSuperseded: boolean
}

export interface PendingAnswer {
  id: string
  eventId: string
  question?: string | null
  proposedValue: string
  /** Det nuværende svar, når kladden retter et tidligere svar. */
  previousValue?: string | null
  explanation?: string | null
  isConfirmed: boolean
  isAutoInferred: boolean
  isEventMatch: boolean
}

export interface ChatRequest {
  sessionId?: string | null
  message: string
  mode: ExecutionMode
  action?: 'confirm' | 'reject' | 'revise' | null
  targetPendingAnswerId?: string | null
  eventId?: string | null
}

export interface ChatResponse {
  sessionId: string
  message: string
  mode: ExecutionMode
  pendingDraft?: PendingAnswer | null
  requiresConfirmation: boolean
  confirmedAnswer?: AnsweredQuestion | null
  suggestions?: Suggestion[]
  answerOptions?: AnswerOption[]
  availableEvents: DcrEvent[]
  executedEvents: DcrEvent[]
  isEnded: boolean
}
