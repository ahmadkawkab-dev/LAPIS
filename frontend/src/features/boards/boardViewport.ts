import { screenToWorld, type BoardCamera } from "./boardZoom.ts";
export type ViewportBounds = {
  left: number;
  top: number;
  right: number;
  bottom: number;
};

export type ViewportDelta = {
  left: number;
  top: number;
};

export function clientPointToBoard(
  viewport: Pick<ViewportBounds, "left" | "top">,
  camera: BoardCamera,
  client: { x: number; y: number },
) {
  return screenToWorld({ x: client.x - viewport.left, y: client.y - viewport.top }, camera);
}

/** Returns the smallest screen-space reveal delta that reveals a node without recentering it. */
export function minimalRevealDelta(
  viewport: ViewportBounds,
  node: ViewportBounds,
  padding = 24,
): ViewportDelta {
  const visibleLeft = viewport.left + padding;
  const visibleRight = viewport.right - padding;
  const visibleTop = viewport.top + padding;
  const visibleBottom = viewport.bottom - padding;

  let left = 0;
  let top = 0;
  if (node.left < visibleLeft) left = node.left - visibleLeft;
  else if (node.right > visibleRight) left = node.right - visibleRight;
  if (node.top < visibleTop) top = node.top - visibleTop;
  else if (node.bottom > visibleBottom) top = node.bottom - visibleBottom;

  return { left, top };
}

export function revealBoardNode(
  viewport: HTMLElement,
  node: HTMLElement,
  panBy: (x: number, y: number) => void,
  padding = 24,
) {
  const delta = minimalRevealDelta(
    viewport.getBoundingClientRect(),
    node.getBoundingClientRect(),
    padding,
  );
  if (delta.left === 0 && delta.top === 0) return false;
  panBy(-delta.left, -delta.top);
  return true;
}
