import { useState } from "react";
import { useChatSession } from "../../hooks/useChatSession";
import { useGraphs } from "../../hooks/useGraphs";
import type { ExecutionMode } from "../../types/chat";
import { ChatComposer } from "./ChatComposer";
import { ChatPanelHeader } from "./ChatPanelHeader";
import { ChatSetup } from "./ChatSetup";
import { ChatWindow } from "./ChatWindow";

interface ChatWidgetProps {
  isOpen: boolean;
  onOpenChange: (isOpen: boolean) => void;
}

/** Den flydende chat: boble, panel og samspillet mellem grafvalg og samtale. */
export function ChatWidget({ isOpen, onOpenChange }: ChatWidgetProps) {
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [mode, setMode] = useState<ExecutionMode>("NeuroSymbolic");
  const graphs = useGraphs();
  const chat = useChatSession();
  const error = chat.error ?? graphs.error;

  function close() {
    onOpenChange(false);
    setIsFullscreen(false);
  }

  return (
    <div
      className={`chat-widget ${isOpen ? "chat-widget--open" : ""} ${isFullscreen ? "chat-widget--fullscreen" : ""}`}
    >
      {isOpen && (
        <section className="chat-panel" aria-label="DCR-assistent">
          <ChatPanelHeader
            isFullscreen={isFullscreen}
            onToggleFullscreen={() => setIsFullscreen((current) => !current)}
            onClose={close}
          />
          <ChatSetup
            graphs={graphs.graphs}
            selectedGraphId={graphs.selectedGraphId}
            onGraphChange={graphs.setSelectedGraphId}
            isLoadingGraphs={graphs.isLoading}
            mode={mode}
            onModeChange={setMode}
            hasSession={chat.sessionId !== null}
            isSending={chat.isSending}
            isFullscreen={isFullscreen}
            onStart={() => chat.start(graphs.selectedGraphId, mode)}
          />
          {error && (
            <div className="error-banner" role="alert">
              <strong>Der opstod en fejl</strong>
              <span>{error}</span>
            </div>
          )}
          {chat.sessionId && (
            <>
              <ChatWindow
                messages={chat.messages}
                pendingDraft={chat.pendingDraft}
                disabled={chat.isSending}
                onConfirmDraft={chat.confirmDraft}
                onRejectDraft={chat.rejectDraft}
              />
              <ChatComposer
                disabled={chat.isSending}
                placeholder={
                  isFullscreen ? "Skriv dit svar her..." : "Skriv en besked..."
                }
                onSend={chat.sendMessage}
              />
            </>
          )}
        </section>
      )}
      <button
        className="chat-bubble"
        type="button"
        aria-label={isOpen ? "Luk chatbot" : "Åbn chatbot"}
        onClick={() => onOpenChange(!isOpen)}
      >
        <span className="chat-bubble__pulse" />
        {isOpen ? "×" : "✦"}
      </button>
      {!isOpen && (
        <span className="chat-bubble__label">Har du brug for hjælp?</span>
      )}
    </div>
  );
}
