import { clampBoardCamera, boardBounds } from "./boardBounds.ts";
import { fitBoardContent, snapCameraAtDefault, type BoardBounds, type BoardCamera, type BoardSize } from "./boardZoom.ts";

type Storage = Pick<globalThis.Storage, "getItem" | "setItem">;
type Timer = ReturnType<typeof setTimeout>;
export const viewportSaveDelay = 300;
export function viewportStorageKey(userId: string, boardId: string) {
  return `wukna:viewport:v1:${encodeURIComponent(userId)}:${encodeURIComponent(boardId)}`;
}

export function readBoardViewport(storage: Storage | null, key: string): BoardCamera | null {
  try {
    const value = JSON.parse(storage?.getItem(key) ?? "null");
    if (value && [value.x, value.y, value.zoom].every((item) => typeof item === "number" && Number.isFinite(item)) && value.zoom > 0)
      return { x: value.x, y: value.y, zoom: value.zoom };
  } catch { /* A missing, blocked, or corrupt preference uses the normal initial view. */ }
  return null;
}

/** Small local preference lifecycle, shared by the viewport hook and refresh/navigation tests. */
export function createBoardViewportState({ userId, boardId, storage, getSize, getContentBounds,
  bounds = boardBounds, getDevicePixelRatio = () => 1,
  schedule = (callback: () => void) => setTimeout(callback, viewportSaveDelay),
  cancel = (timer: Timer) => clearTimeout(timer),
}: {
  userId: string; boardId: string; storage: Storage | null;
  getSize: () => BoardSize; getContentBounds: () => BoardBounds | null;
  bounds?: BoardBounds; getDevicePixelRatio?: () => number;
  schedule?: (callback: () => void) => Timer; cancel?: (timer: Timer) => void;
}) {
  const key = viewportStorageKey(userId, boardId);
  let camera: BoardCamera = { x: 0, y: 0, zoom: 1 };
  let initialized = false;
  let dirty = false;
  let timer: Timer | null = null;
  const constrain = (next: BoardCamera) => {
    const size = getSize();
    return clampBoardCamera(snapCameraAtDefault(clampBoardCamera(next, size, bounds), getDevicePixelRatio()), size, bounds);
  };
  function flush() {
    if (timer !== null) cancel(timer);
    timer = null;
    if (!initialized || !dirty) return;
    try { storage?.setItem(key, JSON.stringify(camera)); } catch { /* Navigation works without storage. */ }
    dirty = false;
  }
  function set(next: BoardCamera) {
    const value = constrain(next);
    if (value.x === camera.x && value.y === camera.y && value.zoom === camera.zoom) return;
    camera = value;
    if (!initialized) return;
    dirty = true;
    if (timer !== null) cancel(timer);
    timer = schedule(flush);
  }
  return {
    getCamera: () => camera,
    initialize: () => {
      if (initialized) return;
      camera = constrain(readBoardViewport(storage, key) ?? fitBoardContent(getContentBounds(), getSize()));
      initialized = true;
    },
    set,
    revalidate: () => set(camera),
    flush,
  };
}
