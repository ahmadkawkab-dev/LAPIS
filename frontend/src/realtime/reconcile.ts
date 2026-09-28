import type { BoardListItemDto, ConnectionDto, NoteDto } from "../api";
import type {
  BoardPresenceSnapshot,
  NoteGeometryPreviewEndedEvent,
  NoteGeometryPreviewEvent,
} from "./events";

export function mergePresenceSnapshot(
  current: BoardPresenceSnapshot | null,
  incoming: BoardPresenceSnapshot,
): BoardPresenceSnapshot {
  return current && incoming.revision <= current.revision ? current : incoming;
}

export function mergeVersionedNote(
  current: NoteDto[],
  incoming: NoteDto,
  deletedVersion = -1,
): NoteDto[] {
  if (deletedVersion >= incoming.version) return current;
  const existing = current.find((note) => note.id === incoming.id);
  if (existing && existing.version >= incoming.version) return current;
  return existing
    ? current.map((note) => note.id === incoming.id ? incoming : note)
    : [...current, incoming];
}

export function removeVersionedNote(
  current: NoteDto[],
  noteId: string,
  deletedVersion: number,
): NoteDto[] {
  const remaining = current.filter((note) =>
    note.id !== noteId || note.version > deletedVersion);
  return remaining.length === current.length ? current : remaining;
}

export function mergeBoardSummary(
  current: BoardListItemDto[],
  incoming: BoardListItemDto,
): BoardListItemDto[] {
  const existing = current.find((board) => board.id === incoming.id);
  if (existing && Date.parse(existing.updatedAt) > Date.parse(incoming.updatedAt))
    return current;
  const merged = existing
    ? current.map((board) => board.id === incoming.id ? incoming : board)
    : [...current, incoming];
  return [...merged].sort((left, right) =>
    Date.parse(right.updatedAt) - Date.parse(left.updatedAt) ||
    right.id.localeCompare(left.id));
}

export async function rejoinThenReconcile(
  rejoin: () => Promise<void>,
  listeners: ReadonlyArray<() => Promise<void>>,
): Promise<boolean> {
  let rejoined = true;
  try {
    await rejoin();
  } catch {
    rejoined = false;
  }
  const results = await Promise.allSettled(listeners.map((listener) => listener()));
  return rejoined && results.every((result) => result.status === "fulfilled");
}

export function shouldAcceptGeometryPreview(
  current: NoteGeometryPreviewEvent | undefined,
  incoming: NoteGeometryPreviewEvent,
  authoritativeVersion: number,
  endedSequence = -1,
): boolean {
  if (incoming.baseVersion < authoritativeVersion) return false;
  if (incoming.sequence <= endedSequence) return false;
  if (!current) return true;
  if (current.connectionId === incoming.connectionId)
    return incoming.sequence > current.sequence;
  return Date.parse(incoming.sentAt) >= Date.parse(current.sentAt);
}

export function shouldEndGeometryPreview(
  current: NoteGeometryPreviewEvent | undefined,
  ended: NoteGeometryPreviewEndedEvent,
): boolean {
  return current?.connectionId === ended.connectionId &&
    ended.sequence >= current.sequence;
}

export function shouldClearGeometryPreview(
  currentVersion: number | undefined,
  incomingVersion: number,
): boolean {
  return currentVersion === undefined || incomingVersion > currentVersion;
}

export function mergeVersionedConnection(current: ConnectionDto[], incoming: ConnectionDto, deletedVersion = -1): ConnectionDto[] {
  const version = incoming.version ?? 0;
  if (deletedVersion >= version) return current;
  const existing = current.find((edge) => edge.id === incoming.id);
  if (existing && (existing.version ?? 0) >= version) return current;
  return existing ? current.map((edge) => edge.id === incoming.id ? incoming : edge) : [...current, incoming];
}
