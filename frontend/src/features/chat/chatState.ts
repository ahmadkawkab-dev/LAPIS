import type { ChatMessage } from "./types.ts";

export const maxChatMessages = 500;
export const chatDraftKey = (userId: string, boardId: string) =>
  `wukna:chat-draft:v1:${encodeURIComponent(userId)}:${encodeURIComponent(boardId)}`;

export function readChatDraft(storage: Storage | null, key: string): string {
  try {
    const text = storage?.getItem(key);
    if (!text) return "";
    const value = JSON.parse(text);
    return value.version === 1 && typeof value.body === "string" && value.body.length <= 4000 ? value.body : "";
  } catch { return ""; }
}
export function saveChatDraft(storage: Storage | null, key: string, body: string) {
  try {
    if (body) storage?.setItem(key, JSON.stringify({ version: 1, body }));
    else storage?.removeItem(key);
  } catch { /* Draft remains in memory when browser storage is unavailable. */ }
}

export function compareSequence(a: string, b: string): number {
  const left = BigInt(a), right = BigInt(b);
  return left < right ? -1 : left > right ? 1 : 0;
}

export function mergeChatMessages(current: readonly ChatMessage[], incoming: readonly ChatMessage[], boardId: string): ChatMessage[] {
  const messages = new Map(current.map(message => [message.id, message]));
  for (const message of incoming)
    if (message.boardId === boardId && /^[1-9]\d{0,18}$/.test(message.sequence)) {
      const previous = messages.get(message.id);
      const oldStatus = previous?.attachment?.scanStatus;
      const newStatus = message.attachment?.scanStatus;
      // A history response started before a scan must not roll back a terminal result.
      messages.set(message.id, oldStatus && ["Available", "Rejected"].includes(oldStatus) &&
        newStatus && !["Available", "Rejected"].includes(newStatus) ? previous! : message);
    }
  return [...messages.values()].sort((a, b) => compareSequence(a.sequence, b.sequence));
}
