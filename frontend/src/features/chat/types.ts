export type ChatSender = {
  userId: string; username: string; displayName: string | null;
  avatarUrl: string | null; avatarVersion: string | null;
};
export type ChatMessage = {
  id: string; boardId: string; sequence: string; cursor: string;
  type: "text" | "attachment" | "scheduledTask"; body: string | null;
  createdAt: string; clientMessageId: string; sender: ChatSender;
  attachment: { id: string; fileName: string; contentType: string; byteSize: number;
    width: number; height: number; scanStatus: string } | null;
  scheduledTask: { title: string; description: string | null; startsAtUtc: string;
    endsAtUtc: string | null; timeZoneId: string; originalOffsetMinutes: number } | null;
};
export type ChatPage = {
  items: ChatMessage[]; olderCursor: string | null; newerCursor: string;
  hasMore: boolean; catchUpThrough: string; serverTime: string;
};
export type ChatSendResult = {
  message: ChatMessage; serverTime: string; nextSendAllowedAt: string | null; isReplay: boolean;
  settingsRevision: number;
};
export type ScheduledTaskWrite = { title: string; description: string | null; localStart: string;
  localEnd: string | null; timeZoneId: string; startOffsetMinutes: number | null;
  endOffsetMinutes: number | null };
export type ScheduledTaskDraft = { title: string; description: string; localStart: string;
  localEnd: string; timeZoneId: string; startOffsetMinutes: string; endOffsetMinutes: string };
export type ChatJoined = {
  boardId: string; membershipInstanceId: string; latestCursor: string;
  slowModeSeconds: number; settingsRevision: number; isMuted: boolean;
  mutedUntil: string | null; serverTime: string; moderationRevision: number;
  nextSendAllowedAt: string | null; isOwner: boolean;
  lastReadSequence: string; unreadCount: number;
};
export type ChatReadState = { lastReadSequence: string; unreadCount: number };
export type MessageCreated = {
  eventId: string; eventVersion: number; boardId: string; messageId: string; sequence: string;
};
export type ChatAttachmentChanged = { eventId: string; eventVersion: number; boardId: string;
  messageId: string; attachmentId: string };
export type AccessRevoked = {
  eventId: string; eventVersion: number; boardId: string; membershipInstanceId: string | null;
};
export type ChatHistoryQuery = { before?: string; after?: string; through?: string; limit?: number };
export type ChatSettingsChanged = { eventVersion: number; boardId: string; revision: string };
export type ChatMemberStateChanged = ChatSettingsChanged & { memberUserId: string; membershipInstanceId: string };
export type ChatTyping = { eventVersion: number; boardId: string; connectionId: string; userId: string;
  membershipInstanceId: string; sequence: string; isTyping: boolean; expiresAt: string; sender: ChatSender | null };
export type ChatModerationMember = { sender: ChatSender; role: number; membershipInstanceId: string;
  isMuted: boolean; mutedUntil: string | null; moderationRevision: number };
export type ChatMemberPage = { items: ChatModerationMember[]; nextUserId: string | null };
export interface ChatApi {
  history(boardId: string, query: ChatHistoryQuery, signal: AbortSignal): Promise<ChatPage>;
  message(boardId: string, messageId: string, signal: AbortSignal): Promise<ChatMessage>;
  send(boardId: string, request: { clientMessageId: string; body: string }, signal: AbortSignal): Promise<ChatSendResult>;
  schedule(boardId: string, request: ScheduledTaskWrite & { clientMessageId: string }, signal: AbortSignal): Promise<ChatSendResult>;
  upload(boardId: string, request: { clientMessageId: string; body: string; file: File }, signal: AbortSignal): Promise<ChatSendResult>;
  download(boardId: string, attachmentId: string, preview: boolean, signal: AbortSignal): Promise<Blob>;
  calendar(boardId: string, messageId: string, signal: AbortSignal): Promise<Blob>;
  state(boardId: string, signal: AbortSignal): Promise<ChatJoined>;
  read(boardId: string, cursor: string, membershipInstanceId: string, signal: AbortSignal): Promise<ChatReadState>;
}
export type ChatTransportStatus = "connecting" | "connected" | "reconnecting" | "offline";
export type ChatTransportHandlers = {
  status: (status: ChatTransportStatus) => void;
  joined: (joined: ChatJoined) => void;
  message: (event: MessageCreated) => void;
  attachment: (event: ChatAttachmentChanged) => void;
  revoked: (event: AccessRevoked) => void;
  forbidden: () => void;
  settings: (event: ChatSettingsChanged) => void;
  memberState: (event: ChatMemberStateChanged) => void;
  typing: (event: ChatTyping) => void;
  typingDenied: () => void;
};
export interface ChatTransport { start(): void; rejoin(): void; stop(): Promise<void>; setTyping(active: boolean): void }
export type ChatTransportFactory = (boardId: string, handlers: ChatTransportHandlers) => ChatTransport;
