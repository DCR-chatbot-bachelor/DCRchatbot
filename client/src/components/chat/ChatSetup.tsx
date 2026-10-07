import type { DcrGraph, ExecutionMode } from "../../types/chat";

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

/** Valg af DCR-graf og tilstand (Baseline/NeuroSymbolic, FR-TEST-1) før samtalen startes. */
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
        <label>
          <input
            type="radio"
            name="mode"
            checked={mode === "NeuroSymbolic"}
            onChange={() => onModeChange("NeuroSymbolic")}
          />
          <span>Neuro-symbolsk</span>
        </label>
        <label>
          <input
            type="radio"
            name="mode"
            checked={mode === "Baseline"}
            onChange={() => onModeChange("Baseline")}
          />
          <span>Baseline</span>
        </label>
      </fieldset>
      <button
        type="button"
        className="button button--primary button--wide"
        disabled={!selectedGraphId || isSending}
        onClick={onStart}
      >
        {startLabel()}
      </button>
    </div>
  );
}
