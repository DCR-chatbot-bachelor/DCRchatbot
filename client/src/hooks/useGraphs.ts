import { useEffect, useState } from "react";
import { getGraphs } from "../api/chatApi";
import type { DcrGraph } from "../types/chat";

/** Henter de tilgængelige DCR-grafer og husker hvilken der er valgt ("" = automatisk). */
export function useGraphs() {
  const [graphs, setGraphs] = useState<DcrGraph[]>([]);
  const [selectedGraphId, setSelectedGraphId] = useState("");
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getGraphs()
      // Standard er "Automatisk" (tom streng): grafen findes ud fra
      // borgerens første besked. En fast graf kan vælges til test.
      .then((availableGraphs) => setGraphs(availableGraphs))
      .catch((requestError: Error) => {
        setError(
          `Kunne ikke hente DCR-grafer. Kontrollér at Web API'et kører. ${requestError.message}`,
        );
      })
      .finally(() => setIsLoading(false));
  }, []);

  return { graphs, selectedGraphId, setSelectedGraphId, isLoading, error };
}
