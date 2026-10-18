import type { AnsweredQuestion } from "../../types/chat";

interface EditAnswerBannerProps {
  answer: AnsweredQuestion;
  onCancel: () => void;
}

/** Vises over tekstfeltet, så borgeren kan se, hvilket svar der rettes. */
export function EditAnswerBanner({ answer, onCancel }: EditAnswerBannerProps) {
  return (
    <div className="edit-banner" role="status">
      <span>
        Du retter dit svar på <strong>{answer.question}</strong> (nu:{" "}
        {answer.value})
      </span>
      <button type="button" className="button button--text" onClick={onCancel}>
        Annuller
      </button>
    </div>
  );
}
