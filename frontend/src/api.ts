import { apiFetch, AuthApiError } from "./auth";
import type { BoardCardTone } from './features/boards/boardCardTone';

export { AuthApiError };

export type BoardDetailDto = {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  role: 0 | 1;
  canEdit: boolean;
  cardColor: BoardCardTone | null;
  cardColorVersion: number;
};
export type PersonalTaskDto = {
  id: string;
  title: string;
  description: string | null;
  plannedDate: string | null;
  plannedTime: string | null;
  timeZoneId: string | null;
  plannedAtUtc: string | null;
  completedAt: string | null;
  createdAt: string;
  updatedAt: string;
  listId: string | null;
};
export type TaskWrite = Pick<PersonalTaskDto, "title" | "description" | "plannedDate" | "plannedTime" | "timeZoneId" | "listId">;
export type PersonalTaskPageDto = { items: PersonalTaskDto[]; hasMore: boolean };
export type TaskListDto = { id: string; name: string; createdAt: string; updatedAt: string };
export type PlanningSettingsDto = { timeZoneId: string | null };
export type TaskTemplateItem = Pick<PersonalTaskDto, "title" | "description" | "plannedTime" | "timeZoneId">;
export type TaskTemplateDto = { id: string; name: string; items: TaskTemplateItem[]; createdAt: string; updatedAt: string };
export type TaskTemplatePageDto = { items: TaskTemplateDto[]; hasMore: boolean; totalCount: number };
export type TaskReminderDto = { taskId: string; minutesBefore: number; dueAtUtc: string; deliveredAt: string | null };
export type NotificationType = "taskReminder" | "chatActivity" | "scheduledTaskReminder" | "sharedBoardActivity" | "boardInvitation" | "taskActivity";
export type NotificationDto = {
  id: string; type: NotificationType; title: string; activityKind: string | null;
  actorUserId: string | null; boardId: string | null; resourceKind: string | null; resourceId: string | null;
  issuedAt: string; updatedAt: string; readAt: string | null; dismissedAt: string | null;
  revision: number; readRevision: number; isUnread: boolean; activityCount: number;
  taskId: string | null; taskTitle: string | null;
};
export type NotificationSettings = {
  inAppEnabled: boolean; pushEnabled: boolean; soundsMuted: boolean; soundVolume: number;
  chatNotificationsEnabled: boolean; taskReminderNotificationsEnabled: boolean;
  scheduledTaskReminderNotificationsEnabled: boolean; sharedBoardNotificationsEnabled: boolean;
  boardInvitationNotificationsEnabled: boolean; taskActivityNotificationsEnabled: boolean;
  chatSoundEnabled: boolean; taskReminderSoundEnabled: boolean; scheduledTaskPostedSoundEnabled: boolean;
  boardInvitationSoundEnabled: boolean; taskCompletedSoundEnabled: boolean; privatePreviewsEnabled: boolean;
};
export type NotificationPreferenceDto = { settings: NotificationSettings; revision: number; updatedAt: string | null };
export type ChatNotificationMode = "allActivity" | "mentionsAndReplies" | "muted";
export type BoardNotificationPreferenceDto = {
  mode: ChatNotificationMode; effectiveMode: ChatNotificationMode; soundsMuted: boolean;
  mutedUntil: string | null; revision: number; updatedAt: string | null;
};
export type UpcomingReminderDto = { taskId: string; taskTitle: string; dueAtUtc: string; minutesBefore: number; resourceKind?: "task" | "calendarEvent" };
export type NotificationPageDto = { items: NotificationDto[]; nextCursor: string | null; totalCount: number; unreadCount: number };
export type UpcomingReminderPageDto = { items: UpcomingReminderDto[]; nextCursor: string | null; totalCount: number };
export type CalendarEventDto = {
  id: string; title: string; description: string | null; location: string | null;
  isAllDay: boolean; allDayStartDate: string | null; allDayEndDateExclusive: string | null;
  localStart: string | null; localEnd: string | null; timeZoneId: string | null;
  startAtUtc: string | null; endAtUtc: string | null; createdAt: string; updatedAt: string;
  sourceChatMessageId?: string | null;
};
export type CalendarEventWrite = Pick<CalendarEventDto,
  "title" | "description" | "location" | "isAllDay" | "allDayStartDate" |
  "allDayEndDateExclusive" | "localStart" | "localEnd" | "timeZoneId">;
export type CalendarItemDto = {
  source: "task" | "event"; id: string; title: string;
  scheduleKind: "dateOnlyTask" | "timedTask" | "allDayEvent" | "timedEvent";
  startDate: string | null; endDateExclusive: string | null;
  startAtUtc: string | null; endAtUtc: string | null; timeZoneId: string | null;
  isCompleted: boolean | null;
};
export type CalendarRangeDto = { items: CalendarItemDto[]; hasMore: boolean };
export type BoardPreviewNodeDto = {
  id: string;
  type: 0 | 1;
  x: number;
  y: number;
  width: number;
  height: number;
  color: string;
};
export type BoardPreviewConnectionDto = {
  sourceId: string;
  targetId: string;
  type: 0 | 1;
};
export type BoardListItemDto = BoardDetailDto & {
  noteCount: number;
  taskListCount: number;
  taskItemCount: number;
  completedTaskItemCount: number;
  memberCount: number;
  previewNodes: BoardPreviewNodeDto[];
  previewConnections: BoardPreviewConnectionDto[];
};
export type MemberDto = {
  userId: string;
  email: string;
  username: string;
  displayName: string | null;
  profileImageUrl: string | null;
  profileImageVersion: string | null;
  role: 0 | 1;
  canEdit: boolean;
};
export type ProfileDto = {
  userId: string;
  username: string;
  displayName: string | null;
  email: string;
  profileImageUrl: string | null;
  profileImageVersion: string | null;
};
export type NoteDto = {
  id: string;
  boardId: string;
  kind: 0 | 1 | 2;
  parentNoteId: string | null;
  title: string;
  content: string;
  positionX: number | null;
  positionY: number | null;
  width: number;
  height: number;
  zIndex: number;
  color: string;
  isCompleted: boolean;
  createdAt: string;
  version: number;
};
export type ConnectionSide = "top" | "right" | "bottom" | "left";
export type ConnectionDto = {
  sourceHandle?: ConnectionSide;
  targetHandle?: ConnectionSide;
  version: number;
  id: string;
  boardId: string;
  sourceNoteId: string;
  targetNoteId: string;
  type: 0 | 1;
  createdAt: string;
};

type CreateNote = {
  zIndex?: number;
  width?: number;
  height?: number;
  kind: 0 | 1 | 2;
  title: string;
  content?: string;
  parentNoteId?: string;
  positionX?: number;
  positionY?: number;
  color?: string;
};
export type PatchNote = Partial<
  Pick<
    NoteDto,
    | "title"
    | "content"
    | "positionX"
    | "positionY"
    | "width"
    | "height"
    | "zIndex"
    | "color"
    | "isCompleted"
  >
>;

async function request<T>(
  path: string,
  method = "GET",
  body?: object,
  version?: number,
  signal?: AbortSignal,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (version !== undefined) headers["If-Match"] = `"${version}"`;
  let response: Response;
  try {
    response = await apiFetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  } catch (error) {
    if (error instanceof AuthApiError) throw error;
    throw new AuthApiError("network_failure", 0);
  }
  if (!response.ok) {
    const payload: { error?: string; code?: string; errors?: string[] } = await response
      .json()
      .catch(() => ({}));
    throw new AuthApiError(
      payload.code ?? payload.error ?? codeForStatus(response.status),
      response.status,
      payload.errors,
    );
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

function codeForStatus(status: number): string {
  return (
    (
      {
        400: "invalid_request",
        401: "unauthenticated",
        403: "forbidden",
        404: "not_found",
        409: "conflict",
      } as Record<number, string>
    )[status] ?? "server_error"
  );
}

export function errorMessage(error: unknown): string {
  if (!(error instanceof AuthApiError))
    return "Could not connect to Wukna. Try again.";
  const messages: Record<string, string> = {
    board_guest_limit_reached: "This board has reached its guest limit. Remove a guest before adding someone new.",
    board_membership_rate_limited: "Wait a moment before changing board membership again.",
    invalid_task_title: "Give the task a title of up to 200 characters.",
    invalid_task_description: "Keep the description under 4,000 characters.",
    invalid_task_schedule: "Choose a date before adding a time and time zone.",
    invalid_task_time_zone: "Choose a valid time zone.",
    invalid_task_local_time: "That local time is unavailable or occurs twice. Choose another time.",
    invalid_task_date: "Choose a valid date for the task.",
    invalid_task_list: "Choose one of your task lists.",
    invalid_task_view: "Choose a valid task view.",
    invalid_task_page: "Could not load that page of tasks. Refresh and try again.",
    invalid_task_template_name: "Give the template a name of up to 80 characters.",
    invalid_task_template_items: "Choose between one and 50 tasks for the template.",
    invalid_task_template_item: "Check each template task's title, description, and schedule.",
    invalid_task_template_page: "Could not load that page of templates. Refresh and try again.",
    invalid_notification_page: "Could not load that page of notifications. Refresh and try again.",
    invalid_notification_revision: "Refresh notifications and try again.",
    invalid_notification_preferences: "Check your notification settings and volume.",
    invalid_board_notification_preferences: "Choose a valid notification mode and mute duration.",
    notification_preferences_changed: "These settings changed in another session. Reload the latest settings before saving.",
    invalid_reminder_offset: "Choose a reminder from now until seven days before the task.",
    reminder_requires_open_timed_task: "Add a date and time to an open task before setting a reminder.",
    reminder_time_in_past: "Choose a reminder time that is still in the future.",
    invalid_snooze_duration: "Choose a snooze between five minutes and one day.",
    completed_task_cannot_snooze: "Reopen the task before snoozing its reminder.",
    invalid_task_list_name: "Give the list a name of up to 80 characters.",
    task_list_name_taken: "You already have a list with that name.",
    invalid_calendar_range: "Choose a calendar range of up to six weeks.",
    invalid_calendar_page: "Could not load more calendar items. Refresh and try again.",
    invalid_calendar_time_zone: "Choose a valid calendar time zone.",
    invalid_calendar_event_title: "Give the event a title of up to 200 characters.",
    invalid_calendar_event_details: "Keep the event description under 4,000 characters and location under 200.",
    invalid_calendar_event_schedule: "Check the event's start and end dates or times.",
    invalid_calendar_event_time_zone: "Choose a valid event time zone.",
    invalid_calendar_event_local_time: "That local time is unavailable or occurs twice. Choose another time.",
    invalid_csrf: "Could not verify this request. Please try again.",
    csrf_unavailable: "Could not restore your session. Please try again shortly.",
    network_failure:
      "Could not reach Wukna. Check your connection and try again.",
    unauthenticated: "Your session expired. Please sign in again.",
    invalid_credentials: "Email or password is incorrect.",
    forbidden: "You do not have permission to make this change.",
    not_found: "This item is unavailable or you no longer have access.",
    board_limit_reached: "Board limit reached. You can have a maximum of 5 boards.",
    note_version_conflict:
      "This note was modified by another board member. View the latest version before editing again.",
    note_has_checklist_items:
      "Remove the checklist items before deleting this list.",
    email_already_registered:
      "This email is already registered. Sign in instead.",
    account_link_required:
      "This email has a local account. Sign in and link Google from your account.",
    oauth_cancelled: "Google sign-in was cancelled.",
    external_login_failed: "Google sign-in could not be completed. Please try again.",
    external_login_code_invalid: "Google sign-in is no longer valid. Please try again.",
    external_login_code_expired: "Google sign-in expired. Please try again.",
    external_login_already_linked: "That Google account is already linked.",
    cannot_remove_only_login:
      "Google is your only sign-in method and cannot be removed.",
    connection_version_conflict: "This connection changed in another session. Check its latest state and try again.",
    connection_version_required: "Reload this connection before moving its endpoints.",
    invalid_connection_version: "Reload this connection before moving its endpoints.",
    invalid_connection_handle: "Choose the top, right, bottom, or left side of a card.",
    connection_exists: "Those notes are already connected.",
    invalid_connection: "Select two different notes and a supported relationship.",
    invalid_connection_notes: "Connections must join notes on this board.",
    invalid_note_title: "Note titles must contain 1 to 200 characters.",
    invalid_note_kind: "Choose a supported note type.",
    invalid_note_dimensions: "Note dimensions must be positive numbers.",
    invalid_note_color: "Choose a valid note color.",
    invalid_note_parent: "The checklist parent must be a task list on this board.",
    invalid_note_parent_or_position:
      "Choose a valid parent and position for this note.",
    invalid_note_position: "This note cannot be moved.",
    note_version_required: "Reload this note before editing it.",
    invalid_note_version: "Reload this note before editing it.",
    username_taken: "That username is already in use.",
    invalid_username: "Use 3–30 letters, numbers, periods, underscores, or hyphens.",
    display_name_too_long: "Display name must be 80 characters or fewer.",
    avatar_too_large: "Profile images must be 5 MB or smaller.",
    avatar_invalid_type: "Use a JPEG, PNG, or WebP image.",
    avatar_invalid_image: "Choose a valid JPEG, PNG, or WebP image.",
  };
  if (error.details.length) return error.details.join(" ");
  if (messages[error.code]) return messages[error.code];
  if (error.code.includes(" ") || error.code.includes(".")) return error.code;
  if (error.status >= 500)
    return "Wukna had a server error. Try again shortly.";
  return "The request could not be completed. Try again.";
}

const boards = "/api/boards";
const board = (id: string) => `${boards}/${encodeURIComponent(id)}`;
const notes = (id: string) => `${board(id)}/notes`;
const connections = (id: string) => `${board(id)}/connections`;

export const boardApi = {
  list: () => request<BoardListItemDto[]>(boards),
  get: (id: string) => request<BoardDetailDto>(board(id)),
  create: (title: string) => request<BoardDetailDto>(boards, "POST", { title }),
  rename: (id: string, title: string) =>
    request<BoardDetailDto>(board(id), "PATCH", { title }),
  appearance: (id: string, cardColor: BoardCardTone | null, expectedVersion: number) =>
    request<BoardDetailDto>(`${board(id)}/appearance`, "PUT", { cardColor, expectedVersion }),
  remove: (id: string) => request<void>(board(id), "DELETE"),
  members: (id: string) => request<MemberDto[]>(`${board(id)}/members`),
  guestLimit: (id: string) => request<{ maxGuests: number; guestCount: number }>(`${board(id)}/guest-limit`),
  setMemberPermission: (id: string, userId: string, canEdit: boolean) =>
    request<void>(`${board(id)}/members/${encodeURIComponent(userId)}`, "PATCH", { canEdit }),
  setGuest: (id: string, email: string, canEdit: boolean) =>
    request<void>(`${board(id)}/guests`, "PUT", { email, canEdit }),
  removeGuest: (id: string, userId: string) =>
    request<void>(
      `${board(id)}/guests/${encodeURIComponent(userId)}`,
      "DELETE",
    ),
};

const tasks = "/api/tasks";
const taskPath = (id: string) => `${tasks}/${encodeURIComponent(id)}`;

export const taskApi = {
  list: (view: "inbox" | "today" | "upcoming" | "all" | "completed" | "week", date: string, offset = 0, listId: string | null = null) =>
    request<PersonalTaskPageDto>(`${tasks}?view=${view}&date=${date}&limit=50&offset=${offset}${listId ? `&listId=${encodeURIComponent(listId)}` : ""}`),
  get: (id: string) => request<PersonalTaskDto>(taskPath(id)),
  create: (payload: TaskWrite) => request<PersonalTaskDto>(tasks, "POST", payload),
  update: (id: string, payload: TaskWrite) => request<PersonalTaskDto>(taskPath(id), "PUT", payload),
  complete: (id: string) => request<PersonalTaskDto>(`${taskPath(id)}/complete`, "POST"),
  reopen: (id: string) => request<PersonalTaskDto>(`${taskPath(id)}/reopen`, "POST"),
  schedule: (id: string, plannedDate: string) => request<PersonalTaskDto>(`${taskPath(id)}/schedule`, "POST", { plannedDate }),
  remove: (id: string) => request<void>(taskPath(id), "DELETE"),
  clearDay: (date: string) => request<{ deleted: number }>(`${tasks}/day/${encodeURIComponent(date)}`, "DELETE"),
};

export const taskReminderApi = {
  get: (id: string) => request<TaskReminderDto | undefined>(`${taskPath(id)}/reminder`),
  put: (id: string, minutesBefore: number) => request<TaskReminderDto>(`${taskPath(id)}/reminder`, "PUT", { minutesBefore }),
  remove: (id: string) => request<void>(`${taskPath(id)}/reminder`, "DELETE"),
};

const notifications = "/api/notifications";
export const notificationApi = {
  get: (id: string, signal?: AbortSignal) => request<NotificationDto>(`${notifications}/${encodeURIComponent(id)}`, "GET", undefined, undefined, signal),
  chatUnread: () => request<{ boardId: string; unreadCount: number }[]>(`${notifications}/chat-unread`),
  list: () => request<NotificationDto[]>(notifications),
  unreadCount: () => request<{ unreadCount: number }>(`${notifications}/unread-count`),
  preferences: (signal?: AbortSignal) => request<NotificationPreferenceDto>(`${notifications}/preferences`, "GET", undefined, undefined, signal),
  savePreferences: (settings: NotificationSettings, revision: number, signal?: AbortSignal) =>
    request<NotificationPreferenceDto>(`${notifications}/preferences`, "PUT", { settings, revision }, undefined, signal),
  boardPreferences: (boardId: string, signal?: AbortSignal) =>
    request<BoardNotificationPreferenceDto>(`${boards}/${encodeURIComponent(boardId)}/notification-preferences`, "GET", undefined, undefined, signal),
  saveBoardPreferences: (boardId: string, value: Omit<BoardNotificationPreferenceDto, "effectiveMode" | "updatedAt">, signal?: AbortSignal) =>
    request<BoardNotificationPreferenceDto>(`${boards}/${encodeURIComponent(boardId)}/notification-preferences`, "PUT", value, undefined, signal),
  upcoming: () => request<UpcomingReminderDto[]>(`${notifications}/upcoming`),
  page: (cursor: string | null = null, unreadOnly = false) => request<NotificationPageDto>(
    `${notifications}/page?limit=30&unreadOnly=${unreadOnly}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`),
  upcomingPage: (cursor: string | null = null) => request<UpcomingReminderPageDto>(
    `${notifications}/upcoming/page?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`),
  read: (id: string, revision?: number) => request<void>(`${notifications}/${encodeURIComponent(id)}/read${revision === undefined ? "" : `?revision=${revision}`}`, "POST"),
  readAll: () => request<void>(`${notifications}/read-all`, "POST"),
  dismissRead: () => request<{ dismissedCount: number }>(`${notifications}/dismiss-read`, "POST"),
  dismiss: (id: string, revision?: number) => request<void>(`${notifications}/${encodeURIComponent(id)}/dismiss${revision === undefined ? "" : `?revision=${revision}`}`, "POST"),
  snooze: (id: string, minutes: number) => request<void>(`${notifications}/${encodeURIComponent(id)}/snooze`, "POST", { minutes }),
};

const taskTemplates = "/api/task-templates";
const taskTemplatePath = (id: string) => `${taskTemplates}/${encodeURIComponent(id)}`;
export const taskTemplateApi = {
  list: () => request<TaskTemplateDto[]>(taskTemplates),
  page: (search = "", offset = 0) => request<TaskTemplatePageDto>(
    `${taskTemplates}/page?limit=50&offset=${offset}&search=${encodeURIComponent(search)}`),
  get: (id: string) => request<TaskTemplateDto>(taskTemplatePath(id)),
  create: (name: string, items: TaskTemplateItem[]) => request<TaskTemplateDto>(taskTemplates, "POST", { name, items }),
  update: (id: string, name: string, items: TaskTemplateItem[]) => request<TaskTemplateDto>(taskTemplatePath(id), "PUT", { name, items }),
  remove: (id: string) => request<void>(taskTemplatePath(id), "DELETE"),
  apply: (id: string, plannedDate: string) => request<PersonalTaskDto[]>(`${taskTemplatePath(id)}/apply`, "POST", { plannedDate }),
};

const taskLists = "/api/task-lists";
export const taskListApi = {
  list: () => request<TaskListDto[]>(taskLists),
  create: (name: string) => request<TaskListDto>(taskLists, "POST", { name }),
  rename: (id: string, name: string) => request<TaskListDto>(`${taskLists}/${encodeURIComponent(id)}`, "PUT", { name }),
  remove: (id: string) => request<void>(`${taskLists}/${encodeURIComponent(id)}`, "DELETE"),
};

export const planningSettingsApi = {
  get: () => request<PlanningSettingsDto>(`${tasks}/settings`),
  update: (timeZoneId: string) => request<PlanningSettingsDto>(`${tasks}/settings`, "PUT", { timeZoneId }),
};

const calendar = "/api/calendar";
const calendarEvent = (id: string) => `${calendar}/events/${encodeURIComponent(id)}`;
const chatCalendarPath = (boardId: string, messageId: string) =>
  `${calendar}/events/from-chat/${encodeURIComponent(boardId)}/${encodeURIComponent(messageId)}`;
export const calendarApi = {
  chatEvent: (boardId: string, messageId: string, signal?: AbortSignal) =>
    request<CalendarEventDto | undefined>(chatCalendarPath(boardId, messageId), "GET", undefined, undefined, signal),
  addChatEvent: (boardId: string, messageId: string, signal?: AbortSignal) =>
    request<CalendarEventDto>(chatCalendarPath(boardId, messageId), "POST", undefined, undefined, signal),
  range: (from: string, to: string, timeZone: string, offset = 0) =>
    request<CalendarRangeDto>(`${calendar}?from=${from}&to=${to}&timeZone=${encodeURIComponent(timeZone)}&offset=${offset}`),
  getEvent: (id: string) => request<CalendarEventDto>(calendarEvent(id)),
  createEvent: (payload: CalendarEventWrite) => request<CalendarEventDto>(`${calendar}/events`, "POST", payload),
  updateEvent: (id: string, payload: CalendarEventWrite) => request<CalendarEventDto>(calendarEvent(id), "PUT", payload),
  removeEvent: (id: string) => request<void>(calendarEvent(id), "DELETE"),
};

export const profileApi = {
  get: () => request<ProfileDto>("/api/profile"),
  update: (username: string, displayName: string) =>
    request<ProfileDto>("/api/profile", "PATCH", { username, displayName }),
  uploadAvatar: async (file: File) => {
    const data = new FormData();
    data.append("file", file);
    const response = await apiFetch("/api/profile/avatar", { method: "PUT", body: data });
    if (!response.ok) {
      const payload = await response.json().catch(() => ({}));
      throw new AuthApiError(payload.code ?? codeForStatus(response.status), response.status);
    }
    return response.json() as Promise<{ profileImageUrl: string; profileImageVersion: string }>;
  },
  removeAvatar: () => request<{ profileImageUrl: null; profileImageVersion: null }>("/api/profile/avatar", "DELETE"),
};

export type OnboardingDto = { status: "NotStarted" | "Completed" | "Skipped"; version: number };
export const onboardingApi = {
  get: (signal?: AbortSignal) => request<OnboardingDto>("/api/profile/onboarding", "GET", undefined, undefined, signal),
  save: (value: OnboardingDto, signal?: AbortSignal) => request<OnboardingDto>("/api/profile/onboarding", "PUT", value, undefined, signal),
};

export const noteApi = {
  list: (id: string) => request<NoteDto[]>(notes(id)),
  get: (id: string, noteId: string) =>
    request<NoteDto>(`${notes(id)}/${encodeURIComponent(noteId)}`),
  create: (id: string, payload: CreateNote) =>
    request<NoteDto>(notes(id), "POST", payload),
  patch: (id: string, noteId: string, version: number, payload: PatchNote) =>
    request<NoteDto>(
      `${notes(id)}/${encodeURIComponent(noteId)}`,
      "PATCH",
      payload,
      version,
    ),
  remove: (id: string, noteId: string, version: number) =>
    request<void>(
      `${notes(id)}/${encodeURIComponent(noteId)}`,
      "DELETE",
      undefined,
      version,
    ),
};

export const connectionApi = {
  list: (id: string) => request<ConnectionDto[]>(connections(id)),
  create: (
    id: string,
    sourceNoteId: string,
    targetNoteId: string,
    type: 0 | 1,
    sourceHandle: ConnectionSide = "right",
    targetHandle: ConnectionSide = "left",
  ) =>
    request<ConnectionDto>(connections(id), "POST", {
      sourceNoteId,
      targetNoteId,
      type,
      sourceHandle,
      targetHandle,
    }),
  reconnect: (id: string, connectionId: string, version: number,
    payload: { sourceNoteId: string; targetNoteId: string; sourceHandle: ConnectionSide; targetHandle: ConnectionSide }) =>
    request<ConnectionDto>(`${connections(id)}/${encodeURIComponent(connectionId)}`, "PATCH", payload, version),
  remove: (id: string, connectionId: string) =>
    request<void>(
      `${connections(id)}/${encodeURIComponent(connectionId)}`,
      "DELETE",
    ),
};

export type CalendarReminderDto = { calendarEventId: string; minutesBefore: number; dueAtUtc: string; deliveredAt: string | null };
export const calendarReminderApi = {
  get: (id: string, signal?: AbortSignal) => request<CalendarReminderDto | undefined>(`/api/calendar/events/${encodeURIComponent(id)}/reminder`, "GET", undefined, undefined, signal),
  put: (id: string, minutesBefore: number) => request<CalendarReminderDto>(`/api/calendar/events/${encodeURIComponent(id)}/reminder`, "PUT", { minutesBefore }),
  remove: (id: string) => request<void>(`/api/calendar/events/${encodeURIComponent(id)}/reminder`, "DELETE"),
  snooze: (id: string, minutes: number) => request<void>(`/api/calendar/events/${encodeURIComponent(id)}/reminder/snooze`, "POST", { minutes }),
};
export type PushDevice = { id: string; installationId: string; createdAt: string };
export const pushApi = {
  configuration: () => request<{ enabled: boolean; publicKey: string | null }>(`${notifications}/push/configuration`),
  devices: () => request<PushDevice[]>(`${notifications}/push/subscriptions`),
  register: (installationId: string, endpoint: string, p256dh: string, auth: string) => request<{ id: string; installationId: string }>(`${notifications}/push/subscriptions`, "PUT", { installationId, endpoint, p256dh, auth }),
  remove: (id: string) => request<void>(`${notifications}/push/subscriptions/${encodeURIComponent(id)}`, "DELETE"),
  disableInstallation: (id: string) => request<void>(`${notifications}/push/installation/${encodeURIComponent(id)}`, "DELETE"),
  presence: (value: { installationId: string; tabId: string; visible: boolean; boardId: string | null; chatVisible: boolean }, signal?: AbortSignal) => request<void>(`${notifications}/push/presence`, "PUT", value, undefined, signal),
};
