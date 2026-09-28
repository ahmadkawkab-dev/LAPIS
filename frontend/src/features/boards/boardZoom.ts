export type BoardPoint = { x: number; y: number };
/** Translation is in viewport pixels; entity positions always remain in world units. */
export type BoardCamera = BoardPoint & { zoom: number };
export type BoardBounds = { left: number; top: number; right: number; bottom: number };
export type BoardSize = { width: number; height: number };
export const boardZoom = {
  min: 0.25,
  default: 1,
  max: 1.75,
  steps: [0.25, 0.33, 0.5, 0.67, 0.8, 1, 1.25, 1.5, 1.75],
  padding: 64,
} as const;

export function clampZoom(zoom: number) {
  return Math.min(boardZoom.max, Math.max(boardZoom.min, Number.isFinite(zoom) ? zoom : boardZoom.default));
}
export function nextZoomStep(zoom: number, direction: 1 | -1) {
  const steps = direction === 1 ? boardZoom.steps : [...boardZoom.steps].reverse();
  return steps.find((step) => direction === 1 ? step > zoom + 0.001 : step < zoom - 0.001)
    ?? (direction === 1 ? boardZoom.max : boardZoom.min);
}
export function contentBounds(notes: readonly { positionX: number | null; positionY: number | null; width: number; height: number }[]): BoardBounds | null {
  if (!notes.length) return null;
  const bounds = { left: Infinity, top: Infinity, right: -Infinity, bottom: -Infinity };
  for (const note of notes) {
    const x = note.positionX ?? 0, y = note.positionY ?? 0;
    bounds.left = Math.min(bounds.left, x);
    bounds.top = Math.min(bounds.top, y);
    bounds.right = Math.max(bounds.right, x + note.width);
    bounds.bottom = Math.max(bounds.bottom, y + note.height);
  }
  return bounds;
}
export function screenToWorld(point: BoardPoint, camera: BoardCamera): BoardPoint {
  return { x: (point.x - camera.x) / camera.zoom, y: (point.y - camera.y) / camera.zoom };
}
export function worldToScreen(point: BoardPoint, camera: BoardCamera): BoardPoint {
  return { x: point.x * camera.zoom + camera.x, y: point.y * camera.zoom + camera.y };
}
export function screenDeltaToWorld(delta: BoardPoint, zoom: number): BoardPoint {
  return { x: delta.x / zoom, y: delta.y / zoom };
}
export function moveWorldPoint(point: BoardPoint, screenDelta: BoardPoint, zoom: number): BoardPoint {
  const delta = screenDeltaToWorld(screenDelta, zoom);
  return { x: point.x + delta.x, y: point.y + delta.y };
}
export function zoomAtPoint(camera: BoardCamera, zoom: number, anchor: BoardPoint): BoardCamera {
  const world = screenToWorld(anchor, camera);
  const next = clampZoom(zoom);
  return { x: anchor.x - world.x * next, y: anchor.y - world.y * next, zoom: next };
}
/** At normal zoom, avoid half-device-pixel camera translations resampling every text/border. */
export function snapCameraAtDefault(camera: BoardCamera, devicePixelRatio = 1): BoardCamera {
  if (camera.zoom !== 1) return camera;
  const dpr = Number.isFinite(devicePixelRatio) && devicePixelRatio > 0 ? devicePixelRatio : 1;
  return { ...camera, x: Math.round(camera.x * dpr) / dpr, y: Math.round(camera.y * dpr) / dpr };
}
export function fitBoardContent(bounds: BoardBounds | null, viewport: BoardSize): BoardCamera {
  if (!bounds) return { zoom: 1, x: 0, y: 0 };
  const zoom = clampZoom(Math.min(1,
    Math.max(1, viewport.width - boardZoom.padding * 2) / Math.max(1, bounds.right - bounds.left),
    Math.max(1, viewport.height - boardZoom.padding * 2) / Math.max(1, bounds.bottom - bounds.top)));
  return { zoom, x: viewport.width / 2 - (bounds.left + bounds.right) / 2 * zoom,
    y: viewport.height / 2 - (bounds.top + bounds.bottom) / 2 * zoom };
}
