import type { PendingAnswer } from "../../types/chat";

interface DraftConfirmationProps {
  draft: PendingAnswer;
  disabled: boolean;
  onConfirm: () => void;
  onReject: () => void;
}

/** Viser kladden fra backenden og lader borgeren bekræfte eller afvise den (FR-HITL-1). */
export function DraftConfirmation({
  draft,
  disabled,
  onConfirm,
  onReject,
}: DraftConfirmationProps) {
  return (
    <div className="draft-card">
      {draft.question ? (
        <div>
          <span className="draft-card__label">Spørger du om </span>
          <strong>{draft.question}</strong>
        </div>
      ) : (
        <div>
          <span className="draft-card__label">Bekræft dit svar</span>
          <strong>{draft.proposedValue}</strong>
          {draft.explanation && <p>{draft.explanation}</p>}
        </div>
      )}
      <div className="draft-card__actions">
        <button
          type="button"
          className="button button--primary"
          disabled={disabled}
          onClick={onConfirm}
        >
          Ja
        </button>
        <button
          type="button"
          className="button button--secondary"
          disabled={disabled}
          onClick={onReject}
        >
          Nej
        </button>
      </div>
    </div>
  );
}
