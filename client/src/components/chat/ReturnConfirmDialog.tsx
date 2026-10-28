import { useEffect, useRef } from "react";
import { Undo2 } from "lucide-react";
import type { AnsweredQuestion } from "../../types/chat";

interface ReturnConfirmDialogProps {
  answer: AnsweredQuestion;
  onConfirm: () => void;
  onCancel: () => void;
}

/**
 * Spørger inde i chatpanelet, om samtalen skal gå tilbage til et svar.
 * Erstatter browserens window.confirm, som dækkede hele skærmen.
 */
export function ReturnConfirmDialog({ answer, onConfirm, onCancel }: ReturnConfirmDialogProps) {
  const confirmRef = useRef<HTMLButtonElement>(null);

  // Fokus på "Gå tilbage", og Esc lukker boksen.
  useEffect(() => {
    confirmRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") onCancel();
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onCancel]);

  return (
    <div className="return-dialog" onClick={onCancel}>
      <div
        className="return-dialog__card"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="return-dialog-title"
        aria-describedby="return-dialog-text"
        onClick={(event) => event.stopPropagation()}
      >
        <span className="return-dialog__icon" aria-hidden="true">
          <Undo2 size={16} />
        </span>
        <h3 id="return-dialog-title">Gå tilbage i samtalen?</h3>
        <p id="return-dialog-text">
          Samtalen går tilbage til dit svar på <em>&quot;{answer.question}&quot;</em>.
          Svarene, du har givet efter det, bliver fjernet.
        </p>
        <div className="return-dialog__actions">
          <button type="button" className="button button--secondary" onClick={onCancel}>
            Annuller
          </button>
          <button ref={confirmRef} type="button" className="button button--primary" onClick={onConfirm}>
            Gå tilbage
          </button>
        </div>
      </div>
    </div>
  );
}
