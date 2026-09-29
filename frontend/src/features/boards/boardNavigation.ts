import type { BoardCamera, BoardPoint } from "./boardZoom.ts";
import type { ViewportBounds } from "./boardViewport.ts";

/** Ordinary object/control clicks never become canvas navigation. Space/middle is an explicit hand gesture. */
export function shouldPanBoard(button: number, spaceHeld: boolean, onObject: boolean, onControl: boolean) {
  return button === 1 || (button === 0 && !onControl && (spaceHeld || !onObject));
}

export function panCamera(camera: BoardCamera, delta: BoardPoint): BoardCamera {
  return { ...camera, x: camera.x + delta.x, y: camera.y + delta.y };
}

/** Wheel/trackpad deltas are screen units. Some browsers already remap Shift-wheel into deltaX. */
export function wheelPanDelta(event: { deltaX: number; deltaY: number; deltaMode: number; shiftKey: boolean }, viewportHeight: number): BoardPoint {
  const multiplier = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? viewportHeight : 1;
  const shiftVertical = event.shiftKey && event.deltaX === 0;
  return { x: (0 - (shiftVertical ? event.deltaY : event.deltaX)) * multiplier,
    y: (0 - (shiftVertical ? 0 : event.deltaY)) * multiplier };
}

export type WorldDragAnchor = { offset: BoardPoint };
export function beginWorldDrag(position: BoardPoint, pointerWorld: BoardPoint): WorldDragAnchor {
  return { offset: { x: pointerWorld.x - position.x, y: pointerWorld.y - position.y } };
}
/** Recompute from the live camera, including when the pointer is stationary during edge auto-pan. */
export function worldDragPosition(anchor: WorldDragAnchor, pointerWorld: BoardPoint): BoardPoint {
  return { x: pointerWorld.x - anchor.offset.x, y: pointerWorld.y - anchor.offset.y };
}

export const edgeAutoPan = { band: 80, maxSpeed: 640, maxFrameSeconds: 0.05 } as const;
/** Velocity is camera translation in screen pixels/second; it is independent of board zoom. */
export function edgePanVelocity(pointer: BoardPoint, viewport: ViewportBounds): BoardPoint {
  function axis(value: number, start: number, end: number) {
    const band = Math.min(edgeAutoPan.band, (end - start) / 2);
    if (band <= 0) return 0;
    const strength = (distance: number) => Math.max(0, Math.min(1, (band - distance) / band)) ** 2;
    return edgeAutoPan.maxSpeed * (strength(value - start) - strength(end - value));
  }
  return { x: axis(pointer.x, viewport.left, viewport.right), y: axis(pointer.y, viewport.top, viewport.bottom) };
}

type FrameScheduler = {
  request: (callback: (time: number) => void) => number;
  cancel: (frame: number) => void;
  now: () => number;
};
export function createEdgeAutoPan({ getViewport, panBy, scheduler, constrainVelocity = (speed) => speed }: {
  getViewport: () => ViewportBounds | null;
  /** Return the actual translation applied, so pixel snapping cannot discard slow motion. */
  panBy: (delta: BoardPoint) => BoardPoint;
  scheduler: FrameScheduler;
  /** Suppress blocked axes so boundary motion cannot accumulate or keep an idle frame loop alive. */
  constrainVelocity?: (speed: BoardPoint) => BoardPoint;
}) {
  let pointer: BoardPoint | null = null;
  let onPan: (() => void) | null = null;
  let frame: number | null = null;
  let lastTime = 0;
  let remainder: BoardPoint = { x: 0, y: 0 };
  const velocity = () => {
    const viewport = getViewport();
    return pointer && viewport ? constrainVelocity(edgePanVelocity(pointer, viewport)) : { x: 0, y: 0 };
  };
  function cancelFrame() {
    if (frame !== null) scheduler.cancel(frame);
    frame = null;
  }
  function schedule() {
    if (frame === null && pointer && onPan) frame = scheduler.request(tick);
  }
  function tick(time: number) {
    frame = null;
    if (!pointer || !onPan) return;
    const speed = velocity();
    if (!speed.x && !speed.y) { remainder = { x: 0, y: 0 }; return; }
    const seconds = Math.min(edgeAutoPan.maxFrameSeconds, Math.max(0, (time - lastTime) / 1000));
    lastTime = time;
    remainder = { x: speed.x ? remainder.x + speed.x * seconds : 0, y: speed.y ? remainder.y + speed.y * seconds : 0 };
    const applied = panBy(remainder);
    remainder = { x: remainder.x - applied.x, y: remainder.y - applied.y };
    if (applied.x || applied.y) onPan();
    schedule();
  }
  function update(next: BoardPoint) {
    pointer = next;
    const speed = velocity();
    if (!speed.x && !speed.y) { cancelFrame(); remainder = { x: 0, y: 0 }; return; }
    if (frame === null) { lastTime = scheduler.now(); schedule(); }
  }
  function stop() {
    cancelFrame(); pointer = null; onPan = null; remainder = { x: 0, y: 0 };
  }
  return {
    start: (next: BoardPoint, callback: () => void) => { stop(); onPan = callback; update(next); },
    update,
    stop,
  };
}
