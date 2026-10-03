import { useCallback, useEffect, useRef, useState } from "react";
import { Avatar } from "../../components/ui/Avatar";
import { Button } from "../../components/ui/Button";
import { chatErrorMessage, chatModerationApi } from "./chatApi";
import type { ChatController, ChatSnapshot } from "./ChatController";
import type { ChatModerationMember } from "./types";

export function ChatControls({ controller, snapshot, onBack }: {
  controller: ChatController; snapshot: ChatSnapshot; onBack: () => void;
}) {
  const [members, setMembers] = useState<ChatModerationMember[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [seconds, setSeconds] = useState(String(snapshot.joined?.slowModeSeconds ?? 0));
  const [duration, setDuration] = useState("permanent");
  const [busy, setBusy] = useState(false), [loading, setLoading] = useState(true), [error, setError] = useState("");
  const [now, setNow] = useState(Date.now);
  const lifetime = useRef<AbortController | null>(null);
  const listVersion = useRef(0);
  const heading = useRef<HTMLHeadingElement>(null);
  const revision = snapshot.joined?.settingsRevision;
  const slowMode = snapshot.joined?.slowModeSeconds ?? 0;
  useEffect(() => { setSeconds(String(slowMode)); }, [revision, slowMode]);
  const load = useCallback(async (after?: string) => {
    const signal = lifetime.current?.signal;
    if (!signal || signal.aborted) return;
    const version = ++listVersion.current;
    setLoading(true);
    try {
      const page = await chatModerationApi.members(controller.boardId, signal, after);
      if (signal.aborted || version !== listVersion.current) return;
      setMembers(current => after ? [...current, ...page.items] : page.items); setNext(page.nextUserId);
    } catch (cause) { if (!signal.aborted && version === listVersion.current) setError(chatErrorMessage(cause)); }
    finally { if (!signal.aborted && version === listVersion.current) setLoading(false); }
  }, [controller]);
  useEffect(() => {
    const abort = new AbortController(); lifetime.current = abort;
    heading.current?.focus({ preventScroll: true });
    return () => { abort.abort(); lifetime.current = null; ++listVersion.current; };
  }, []);
  useEffect(() => { void load(); }, [load, snapshot.moderationVersion]);
  const hasTimedMute = members.some(member => member.isMuted && member.mutedUntil !== null);
  useEffect(() => {
    if (!hasTimedMute) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [hasTimedMute]);

  async function update(action: (signal: AbortSignal) => Promise<unknown>) {
    const signal = lifetime.current?.signal;
    if (!signal || busy) return;
    setBusy(true); setError("");
    try { await action(signal); }
    catch (cause) { if (!signal.aborted) setError(chatErrorMessage(cause)); }
    finally {
      if (!signal.aborted) {
        // Refresh even after a lost reply: PUT saves a desired state and can be replayed safely.
        await Promise.all([load(), controller.refreshState()]);
        if (!signal.aborted) setBusy(false);
      }
    }
  }
  return <section className="chat-controls" aria-label="Chat settings">
    <Button variant="quiet" size="compact" onClick={onBack}>Back to messages</Button>
    <h3 ref={heading} tabIndex={-1}>Chat settings</h3>
    {error && <p className="chat-control-error" role="alert">{error}</p>}
    <form onSubmit={event => { event.preventDefault();
      if (revision !== undefined) void update(signal => chatModerationApi.settings(controller.boardId, Number(seconds), revision, signal));
    }}>
      <label htmlFor="chat-slow-mode">Slow mode (seconds)</label>
      <div className="chat-control-field"><input id="chat-slow-mode" type="number" min={0} max={21600} step={1} required
        value={seconds} onChange={event => setSeconds(event.target.value)} disabled={busy} />
        <Button type="submit" size="compact" disabled={busy || seconds.trim() === "" || Number(seconds) === slowMode}>Save</Button></div>
      <p>0 turns slow mode off. Each guest has their own delay; you can send at any time.</p>
    </form>
    <h3>Member chat access</h3>
    <label htmlFor="chat-mute-duration">New mute duration</label>
    <select id="chat-mute-duration" value={duration} onChange={event => setDuration(event.target.value)} disabled={busy}>
      <option value="permanent">Until unmuted</option><option value="3600">1 hour</option><option value="86400">1 day</option>
    </select>
    <p>Muted members can still read messages and use the board.</p>
    <ul className="chat-control-members">{members.map(member => {
      const sender = member.sender;
      const name = sender.displayName || sender.username;
      const muted = member.isMuted && (!member.mutedUntil || Date.parse(member.mutedUntil) > now + snapshot.serverOffsetMs);
      return <li key={sender.userId}>
        <Avatar size="small" identity={{ username: sender.username, displayName: sender.displayName, profileImageUrl: sender.avatarUrl }} />
        <div><strong>{name}</strong><span>{member.role === 1 ? "Owner" : muted ?
          member.mutedUntil ? `Muted until ${new Date(member.mutedUntil).toLocaleString()}` : "Muted" : "Can send messages"}</span></div>
        {member.role !== 1 && <Button size="compact" variant="quiet" disabled={busy || loading}
          aria-label={`${muted ? "Unmute" : "Mute"} ${name}`} onClick={() => void update(signal => chatModerationApi.mute(
            controller.boardId, member, !muted, muted || duration === "permanent" ? null :
              new Date(Date.now() + snapshot.serverOffsetMs + Number(duration) * 1000).toISOString(), signal))}>
          {muted ? "Unmute" : "Mute"}</Button>}
      </li>;
    })}</ul>
    {loading && <p role="status">Loading member status…</p>}
    {next && <Button variant="quiet" disabled={busy || loading} onClick={() => void load(next)}>More members</Button>}
    {!loading && !members.length && <Button variant="quiet" onClick={() => void load()}>Reload members</Button>}
  </section>;
}
