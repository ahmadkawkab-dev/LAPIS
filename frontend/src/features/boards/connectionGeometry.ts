import type { ConnectionDto, ConnectionSide, NoteDto } from "../../api";
import { boardZoom } from "./boardZoom.ts";
export const connectionSides = ["top", "right", "bottom", "left"] as const;
export type ConnectionRect = Pick<NoteDto, "positionX" | "positionY" | "width" | "height">;
export type ConnectionPoint = { x: number; y: number; dx: number; dy: number; side?: ConnectionSide };
export function isConnectionSide(value: unknown): value is ConnectionSide {
  return connectionSides.some((side) => side === value);
}
export function connectionAnchor(note: ConnectionRect, side: ConnectionSide): ConnectionPoint {
  const x = note.positionX ?? 0, y = note.positionY ?? 0;
  switch (side) {
    case "top": return { x: x + note.width / 2, y, dx: 0, dy: -1, side };
    case "right": return { x: x + note.width, y: y + note.height / 2, dx: 1, dy: 0, side };
    case "bottom": return { x: x + note.width / 2, y: y + note.height, dx: 0, dy: 1, side };
    case "left": return { x, y: y + note.height / 2, dx: -1, dy: 0, side };
  }
}
/** Keep the enlarged outward hit area out of neighboring cards at every supported zoom. */
export function connectionHandleReach(note: ConnectionRect & { id: string }, others: (ConnectionRect & { id: string })[]) {
  const maximum = 44 / boardZoom.min, half = maximum / 2;
  const reach = { top: maximum, right: maximum, bottom: maximum, left: maximum };
  const anchors = connectionSides.map((side) => connectionAnchor(note, side));
  for (const other of others) {
    if (other.id === note.id) continue;
    const left = other.positionX ?? 0, top = other.positionY ?? 0;
    const right = left + other.width, bottom = top + other.height;
    for (const point of anchors) {
      const side = point.side!;
      const vertical = side === "top" || side === "bottom";
      if (vertical ? right <= point.x - half || left >= point.x + half
        : bottom <= point.y - half || top >= point.y + half) continue;
      const distance = side === "top" ? point.y - bottom : side === "bottom" ? top - point.y
        : side === "left" ? point.x - right : left - point.x;
      const farDistance = side === "top" ? point.y - top : side === "bottom" ? bottom - point.y
        : side === "left" ? point.x - left : right - point.x;
      if (farDistance > 0) reach[side] = Math.max(0, Math.min(reach[side], distance - 2));
    }
  }
  return reach;
}
export function nearestConnectionSide(note: ConnectionRect, point: { x: number; y: number }): ConnectionSide {
  const x = note.positionX ?? 0, y = note.positionY ?? 0;
  const distance = { top: Math.abs(point.y - y), right: Math.abs(point.x - x - note.width),
    bottom: Math.abs(point.y - y - note.height), left: Math.abs(point.x - x) };
  return connectionSides.reduce((nearest, side) => distance[side] < distance[nearest] ? side : nearest);
}
export function connectionEndpoints(source: ConnectionRect, target: ConnectionRect,
  sourceSide?: ConnectionSide, targetSide?: ConnectionSide) {
  // Missing legacy endpoint fields retain automatic nearest-side routing.
  const starts = isConnectionSide(sourceSide) ? [connectionAnchor(source, sourceSide)]
    : ["right", "left", "bottom", "top"].map((side) => connectionAnchor(source, side as ConnectionSide));
  const ends = isConnectionSide(targetSide) ? [connectionAnchor(target, targetSide)]
    : ["right", "left", "bottom", "top"].map((side) => connectionAnchor(target, side as ConnectionSide));
  let best = { start: starts[0], end: ends[0], distance: Infinity };
  for (const start of starts) for (const end of ends) {
    const distance = (end.x - start.x) ** 2 + (end.y - start.y) ** 2;
    if (distance < best.distance) best = { start, end, distance };
  }
  return best;
}
export function connectionCurve(start: ConnectionPoint, end: ConnectionPoint) {
  const distance = Math.hypot(end.x - start.x, end.y - start.y);
  const bend = Math.max(40, Math.min(140, distance * .42));
  return `M ${start.x} ${start.y} C ${start.x + start.dx * bend} ${start.y + start.dy * bend}, ${end.x + end.dx * bend} ${end.y + end.dy * bend}, ${end.x} ${end.y}`;
}
export function nearestConnectionPath(source: ConnectionRect, target: ConnectionRect,
  sourceSide?: ConnectionSide, targetSide?: ConnectionSide) {
  const { start, end } = connectionEndpoints(source, target, sourceSide, targetSide);
  return connectionCurve(start, end);
}
export function connectionPreviewPath(fixed: ConnectionRect, fixedSide: ConnectionSide,
  pointer: { x: number; y: number }, target?: ConnectionRect, targetSide?: ConnectionSide | null, movingSource = false) {
  const anchor = connectionAnchor(fixed, fixedSide);
  const moving = target && targetSide ? connectionAnchor(target, targetSide) : { ...pointer, dx: 0, dy: 0 };
  return movingSource ? connectionCurve(moving, anchor) : connectionCurve(anchor, moving);
}
export function connectedNoteIds(noteId: string, edges: ConnectionDto[]) {
  const connected = new Set<string>();
  for (const edge of edges) {
    if (edge.sourceNoteId === noteId) connected.add(edge.targetNoteId);
    else if (edge.targetNoteId === noteId) connected.add(edge.sourceNoteId);
  }
  return connected;
}
export function notesAreConnected(firstId: string, secondId: string, edges: ConnectionDto[], exceptId?: string) {
  return edges.some((edge) => (!exceptId || edge.id !== exceptId) &&
    ((edge.sourceNoteId === firstId && edge.targetNoteId === secondId) ||
     (edge.sourceNoteId === secondId && edge.targetNoteId === firstId)));
}
