import type { ChatSender, ChatTyping } from "./types.ts";

/** Per-connection leases keep one tab's stop from hiding another tab's activity. */
export class ChatTypingState {
  private readonly rows = new Map<string, ChatTyping>();
  accept(event: ChatTyping, boardId: string, ownUserId: string, serverNow: number) {
    if (event.eventVersion !== 1 || event.boardId !== boardId || event.userId === ownUserId ||
        !/^[1-9]\d{0,18}$/.test(event.sequence) || !Number.isFinite(Date.parse(event.expiresAt)) ||
        event.isTyping && (!event.sender || event.sender.userId !== event.userId)) return;
    const previous = this.rows.get(event.connectionId);
    if (previous && BigInt(previous.sequence) >= BigInt(event.sequence)) return;
    this.rows.delete(event.connectionId);
    this.rows.set(event.connectionId, event.isTyping && Date.parse(event.expiresAt) <= serverNow
      ? { ...event, isTyping: false } : event);
    // Includes stop tombstones to reject delayed starts, without accumulating disconnected tabs.
    if (this.rows.size > 256) this.rows.delete(this.rows.keys().next().value!);
  }
  members(serverNow: number): ChatSender[] {
    const users = new Map<string, ChatSender>();
    for (const item of this.rows.values()) if (item.isTyping && item.sender && Date.parse(item.expiresAt) > serverNow)
      users.set(item.userId, item.sender);
    return [...users.values()];
  }
  nextExpiry(serverNow: number) {
    const instants = [...this.rows.values()].filter(item => item.isTyping && Date.parse(item.expiresAt) > serverNow)
      .map(item => Date.parse(item.expiresAt));
    return instants.length ? Math.min(...instants) : null;
  }
  clearMember(userId: string, instance: string) {
    for (const [key, item] of this.rows) if (item.userId === userId && item.membershipInstanceId === instance)
      this.rows.set(key, { ...item, isTyping: false });
  }
  clear() { this.rows.clear(); }
}

export function typingLabel(members: ChatSender[]) {
  const names = members.slice(0, 2).map(member => member.displayName || member.username);
  if (!names.length) return "";
  if (members.length === 1) return `${names[0]} is typing…`;
  return `${names.join(" and ")}${members.length > 2 ? ` and ${members.length - 2} more` : ""} are typing…`;
}
