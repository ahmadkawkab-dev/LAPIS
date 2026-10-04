import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { memo, useEffect, useId, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { Bell, ArrowDown, CalendarClock, ImagePlus, RefreshCw, Send, Settings, X } from "lucide-react";
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
import { mentionMembers } from "./chatApi";
import { editMentions, normalizedMentions, mentionQuery } from "./chatMentions";
import type { ChatMention } from "./types";
import { BoardNotificationPreferences } from "../notifications/BoardNotificationPreferences";
import { setChatNotificationView } from "../notifications/notificationState";
import { compareSequence } from "./chatState";

export type ChatScrollPosition = { top: number; latest: boolean; initialized: boolean };
const time = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" });
const date = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", year: "numeric" });
function stamp(value: string) { return `${date.format(new Date(value))}, ${time.format(new Date(value))}`; }

const Message = memo(function Message({ message, own, onReply, onOpenReply }: { message: ChatMessage; own: boolean; onReply: (message: ChatMessage) => void; onOpenReply: (id: string) => void }) {
  const sender = message.sender;
  return <li className={`chat-message${own ? " chat-message--own" : ""}`} data-chat-message={message.id}>
    <Avatar size="small" identity={{ username: sender.username, displayName: sender.displayName, profileImageUrl: sender.avatarUrl }} />
    <div className="chat-message-content">
      <div className="chat-message-meta"><strong>{own ? "You" : sender.displayName || sender.username}</strong>
        <time dateTime={message.createdAt} title={stamp(message.createdAt)}>{stamp(message.createdAt)}</time></div>
      {message.replyToMessageId && <Button variant="quiet" size="compact" className="chat-reply-reference" onClick={() => onOpenReply(message.replyToMessageId!)}>↪ Reply to an earlier message</Button>}
      {message.body && <p className="chat-message-body" dir="auto">{messageBody(message)}</p>}
      <Button variant="quiet" size="compact" onClick={() => onReply(message)} aria-label={`Reply to ${sender.displayName || sender.username}`}>Reply</Button>
      {message.type === "attachment" && <ChatAttachmentView message={message} />}
      {message.type === "scheduledTask" && (message.scheduledTask ?
        <ChatScheduledTaskCard message={message} task={message.scheduledTask} /> : <p className="chat-message-body">Scheduled task unavailable</p>)}
    </div>
  </li>;
});

function messageBody(message: ChatMessage) {
  const body = message.body ?? ""; const parts = []; let end = 0;
  for (const token of [...(message.mentions ?? [])].sort((a, b) => a.start - b.start)) {
    if (token.start < end || token.start + token.length > body.length) continue;
    parts.push(body.slice(end, token.start), <mark className="chat-mention" key={token.start}>{body.slice(token.start, token.start + token.length)}</mark>);
    end = token.start + token.length;
  }
  parts.push(body.slice(end)); return parts;
}

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
  const [notificationSettings, setNotificationSettings] = useState(false);
  const [reply, setReply] = useState<ChatMessage | null>(null);
  const [notifyAuthor, setNotifyAuthor] = useState(true);
  const [mentions, setMentions] = useState<ChatMention[]>([]);
  const [query, setQuery] = useState<{ start: number; search: string } | null>(null);
  const [members, setMembers] = useState<{ userId: string; username: string; displayName: string | null }[]>([]);
  const [memberIndex, setMemberIndex] = useState(0);
  const [mentionFailure, setMentionFailure] = useState("");
  useEffect(() => {
    if (!query) { setMembers([]); return; }
    const request = new AbortController(); setMemberIndex(0); setMentionFailure("");
    const timer = setTimeout(() => { void mentionMembers(controller.boardId, query.search, request.signal).then(items => {
      if (!request.signal.aborted) setMembers(items);
    }).catch(() => { if (!request.signal.aborted) setMentionFailure("Could not load members. Type again to retry."); }); }, 150);
    return () => { clearTimeout(timer); request.abort(); };
  }, [controller.boardId, query]);
  function selectMention(member: typeof members[number]) {
    if (!query || !member || mentions.length >= 10) return;
    const caret = composer.current?.selectionStart ?? query.start;
    const token = `@${member.username}`;
    const draft = snapshot.draft.slice(0, query.start) + token + " " + snapshot.draft.slice(caret);
    setMentions([...editMentions(snapshot.draft, draft, mentions), { userId: member.userId, start: query.start, length: token.length }]);
    controller.setDraft(draft); setQuery(null);
    requestAnimationFrame(() => { composer.current?.focus(); composer.current?.setSelectionRange(query.start + token.length + 1, query.start + token.length + 1); });
  }
  function beginReply(message: ChatMessage) {
    setReply(message); setNotifyAuthor(true); setScheduleOpen(false); controller.selectAttachment(null);
    composer.current?.focus();
  }
  const settingsButton = useRef<HTMLButtonElement>(null);
  const muted = controller.isMuted(now);
  const wait = Math.max(0, Math.ceil((snapshot.cooldownUntil - now) / 1000));
  const canSend = controller.canSend(now);
  const status = snapshot.status === "connected" ? "Live" : snapshot.status === "offline" ? "Offline · retrying" : "Connecting…";
  const needsClock = wait > 0 || muted && snapshot.mutedUntil !== null;

  useEffect(() => {
    if (!modal) closeButton.current?.focus({ preventScroll: true });
    return () => { setChatNotificationView(null); controller.stopTyping(); controller.setViewingLatest(false, !scrollPosition.current.latest); };
  }, [controller, modal, scrollPosition]);
  useEffect(() => {
    setChatNotificationView({ boardId: controller.boardId, visible: document.visibilityState === "visible" && document.hasFocus() && !controls && !snapshot.revoked,
      latest: !controls && !snapshot.revoked && scrollPosition.current.latest });
  }, [controller.boardId, controls, snapshot.revoked, scrollPosition]);
  useEffect(() => {
    const changed = () => remember();
    window.addEventListener("focus", changed); window.addEventListener("blur", changed);
    document.addEventListener("visibilitychange", changed);
    return () => {
      window.removeEventListener("focus", changed); window.removeEventListener("blur", changed);
      document.removeEventListener("visibilitychange", changed);
    };
  }, [controller, controls, scrollPosition]);
  useEffect(() => {
    if (!needsClock) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [needsClock]);

  function remember() {
    const element = list.current;
    if (!element) return;
    const state = controller.getSnapshot();
    const latest = element.scrollHeight - element.clientHeight - element.scrollTop < 64 && !state.windowHasNewer && !state.revoked;
    const reading = document.visibilityState === "visible" && document.hasFocus() && !controls && !state.revoked;
    setChatNotificationView({ boardId: controller.boardId, visible: reading, latest });
    follow.current = latest;
    scrollPosition.current = { top: element.scrollTop, latest, initialized: true };
    controller.setViewingLatest(reading && latest, !latest);
    const first = state.messages[0], last = state.messages.at(-1);
    const olderUnread = first && state.hasOlder &&
      BigInt(state.lastReadSequence) + 1n < BigInt(first.sequence);
    if (reading && latest && last && !olderUnread) controller.markReadThrough(last);
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
    if (document.visibilityState === "visible" && document.hasFocus() && last) controller.markReadThrough(last);
  }
  async function openReply(id: string) {
    follow.current = false;
    await controller.focusMessage(id);
    requestAnimationFrame(() => list.current?.querySelector<HTMLElement>(`[data-chat-message="${id}"]`)?.scrollIntoView({ block: "center" }));
  }
  function send() {
    if (!controller.canSend()) return;
    // Capture/clear the draft synchronously; loading the latest window must not delay its send.
    follow.current = true;
    void controller.showLatest();
    void controller.sendDraft({ mentions: normalizedMentions(snapshot.draft, mentions), replyToMessageId: reply?.id ?? null, notifyReplyAuthor: notifyAuthor });
    setReply(null); setMentions([]); setQuery(null);
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
        }}><Settings size={16} aria-hidden="true" /></Button>}
      <Button variant="quiet" size="compact" className="wk-icon-button" aria-label="Your chat notifications" title="Your chat notifications" aria-expanded={notificationSettings} onClick={() => setNotificationSettings(value => !value)}><Bell size={16} aria-hidden="true" /></Button>
      <IconButton label="Refresh chat" disabled={snapshot.revoked || snapshot.syncing} onClick={() => void controller.refresh()}><RefreshCw size={16} aria-hidden="true" /></IconButton>
      <Button ref={closeButton} variant="quiet" size="compact" className="wk-icon-button" aria-label="Close chat" onClick={onClose}><X size={18} /></Button>
    </header>
    {notificationSettings && <BoardNotificationPreferences key={controller.boardId} boardId={controller.boardId} />}
    <div className="chat-status" role="status"><span className={`chat-status-dot${snapshot.status === "connected" ? " is-live" : ""}`} />
      {snapshot.revoked ? "Access removed" : status}
      {snapshot.joined && snapshot.joined.slowModeSeconds > 0 && <span>Slow mode · {snapshot.joined.slowModeSeconds}s</span>}
    </div>
    {snapshot.error && <div className="chat-error" role="alert">{snapshot.error}
      {!snapshot.revoked && <Button variant="quiet" size="compact" onClick={() => void controller.refresh()}>Retry</Button>}</div>}
    {controls && snapshot.joined?.isOwner && !snapshot.revoked ? <ChatControls controller={controller} snapshot={snapshot}
      onBack={() => { setControls(false); queueMicrotask(() => settingsButton.current?.focus()); }} /> : <>
    <div className="chat-history" ref={list} onScroll={remember} tabIndex={0} aria-label="Chat history" aria-busy={!snapshot.loaded || snapshot.loadingOlder}>
      {!snapshot.loaded && !snapshot.revoked && <LoadingSkeleton layout="chat" label="Loading messages…" />}
      {snapshot.loaded && snapshot.hasOlder && <Button className="chat-older" variant="quiet" size="compact" disabled={snapshot.loadingOlder} onClick={() => void older()}>
        {snapshot.loadingOlder ? "Loading earlier messages…" : "Earlier messages"}</Button>}
      {snapshot.loaded && !snapshot.messages.length && !snapshot.pending.length && <div className="chat-empty"><p>No messages yet.</p><p>Start a conversation with this board’s members.</p></div>}
      <ol className="chat-messages">{snapshot.messages.map(message => <Message key={message.id} message={message} own={message.sender.userId === controller.userId} onReply={beginReply} onOpenReply={id => void openReply(id)} />)}
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
      <ArrowDown size={14} aria-hidden="true" />{snapshot.unseen > 0 ? `${snapshot.unseen} new message${snapshot.unseen === 1 ? "" : "s"}` : "Latest messages"}</Button>}
    <div className="chat-typing" role="status" aria-live="polite">{typingLabel(snapshot.typing)}</div>
    {scheduleOpen ? <ChatScheduledTaskComposer controller={controller} draft={snapshot.scheduledDraft}
      canSchedule={controller.canSchedule(now)} hint={snapshot.revoked ? "Chat access ended." : muted ? "You’re muted in this chat." :
        wait > 0 ? `You can post again in ${wait}s` : "Post to this board’s chat"}
      onBack={() => setScheduleOpen(false)} onSend={sendScheduledTask} /> :
    <form className="chat-composer" onSubmit={event => { event.preventDefault(); send(); }}>
      {reply && <div className="chat-reply-composer"><span>Replying to {reply.sender.displayName || reply.sender.username}</span>
        <label><input type="checkbox" checked={notifyAuthor} onChange={event => setNotifyAuthor(event.target.checked)} /> Notify author</label>
        <Button type="button" variant="quiet" size="compact" onClick={() => setReply(null)}>Cancel reply</Button></div>}
      <label id={labelId} htmlFor="chat-draft">Message</label>
      {snapshot.selectedFile && <div className="chat-selected-file"><ImagePlus size={17} aria-hidden="true" /><span title={snapshot.selectedFile.name}>{snapshot.selectedFile.name}</span>
        <Button type="button" variant="quiet" size="compact" aria-label="Remove selected image" onClick={() => controller.selectAttachment(null)}><X size={16} /></Button></div>}
      {snapshot.attachmentError && <p className="chat-pending-error" role="alert">{snapshot.attachmentError}</p>}
      <textarea id="chat-draft" ref={composer} value={snapshot.draft} rows={3} maxLength={snapshot.selectedFile ? 2000 : 4000}
        disabled={snapshot.revoked || muted} aria-labelledby={labelId} aria-describedby={hintId}
        placeholder={muted ? "You can still read this chat" : "Message this board…"}
        onChange={event => { const draft = event.target.value; setMentions(editMentions(snapshot.draft, draft, mentions));
          setQuery(snapshot.selectedFile ? null : mentionQuery(draft, event.target.selectionStart)); controller.setDraft(draft); controller.typingActivity(); }}
        onClick={event => setQuery(snapshot.selectedFile ? null : mentionQuery(snapshot.draft, event.currentTarget.selectionStart))}
        aria-autocomplete="list" aria-haspopup="listbox" aria-activedescendant={members[memberIndex] ? `chat-mention-${memberIndex}` : undefined}
        aria-controls={members.length ? "chat-mention-members" : undefined}
        onBlur={() => controller.stopTyping()}
        onKeyDown={event => {
          if (members.length && !event.nativeEvent.isComposing) {
            if (event.key === "ArrowDown" || event.key === "ArrowUp") { event.preventDefault(); setMemberIndex(value => (value + (event.key === "ArrowDown" ? 1 : members.length - 1)) % members.length); return; }
            if (event.key === "Enter" && !event.shiftKey && members[memberIndex]) { event.preventDefault(); selectMention(members[memberIndex]); return; }
            if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); setQuery(null); return; }
          }
          if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); send(); } }} />
      {members.length > 0 && <div id="chat-mention-members" role="listbox" aria-label="Board members" className="chat-mention-picker">
        {members.map((member, index) => <button type="button" id={`chat-mention-${index}`} role="option" aria-selected={index === memberIndex} key={member.userId}
          onMouseDown={event => event.preventDefault()} onClick={() => selectMention(member)}>@{member.username}{member.displayName ? ` · ${member.displayName}` : ""}</button>)}</div>}
      {mentionFailure && <p role="status">{mentionFailure}</p>}
      <div className="chat-composer-footer"><input ref={fileInput} className="chat-file-input" type="file" accept="image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp"
        aria-hidden="true" tabIndex={-1} disabled={snapshot.revoked || muted || snapshot.sending} onChange={event => {
          controller.selectAttachment(event.target.files?.[0] ?? null); setReply(null); setMentions([]); setQuery(null); event.target.value = "";
        }} />
        <Button type="button" variant="quiet" size="compact" aria-label="Attach image" title="Attach JPEG, PNG or WebP image"
          disabled={snapshot.revoked || muted || snapshot.sending} onClick={() => fileInput.current?.click()}><ImagePlus size={17} aria-hidden="true" /></Button>
        <Button type="button" variant="quiet" size="compact" aria-label="Create scheduled task" title="Create scheduled task"
          disabled={snapshot.revoked || muted || snapshot.sending} onClick={() => { controller.stopTyping(); setScheduleOpen(true); }}>
          <CalendarClock size={17} aria-hidden="true" /><span>Task</span></Button>
        <p id={hintId}>{snapshot.revoked ? "Chat access ended." : muted ? "You’re muted in this chat." :
        wait > 0 ? `You can send again in ${wait}s` : "Enter to send · Shift + Enter for a new line"}
        {snapshot.draft.length > (snapshot.selectedFile ? 1500 : 3500) && <span> · {snapshot.draft.length}/{snapshot.selectedFile ? "2,000" : "4,000"}</span>}</p>
        <Button type="submit" size="compact" disabled={!canSend} aria-label="Send message"><Send size={16} aria-hidden="true" /><span>Send</span></Button></div>
    </form>}</>}
  </aside>;
}
