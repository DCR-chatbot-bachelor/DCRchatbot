import { useState } from "react";
import type { FormEvent } from "react";

interface ChatComposerProps {
  disabled: boolean;
  placeholder: string;
  onSend: (message: string) => void;
}

export function ChatComposer({
  disabled,
  placeholder,
  onSend,
}: ChatComposerProps) {
  const [message, setMessage] = useState("");

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!message.trim() || disabled) return;
    onSend(message);
    setMessage("");
  }

  return (
    <form className="composer" onSubmit={handleSubmit}>
      <label className="sr-only" htmlFor="message">
        Skriv din besked
      </label>
      <textarea
        id="message"
        value={message}
        disabled={disabled}
        onChange={(event) => setMessage(event.target.value)}
        placeholder={placeholder}
        rows={1}
        onKeyDown={(event) => {
          if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            event.currentTarget.form?.requestSubmit();
          }
        }}
      />
      <button
        type="submit"
        className="send-button"
        aria-label="Send besked"
        disabled={!message.trim() || disabled}
      >
        {disabled ? "..." : "↑"}
      </button>
    </form>
  );
}
