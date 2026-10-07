import { useEffect, useRef } from 'react'
import type { PendingAnswer } from '../types/chat'
import { Message } from './Message'

export interface ChatMessage {
  id: string
  sender: 'user' | 'bot'
  content: string
  timestamp: string
}

interface ChatWindowProps {
  messages: ChatMessage[]
  pendingDraft: PendingAnswer | null
  isRevising: boolean
  disabled: boolean
  onDraftAction: (action: 'confirm' | 'reject' | 'revise') => void
}

export function ChatWindow({
  messages,
  pendingDraft,
  isRevising,
  disabled,
  onDraftAction,
}: ChatWindowProps) {
  const endRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, pendingDraft])

  return (
    <section className="chat-window" aria-label="Chat">
      <div className="chat-window__messages" aria-live="polite">
        <div className="chat-window__content">
          {messages.length === 0 && (
            <div className="chat-window__empty">
              <span className="chat-window__empty-icon" aria-hidden="true">
                ✦
              </span>
              <h2>Start en samtale</h2>
              <p>Skriv en besked, så hjælper jeg dig gennem den valgte DCR-graf.</p>
            </div>
          )}
          {messages.map((message) => (
            <Message key={message.id} {...message} />
          ))}
          {pendingDraft && (
            <div className="draft-card">
              {pendingDraft.question ? (
                <div>
                  <span className="draft-card__label">Spørger du om </span>
                  <strong>{pendingDraft.question}</strong>
                </div>
              ) : (
                <div>
                  <span className="draft-card__label">Bekræft dit svar</span>
                  <strong>{pendingDraft.proposedValue}</strong>
                  {pendingDraft.explanation && <p>{pendingDraft.explanation}</p>}
                </div>
              )}
              <div className="draft-card__actions">
                <button
                  type="button"
                  className="button button--primary"
                  disabled={disabled}
                  onClick={() => onDraftAction('confirm')}
                >
                  Ja, fortsæt
                </button>
                <button
                  type="button"
                  className="button button--secondary"
                  disabled={disabled}
                  onClick={() => onDraftAction('revise')}
                >
                  Ret svar
                </button>
                <button
                  type="button"
                  className="button button--text"
                  disabled={disabled}
                  onClick={() => onDraftAction('reject')}
                >
                  Nej
                </button>
              </div>
              {isRevising && (
                <p className="draft-card__hint">
                  Skriv dit rettede svar i feltet nedenfor.
                </p>
              )}
            </div>
          )}
          <div ref={endRef} />
        </div>
      </div>
    </section>
  )
}
