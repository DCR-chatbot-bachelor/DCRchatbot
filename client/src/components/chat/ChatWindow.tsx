import { useEffect, useRef } from "react";
import { MessageCircle } from "lucide-react";
import type {
  AnsweredQuestion,
  ChatMessage,
  PendingAnswer,
  QuickReply,
} from "../../types/chat";
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
  onReturnTo: (answer: AnsweredQuestion) => void;
  onSelectQuickReply: (reply: QuickReply) => void;
}

export function ChatWindow({
  messages,
  pendingDraft,
  editingEventId,
  disabled,
  onConfirmDraft,
  onRejectDraft,
  onEditAnswer,
  onReturnTo,
  onSelectQuickReply,
}: ChatWindowProps) {
  const endRef = useRef<HTMLDivElement>(null);

  // Knapperne kan kun vælges fra den seneste besked, og ikke mens en kladde
  // eller en rettelse er åben, hvor et klik ellers ville blive et svar på den.
  const lastMessageId = messages.at(-1)?.id;
  const canSelectQuickReplies = pendingDraft === null && editingEventId === null;

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
                <MessageCircle size={18} />
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
              onReturnTo={onReturnTo}
              quickReplies={
                canSelectQuickReplies && message.id === lastMessageId
                  ? message.quickReplies
                  : undefined
              }
              onSelectQuickReply={onSelectQuickReply}
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
