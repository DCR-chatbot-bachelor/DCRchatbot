import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ChatWindow, type ChatMessage } from './components/ChatWindow'
import {
  getGraphs,
  sendChatMessage,
  startChat,
} from './api/chatApi'
import type {
  ChatResponse,
  DcrGraph,
  ExecutionMode,
  PendingAnswer,
} from './types/chat'
import './App.css'

function responseToMessage(response: ChatResponse): ChatMessage | null {
  if (!response.message.trim()) return null
  return {
    id: crypto.randomUUID(),
    sender: 'bot',
    content: response.message,
    timestamp: new Date().toISOString(),
  }
}

function App() {
  const [graphs, setGraphs] = useState<DcrGraph[]>([])
  const [selectedGraphId, setSelectedGraphId] = useState('')
  const [mode, setMode] = useState<ExecutionMode>('NeuroSymbolic')
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [pendingDraft, setPendingDraft] = useState<PendingAnswer | null>(null)
  const [message, setMessage] = useState('')
  const [loadingGraphs, setLoadingGraphs] = useState(true)
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getGraphs()
      .then((availableGraphs) => {
        setGraphs(availableGraphs)
        setSelectedGraphId(availableGraphs[0]?.graphId ?? '')
      })
      .catch((requestError: Error) => {
        setError(
          `Kunne ikke hente DCR-grafer. Kontrollér at Web API'et kører. ${requestError.message}`,
        )
      })
      .finally(() => setLoadingGraphs(false))
  }, [])

  async function applyResponse(response: ChatResponse) {
    setSessionId(response.sessionId)
    setPendingDraft(response.pendingDraft ?? null)
    const botMessage = responseToMessage(response)
    if (botMessage) setMessages((current) => [...current, botMessage])
  }

  async function handleStartChat() {
    if (!selectedGraphId) return
    setSending(true)
    setError(null)
    try {
      const response = await startChat(selectedGraphId, mode)
      setMessages([])
      setSessionId(null)
      setPendingDraft(null)
      await applyResponse(response)
    } catch (requestError) {
      setError(`Samtalen kunne ikke startes. ${(requestError as Error).message}`)
    } finally {
      setSending(false)
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const trimmedMessage = message.trim()
    if (!trimmedMessage || !sessionId || sending) return

    const userMessage: ChatMessage = {
      id: crypto.randomUUID(),
      sender: 'user',
      content: trimmedMessage,
      timestamp: new Date().toISOString(),
    }
    setMessages((current) => [...current, userMessage])
    setMessage('')
    setSending(true)
    setError(null)
    try {
      await applyResponse(
        await sendChatMessage({
          sessionId,
          message: trimmedMessage,
          mode,
        }),
      )
    } catch (requestError) {
      setError(`Beskeden kunne ikke sendes. ${(requestError as Error).message}`)
    } finally {
      setSending(false)
    }
  }

  async function handleDraftAction(
    action: 'confirm' | 'reject' | 'revise',
  ) {
    if (!sessionId || !pendingDraft || sending) return
    setSending(true)
    setError(null)
    try {
      await applyResponse(
        await sendChatMessage({
          sessionId,
          message: '',
          mode,
          action,
          targetPendingAnswerId: pendingDraft.id,
        }),
      )
    } catch (requestError) {
      setError(
        `Bekræftelsen kunne ikke sendes. ${(requestError as Error).message}`,
      )
    } finally {
      setSending(false)
    }
  }

  const selectedGraph = graphs.find(
    (graph) => graph.graphId === selectedGraphId,
  )

  return (
    <main className="app-shell">
      <header className="app-header">
        <div className="brand">
          <div className="brand__mark" aria-hidden="true">D</div>
          <div>
            <strong>DCR Chatbot</strong>
            <span>Neuro-symbolsk vejledning</span>
          </div>
        </div>
        <div className="status-pill">
          <span className="status-pill__dot" aria-hidden="true" />
          Forbundet til klienten
        </div>
      </header>

      <div className="app-layout">
        <aside className="sidebar">
          <div>
            <p className="eyebrow">Samtaleopsætning</p>
            <h1>Vælg en DCR-graf</h1>
            <p className="sidebar__intro">
              Vælg den graf, som samtalen skal følge. Assistentens svar
              valideres mod grafens aktuelle tilstand.
            </p>
          </div>

          <label className="field-label" htmlFor="graph">
            Tilgængelige grafer
          </label>
          <select
            id="graph"
            className="select"
            value={selectedGraphId}
            disabled={loadingGraphs || sending || graphs.length === 0}
            onChange={(event) => setSelectedGraphId(event.target.value)}
          >
            <option value="">
              {loadingGraphs ? 'Henter grafer...' : 'Vælg en graf'}
            </option>
            {graphs.map((graph) => (
              <option key={graph.graphId} value={graph.graphId}>
                {graph.title || graph.graphId}
              </option>
            ))}
          </select>

          {selectedGraph && (
            <div className="graph-summary">
              <span className="graph-summary__icon" aria-hidden="true">⌁</span>
              <div>
                <strong>{selectedGraph.title || selectedGraph.graphId}</strong>
                <span>
                  ID: {selectedGraph.graphId}
                  {selectedGraph.language && ` · ${selectedGraph.language}`}
                </span>
              </div>
            </div>
          )}

          <fieldset className="mode-picker">
            <legend className="field-label">Kørselstilstand</legend>
            <label>
              <input
                type="radio"
                name="mode"
                checked={mode === 'NeuroSymbolic'}
                onChange={() => setMode('NeuroSymbolic')}
              />
              <span>
                <strong>Neuro-symbolsk</strong>
                <small>Graf, guardrails og bekræftelser</small>
              </span>
            </label>
            <label>
              <input
                type="radio"
                name="mode"
                checked={mode === 'Baseline'}
                onChange={() => setMode('Baseline')}
              />
              <span>
                <strong>Baseline</strong>
                <small>Ren LLM til sammenligning</small>
              </span>
            </label>
          </fieldset>

          <button
            type="button"
            className="button button--primary button--wide"
            disabled={!selectedGraphId || sending}
            onClick={handleStartChat}
          >
            {sending && !sessionId ? 'Starter...' : 'Start ny samtale'}
          </button>
        </aside>

        <section className="chat-panel">
          <div className="chat-panel__header">
            <div>
              <p className="eyebrow">Aktiv samtale</p>
              <h2>{selectedGraph?.title || 'Ingen graf valgt'}</h2>
            </div>
            {sessionId && <span className="session-label">Session aktiv</span>}
          </div>

          {error && (
            <div className="error-banner" role="alert">
              <strong>Der opstod en fejl</strong>
              <span>{error}</span>
            </div>
          )}

          <ChatWindow
            messages={messages}
            pendingDraft={pendingDraft}
            disabled={sending}
            onDraftAction={handleDraftAction}
          />

          <form className="composer" onSubmit={handleSubmit}>
            <label className="sr-only" htmlFor="message">
              Skriv din besked
            </label>
            <textarea
              id="message"
              value={message}
              disabled={!sessionId || sending}
              onChange={(event) => setMessage(event.target.value)}
              placeholder={
                sessionId
                  ? 'Skriv en besked til assistenten...'
                  : 'Start en samtale for at skrive en besked'
              }
              rows={1}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && !event.shiftKey) {
                  event.preventDefault()
                  event.currentTarget.form?.requestSubmit()
                }
              }}
            />
            <button
              type="submit"
              className="send-button"
              aria-label="Send besked"
              disabled={!sessionId || !message.trim() || sending}
            >
              {sending ? '...' : '↑'}
            </button>
          </form>
          <p className="composer-hint">Enter for at sende · Shift + Enter for ny linje</p>
        </section>
      </div>
    </main>
  )
}

export default App
