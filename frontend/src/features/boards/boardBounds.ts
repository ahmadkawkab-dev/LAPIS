import { clampZoom, type BoardBounds, type BoardCamera, type BoardPoint, type BoardSize } from "./boardZoom.ts";

/** A fixed 12,000 × 8,000 world, centered on the existing coordinate origin. */
export const boardBounds: BoardBounds = { left: -6000, top: -4000, right: 6000, bottom: 4000 };
export const boardBoundaryGutter = 32; // Screen pixels: enough to see the quiet page edge.

export function clampBoardPosition(point: BoardPoint, size: BoardSize, bounds = boardBounds): BoardPoint {
  return {
    x: Math.max(bounds.left, Math.min(bounds.right - Math.min(size.width, bounds.right - bounds.left), point.x)),
    y: Math.max(bounds.top, Math.min(bounds.bottom - Math.min(size.height, bounds.bottom - bounds.top), point.y)),
  };
}

/** Includes the entire card, and also makes legacy out-of-bounds geometry reachable. */
export function constrainBoardGeometry<T extends { positionX: number | null; positionY: number | null; width: number; height: number }>(note: T, bounds = boardBounds): T {
  const width = Math.min(note.width, bounds.right - bounds.left);
  const height = Math.min(note.height, bounds.bottom - bounds.top);
  const position = clampBoardPosition({ x: note.positionX ?? 0, y: note.positionY ?? 0 }, { width, height }, bounds);
  return { ...note, width, height, positionX: position.x, positionY: position.y };
}

/** Camera translation is in screen pixels. World coordinates are never changed by navigation. */
export function clampBoardCamera(camera: BoardCamera, viewport: BoardSize, bounds = boardBounds): BoardCamera {
  const zoom = clampZoom(camera.zoom);
  function axis(value: number, start: number, end: number, extent: number) {
    if ((end - start) * zoom + boardBoundaryGutter * 2 <= extent)
      return extent / 2 - (start + end) / 2 * zoom;
    const min = extent - boardBoundaryGutter - end * zoom;
    const max = boardBoundaryGutter - start * zoom;
    return Math.min(max, Math.max(min, Number.isFinite(value) ? value : 0));
  }
  return { x: axis(camera.x, bounds.left, bounds.right, viewport.width),
    y: axis(camera.y, bounds.top, bounds.bottom, viewport.height), zoom };
}
