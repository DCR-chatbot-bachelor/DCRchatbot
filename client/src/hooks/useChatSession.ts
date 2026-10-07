import { useState } from "react";
import {
  confirmChatDraft,
  rejectChatDraft,
  sendChatMessage,
  startChat,
} from "../api/chatApi";
import type {
  ChatMessage,
  ChatResponse,
  ExecutionMode,
  PendingAnswer,
} from "../types/chat";

function createMessage(
  sender: ChatMessage["sender"],
  content: string,
): ChatMessage {
  return {
    id: crypto.randomUUID(),
    sender,
    content,
    timestamp: new Date().toISOString(),
  };
}

/**
 * Holder samtalens tilstand (session-id, beskeder, kladde) og står for alle
 * kald til chat-API'et, så komponenterne kun skal vise data og kalde handlinger.
 */
export function useChatSession() {
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [mode, setMode] = useState<ExecutionMode>("NeuroSymbolic");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [pendingDraft, setPendingDraft] = useState<PendingAnswer | null>(null);
  const [isSending, setIsSending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function addMessage(sender: ChatMessage["sender"], content: string) {
    if (!content.trim()) return;
    setMessages((current) => [...current, createMessage(sender, content)]);
  }

  function applyResponse(response: ChatResponse) {
    setSessionId(response.sessionId);
    setPendingDraft(response.pendingDraft ?? null);
    addMessage("bot", response.message);
  }

  async function run(
    action: () => Promise<ChatResponse>,
    errorPrefix: string,
  ) {
    setIsSending(true);
    setError(null);
    try {
      applyResponse(await action());
    } catch (requestError) {
      setError(`${errorPrefix} ${(requestError as Error).message}`);
    } finally {
      setIsSending(false);
    }
  }

  async function start(graphId: string, selectedMode: ExecutionMode) {
    if (!graphId || isSending) return;
    setSessionId(null);
    setMessages([]);
    setPendingDraft(null);
    setMode(selectedMode);
    await run(
      () => startChat(graphId, selectedMode),
      "Samtalen kunne ikke startes.",
    );
  }

  async function sendMessage(text: string) {
    const message = text.trim();
    if (!message || !sessionId || isSending) return;
    addMessage("user", message);
    await run(
      () => sendChatMessage({ sessionId, message, mode }),
      "Beskeden kunne ikke sendes.",
    );
  }

  // FR-HITL-1: kladden når først DCR, når borgeren aktivt bekræfter den.
  async function confirmDraft() {
    if (!sessionId || !pendingDraft || isSending) return;
    await run(
      () => confirmChatDraft(sessionId, pendingDraft.id),
      "Bekræftelsen kunne ikke sendes.",
    );
  }

  async function rejectDraft() {
    if (!sessionId || !pendingDraft || isSending) return;
    await run(
      () => rejectChatDraft(sessionId, pendingDraft.id),
      "Afvisningen kunne ikke sendes.",
    );
  }

  return {
    sessionId,
    messages,
    pendingDraft,
    isSending,
    error,
    start,
    sendMessage,
    confirmDraft,
    rejectDraft,
  };
}
