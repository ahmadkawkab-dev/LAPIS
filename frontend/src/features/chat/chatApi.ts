import { apiFetch, AuthApiError } from "../../auth.ts";
import type { ChatApi, ChatHistoryQuery, ChatMemberPage, ChatModerationMember } from "./types.ts";

export class ChatApiError extends AuthApiError {
  readonly retryAfterSeconds: number;
  readonly serverTime: string | null;
  readonly nextSendAllowedAt: string | null;
  readonly mutedUntil: string | null;
  readonly settingsRevision: number | null;
  readonly moderationRevision: number | null;
  readonly membershipInstanceId: string | null;
  constructor(code: string, status: number, retryAfterSeconds = 0,
    serverTime: string | null = null, nextSendAllowedAt: string | null = null,
    mutedUntil: string | null = null, settingsRevision: number | null = null,
    moderationRevision: number | null = null, membershipInstanceId: string | null = null) {
    super(code, status); this.retryAfterSeconds = retryAfterSeconds; this.serverTime = serverTime;
    this.nextSendAllowedAt = nextSendAllowedAt; this.mutedUntil = mutedUntil;
    this.settingsRevision = settingsRevision;
    this.moderationRevision = moderationRevision; this.membershipInstanceId = membershipInstanceId;
  }
}

async function requestResponse(path: string, signal: AbortSignal, body?: object | FormData, method = body ? "POST" : "GET"): Promise<Response> {
  const multipart = body instanceof FormData;
  const response = await apiFetch(path, {
    method, cache: "no-store",
    headers: body && !multipart ? { "Content-Type": "application/json" } : undefined,
    body: multipart ? body : body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.any([signal, AbortSignal.timeout(multipart ? 90_000 : 30_000)]),
  });
  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    const retry = response.headers.get("Retry-After");
    const seconds = retry && /^\d+$/.test(retry) ? Number(retry) :
      retry ? Math.max(0, (Date.parse(retry) - Date.now()) / 1000) : 0;
    throw new ChatApiError(payload.error ?? payload.code ?? (response.status === 404 ? "not_found" : "chat_request_failed"),
      response.status, Number.isFinite(seconds) ? seconds : 0, payload.serverTime ?? null,
      payload.nextSendAllowedAt ?? null, payload.mutedUntil ?? null, payload.settingsRevision ?? null,
      payload.moderationRevision ?? null, payload.membershipInstanceId ?? null);
  }
  return response;
}
async function request<T>(path: string, signal: AbortSignal, body?: object | FormData, method = body ? "POST" : "GET"): Promise<T> {
  return (await requestResponse(path, signal, body, method)).json() as Promise<T>;
}

const path = (boardId: string) => `/api/boards/${encodeURIComponent(boardId)}/chat/messages`;
export const chatApi: ChatApi = {
  state: (boardId, signal) => request(`/api/boards/${encodeURIComponent(boardId)}/chat/state`, signal),
  read: (boardId, cursor, membershipInstanceId, signal) => request(`${base(boardId)}/read`, signal,
    { cursor, membershipInstanceId }, "PUT"),
  history: (boardId, query: ChatHistoryQuery, signal) => {
    const params = new URLSearchParams({ limit: String(query.limit ?? 50) });
    for (const key of ["before", "after", "through"] as const)
      if (query[key]) params.set(key, query[key]!);
    return request(`${path(boardId)}?${params}`, signal);
  },
  send: (boardId, body, signal) => request(path(boardId), signal, body),
  schedule: (boardId, body, signal) => request(`${base(boardId)}/scheduled-tasks`, signal, body),
  message: (boardId, messageId, signal) => request(`${path(boardId)}/${encodeURIComponent(messageId)}`, signal),
  upload: (boardId, body, signal) => {
    const form = new FormData();
    form.set("clientMessageId", body.clientMessageId);
    form.set("body", body.body);
    form.set("file", body.file);
    return request(`${base(boardId)}/attachments`, signal, form);
  },
  download: async (boardId, attachmentId, preview, signal) => {
    const response = await requestResponse(`${base(boardId)}/attachments/${encodeURIComponent(attachmentId)}${preview ? "?preview=true" : ""}`, signal);
    if (response.headers.get("Content-Type")?.split(";", 1)[0].trim().toLowerCase() !== "image/webp")
      throw new ChatApiError("chat_attachment_unavailable", 503);
    return response.blob();
  },
  calendar: async (boardId, messageId, signal) => {
    const response = await requestResponse(`${path(boardId)}/${encodeURIComponent(messageId)}/calendar.ics`, signal);
    if (response.headers.get("Content-Type")?.split(";", 1)[0].trim().toLowerCase() !== "text/calendar")
      throw new ChatApiError("chat_calendar_unavailable", 503);
    return response.blob();
  },
};
const base = (boardId: string) => `/api/boards/${encodeURIComponent(boardId)}/chat`;
export const chatModerationApi = {
  members: (boardId: string, signal: AbortSignal, after?: string): Promise<ChatMemberPage> =>
    request(`${base(boardId)}/members${after ? `?afterUserId=${encodeURIComponent(after)}` : ""}`, signal),
  settings: (boardId: string, slowModeSeconds: number, expectedRevision: number, signal: AbortSignal) =>
    request<{ slowModeSeconds: number; settingsRevision: number; serverTime: string }>(`${base(boardId)}/settings`, signal,
      { slowModeSeconds, expectedRevision }, "PUT"),
  mute: (boardId: string, member: ChatModerationMember, isMuted: boolean, mutedUntil: string | null, signal: AbortSignal): Promise<ChatModerationMember> =>
    request(`${base(boardId)}/members/${encodeURIComponent(member.sender.userId)}/mute`, signal,
      { isMuted, mutedUntil, membershipInstanceId: member.membershipInstanceId, expectedRevision: member.moderationRevision }, "PUT"),
};

export function chatErrorMessage(cause: unknown): string {
  if (cause instanceof AuthApiError) {
    const messages: Record<string, string> = {
      chat_muted: "You’re muted in this board chat. You can still read messages.",
      chat_cooldown: "Slow mode is on. Wait a moment before sending again.",
      chat_rate_limited: "You’ve sent several messages recently. Wait a moment, then retry.",
      chat_invalid_message: "Write a message using 1–4,000 characters.",
      chat_operation_conflict: "This retry doesn’t match its saved operation. Refresh chat to check the saved message.",
      chat_settings_conflict: "Chat settings changed. Review the current settings and try again.",
      chat_moderation_conflict: "This member’s mute changed. Review their current status and try again.",
      chat_membership_changed: "This member was removed and invited again. Review their current status and try again.",
      chat_moderation_rate_limited: "Wait a moment before changing chat settings again.",
      chat_invalid_settings: "Choose a slow-mode delay between 0 and 21,600 seconds.",
      chat_invalid_mute: "Choose a future time for this mute.",
      chat_invalid_scheduled_task: "Check the task title, date, time, and description.",
      chat_invalid_time_zone: "Choose a valid time zone.",
      chat_invalid_local_time: "That clock time does not exist in the chosen time zone.",
      chat_ambiguous_local_time: "That clock time happens twice. Choose the first or second occurrence.",
      chat_invalid_offset: "Choose a valid occurrence for this clock time.",
      chat_invalid_task_range: "The end must be after the start.",
      chat_invalid_read_cursor: "This read position is no longer valid. Refresh chat and try again.",
      chat_calendar_unavailable: "Calendar download is temporarily unavailable. Try again later.",
      chat_upload_type: "Choose a JPEG, PNG, or WebP image.",
      chat_image_type: "Choose a JPEG, PNG, or WebP image.",
      chat_invalid_image: "This image couldn’t be read. Choose another file.",
      chat_upload_too_large: "This image exceeds the server’s upload limit (5 MB by default).",
      chat_filename: "Choose an image with a shorter filename.",
      chat_invalid_upload: "This image upload couldn’t be read. Choose the file again.",
      chat_upload_rate_limited: "You’ve uploaded several images recently. Wait a moment, then retry.",
      chat_invalid_caption: "Keep the image caption under 2,000 characters.",
      chat_attachment_quota: "This board’s image storage is full.",
      chat_scan_backlog: "Too many images are waiting for a safety check. Try again later.",
      chat_upload_in_progress: "This image is still uploading. Wait a moment, then retry.",
      chat_upload_expired: "The upload timed out. Retry to check if it was saved.",
      chat_attachments_disabled: "Image attachments aren’t available on this server yet.",
      chat_attachment_unavailable: "This image isn’t available right now. Try again later.",
      unauthenticated: "Sign in again to use chat.",
    };
    if (messages[cause.code]) return messages[cause.code];
    if (cause.status >= 500) return "Chat is temporarily unavailable. Your message is kept here for retry.";
  }
  return "Couldn’t reach chat. Check your connection and try again.";
}
