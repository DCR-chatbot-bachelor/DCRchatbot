import { useEffect, useState } from "react";
import { getGraphs } from "../api/chatApi";
import type { DcrGraph } from "../types/chat";

/** Henter de tilgængelige DCR-grafer og husker hvilken der er valgt. */
export function useGraphs() {
  const [graphs, setGraphs] = useState<DcrGraph[]>([]);
  const [selectedGraphId, setSelectedGraphId] = useState("");
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getGraphs()
      .then((availableGraphs) => {
        setGraphs(availableGraphs);
        setSelectedGraphId(availableGraphs[0]?.graphId ?? "");
      })
      .catch((requestError: Error) => {
        setError(
          `Kunne ikke hente DCR-grafer. Kontrollér at Web API'et kører. ${requestError.message}`,
        );
      })
      .finally(() => setIsLoading(false));
  }, []);

  return { graphs, selectedGraphId, setSelectedGraphId, isLoading, error };
}
