import { useEffect, useMemo, useRef, type RefObject } from "react";
import { boardZoom, clampZoom, fitBoardContent, nextZoomStep, snapCameraAtDefault, zoomAtPoint,
  type BoardBounds, type BoardCamera, type BoardPoint } from "../boardZoom";
import { clientPointToBoard } from "../boardViewport";
import { createEdgeAutoPan, panCamera, shouldPanBoard, wheelPanDelta } from "../boardNavigation";

export function useBoardViewport(canvasRef: RefObject<HTMLDivElement | null>, bounds: BoardBounds | null, ready: boolean) {
  const worldRef = useRef<HTMLDivElement>(null);
  const boundsRef = useRef(bounds);
  boundsRef.current = bounds;
  const controller = useMemo(() => {
    let camera: BoardCamera = { x: 0, y: 0, zoom: 1 };
    let snapshot = { zoom: camera.zoom };
    let frame = 0;
    let interacting = false;
    const listeners = new Set<() => void>();
    const size = () => ({ width: canvasRef.current?.clientWidth ?? 800, height: canvasRef.current?.clientHeight ?? 600 });
    const paint = () => {
      frame = 0;
      const world = worldRef.current;
      if (world) {
        world.style.transform = `translate(${camera.x}px, ${camera.y}px) scale(${camera.zoom})`;
        world.style.setProperty("--board-zoom", String(camera.zoom));
      }
      // Pan never triggers React updates; only the zoom control subscribes to this snapshot.
      if (snapshot.zoom !== camera.zoom) {
        snapshot = { zoom: camera.zoom };
        listeners.forEach((listener) => listener());
      }
    };
    function setCamera(next: BoardCamera, deferred = false) {
      camera = snapCameraAtDefault({ ...next, zoom: clampZoom(next.zoom) }, window.devicePixelRatio);
      if (deferred) { if (!frame) frame = requestAnimationFrame(paint); }
      else { cancelAnimationFrame(frame); paint(); }
    }
    function zoom(next: number, anchor?: { x: number; y: number }, deferred = false) {
      if (interacting) return;
      const viewport = size();
      setCamera(zoomAtPoint(camera, next, anchor ?? { x: viewport.width / 2, y: viewport.height / 2 }), deferred);
    }
    function panBy(x: number, y: number, deferred = false) {
      const previous = camera;
      setCamera(panCamera(camera, { x, y }), deferred);
      return { x: camera.x - previous.x, y: camera.y - previous.y };
    }
    const autoPan = createEdgeAutoPan({
      getViewport: () => canvasRef.current?.getBoundingClientRect() ?? null,
      panBy: (delta) => panBy(delta.x, delta.y),
      scheduler: { request: (callback) => requestAnimationFrame(callback), cancel: (frame) => cancelAnimationFrame(frame), now: () => performance.now() },
    });
    return {
      clientToWorld: (point: BoardPoint) => clientPointToBoard(canvasRef.current?.getBoundingClientRect() ?? { left: 0, top: 0 }, camera, point),
      startEdgePan: autoPan.start,
      updateEdgePan: autoPan.update,
      stopEdgePan: autoPan.stop,
      getCamera: () => camera,
      getScale: () => camera.zoom,
      getSnapshot: () => snapshot,
      subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
      zoom,
      zoomIn: () => zoom(nextZoomStep(camera.zoom, 1)),
      zoomOut: () => zoom(nextZoomStep(camera.zoom, -1)),
      reset: () => zoom(boardZoom.default),
      fit: () => { if (!interacting) setCamera(fitBoardContent(boundsRef.current, size())); },
      panBy,
      panTo: (x: number, y: number, deferred = false) => setCamera({ ...camera, x, y }, deferred),
      refresh: () => { cancelAnimationFrame(frame); paint(); },
      setInteracting: (active: boolean) => { interacting = active; },
      cancel: () => { autoPan.stop(); cancelAnimationFrame(frame); frame = 0; interacting = false; },
    };
  }, [canvasRef]);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !ready) return;
    controller.refresh();
    const observer = new ResizeObserver(controller.refresh);
    observer.observe(canvas);
    let activePointer: number | null = null;
    let spaceHeld = false;
    let pan: { pointerId: number; clientX: number; clientY: number; x: number; y: number } | null = null;
    function wheel(event: WheelEvent) {
      const zooming = event.ctrlKey || event.metaKey;
      const delta = wheelPanDelta(event, canvas!.clientHeight);
      const scrollArea = (event.target as HTMLElement).closest<HTMLElement>("[data-board-scroll], textarea");
      if (!zooming && scrollArea && ((delta.y !== 0 && scrollArea.scrollHeight > scrollArea.clientHeight)
        || (delta.x !== 0 && scrollArea.scrollWidth > scrollArea.clientWidth))) return;
      event.preventDefault();
      if (activePointer !== null) return;
      const multiplier = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? canvas!.clientHeight : 1;
      if (zooming) {
        const rect = canvas!.getBoundingClientRect();
        controller.zoom(controller.getScale() * Math.exp(-event.deltaY * multiplier * 0.002),
          { x: event.clientX - rect.left, y: event.clientY - rect.top }, true);
      } else {
        controller.panBy(delta.x, delta.y, true);
      }
    }
    function down(event: PointerEvent) {
      controller.refresh(); // Flush any queued wheel transform before interpreting pointer coordinates.
      if (activePointer !== null) return;
      activePointer = event.pointerId;
      controller.setInteracting(true);
      const target = event.target as Element;
      const onObject = !!target.closest("[data-note-id]");
      const onControl = !!target.closest("input, textarea, button, select, a, [contenteditable='true'], [role='button']");
      if (!shouldPanBoard(event.button, spaceHeld, onObject, onControl)) return;
      event.preventDefault();
      // Empty-space events still reach the shell's deselect/focus handler. Hand gestures over objects do not.
      if (onObject || onControl) event.stopPropagation();
      const camera = controller.getCamera();
      pan = { pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY, x: camera.x, y: camera.y };
      canvas!.classList.add("is-panning");
      canvas!.setPointerCapture(event.pointerId);
    }
    function move(event: PointerEvent) {
      if (!pan || event.pointerId !== pan.pointerId) return;
      event.preventDefault(); event.stopPropagation();
      controller.panTo(pan.x + event.clientX - pan.clientX, pan.y + event.clientY - pan.clientY, true);
    }
    function up(event?: PointerEvent) {
      if (event && activePointer !== event.pointerId) return;
      controller.stopEdgePan();
      if (pan && canvas!.hasPointerCapture(pan.pointerId)) canvas!.releasePointerCapture(pan.pointerId);
      activePointer = null; pan = null;
      controller.setInteracting(false);
      canvas!.classList.remove("is-panning");
      controller.refresh();
    }
    function keydown(event: KeyboardEvent) {
      const onControl = (event.target as Element).closest("input, textarea, button, select, [contenteditable='true']");
      if (event.code === "Space" && !onControl) { event.preventDefault(); spaceHeld = true; return; }
      if (event.target !== canvas || activePointer !== null) return;
      const distance = event.shiftKey ? 160 : 48;
      const delta = { ArrowLeft: [distance, 0], ArrowRight: [-distance, 0], ArrowUp: [0, distance], ArrowDown: [0, -distance] }[event.key];
      if (delta) { event.preventDefault(); controller.panBy(delta[0], delta[1]); }
    }
    const blur = () => { spaceHeld = false; up(); };
    const visibility = () => { if (document.hidden) blur(); };
    const keyup = (event: KeyboardEvent) => { if (event.code === "Space") spaceHeld = false; };
    canvas.addEventListener("wheel", wheel, { passive: false });
    canvas.addEventListener("pointerdown", down, true);
    canvas.addEventListener("pointermove", move, true);
    canvas.addEventListener("keydown", keydown);
    canvas.addEventListener("lostpointercapture", up);
    document.addEventListener("visibilitychange", visibility);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", up);
    window.addEventListener("keyup", keyup);
    window.addEventListener("blur", blur);
    return () => {
      observer.disconnect(); controller.cancel();
      canvas.removeEventListener("wheel", wheel);
      canvas.removeEventListener("pointerdown", down, true);
      canvas.removeEventListener("pointermove", move, true);
      canvas.removeEventListener("keydown", keydown);
      canvas.removeEventListener("lostpointercapture", up);
      document.removeEventListener("visibilitychange", visibility);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", up);
      window.removeEventListener("keyup", keyup);
      window.removeEventListener("blur", blur);
    };
  }, [canvasRef, controller, ready]);
  return { worldRef, controller };
}
export type BoardViewportController = ReturnType<typeof useBoardViewport>["controller"];
