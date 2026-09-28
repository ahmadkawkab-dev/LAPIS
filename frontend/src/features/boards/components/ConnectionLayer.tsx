import type { CSSProperties, PointerEvent as Pointer } from "react";
import type { ConnectionDto, ConnectionSide, NoteDto } from "../../../api";
import { connectionAnchor, connectionHandleReach, connectionEndpoints, connectionPreviewPath, nearestConnectionPath } from "../connectionGeometry";

export type ConnectionDraft = {
  fixedNoteId: string;
  fixedSide: ConnectionSide;
  endpoint: "source" | "target";
  connectionId?: string;
  version?: number;
  x: number;
  y: number;
  targetId: string | null;
  targetSide: ConnectionSide | null;
};
export function ConnectionLayer({ notes, edges, selectedId, editable, draft, select, start, move, end, cancel }: {
  notes: NoteDto[]; edges: ConnectionDto[]; selectedId: string | null; editable: boolean; draft: ConnectionDraft | null;
  select: (id: string) => void;
  start: (edge: ConnectionDto, endpoint: "source" | "target", event: Pointer<HTMLButtonElement>) => void;
  move: (event: Pointer<HTMLButtonElement>) => void;
  end: (event: Pointer<HTMLButtonElement>) => void;
  cancel: () => void;
}) {
  const byId = new Map(notes.map((note) => [note.id, note]));
  const endpointZ = Math.max(0, ...notes.map((note) => note.zIndex)) + 1;
  const selected = edges.find((edge) => edge.id === selectedId);
  const source = selected && byId.get(selected.sourceNoteId), target = selected && byId.get(selected.targetNoteId);
  const endpoints = source && target && connectionEndpoints(source, target, selected?.sourceHandle, selected?.targetHandle);
  const fixed = draft && byId.get(draft.fixedNoteId), hovered = draft?.targetId ? byId.get(draft.targetId) : undefined;
  const moving = hovered && draft?.targetSide ? connectionAnchor(hovered, draft.targetSide) : draft;
  return <>
    <svg className="connections">
      <defs><marker id="connection-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" /></marker></defs>
      {edges.map((edge) => {
        const a = byId.get(edge.sourceNoteId), b = byId.get(edge.targetNoteId);
        if (!a || !b) return null;
        const path = nearestConnectionPath(a, b, edge.sourceHandle, edge.targetHandle);
        const label = `Connection from ${a.title || "Untitled"} to ${b.title || "Untitled"}`;
        return <g key={edge.id} data-connection-id={edge.id} className={edge.id === selectedId ? "is-selected" : undefined}>
          <path d={path} className={`connection-path ${edge.type === 1 ? "prerequisite-edge" : "related-edge"} ${draft?.connectionId === edge.id ? "is-reconnecting" : ""}`} markerEnd="url(#connection-arrow)" />
          <path d={path} className="connection-hit" tabIndex={0} role="button" aria-label={label} aria-pressed={edge.id === selectedId}
            onPointerDown={(event) => { if (event.button === 0) { event.preventDefault(); event.stopPropagation(); select(edge.id); } }}
            onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); select(edge.id); } }} />
        </g>;
      })}
      {draft && fixed && <path className="draft-edge" d={connectionPreviewPath(fixed, draft.fixedSide, draft, hovered, draft.targetSide, draft.endpoint === "source")} markerEnd="url(#connection-arrow)" />}
    </svg>
    {editable && selected && endpoints && (["source", "target"] as const).map((endpoint) => {
      const point = endpoint === "source" ? endpoints.start : endpoints.end;
      const dragging = draft?.connectionId === selected.id && draft.endpoint === endpoint;
      const location = dragging && moving ? moving : point;
      const card = endpoint === "source" ? source! : target!;
      const reach = connectionHandleReach(card, notes);
      const side = dragging && draft.targetSide ? draft.targetSide : point.side;
      return <button key={endpoint} type="button" className={`connection-handle connection-endpoint ${dragging ? "is-reconnecting" : ""}`}
        data-connection-id={selected.id} data-connection-side={side} style={{ left: location.x, top: location.y, zIndex: `calc(var(--layer-notes) + ${endpointZ})`, "--connection-hit-reach": `${reach[point.side!]}px` } as CSSProperties}
        aria-label={`Reconnect ${endpoint} of selected connection`} title={`Drag to move the ${endpoint} endpoint`}
        onPointerDown={(event) => { event.stopPropagation(); if (event.button !== 0) return; event.currentTarget.setPointerCapture(event.pointerId); start(selected, endpoint, event); }}
        onPointerMove={move} onPointerUp={end} onPointerCancel={cancel} onLostPointerCapture={cancel}
        onClick={(event) => event.stopPropagation()} />;
    })}
  </>;
}
