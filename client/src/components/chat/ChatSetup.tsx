import type { DcrGraph, ExecutionMode } from "../../types/chat";
import { Plus } from "lucide-react";

interface ChatSetupProps {
  graphs: DcrGraph[];
  selectedGraphId: string;
  onGraphChange: (graphId: string) => void;
  isLoadingGraphs: boolean;
  mode: ExecutionMode;
  onModeChange: (mode: ExecutionMode) => void;
  hasSession: boolean;
  isSending: boolean;
  isFullscreen: boolean;
  onStart: () => void;
}

// FR-TEST-1: de tre tilstande, fra "DCR styrer alt" til "LLM'en styrer alt".
const MODES: { mode: ExecutionMode; label: string; description: string }[] = [
  {
    mode: "DcrStrict",
    label: "DCR-styret",
    description: "DCR vælger svaret, og teksten vises ordret fra grafen.",
  },
  {
    mode: "DcrGuided",
    label: "DCR-guidet",
    description: "DCR vælger svaret; AI'en formulerer det ud fra grafens tekst.",
  },
  {
    mode: "LlmOnly",
    label: "Kun AI",
    description: "AI'en vælger og formulerer svaret alene ud fra grafens FAQ-tekst, uden DCR.",
  },
];

/** Valg af DCR-graf og tilstand (FR-TEST-1) før samtalen startes. */
export function ChatSetup({
  graphs,
  selectedGraphId,
  onGraphChange,
  isLoadingGraphs,
  mode,
  onModeChange,
  hasSession,
  isSending,
  isFullscreen,
  onStart,
}: ChatSetupProps) {
  function startLabel() {
    if (isSending && !hasSession) return "Starter...";
    if (!hasSession) return "Start samtale";
    return isFullscreen ? "Ny samtale" : "Start ny samtale";
  }

  return (
    <div className="chat-setup">
      <label className="field-label" htmlFor="graph">
        {isFullscreen ? "Samtaleområde" : "Hvad vil du have hjælp til?"}
      </label>
      <select
        id="graph"
        className="select"
        value={selectedGraphId}
        disabled={isLoadingGraphs || isSending || graphs.length === 0}
        onChange={(event) => onGraphChange(event.target.value)}
      >
        <option value="">
          {isLoadingGraphs ? "Henter muligheder..." : "Vælg et område"}
        </option>
        {graphs.map((graph) => (
          <option key={graph.graphId} value={graph.graphId}>
            {graph.title || graph.graphId}
          </option>
        ))}
      </select>
      <fieldset className="mode-picker">
        <legend className="field-label">
          {isFullscreen ? "Tilstand" : "Demo mode"}
        </legend>
        {MODES.map((option) => (
          <label key={option.mode} title={option.description}>
            <input
              type="radio"
              name="mode"
              checked={mode === option.mode}
              onChange={() => onModeChange(option.mode)}
            />
            <span>{option.label}</span>
          </label>
        ))}
      </fieldset>
      <button
        type="button"
        className="button button--primary button--wide"
        disabled={!selectedGraphId || isSending}
        onClick={onStart}
      >
        {!hasSession && <Plus size={14} aria-hidden="true" />}
        <span>{startLabel()}</span>
      </button>
    </div>
  );
}
