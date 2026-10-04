import { TourHelpIcon } from "./TourHelpIcon";
import { useEffect, useId, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import { ArrowLeft, ArrowRight } from "lucide-react";
import { Button } from "../../components/ui/Button";
import { placeCoach, tourStep, TOUR_LENGTH, type Rect, type TourController } from "./tour";
import "./onboarding.css";

function visible(element: HTMLElement) {
  const rect = element.getBoundingClientRect();
  return element.getClientRects().length > 0 && rect.width > 0 && rect.height > 0 &&
    !element.closest('[inert], [aria-hidden="true"], [aria-busy="true"]') &&
    !element.matches(':disabled, [aria-disabled="true"]');
}

export default function WuknaTour({ controller, path, ready }: {
  controller: TourController; path: string; ready: boolean;
}) {
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const card = useRef<HTMLDivElement>(null), heading = useRef<HTMLHeadingElement>(null);
  const returnFocus = useRef<HTMLElement | null>(null);
  const [geometry, setGeometry] = useState<{ rect: Rect | null; left: number; top: number; suspended: boolean; viaMore: boolean }>({ rect: null, left: 12, top: 12, suspended: false, viaMore: false });
  const titleId = useId(), textId = useId(), progressId = useId(), hintId = useId();
  const definition = tourStep(state.step ?? 0);
  const targetsKey = definition.targets.join(",");

  useEffect(() => {
    if (state.step === null) return;
    const current = document.activeElement;
    returnFocus.current = current instanceof HTMLElement && current !== document.body ? current : null;
    return () => {
      const replay = Array.from(document.querySelectorAll<HTMLElement>('[data-tour="replay"]')).find(visible);
      const previous = returnFocus.current;
      const destination = previous?.isConnected && visible(previous) ? previous : replay ?? document.getElementById("wk-main-content");
      destination?.focus({ preventScroll: true });
    };
  }, [state.step !== null]);

  useLayoutEffect(() => {
    if (state.step === null) return;
    let frame = 0;
    let observed: HTMLElement | null = null;
    let scrolled: HTMLElement | null = null;
    const resize = new ResizeObserver(() => schedule());
    const update = () => {
      frame = 0;
      const suspended = !!document.querySelector('dialog[open], #wk-main-content .wk-loading[aria-busy="true"], [data-tour-loading]') || !ready;
      let element: HTMLElement | null = null;
      if (!suspended) {
        for (const identifier of targetsKey.split(",")) {
          element = Array.from(document.querySelectorAll<HTMLElement>(`[data-tour="${identifier}"]`)).find(visible) ?? null;
          if (element) break;
        }
      }
      if (observed !== element) {
        if (observed) resize.unobserve(observed);
        if (element) resize.observe(element);
        observed = element;
      }
      let rect: Rect | null = null;
      if (element) {
        const bounds = element.getBoundingClientRect();
        if (scrolled !== element &&
            (bounds.bottom <= 0 || bounds.top >= innerHeight || bounds.right <= 0 || bounds.left >= innerWidth)) {
          scrolled = element;
          element.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "instant" });
        }
        const next = element.getBoundingClientRect();
        const left = Math.max(4, next.left - 6), top = Math.max(4, next.top - 6);
        const right = Math.min(innerWidth - 4, next.right + 6), bottom = Math.min(innerHeight - 4, next.bottom + 6);
        if (right > left && bottom > top) rect = { left, top, width: right - left, height: bottom - top };
      }
      const size = card.current?.getBoundingClientRect() ?? { width: Math.min(352, innerWidth - 24), height: 300 };
      const position = placeCoach(rect, size, { width: innerWidth, height: innerHeight });
      const next = { rect, ...position, suspended, viaMore: element?.dataset.tour === "more" };
      setGeometry(previous => JSON.stringify(previous) === JSON.stringify(next) ? previous : next);
    };
    function schedule() { if (!frame) frame = requestAnimationFrame(update); }
    // Observe structure and modal visibility only, not canvas gestures or styles.
    const mutations = new MutationObserver(records => {
      if (records.some(record => !(record.target instanceof Element) || !record.target.closest("[data-tour-layer]"))) schedule();
    });
    mutations.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["open", "disabled", "aria-busy", "aria-disabled"] });
    if (card.current) resize.observe(card.current);
    window.addEventListener("resize", schedule);
    window.addEventListener("scroll", schedule, true);
    update();
    return () => { cancelAnimationFrame(frame); mutations.disconnect(); resize.disconnect(); window.removeEventListener("resize", schedule); window.removeEventListener("scroll", schedule, true); };
  }, [state.step, path, targetsKey, ready, geometry.suspended]);

  useEffect(() => {
    if (state.step === null || geometry.suspended) return;
    const frame = requestAnimationFrame(() => heading.current?.focus({ preventScroll: true }));
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented || document.querySelector("dialog[open]")) return;
      event.preventDefault(); controller.close("Skipped");
    };
    window.addEventListener("keydown", escape);
    return () => { cancelAnimationFrame(frame); window.removeEventListener("keydown", escape); };
  }, [state.step, geometry.suspended, controller]);

  if (state.step === null) return state.failure ? createPortal(
    <div className="wk-tour-save-error" role="status"><span>Tour closed. Could not save your preference.</span>
      <Button variant="secondary" size="compact" loading={state.saving} onClick={() => void controller.retry()}>Retry saving</Button></div>, document.querySelector(".wk-feedback-stack") ?? document.body,
  ) : null;
  if (geometry.suspended) return null;
  const rect = geometry.rect;
  return createPortal(<div className="wk-tour-layer" data-tour-layer="true">
    {rect && <svg className="wk-tour-spotlight" aria-hidden="true" width="100%" height="100%">
      <defs><mask id={`${titleId}-spotlight`}><rect width="100%" height="100%" fill="white" />
        <rect x={rect.left} y={rect.top} width={rect.width} height={rect.height} rx="8" fill="black" /></mask></defs>
      <rect width="100%" height="100%" fill="var(--foreground)" opacity=".14" mask={`url(#${titleId}-spotlight)`} />
      <rect x={rect.left} y={rect.top} width={rect.width} height={rect.height} rx="8" fill="none" stroke="var(--focus)" strokeWidth="2" />
    </svg>}
    <div ref={card} className="wk-tour-card" role="dialog" aria-modal="false" aria-labelledby={titleId}
      aria-describedby={`${textId} ${progressId}${geometry.viaMore || !rect ? ` ${hintId}` : ""}`} style={{ left: geometry.left, top: geometry.top }}>
      <div className="wk-tour-meta"><span><TourHelpIcon /> Wukna tour</span><span id={progressId}>{state.step + 1} of {TOUR_LENGTH}</span></div>
      <h2 ref={heading} id={titleId} tabIndex={-1}>{definition.title}</h2>
      <p id={textId}>{definition.text}</p>
      {rect && geometry.viaMore && <p id={hintId} className="wk-tour-hint">Find {definition.title} in the More menu.</p>}
      {!rect && <p id={hintId} className="wk-tour-hint">Explore this area from the main navigation whenever you’re ready.</p>}
      <div className="wk-tour-actions">
        <Button variant="quiet" size="compact" onClick={() => controller.close("Skipped")}>Skip tour</Button>
        <div className="wk-tour-pagination">
          <Button variant="quiet" size="compact" disabled={state.step === 0} onClick={() => controller.move(-1)}><ArrowLeft size={15} aria-hidden="true" /> Back</Button>
          <Button size="compact" onClick={() => state.step === TOUR_LENGTH - 1 ? controller.close("Completed") : controller.move(1)}>
            {state.step === TOUR_LENGTH - 1 ? "Finish" : "Next"}<ArrowRight size={15} aria-hidden="true" /></Button>
        </div>
      </div>
    </div>
  </div>, document.body);
}
