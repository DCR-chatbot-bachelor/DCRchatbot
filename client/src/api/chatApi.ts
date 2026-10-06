import type {
  ChatRequest,
  ChatResponse,
  DcrGraph,
  ExecutionMode,
} from '../types/chat'

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '')

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...init?.headers,
    },
  })

  if (!response.ok) {
    const details = await response.text()
    throw new Error(
      details || `API-kaldet fejlede med status ${response.status}.`,
    )
  }

  return response.json() as Promise<T>
}

export async function getGraphs(): Promise<DcrGraph[]> {
  return request<DcrGraph[]>('/api/graphs')
}

export async function startChat(
  graphId: string,
  mode: ExecutionMode,
): Promise<ChatResponse> {
  return request<ChatResponse>('/api/chat/start', {
    method: 'POST',
    body: JSON.stringify({ graphId, mode }),
  })
}

export async function sendChatMessage(
  chatRequest: ChatRequest,
): Promise<ChatResponse> {
  // Session-id sendes både i body (DTO'en) og som header, så klienten er
  // kompatibel med issue 21's stateless session-kontrakt.
  return request<ChatResponse>('/api/chat/message', {
    method: 'POST',
    headers: {
      'X-Session-Id': chatRequest.sessionId ?? '',
    },
    body: JSON.stringify(chatRequest),
  })
}
