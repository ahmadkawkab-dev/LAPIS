import { apiFetch, AuthApiError } from "./auth";

export { AuthApiError };

export type BoardDetailDto = {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  role: 0 | 1;
  canEdit: boolean;
};
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
  remove: (id: string) => request<void>(board(id), "DELETE"),
  members: (id: string) => request<MemberDto[]>(`${board(id)}/members`),
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
