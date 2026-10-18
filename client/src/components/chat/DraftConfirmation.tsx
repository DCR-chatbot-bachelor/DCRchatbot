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
          {draft.isEventMatch ? (
            <>
              <span className="draft-card__label">
                Jeg forstod, at din besked handler om
              </span>
              <strong>{draft.question}</strong>
            </>
          ) : draft.previousValue != null ? (
            <>
              <span className="draft-card__label">Vil du ændre dit svar på</span>
              <strong>{draft.question}</strong>
              <p>
                Fra <strong>{draft.previousValue}</strong> til{" "}
                <strong>{draft.proposedValue}</strong>
              </p>
            </>
          ) : (
            <>
              <span className="draft-card__label">Svarer du på </span>
              <strong>{draft.question}</strong>
              <p>
                Dit svar: <strong>{draft.proposedValue}</strong>
              </p>
            </>
          )}
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
