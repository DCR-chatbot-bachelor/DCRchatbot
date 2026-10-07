import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { ChatWindow, type ChatMessage } from "./components/ChatWindow";
import {
  confirmChatDraft,
  getGraphs,
  rejectChatDraft,
  reviseChatDraft,
  sendChatMessage,
  startChat,
} from "./api/chatApi";
import type {
  ChatResponse,
  DcrGraph,
  ExecutionMode,
  PendingAnswer,
} from "./types/chat";
import "./App.css";

function responseToMessage(response: ChatResponse): ChatMessage | null {
  if (!response.message.trim()) return null;
  return {
    id: crypto.randomUUID(),
    sender: "bot",
    content: response.message,
    timestamp: new Date().toISOString(),
  };
}

function App() {
  const [graphs, setGraphs] = useState<DcrGraph[]>([]);
  const [selectedGraphId, setSelectedGraphId] = useState("");
  const [mode, setMode] = useState<ExecutionMode>("NeuroSymbolic");
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [pendingDraft, setPendingDraft] = useState<PendingAnswer | null>(null);
  const [message, setMessage] = useState("");
  const [isRevisingDraft, setIsRevisingDraft] = useState(false);
  const [loadingGraphs, setLoadingGraphs] = useState(true);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isChatOpen, setIsChatOpen] = useState(false);
  const [isChatFullscreen, setIsChatFullscreen] = useState(false);

  useEffect(() => {
    getGraphs()
      .then((availableGraphs) => {
        setGraphs(availableGraphs);
        setSelectedGraphId(availableGraphs[0]?.graphId ?? "");
      })
      .catch((requestError: Error) => {
        setError(
          `Kunne ikke hente DCR-grafer. Kontrollér at Web API'et kører. ${requestError.message}`,
        );
      })
      .finally(() => setLoadingGraphs(false));
  }, []);

  async function applyResponse(response: ChatResponse) {
    setSessionId(response.sessionId);
    setPendingDraft(response.pendingDraft ?? null);
    const botMessage = responseToMessage(response);
    if (botMessage) setMessages((current) => [...current, botMessage]);
  }

  async function handleStartChat() {
    if (!selectedGraphId) return;
    setIsChatOpen(true);
    setSending(true);
    setError(null);
    try {
      const response = await startChat(selectedGraphId, mode);
      setMessages([]);
      setSessionId(null);
      setPendingDraft(null);
      setIsRevisingDraft(false);
      await applyResponse(response);
    } catch (requestError) {
      setError(
        `Samtalen kunne ikke startes. ${(requestError as Error).message}`,
      );
    } finally {
      setSending(false);
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const trimmedMessage = message.trim();
    if (!trimmedMessage || !sessionId || sending) return;

    const userMessage: ChatMessage = {
      id: crypto.randomUUID(),
      sender: "user",
      content: trimmedMessage,
      timestamp: new Date().toISOString(),
    };
    setMessages((current) => [...current, userMessage]);
    setMessage("");
    setSending(true);
    setError(null);
    try {
      const response = isRevisingDraft
        ? await reviseChatDraft({
            sessionId,
            message: trimmedMessage,
            mode,
            targetPendingAnswerId: pendingDraft?.id,
          })
        : await sendChatMessage({
            sessionId,
            message: trimmedMessage,
            mode,
          });
      await applyResponse(response);
      setIsRevisingDraft(false);
    } catch (requestError) {
      setError(
        `Beskeden kunne ikke sendes. ${(requestError as Error).message}`,
      );
    } finally {
      setSending(false);
    }
  }

  async function handleDraftAction(action: "confirm" | "reject" | "revise") {
    if (!sessionId || !pendingDraft || sending) return;
    if (action === "revise") {
      setIsRevisingDraft(true);
      return;
    }
    setSending(true);
    setError(null);
    try {
      const response =
        action === "confirm"
          ? await confirmChatDraft(sessionId, pendingDraft.id)
          : await rejectChatDraft(sessionId, pendingDraft.id);
      await applyResponse(response);
    } catch (requestError) {
      setError(
        `Bekræftelsen kunne ikke sendes. ${(requestError as Error).message}`,
      );
    } finally {
      setSending(false);
    }
  }

  return (
    <main className="demo-page">
      <header className="site-header">
        <a className="site-brand" href="#top" aria-label="DCR Bot">
          <span className="site-brand__mark">✦</span>
          <span>
            <strong>DCR Bot</strong>
            <small>Digital rådgivning</small>
          </span>
        </a>
        <nav className="site-nav" aria-label="Hovedmenu">
          <a href="#services">Selvbetjening</a>
          <a href="#guides">Guides</a>
          <a href="#about">Om DCR Bot</a>
        </nav>
        <button className="site-login" type="button">
          Log ind
        </button>
      </header>

      <section className="hero-section" id="top">
        <div className="hero-copy">
          <p className="eyebrow">Velkommen til DCR Bot</p>
          <h1>
            Din digitale vej
            <br />
            til <em>tryg</em> rådgivning.
          </h1>
          <p className="hero-text">
            Få hjælp til at finde den rigtige information og udfylde dine
            oplysninger. Vores digitale assistent guider dig trin for trin.
          </p>
          <div className="hero-actions">
            <button
              className="hero-button"
              type="button"
              onClick={() => setIsChatOpen(true)}
            >
              Start en samtale <span aria-hidden="true">↗</span>
            </button>
            <a href="#services" className="hero-link">
              Se selvbetjening <span>↓</span>
            </a>
          </div>
          <div className="trust-row">
            <span className="trust-avatar">✓</span>
            <span>Sikker og fortrolig vejledning</span>
            <span className="trust-divider" />
            <span>Tilgængelig døgnet rundt</span>
          </div>
        </div>
        <div className="hero-visual" aria-hidden="true">
          <div className="hero-orb hero-orb--large" />
          <div className="hero-orb hero-orb--small" />
          <div className="hero-card hero-card--top">
            <span>✦</span> Enkel vejledning
          </div>
          <div className="hero-card hero-card--bottom">
            <strong>24/7</strong>
            <span>Altid her for dig</span>
          </div>
          <div className="hero-person">✦</div>
        </div>
      </section>

      <section className="feature-section" id="services">
        <div>
          <p className="eyebrow">Én samlet indgang</p>
          <h2>Det skal være nemt at komme videre.</h2>
        </div>
        <div className="feature-grid">
          <article>
            <span className="feature-icon">⌁</span>
            <h3>Find svar</h3>
            <p>Få klar og forståelig information, når du har brug for den.</p>
          </article>
          <article>
            <span className="feature-icon">✓</span>
            <h3>Bliv guidet</h3>
            <p>Gå gennem processen i dit eget tempo med hjælp undervejs.</p>
          </article>
          <article>
            <span className="feature-icon">↗</span>
            <h3>Kom videre</h3>
            <p>
              Få overblik over dine næste skridt og de oplysninger, du mangler.
            </p>
          </article>
        </div>
      </section>

      <div
        className={`chat-widget ${isChatOpen ? "chat-widget--open" : ""} ${isChatFullscreen ? "chat-widget--fullscreen" : ""}`}
      >
        {isChatOpen && (
          <section className="chat-panel" aria-label="DCR-assistent">
            <div className="chat-panel__header">
              <div className="chat-agent">
                <span className="chat-agent__avatar">✦</span>
                <div>
                  <strong>DCR-assistenten</strong>
                  <span>
                    <i /> Online nu
                  </span>
                </div>
              </div>
              <div className="chat-panel__actions">
                <button
                  className="chat-expand"
                  type="button"
                  aria-label={
                    isChatFullscreen
                      ? "Forlad fuldskærm"
                      : "Åbn chatbot i fuldskærm"
                  }
                  title={isChatFullscreen ? "Forlad fuldskærm" : "Fuldskærm"}
                  onClick={() =>
                    setIsChatFullscreen((fullscreen) => !fullscreen)
                  }
                >
                  {isChatFullscreen ? "↙" : "↗"}
                </button>
                <button
                  className="chat-close"
                  type="button"
                  aria-label="Luk chatbot"
                  onClick={() => {
                    setIsChatOpen(false);
                    setIsChatFullscreen(false);
                  }}
                >
                  ×
                </button>
              </div>
            </div>
            <div className="chat-setup">
              <label className="field-label" htmlFor="graph">
                {isChatFullscreen
                  ? "Samtaleområde"
                  : "Hvad vil du have hjælp til?"}
              </label>
              <select
                id="graph"
                className="select"
                value={selectedGraphId}
                disabled={loadingGraphs || sending || graphs.length === 0}
                onChange={(event) => setSelectedGraphId(event.target.value)}
              >
                <option value="">
                  {loadingGraphs ? "Henter muligheder..." : "Vælg et område"}
                </option>
                {graphs.map((graph) => (
                  <option key={graph.graphId} value={graph.graphId}>
                    {graph.title || graph.graphId}
                  </option>
                ))}
              </select>
              <fieldset className="mode-picker">
                <legend className="field-label">
                  {isChatFullscreen ? "Tilstand" : "Demo mode"}
                </legend>
                <label>
                  <input
                    type="radio"
                    name="mode"
                    checked={mode === "NeuroSymbolic"}
                    onChange={() => setMode("NeuroSymbolic")}
                  />
                  <span>Neuro-symbolsk</span>
                </label>
                <label>
                  <input
                    type="radio"
                    name="mode"
                    checked={mode === "Baseline"}
                    onChange={() => setMode("Baseline")}
                  />
                  <span>Baseline</span>
                </label>
              </fieldset>
              <button
                type="button"
                className="button button--primary button--wide"
                disabled={!selectedGraphId || sending}
                onClick={handleStartChat}
              >
                {sending && !sessionId
                  ? "Starter..."
                  : sessionId
                    ? isChatFullscreen
                      ? "Ny samtale"
                      : "Start ny samtale"
                    : "Start samtale"}
              </button>
            </div>
            {error && (
              <div className="error-banner" role="alert">
                <strong>Der opstod en fejl</strong>
                <span>{error}</span>
              </div>
            )}
            {sessionId && (
              <ChatWindow
                messages={messages}
                pendingDraft={pendingDraft}
                isRevising={isRevisingDraft}
                disabled={sending}
                onDraftAction={handleDraftAction}
              />
            )}
            {sessionId && (
              <form className="composer" onSubmit={handleSubmit}>
                <label className="sr-only" htmlFor="message">
                  Skriv din besked
                </label>
                <textarea
                  id="message"
                  value={message}
                  disabled={sending}
                  onChange={(event) => setMessage(event.target.value)}
                  placeholder={
                    isRevisingDraft
                      ? "Skriv dit rettede svar..."
                      : isChatFullscreen
                        ? "Skriv dit svar her..."
                        : "Skriv en besked..."
                  }
                  rows={1}
                  onKeyDown={(event) => {
                    if (event.key === "Enter" && !event.shiftKey) {
                      event.preventDefault();
                      event.currentTarget.form?.requestSubmit();
                    }
                  }}
                />
                <button
                  type="submit"
                  className="send-button"
                  aria-label="Send besked"
                  disabled={!message.trim() || sending}
                >
                  {sending ? "..." : "↑"}
                </button>
              </form>
            )}
          </section>
        )}
        <button
          className="chat-bubble"
          type="button"
          aria-label={isChatOpen ? "Luk chatbot" : "Åbn chatbot"}
          onClick={() => setIsChatOpen((open) => !open)}
        >
          <span className="chat-bubble__pulse" />
          {isChatOpen ? "×" : "✦"}
        </button>
        {!isChatOpen && (
          <span className="chat-bubble__label">Har du brug for hjælp?</span>
        )}
      </div>
    </main>
  );
}

export default App;
