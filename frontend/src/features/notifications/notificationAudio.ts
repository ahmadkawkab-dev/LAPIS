import { notificationSounds, type NotificationSoundKey } from "./notificationSounds.ts";

type PlaybackResult = "played" | "blocked" | "failed" | "cancelled";

/** A context resumed during user input remains usable for later realtime alerts. */
export class NotificationAudioPlayer {
  private context: AudioContext | null = null;
  private buffers = new Map<string, Promise<AudioBuffer>>();
  private active: { source: AudioBufferSourceNode; gain: GainNode } | null = null;
  private generation = 0;
  private readonly createContext: () => AudioContext;
  private readonly fetchClip: typeof fetch;

  constructor(createContext: () => AudioContext = () => new AudioContext(), fetchClip: typeof fetch = (...args) => fetch(...args)) {
    this.createContext = createContext;
    this.fetchClip = fetchClip;
  }

  private getContext() { return this.context ??= this.createContext(); }

  get ready() { return this.context?.state === "running"; }

  /** Call synchronously from a trusted click, tap or key event, before any awaits. */
  async unlock(): Promise<boolean> {
    try {
      const context = this.getContext();
      if (context.state !== "running") await context.resume();
      return context.state === "running";
    } catch { return false; }
  }

  stop() {
    this.generation++;
    const active = this.active; this.active = null;
    if (active) { active.source.onended = null; active.source.stop(); active.source.disconnect(); active.gain.disconnect(); }
  }

  async play(key: NotificationSoundKey, volume: number): Promise<PlaybackResult> {
    this.stop();
    if (!Number.isFinite(volume) || volume <= 0) return "cancelled";
    const generation = this.generation;
    try {
      const context = this.getContext();
      if (context.state !== "running") return "blocked";
      const path = notificationSounds.find(clip => clip.key === key)!.path;
      let buffer = this.buffers.get(path);
      if (!buffer) {
        buffer = this.fetchClip(path).then(async response => {
          if (!response.ok) throw new Error("Sound unavailable");
          return context.decodeAudioData(await response.arrayBuffer());
        });
        this.buffers.set(path, buffer);
        // A transient asset failure must not poison all future attempts.
        void buffer.catch(() => { if (this.buffers.get(path) === buffer) this.buffers.delete(path); });
      }
      const decoded = await buffer;
      if (generation !== this.generation) return "cancelled";
      if (context.state !== "running") return "blocked";
      const source = context.createBufferSource(), gain = context.createGain();
      source.buffer = decoded; gain.gain.value = Math.min(1, volume);
      source.connect(gain); gain.connect(context.destination);
      const active = { source, gain }; this.active = active;
      source.onended = () => { source.disconnect(); gain.disconnect(); if (this.active === active) this.active = null; };
      source.start();
      return "played";
    } catch { return generation === this.generation ? "failed" : "cancelled"; }
  }
}

export const notificationAudio = new NotificationAudioPlayer();
