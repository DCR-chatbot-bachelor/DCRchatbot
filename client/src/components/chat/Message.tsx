import type { AnsweredQuestion, MessageAnswer } from "../../types/chat";

interface MessageProps {
  sender: "user" | "bot";
  content: string;
  timestamp?: string;
  answers?: MessageAnswer[];
  editingEventId?: string | null;
  disabled?: boolean;
  onEditAnswer?: (answer: AnsweredQuestion) => void;
}

export function Message({
  sender,
  content,
  timestamp,
  answers = [],
  editingEventId = null,
  disabled = false,
  onEditAnswer,
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
                ✎
              </button>
            ))}
          <p>{content}</p>
        </div>
      </div>
    </article>
  );
}
