interface MessageProps {
  sender: "user" | "bot";
  content: string;
  timestamp?: string;
}

export function Message({ sender, content, timestamp }: MessageProps) {
  return (
    <article className={`message message--${sender}`}>
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
        </div>
        <p>{content}</p>
      </div>
    </article>
  );
}
