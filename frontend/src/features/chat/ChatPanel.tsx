import { memo, useEffect, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { ArrowDown, CalendarClock, ImagePlus, RefreshCw, Send, Settings, X } from "lucide-react";
import { Avatar } from "../../components/ui/Avatar";
import { Button, IconButton } from "../../components/ui/Button";
import type { ChatController, ChatSnapshot } from "./ChatController";
import type { ChatMessage } from "./types";
import { ChatControls } from "./ChatControls";
import { ChatAttachmentView } from "./ChatAttachmentView";
import { ChatScheduledTaskCard } from "./ChatScheduledTaskCard";
import { ChatScheduledTaskComposer } from "./ChatScheduledTaskComposer";
import type { ScheduledTaskWrite } from "./types";
import { typingLabel } from "./chatTyping";
import { compareSequence } from "./chatState";

export type ChatScrollPosition = { top: number; latest: boolean; initialized: boolean };
const time = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" });
const date = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", year: "numeric" });
function stamp(value: string) { return `${date.format(new Date(value))}, ${time.format(new Date(value))}`; }

const Message = memo(function Message({ message, own }: { message: ChatMessage; own: boolean }) {
  const sender = message.sender;
  return <li className={`chat-message${own ? " chat-message--own" : ""}`} data-chat-message={message.id}>
    <Avatar size="small" identity={{ username: sender.username, displayName: sender.displayName, profileImageUrl: sender.avatarUrl }} />
    <div className="chat-message-content">
      <div className="chat-message-meta"><strong>{own ? "You" : sender.displayName || sender.username}</strong>
        <time dateTime={message.createdAt} title={stamp(message.createdAt)}>{stamp(message.createdAt)}</time></div>
      {message.body && <p className="chat-message-body" dir="auto">{message.body}</p>}
      {message.type === "attachment" && <ChatAttachmentView message={message} />}
      {message.type === "scheduledTask" && (message.scheduledTask ?
        <ChatScheduledTaskCard message={message} task={message.scheduledTask} /> : <p className="chat-message-body">Scheduled task unavailable</p>)}
    </div>
  </li>;
});

export function ChatPanel({ controller, snapshot, boardTitle, onClose, scrollPosition, modal }: {
  controller: ChatController; snapshot: ChatSnapshot; boardTitle: string; onClose: () => void;
  scrollPosition: RefObject<ChatScrollPosition>; modal: boolean;
}) {
  const list = useRef<HTMLDivElement>(null);
  const composer = useRef<HTMLTextAreaElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const initialized = useRef(false);
  const olderAnchor = useRef<{ top: number; height: number; firstId: string } | null>(null);
  const follow = useRef(scrollPosition.current.latest);
  const hintId = useId(), labelId = useId();
  const [now, setNow] = useState(Date.now);
  const [controls, setControls] = useState(false);
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const settingsButton = useRef<HTMLButtonElement>(null);
  const muted = controller.isMuted(now);
  const wait = Math.max(0, Math.ceil((snapshot.cooldownUntil - now) / 1000));
  const canSend = controller.canSend(now);
  const status = snapshot.status === "connected" ? "Live" : snapshot.status === "offline" ? "Offline · retrying" : "Connecting…";
  const needsClock = wait > 0 || muted && snapshot.mutedUntil !== null;

  useEffect(() => {
    if (!modal) closeButton.current?.focus({ preventScroll: true });
    return () => { controller.stopTyping(); controller.setViewingLatest(false, !scrollPosition.current.latest); };
  }, [controller, modal, scrollPosition]);
  useEffect(() => {
    if (!needsClock) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [needsClock]);

  function remember() {
    const element = list.current;
    if (!element) return;
    const state = controller.getSnapshot();
    const latest = document.visibilityState === "visible" &&
      element.scrollHeight - element.clientHeight - element.scrollTop < 64 && !state.windowHasNewer;
    follow.current = latest;
    scrollPosition.current = { top: element.scrollTop, latest, initialized: true };
    controller.setViewingLatest(latest, !latest);
    const first = state.messages[0], last = state.messages.at(-1);
    const olderUnread = first && state.hasOlder &&
      BigInt(state.lastReadSequence) + 1n < BigInt(first.sequence);
    if (latest && last && !olderUnread) controller.markReadThrough(last);
  }
  useLayoutEffect(() => {
    if (controls) { initialized.current = false; return; }
    const element = list.current;
    if (!element || !snapshot.loaded || !snapshot.joined) return;
    if (!initialized.current) {
      initialized.current = true;
      if (scrollPosition.current.initialized && !scrollPosition.current.latest)
        element.scrollTop = scrollPosition.current.top;
      else if (snapshot.unseen > 0 && snapshot.messages.length) {
        const unread = snapshot.messages.find(message => message.sender.userId !== controller.userId &&
          compareSequence(message.sequence, snapshot.lastReadSequence) > 0);
        const row = unread && Array.from(element.querySelectorAll<HTMLElement>("[data-chat-message]"))
          .find(item => item.dataset.chatMessage === unread.id);
        element.scrollTop = row?.offsetTop ?? 0;
        follow.current = false;
      } else element.scrollTop = element.scrollHeight;
    } else if (olderAnchor.current) {
      const anchor = olderAnchor.current;
      // Loading history can trim the far end of the bounded window; use the actual row as the anchor.
      const row = Array.from(element.querySelectorAll<HTMLElement>("[data-chat-message]"))
        .find(item => item.dataset.chatMessage === anchor.firstId);
      if (row) element.scrollTop += row.offsetTop - anchor.height;
      else element.scrollTop = anchor.top;
      olderAnchor.current = null;
    } else if (follow.current) element.scrollTop = element.scrollHeight;
    remember();
  }, [snapshot.messages, snapshot.pending, snapshot.loaded, snapshot.joined, snapshot.lastReadSequence,
    snapshot.unseen, controls, scheduleOpen]);

  async function older() {
    const previous = controller.getSnapshot().messages;
    const element = list.current, first = element?.querySelector<HTMLElement>("[data-chat-message]");
    if (element && first) olderAnchor.current = { top: element.scrollTop, height: first.offsetTop, firstId: first.dataset.chatMessage! };
    follow.current = false; controller.setViewingLatest(false, true);
    await controller.loadOlder();
    // On a failed request there was no layout change to consume the anchor.
    if (controller.getSnapshot().messages === previous) olderAnchor.current = null;
  }
  async function latest() {
    follow.current = true;
    await controller.showLatest();
    if (list.current) list.current.scrollTop = list.current.scrollHeight;
    remember();
    const last = controller.getSnapshot().messages.at(-1);
    if (document.visibilityState === "visible" && last) controller.markReadThrough(last);
  }
  function send() {
    if (!controller.canSend()) return;
    // Capture/clear the draft synchronously; loading the latest window must not delay its send.
    follow.current = true;
    void controller.showLatest();
    void controller.sendDraft();
    composer.current?.focus({ preventScroll: true });
  }
  function sendScheduledTask(task: ScheduledTaskWrite) {
    if (!controller.canSchedule()) return;
    follow.current = true;
    void controller.showLatest();
    setScheduleOpen(false);
    void controller.sendScheduledTask(task);
  }
  return <aside id="board-chat" className="board-chat" aria-label="Board chat"
    onKeyDown={event => { if (!modal && event.key === "Escape") {
      event.stopPropagation(); if (scheduleOpen) setScheduleOpen(false); else onClose();
    } }}>
    <header className="chat-header"><div><h2>Board chat</h2><p title={boardTitle}>{boardTitle}</p></div>
      {snapshot.joined?.isOwner && !snapshot.revoked && <Button ref={settingsButton} variant="quiet" size="compact"
        className="wk-icon-button" aria-label="Chat settings" aria-expanded={controls} onClick={() => {
          controller.stopTyping(); setScheduleOpen(false); setControls(value => !value);
        }}><Settings size={16} /></Button>}
      <IconButton label="Refresh chat" disabled={snapshot.revoked || snapshot.syncing} onClick={() => void controller.refresh()}><RefreshCw size={16} /></IconButton>
      <Button ref={closeButton} variant="quiet" size="compact" className="wk-icon-button" aria-label="Close chat" onClick={onClose}><X size={18} /></Button>
    </header>
    <div className="chat-status" role="status"><span className={`chat-status-dot${snapshot.status === "connected" ? " is-live" : ""}`} />
      {snapshot.revoked ? "Access removed" : status}
      {snapshot.joined && snapshot.joined.slowModeSeconds > 0 && <span>Slow mode · {snapshot.joined.slowModeSeconds}s</span>}
    </div>
    {snapshot.error && <div className="chat-error" role="alert">{snapshot.error}
      {!snapshot.revoked && <Button variant="quiet" size="compact" onClick={() => void controller.refresh()}>Retry</Button>}</div>}
    {controls && snapshot.joined?.isOwner && !snapshot.revoked ? <ChatControls controller={controller} snapshot={snapshot}
      onBack={() => { setControls(false); queueMicrotask(() => settingsButton.current?.focus()); }} /> : <>
    <div className="chat-history" ref={list} onScroll={remember} tabIndex={0} aria-label="Chat history" aria-busy={!snapshot.loaded || snapshot.loadingOlder}>
      {!snapshot.loaded && !snapshot.revoked && <p className="chat-empty">Loading messages…</p>}
      {snapshot.loaded && snapshot.hasOlder && <Button className="chat-older" variant="quiet" size="compact" disabled={snapshot.loadingOlder} onClick={() => void older()}>
        {snapshot.loadingOlder ? "Loading earlier messages…" : "Earlier messages"}</Button>}
      {snapshot.loaded && !snapshot.messages.length && !snapshot.pending.length && <div className="chat-empty"><p>No messages yet.</p><p>Start a conversation with this board’s members.</p></div>}
      <ol className="chat-messages">{snapshot.messages.map(message => <Message key={message.id} message={message} own={message.sender.userId === controller.userId} />)}
        {snapshot.pending.map(message => <li className="chat-message chat-message--pending" key={message.clientMessageId}>
          <div className="chat-message-content"><div className="chat-message-meta"><strong>You</strong><span>{message.status === "sending" ? "Sending…" : "Not confirmed"}</span></div>
            {message.file && <p className="chat-pending-file"><ImagePlus size={16} aria-hidden="true" />{message.file.name}</p>}
            {message.task && <div className="chat-task-card chat-task-card--pending"><div className="chat-task-title">
              <CalendarClock size={16} aria-hidden="true" /><strong dir="auto">{message.task.title}</strong></div>
              <span>{message.task.localStart.replace("T", " ")} · {message.task.timeZoneId}</span></div>}
            {message.body && <p className="chat-message-body" dir="auto">{message.body}</p>}
            {message.error && <p className="chat-pending-error" role="status">{message.error}</p>}
            {message.status === "failed" && <div className="chat-retry"><Button variant="quiet" size="compact" disabled={snapshot.sending || muted || wait > 0} onClick={() => void controller.retry(message.clientMessageId)}>Retry</Button>
              <Button variant="quiet" size="compact" onClick={() => controller.discard(message.clientMessageId)}>Discard</Button></div>}
          </div></li>)}
      </ol>
    </div>
    {(snapshot.unseen > 0 || snapshot.windowHasNewer) && <Button variant="secondary" size="compact" className="chat-new" onClick={() => void latest()}>
      <ArrowDown size={14} />{snapshot.unseen > 0 ? `${snapshot.unseen} new message${snapshot.unseen === 1 ? "" : "s"}` : "Latest messages"}</Button>}
    <div className="chat-typing" role="status" aria-live="polite">{typingLabel(snapshot.typing)}</div>
    {scheduleOpen ? <ChatScheduledTaskComposer controller={controller} draft={snapshot.scheduledDraft}
      canSchedule={controller.canSchedule(now)} hint={snapshot.revoked ? "Chat access ended." : muted ? "You’re muted in this chat." :
        wait > 0 ? `You can post again in ${wait}s` : "Post to this board’s chat"}
      onBack={() => setScheduleOpen(false)} onSend={sendScheduledTask} /> :
    <form className="chat-composer" onSubmit={event => { event.preventDefault(); send(); }}>
      <label id={labelId} htmlFor="chat-draft">Message</label>
      {snapshot.selectedFile && <div className="chat-selected-file"><ImagePlus size={17} aria-hidden="true" /><span title={snapshot.selectedFile.name}>{snapshot.selectedFile.name}</span>
        <Button type="button" variant="quiet" size="compact" aria-label="Remove selected image" onClick={() => controller.selectAttachment(null)}><X size={16} /></Button></div>}
      {snapshot.attachmentError && <p className="chat-pending-error" role="alert">{snapshot.attachmentError}</p>}
      <textarea id="chat-draft" ref={composer} value={snapshot.draft} rows={3} maxLength={snapshot.selectedFile ? 2000 : 4000}
        disabled={snapshot.revoked || muted} aria-labelledby={labelId} aria-describedby={hintId}
        placeholder={muted ? "You can still read this chat" : "Message this board…"}
        onChange={event => { controller.setDraft(event.target.value); controller.typingActivity(); }}
        onBlur={() => controller.stopTyping()}
        onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); send(); } }} />
      <div className="chat-composer-footer"><input ref={fileInput} className="chat-file-input" type="file" accept="image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp"
        aria-hidden="true" tabIndex={-1} disabled={snapshot.revoked || muted || snapshot.sending} onChange={event => {
          controller.selectAttachment(event.target.files?.[0] ?? null); event.target.value = "";
        }} />
        <Button type="button" variant="quiet" size="compact" aria-label="Attach image" title="Attach JPEG, PNG or WebP image"
          disabled={snapshot.revoked || muted || snapshot.sending} onClick={() => fileInput.current?.click()}><ImagePlus size={17} aria-hidden="true" /></Button>
        <Button type="button" variant="quiet" size="compact" aria-label="Create scheduled task" title="Create scheduled task"
          disabled={snapshot.revoked || muted || snapshot.sending} onClick={() => { controller.stopTyping(); setScheduleOpen(true); }}>
          <CalendarClock size={17} aria-hidden="true" /><span>Task</span></Button>
        <p id={hintId}>{snapshot.revoked ? "Chat access ended." : muted ? "You’re muted in this chat." :
        wait > 0 ? `You can send again in ${wait}s` : "Enter to send · Shift + Enter for a new line"}
        {snapshot.draft.length > (snapshot.selectedFile ? 1500 : 3500) && <span> · {snapshot.draft.length}/{snapshot.selectedFile ? "2,000" : "4,000"}</span>}</p>
        <Button type="submit" size="compact" disabled={!canSend} aria-label="Send message"><Send size={16} /><span>Send</span></Button></div>
    </form>}</>}
  </aside>;
}
