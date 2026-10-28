import type { AnsweredQuestion, MessageAnswer, QuickReply } from "../../types/chat";
import { Pencil } from "lucide-react";

interface MessageProps {
  sender: "user" | "bot";
  content: string;
  timestamp?: string;
  answers?: MessageAnswer[];
  editingEventId?: string | null;
  disabled?: boolean;
  onEditAnswer?: (answer: AnsweredQuestion) => void;
  onReturnTo?: (answer: AnsweredQuestion) => void;
  /** Kun sat på den seneste bot-besked, så gamle knapper ikke kan vælges. */
  quickReplies?: QuickReply[];
  onSelectQuickReply?: (reply: QuickReply) => void;
}

export function Message({
  sender,
  content,
  timestamp,
  answers = [],
  editingEventId = null,
  disabled = false,
  onEditAnswer,
  onReturnTo,
  quickReplies = [],
  onSelectQuickReply,
}: MessageProps) {
  const editable = answers.filter((a) => !a.isSuperseded);
  const isSuperseded = answers.length > 0 && editable.length === 0;

  return (
    <article
      className={`message message--${sender} ${isSuperseded ? "message--superseded" : ""}`}
    >
      <div className="message__body">
        <div className="message__meta">
          <strong>{sender === "user" ? "Dig" : "DCR-assistenten"}</strong>
          {timestamp && (
            <time dateTime={timestamp}>
              {new Date(timestamp).toLocaleTimeString("da-DK", {
                hour: "2-digit",
                minute: "2-digit",
              })}
            </time>
          )}
          {isSuperseded && <span className="message__note">rettet</span>}
        </div>
        <div className="message__bubble">
          {onEditAnswer &&
            editable.map(({ answer }) => (
              // FR-HITL-2: en lille blyant pr. svar, beskeden gav.
              <button
                key={answer.eventId}
                type="button"
                className={`message__edit ${editingEventId === answer.eventId ? "message__edit--active" : ""}`}
                title={`Ret dit svar på "${answer.question}" (${answer.value})`}
                aria-label={`Ret dit svar på ${answer.question}`}
                disabled={disabled}
                onClick={() => onEditAnswer(answer)}
              >
                <Pencil size={12} aria-hidden="true" />
              </button>
              <span key={answer.eventId} className="message__answer-actions">
                <button
                  type="button"
                  className={`message__edit ${editingEventId === answer.eventId ? "message__edit--active" : ""}`}
                  title={`Ret dit svar på "${answer.question}" (${answer.value})`}
                  aria-label={`Ret dit svar på ${answer.question}`}
                  disabled={disabled}
                  onClick={() => onEditAnswer(answer)}
                >
                  ✎
                </button>
                {(answer.isAutoInferred || answer.sourceMessageId) && onReturnTo && (
                  <button
                    type="button"
                    className="message__return"
                    title="Vend tilbage hertil"
                    aria-label="Vend tilbage hertil"
                    disabled={disabled}
                    onClick={() => onReturnTo(answer)}
                  >
                      ↩
                  </button>
                )}
              </span>
            ))}
          <p>{content}</p>
        </div>
        {onSelectQuickReply && quickReplies.length > 0 && (
          // Grafens emner (FR-LLM-3) eller svar på et choice-spørgsmål;
          // et klik sendes som borgerens besked.
          <div className="message__quick-replies">
            {quickReplies.map((reply) => (
              <button
                key={reply.key}
                type="button"
                className="quick-reply"
                disabled={disabled}
                onClick={() => onSelectQuickReply(reply)}
              >
                {reply.label}
              </button>
            ))}
          </div>
        )}
      </div>
    </article>
  );
}
