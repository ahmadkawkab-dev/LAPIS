import type {
  BoardListItemDto,
  ConnectionDto,
  NoteDto,
} from "../api";

export const realtimeEvents = {
  boardUpdated: "BoardUpdated",
  noteCreated: "NoteCreated",
  noteUpdated: "NoteUpdated",
  noteDeleted: "NoteDeleted",
  connectionCreated: "ConnectionCreated",
  connectionUpdated: "ConnectionUpdated",
  connectionDeleted: "ConnectionDeleted",
  membersChanged: "MembersChanged",
  profileChanged: "ProfileChanged",
  userProfileChanged: "UserProfileChanged",
  boardSummaryChanged: "BoardSummaryChanged",
  boardSummaryRemoved: "BoardSummaryRemoved",
  boardAccessRevoked: "BoardAccessRevoked",
  noteGeometryPreview: "NoteGeometryPreview",
  noteGeometryPreviewEnded: "NoteGeometryPreviewEnded",
  boardPresenceChanged: "BoardPresenceChanged",
  noteEditingStarted: "NoteEditingStarted",
  noteEditingStopped: "NoteEditingStopped",
  boardCursorMoved: "BoardCursorMoved",
  boardCursorStopped: "BoardCursorStopped",
} as const;

export type BoardUpdatedEvent = {
  boardId: string;
  title: string;
  updatedAt: string;
  cardColor: BoardListItemDto['cardColor'];
  cardColorVersion: number;
};

export type NoteChangedEvent = NoteDto;

export type NoteDeletedEvent = {
  boardId: string;
  noteId: string;
  version: number;
};

export type ConnectionCreatedEvent = ConnectionDto;

export type ConnectionDeletedEvent = {
  boardId: string;
  connectionId: string;
  version?: number;
};

export type BoardScopedEvent = { boardId: string };
export type ProfileChangedEvent = { boardId: string; userId: string };
export type UserProfileChangedEvent = { userId: string };

export type BoardSummaryChangedEvent = BoardListItemDto;

export type NoteGeometryOperation = 0 | 1;

export type NoteGeometryPreviewRequest = {
  boardId: string;
  noteId: string;
  operation: NoteGeometryOperation;
  x: number | null;
  y: number | null;
  width: number | null;
  height: number | null;
  baseVersion: number;
  sequence: number;
};

export type NoteGeometryPreviewEvent = NoteGeometryPreviewRequest & {
  userId: string;
  connectionId: string;
  sentAt: string;
};

export type NoteGeometryPreviewEndedEvent = {
  boardId: string;
  noteId: string;
  userId: string;
  connectionId: string;
  sequence: number;
};

export type BoardPresenceSnapshot = {
  boardId: string;
  revision: number;
  viewers: Array<{
    userId: string;
    connectionCount: number;
  }>;
};

export type NoteEditingStartedEvent = {
  boardId: string;
  noteId: string;
  userId: string;
  connectionId: string;
  sequence: number;
  expiresAt: string;
};

export type NoteEditingStoppedEvent = {
  boardId: string;
  noteId: string;
  userId: string;
  connectionId: string;
  sequence: number;
};

export type BoardCursorMovedEvent = {
  boardId: string;
  userId: string;
  connectionId: string;
  x: number;
  y: number;
  sequence: number;
  expiresAt: string;
};

export type BoardCursorStoppedEvent = {
  boardId: string;
  userId: string;
  connectionId: string;
  sequence: number;
};
