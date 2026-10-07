import { useEffect, useRef } from "react";
import type { ChatMessage, PendingAnswer } from "../../types/chat";
import { DraftConfirmation } from "./DraftConfirmation";
import { Message } from "./Message";

interface ChatWindowProps {
  messages: ChatMessage[];
  pendingDraft: PendingAnswer | null;
  disabled: boolean;
  onConfirmDraft: () => void;
  onRejectDraft: () => void;
}

export function ChatWindow({
  messages,
  pendingDraft,
  disabled,
  onConfirmDraft,
  onRejectDraft,
}: ChatWindowProps) {
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, pendingDraft]);

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
              <p>
                Skriv en besked, så hjælper jeg dig gennem den valgte DCR-graf.
              </p>
            </div>
          )}
          {messages.map((message) => (
            <Message key={message.id} {...message} />
          ))}
          {pendingDraft && (
            <DraftConfirmation
              draft={pendingDraft}
              disabled={disabled}
              onConfirm={onConfirmDraft}
              onReject={onRejectDraft}
            />
          )}
          <div ref={endRef} />
        </div>
      </div>
    </section>
  );
}
