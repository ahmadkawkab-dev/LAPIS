import { useMemo, useSyncExternalStore } from "react";
import type { MemberDto } from "../../api";
import type { BoardCursorMovedEvent } from "../../realtime/events";
import type { CursorRenderStore } from "../../realtime/cursorRenderStore";
import { collaboratorStyle } from "./collaboratorIdentity";
import { identityLabel } from "../../components/ui/Avatar";

export type RemoteCursorViewer = {
  cursor: BoardCursorMovedEvent;
  label: string;
};

export function RemoteCursors({ store, members }: {
  store: CursorRenderStore;
  members: MemberDto[];
}) {
  const current = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const memberById = useMemo(() => new Map(members.map((member) => [member.userId, member])), [members]);
  const cursors: RemoteCursorViewer[] = Object.values(current).map((cursor) => ({
    cursor,
    label: memberById.has(cursor.userId)
      ? identityLabel(memberById.get(cursor.userId)!)
      : "Board viewer",
  }));
  return (
    <div className="remote-cursors" aria-hidden="true">
      {cursors.map(({ cursor, label }) => (
        <div
          className="remote-cursor"
          key={cursor.connectionId}
          style={{
            left: cursor.x, top: cursor.y,
            ...collaboratorStyle(cursor.userId),
          }}
        >
          <svg viewBox="0 0 20 24" focusable="false">
            <path d="M2 1.7v17.1l4.6-4.2 3.3 7.1 3.4-1.6-3.2-6.8 6.2-.5L2 1.7Z" />
          </svg>
          <span key={cursor.sequence}>{label}</span>
        </div>
      ))}
    </div>
  );
}
