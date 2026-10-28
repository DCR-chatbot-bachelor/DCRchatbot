import { Maximize2, Minimize2, Sparkles, X } from "lucide-react";

interface ChatPanelHeaderProps {
  isFullscreen: boolean;
  onToggleFullscreen: () => void;
  onClose: () => void;
}

export function ChatPanelHeader({
  isFullscreen,
  onToggleFullscreen,
  onClose,
}: ChatPanelHeaderProps) {
  return (
    <div className="chat-panel__header">
      <div className="chat-agent">
        <span className="chat-agent__avatar">
          <Sparkles size={17} strokeWidth={2.2} aria-hidden="true" />
        </span>
        <div>
          <strong>DCR-assistenten</strong>
          <span>
            <i /> Online nu
          </span>
        </div>
      </div>
      <div className="chat-panel__actions">
        <button
          className="chat-expand"
          type="button"
          aria-label={
            isFullscreen ? "Forlad fuldskærm" : "Åbn chatbot i fuldskærm"
          }
          title={isFullscreen ? "Forlad fuldskærm" : "Fuldskærm"}
          onClick={onToggleFullscreen}
        >
          {isFullscreen ? (
            <Minimize2 size={16} aria-hidden="true" />
          ) : (
            <Maximize2 size={16} aria-hidden="true" />
          )}
        </button>
        <button
          className="chat-close"
          type="button"
          aria-label="Luk chatbot"
          onClick={onClose}
        >
          <X size={19} aria-hidden="true" />
        </button>
      </div>
    </div>
  );
}
