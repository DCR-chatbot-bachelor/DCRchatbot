import { useRef, useState } from "react";
import {
  confirmChatDraft,
  editChatAnswer,
  rejectChatDraft,
  sendChatMessage,
  startChat,
} from "../api/chatApi";
import type {
  AnsweredQuestion,
  ChatMessage,
  ChatResponse,
  ExecutionMode,
  PendingAnswer,
  QuickReply,
} from "../types/chat";

// Grafens emner (FR-LLM-3) og svarene på et choice-spørgsmål vises begge
// som knapper under bot-beskeden.
function toQuickReplies(response: ChatResponse): QuickReply[] {
  return [
    ...(response.suggestions ?? []).map((s) => ({ key: `topic:${s.eventId}`, label: s.label })),
    ...(response.answerOptions ?? []).map((o) => ({ key: `answer:${o.value}`, label: o.label })),
  ];
}

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

// Knytter et bekræftet svar til den besked, det kom fra, og markerer
// tidligere svar på samme spørgsmål som rettet (FR-HITL-2).
function attachAnswer(
  messages: ChatMessage[],
  sourceMessageId: string | null,
  answer: AnsweredQuestion,
): ChatMessage[] {
  const lastUserMessage = [...messages].reverse().find((m) => m.sender === "user");
  const targetId = messages.some((m) => m.id === sourceMessageId)
    ? sourceMessageId
    : lastUserMessage?.id;

  return messages.map((message) => {
    const answers = (message.answers ?? []).map((existing) =>
      existing.answer.eventId === answer.eventId
        ? { ...existing, isSuperseded: true }
        : existing,
    );
    return {
      ...message,
      answers:
        message.id === targetId
          ? [...answers, { answer, isSuperseded: false }]
          : message.answers && answers,
    };
  });
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
  const [editingAnswer, setEditingAnswer] = useState<AnsweredQuestion | null>(null);
  // Beskeden, kladderne i køen kom fra; bekræftede svar knyttes til den.
  const draftSourceMessageId = useRef<string | null>(null);

  function addMessage(
    sender: ChatMessage["sender"],
    content: string,
    quickReplies?: QuickReply[],
  ) {
    if (!content.trim()) return null;
    const message = {
      ...createMessage(sender, content),
      quickReplies: quickReplies?.length ? quickReplies : undefined,
    };
    setMessages((current) => [...current, message]);
    return message.id;
  }

  function applyResponse(response: ChatResponse) {
    // The draft card already renders the confirmation question. Avoid
    // displaying the same question again as a normal chat bubble. When a
    // draft was already open, the response answers it (e.g. the FAQ answer)
    // and must be shown, even if the next queued draft follows (FR-DCR-2).
    const answersOpenDraft = pendingDraft !== null;
    setSessionId(response.sessionId);
    setPendingDraft(response.pendingDraft ?? null);
    const confirmedAnswer = response.confirmedAnswer;
    if (confirmedAnswer) {
      setMessages((current) =>
        attachAnswer(current, draftSourceMessageId.current, confirmedAnswer),
      );
    }
    if (!response.pendingDraft || answersOpenDraft) {
      addMessage("bot", response.message, toQuickReplies(response));
    }
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
    setEditingAnswer(null);
    draftSourceMessageId.current = null;
    setMode(selectedMode);
    await run(
      () => startChat(graphId, selectedMode),
      "Samtalen kunne ikke startes.",
    );
  }

  async function sendMessage(text: string) {
    const message = text.trim();
    if (!message || !sessionId || isSending) return;
    const messageId = addMessage("user", message);
    // En ny besked laver nye kladder; et skrevet "ja" til en åben kladde
    // gør ikke, så svarene hører stadig til den oprindelige besked.
    if (pendingDraft === null || editingAnswer) {
      draftSourceMessageId.current = messageId;
    }
    if (editingAnswer) {
      const eventId = editingAnswer.eventId;
      setEditingAnswer(null);
      await run(
        () => editChatAnswer({ sessionId, message, mode, eventId }),
        "Rettelsen kunne ikke sendes.",
      );
      return;
    }
    await run(
      () => sendChatMessage({ sessionId, message, mode }),
      "Beskeden kunne ikke sendes.",
    );
  }

  // Et valgt emne eller svar sendes som en almindelig besked, så det går
  // gennem det normale flow med match og bekræftelse (FR-HITL-1).
  async function selectQuickReply(reply: QuickReply) {
    await sendMessage(reply.label);
  }

  // Næste besked bliver det nye svar på spørgsmålet (FR-HITL-2).
  function startEditing(answer: AnsweredQuestion) {
    setEditingAnswer(answer);
  }

  function cancelEditing() {
    setEditingAnswer(null);
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
    editingAnswer,
    start,
    sendMessage,
    confirmDraft,
    rejectDraft,
    startEditing,
    cancelEditing,
    selectQuickReply,
  };
}
