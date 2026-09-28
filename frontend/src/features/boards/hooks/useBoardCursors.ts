import { useCallback, useEffect, useRef, useState, type PointerEvent as Pointer, type RefObject } from "react";
import { CursorRenderStore } from "../../../realtime/cursorRenderStore";
import { realtimeConnection } from "../../../realtime/connection";
import { applyCursorMoved, applyCursorStopped, shouldAcceptCursorMoved, type BoardCursorsByConnection } from "../../../realtime/cursors";
import type { BoardCursorMovedEvent, BoardCursorStoppedEvent } from "../../../realtime/events";
import type { BoardCamera } from "../boardZoom";
import { clientPointToBoard } from "../boardViewport";

export function useBoardCursors(id: string, currentUserId: string, canvasRef: RefObject<HTMLDivElement | null>, getCamera: () => BoardCamera) {
  const [cursorStore] = useState(() => new CursorRenderStore());
  const cursorSequence = useRef(0);
  const remoteCursorsRef = useRef<BoardCursorsByConnection>({});
  const remoteCursorEnds = useRef(new Map<string, number>());
  const remoteCursorTimers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  const localCursor = useRef<{
    active: boolean;
    lastSentAt: number;
    pending: { x: number; y: number } | null;
    timer: ReturnType<typeof setTimeout> | null;
  }>({ active: false, lastSentAt: 0, pending: null, timer: null });
  const clearAllRemoteCursors = useCallback(() => {
    for (const timer of remoteCursorTimers.current.values()) clearTimeout(timer);
    remoteCursorTimers.current.clear();
    remoteCursorsRef.current = {};
    remoteCursorEnds.current.clear();
    cursorStore.set({});
  }, [cursorStore]);
  const acceptRemoteCursor = useCallback((incoming: BoardCursorMovedEvent) => {
    if (incoming.userId === currentUserId) return;
    if (!shouldAcceptCursorMoved(
      remoteCursorsRef.current[incoming.connectionId],
      incoming,
      remoteCursorEnds.current.get(incoming.connectionId),
    )) return;
    const next = applyCursorMoved(remoteCursorsRef.current, incoming);
    if (next === remoteCursorsRef.current) return;
    remoteCursorsRef.current = next;
    cursorStore.set(next);
    const previous = remoteCursorTimers.current.get(incoming.connectionId);
    if (previous) clearTimeout(previous);
    const parsedExpiry = Date.parse(incoming.expiresAt);
    const remaining = Number.isFinite(parsedExpiry)
      ? Math.min(2_000, Math.max(0, parsedExpiry - Date.now()))
      : 1_500;
    const timer = setTimeout(() => {
      const expired: BoardCursorStoppedEvent = {
        boardId: incoming.boardId,
        userId: incoming.userId,
        connectionId: incoming.connectionId,
        sequence: incoming.sequence,
      };
      const expiredState = applyCursorStopped(remoteCursorsRef.current, expired);
      remoteCursorEnds.current.set(incoming.connectionId, incoming.sequence);
      if (expiredState !== remoteCursorsRef.current) {
        remoteCursorsRef.current = expiredState;
        cursorStore.set(expiredState);
      }
      remoteCursorTimers.current.delete(incoming.connectionId);
    }, remaining);
    remoteCursorTimers.current.set(incoming.connectionId, timer);
  }, [currentUserId, cursorStore]);
  const endRemoteCursor = useCallback((incoming: BoardCursorStoppedEvent) => {
    remoteCursorEnds.current.set(incoming.connectionId, Math.max(
      remoteCursorEnds.current.get(incoming.connectionId) ?? -1,
      incoming.sequence));
    const next = applyCursorStopped(remoteCursorsRef.current, incoming);
    if (next === remoteCursorsRef.current) return;
    remoteCursorsRef.current = next;
    cursorStore.set(next);
    const timer = remoteCursorTimers.current.get(incoming.connectionId);
    if (timer) clearTimeout(timer);
    remoteCursorTimers.current.delete(incoming.connectionId);
  }, [cursorStore]);
  const stopLocalCursor = useCallback(() => {
    const state = localCursor.current;
    if (state.timer) clearTimeout(state.timer);
    state.timer = null;
    state.pending = null;
    if (!state.active) return;
    state.active = false;
    const sequence = ++cursorSequence.current;
    void realtimeConnection.stopBoardCursor(id, sequence);
  }, [id]);
  const flushLocalCursor = useCallback(() => {
    const state = localCursor.current;
    state.timer = null;
    const position = state.pending;
    if (!position || !state.active) return;
    state.pending = null;
    state.lastSentAt = performance.now();
    const sequence = ++cursorSequence.current;
    void realtimeConnection.moveBoardCursor(id, position.x, position.y, sequence);
  }, [id]);
  const moveLocalCursor = useCallback((event: Pointer<HTMLDivElement>) => {
    const canvas = canvasRef.current;
    const rect = canvas?.getBoundingClientRect();
    if (!canvas || !rect || realtimeConnection.getSnapshot() !== "connected") return;
    const state = localCursor.current;
    state.active = true;
    state.pending = clientPointToBoard(
      rect,
      getCamera(),
      { x: event.clientX, y: event.clientY },
    );
    const remaining = 75 - (performance.now() - state.lastSentAt);
    if (remaining <= 0 && state.timer === null) {
      flushLocalCursor();
      return;
    }
    if (state.timer === null)
      state.timer = setTimeout(flushLocalCursor, Math.max(0, remaining));
  }, [flushLocalCursor, canvasRef, getCamera]);
  useEffect(() => () => {
    for (const timer of remoteCursorTimers.current.values()) clearTimeout(timer);
    remoteCursorTimers.current.clear();
    remoteCursorsRef.current = {};
    remoteCursorEnds.current.clear();
    stopLocalCursor();
  }, [stopLocalCursor]);

  return { cursorStore, clearAllRemoteCursors, acceptRemoteCursor, endRemoteCursor, stopLocalCursor, moveLocalCursor };
}
