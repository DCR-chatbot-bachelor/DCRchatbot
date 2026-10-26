import { useEffect, useRef } from "react";
import type { AnsweredQuestion, ChatMessage, PendingAnswer } from "../../types/chat";
import { DraftConfirmation } from "./DraftConfirmation";
import { Message } from "./Message";

interface ChatWindowProps {
  messages: ChatMessage[];
  pendingDraft: PendingAnswer | null;
  editingEventId: string | null;
  disabled: boolean;
  onConfirmDraft: () => void;
  onRejectDraft: () => void;
  onEditAnswer: (answer: AnsweredQuestion) => void;
}

export function ChatWindow({
  messages,
  pendingDraft,
  editingEventId,
  disabled,
  onConfirmDraft,
  onRejectDraft,
  onEditAnswer,
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
            <Message
              key={message.id}
              sender={message.sender}
              content={message.content}
              timestamp={message.timestamp}
              answers={message.answers}
              editingEventId={editingEventId}
              disabled={disabled}
              onEditAnswer={onEditAnswer}
            />
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
