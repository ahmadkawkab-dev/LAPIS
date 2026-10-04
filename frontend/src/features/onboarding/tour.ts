export const TOUR_VERSION = 1;
export const TOUR_LENGTH = 5;
export type TourStatus = { status: "NotStarted" | "Completed" | "Skipped"; version: number };
export type TourEvent = "tour_started" | "tour_step_viewed" | "tour_step_completed" | "tour_skipped" | "tour_completed" | "tour_replayed";
export type TourSnapshot = { step: number | null; loaded: boolean; pending: TourStatus | null; saving: boolean; failure: boolean };

/** Domain state is independent of the coach mark, routes and rendering technology. */
export class TourController {
  private value: TourSnapshot = { step: null, loaded: false, pending: null, saving: false, failure: false };
  private listeners = new Set<() => void>();
  private interacted = false;
  private api: { get: () => Promise<TourStatus>; save: (state: TourStatus) => Promise<TourStatus> };
  private emit: (event: TourEvent, step: number) => void;
  constructor(api: { get: () => Promise<TourStatus>; save: (state: TourStatus) => Promise<TourStatus> },
    emit: (event: TourEvent, step: number) => void = () => {}) { this.api = api; this.emit = emit; }
  getSnapshot = () => this.value;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private set(update: Partial<TourSnapshot>) { this.value = { ...this.value, ...update }; this.listeners.forEach(listener => listener()); }
  async load() {
    try {
      const state = await this.api.get();
      this.set({ loaded: true });
      if (!this.interacted && (state.version < TOUR_VERSION || (state.version === TOUR_VERSION && state.status === "NotStarted"))) this.start(false);
    } catch { this.set({ loaded: true }); } // The workspace remains available; replay can retry independently.
  }
  start = (replay = true) => {
    if (this.value.saving) return;
    this.interacted = true;
    this.set({ step: 0 });
    this.emit(replay ? "tour_replayed" : "tour_started", 0);
    this.emit("tour_step_viewed", 0);
  };
  move = (direction: -1 | 1) => {
    if (this.value.step === null) return;
    const next = Math.max(0, Math.min(TOUR_LENGTH - 1, this.value.step + direction));
    if (next === this.value.step) return;
    if (direction > 0) this.emit("tour_step_completed", this.value.step);
    this.set({ step: next });
    this.emit("tour_step_viewed", next);
  };
  close = (status: "Completed" | "Skipped") => {
    const step = this.value.step ?? 0;
    this.interacted = true;
    this.set({ step: null, pending: { status, version: TOUR_VERSION }, failure: false });
    if (status === "Completed") this.emit("tour_step_completed", step);
    this.emit(status === "Completed" ? "tour_completed" : "tour_skipped", step);
    void this.retry();
  };
  retry = async () => {
    if (!this.value.pending || this.value.saving) return;
    const pending = this.value.pending;
    this.set({ saving: true, failure: false });
    try {
      await this.api.save(pending);
      if (this.value.pending === pending) this.set({ pending: null });
    } catch { this.set({ failure: true }); }
    finally { this.set({ saving: false }); }
  };
}

const steps = [
  { title: "Home", text: "Your day at a glance. Catch up on workspace activity, see what’s coming up and pick up where you left off.", targets: ["home"] },
  { title: "Boards", text: "A shared space for notes, ideas and task lists. Invite people to collaborate in real time and keep conversations together in board chat.", targets: ["boards", "more"] },
  { title: "Calendar", text: "See scheduled tasks and events together. Plan your time and keep track of upcoming work and reminders.", targets: ["calendar"] },
  { title: "Quick tasks", text: "Capture things you need to do without choosing a date. Keep them here until you’re ready to schedule or finish them.", targets: ["quick-tasks", "more"] },
  { title: "Templates", text: "Save reusable plans for routines and repeatable work. Apply a template when you need it. Replay this tour beside your avatar anytime.", targets: ["templates", "more"] },
];
export function tourStep(index: number) {
  return steps[index] ?? steps[0];
}

export type Rect = { left: number; top: number; width: number; height: number };
export function placeCoach(target: Rect | null, card: { width: number; height: number }, viewport: { width: number; height: number }) {
  const gap = 16, edge = 12;
  const maxLeft = Math.max(edge, viewport.width - card.width - edge);
  const maxTop = Math.max(edge, viewport.height - card.height - edge);
  const clamp = (value: number, max: number) => Math.max(edge, Math.min(value, max));
  if (viewport.width <= 800 || !target) return { left: clamp((viewport.width - card.width) / 2, maxLeft),
    top: target && target.top + target.height > maxTop - gap ? edge : maxTop };
  const candidates = [
    { left: target.left + target.width + gap, top: target.top },
    { left: target.left - card.width - gap, top: target.top },
    { left: target.left, top: target.top + target.height + gap },
    { left: target.left, top: target.top - card.height - gap },
  ];
  const fitting = candidates.find(p => p.left >= edge && p.left <= maxLeft && p.top >= edge && p.top <= maxTop);
  const chosen = fitting ?? candidates.find(p => p.left >= edge && p.left <= maxLeft) ?? candidates[2];
  return { left: clamp(chosen.left, maxLeft), top: clamp(chosen.top, maxTop) };
}
