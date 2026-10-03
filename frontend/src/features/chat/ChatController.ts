import { ChatApiError, chatErrorMessage } from "./chatApi.ts";
import { chatDraftKey, compareSequence, maxChatMessages, mergeChatMessages, readChatDraft, saveChatDraft } from "./chatState.ts";
import { ChatTypingState } from "./chatTyping.ts";
import { emptyScheduledTaskDraft } from "./scheduledTask.ts";
import type { AccessRevoked, ChatApi, ChatJoined, ChatMessage, ChatPage, ChatSender, ChatTransport, ChatTransportFactory, ChatTransportStatus, ScheduledTaskDraft, ScheduledTaskWrite } from "./types.ts";

export type PendingChatMessage = { clientMessageId: string; body: string; createdAt: string; file: File | null;
  type: ChatMessage["type"]; task: ScheduledTaskWrite | null; status: "sending" | "failed"; error: string | null };
export type ChatSnapshot = {
  messages: ChatMessage[]; pending: PendingChatMessage[]; draft: string; selectedFile: File | null; attachmentError: string | null;
  scheduledDraft: ScheduledTaskDraft;
  loaded: boolean; syncing: boolean; loadingOlder: boolean; hasOlder: boolean;
  sending: boolean; revoked: boolean; error: string | null; status: ChatTransportStatus;
  joined: ChatJoined | null; muted: boolean; mutedUntil: string | null;
  serverOffsetMs: number; cooldownUntil: number; unseen: number; lastReadSequence: string; windowHasNewer: boolean;
  typing: ChatSender[]; moderationVersion: number;
};
type Options = { storage?: Storage | null; now?: () => number; operationId?: () => string;
  schedule?: (callback: () => void, delay: number) => ReturnType<typeof setTimeout>;
  cancel?: (timer: ReturnType<typeof setTimeout>) => void };

/** Owns only this board's chat. Canvas state and authentication stay in their existing owners. */
export class ChatController {
  private snapshot: ChatSnapshot;
  private readonly listeners = new Set<() => void>();
  private readonly draftKey: string;
  private readonly now: () => number;
  private readonly schedule: NonNullable<Options["schedule"]>;
  private readonly cancel: NonNullable<Options["cancel"]>;
  private active = false;
  private epoch = 0;
  private historyVersion = 0;
  private abort = new AbortController();
  private transport: ChatTransport | null = null;
  private cursor: string | null = null;
  private through: string | null = null;
  private caughtSequence = "0";
  private dirty = false;
  private syncing: Promise<void> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private viewingLatest = false;
  private pinHistory = false;
  private readonly deferredRevocations: AccessRevoked[] = [];
  private readonly remoteTyping = new ChatTypingState();
  private typingTimer: ReturnType<typeof setTimeout> | null = null;
  private idleTimer: ReturnType<typeof setTimeout> | null = null;
  private localTyping = false;
  private lastTypingAt = -Infinity;
  private stateRequest: Promise<void> | null = null;
  private stateDirty = false;
  private lastAttachmentSweep = -Infinity;
  private readonly refreshingAttachments = new Map<string, Promise<void>>();
  private readonly attachmentRefreshDirty = new Set<string>();
  private readTarget: Pick<ChatMessage, "cursor" | "sequence"> | null = null;
  private readInFlight: Promise<void> | null = null;
  private readRetryTimer: ReturnType<typeof setTimeout> | null = null;

  readonly boardId: string;
  readonly userId: string;
  private readonly api: ChatApi;
  private readonly transportFactory: ChatTransportFactory;
  private readonly options: Options;

  constructor(boardId: string, userId: string, api: ChatApi,
    transportFactory: ChatTransportFactory, options: Options = {}) {
    this.boardId = boardId; this.userId = userId; this.api = api;
    this.transportFactory = transportFactory; this.options = options;
    this.now = options.now ?? Date.now;
    this.schedule = options.schedule ?? ((callback, delay) => setTimeout(callback, delay));
    this.cancel = options.cancel ?? (timer => clearTimeout(timer));
    this.draftKey = chatDraftKey(userId, boardId);
    this.snapshot = { messages: [], pending: [], draft: readChatDraft(options.storage ?? null, this.draftKey), selectedFile: null, attachmentError: null,
      scheduledDraft: emptyScheduledTaskDraft(),
      loaded: false, syncing: false, loadingOlder: false, hasOlder: false, sending: false,
      revoked: false, error: null, status: "connecting", joined: null, muted: false, mutedUntil: null,
      serverOffsetMs: 0, cooldownUntil: 0, unseen: 0, lastReadSequence: "0",
      windowHasNewer: false, typing: [], moderationVersion: 0 };
  }

  getSnapshot = () => this.snapshot;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };

  activate() {
    if (this.active || this.snapshot.revoked) return;
    this.active = true;
    this.abort = new AbortController();
    const epoch = ++this.epoch;
    this.transport = this.transportFactory(this.boardId, {
      status: status => {
        if (!this.valid(epoch)) return;
        if (status !== "connected") { this.stopTyping(); this.clearTyping(); }
        this.patch({ status });
      },
      joined: joined => {
        if (!this.valid(epoch) || joined.boardId !== this.boardId) return;
        this.applyState(joined);
        // The join's latest cursor is only a head hint, never recovery progress.
        for (const event of this.deferredRevocations.splice(0)) this.handleRevocation(event);
        if (this.valid(epoch)) {
          void this.reconcile();
          void this.refreshUnresolvedAttachments();
        }
      },
      message: event => { if (this.valid(epoch) && event.boardId === this.boardId) void this.reconcile(); },
      attachment: event => {
        if (!this.valid(epoch) || event.boardId !== this.boardId || event.eventVersion !== 1) return;
        void this.refreshMessage(event.messageId);
        void this.reconcile();
      },
      revoked: event => { if (this.valid(epoch)) this.handleRevocation(event); },
      forbidden: () => { if (this.valid(epoch)) this.revoke(); },
      typingDenied: () => { if (this.valid(epoch)) { this.stopTyping(); void this.refreshState(); } },
      settings: event => {
        if (!this.valid(epoch) || event.boardId !== this.boardId || event.eventVersion !== 1) return;
        this.patch({ moderationVersion: this.snapshot.moderationVersion + 1 });
        void this.refreshState();
      },
      memberState: event => {
        if (!this.valid(epoch) || event.boardId !== this.boardId || event.eventVersion !== 1) return;
        this.remoteTyping.clearMember(event.memberUserId, event.membershipInstanceId); this.updateTyping();
        if (event.memberUserId === this.userId && event.membershipInstanceId !== this.snapshot.joined?.membershipInstanceId) return;
        this.patch({ moderationVersion: this.snapshot.moderationVersion + 1 });
        if (event.memberUserId === this.userId) void this.refreshState();
      },
      typing: event => {
        if (!this.valid(epoch) || this.snapshot.status !== "connected") return;
        this.remoteTyping.accept(event, this.boardId, this.userId, this.now() + this.snapshot.serverOffsetMs);
        this.updateTyping();
      },
    });
    this.transport.start();
    void this.reconcile();
  }

  stop() {
    this.stopTyping(); this.clearTyping();
    this.active = false; ++this.epoch;
    this.abort.abort();
    if (this.retryTimer !== null) this.cancel(this.retryTimer);
    if (this.readRetryTimer !== null) this.cancel(this.readRetryTimer);
    this.retryTimer = null; this.syncing = null; this.stateRequest = null; this.stateDirty = false;
    this.readRetryTimer = null; this.readInFlight = null; this.readTarget = null;
    this.lastAttachmentSweep = -Infinity; this.refreshingAttachments.clear(); this.attachmentRefreshDirty.clear();
    void this.transport?.stop(); this.transport = null;
    this.patch({ syncing: false, loadingOlder: false, sending: false,
      pending: this.snapshot.pending.map(message => message.status === "sending"
        ? { ...message, status: "failed", error: "Delivery wasn’t confirmed. Retry to check the saved message." } : message) });
  }

  setDraft(draft: string) {
    if (this.snapshot.revoked) return;
    this.patch({ draft: draft.slice(0, this.snapshot.selectedFile ? 2000 : 4000) });
    saveChatDraft(this.options.storage ?? null, this.draftKey, this.snapshot.draft);
    if (!draft.trim()) this.stopTyping();
  }

  setScheduledDraft(update: Partial<ScheduledTaskDraft>) {
    if (this.snapshot.revoked) return;
    this.patch({ scheduledDraft: { ...this.snapshot.scheduledDraft, ...update } });
  }

  selectAttachment(file: File | null) {
    if (this.snapshot.revoked) return;
    const extension = file?.name.split(".").at(-1)?.toLowerCase();
    const validType = file?.type === "image/jpeg" && ["jpg", "jpeg"].includes(extension ?? "") ||
      file?.type === "image/png" && extension === "png" || file?.type === "image/webp" && extension === "webp";
    if (file && !validType) {
      this.patch({ selectedFile: null, attachmentError: "Choose a JPEG, PNG, or WebP image." }); return;
    }
    if (file && (file.size === 0 || file.size > 10 * 1024 * 1024)) {
      this.patch({ selectedFile: null, attachmentError: "This image exceeds the maximum upload size." }); return;
    }
    if (file && this.snapshot.draft.trim().length > 2000) {
      this.patch({ attachmentError: "Shorten your message to 2,000 characters before adding an image." }); return;
    }
    this.patch({ selectedFile: file, attachmentError: null });
  }

  typingActivity() {
    if (!this.active || this.snapshot.status !== "connected" || this.isMuted() || !this.snapshot.draft.trim()) {
      this.stopTyping(); return;
    }
    if (!this.localTyping || this.now() - this.lastTypingAt >= 2500) {
      this.transport?.setTyping?.(true); this.localTyping = true; this.lastTypingAt = this.now();
    }
    if (this.idleTimer !== null) this.cancel(this.idleTimer);
    this.idleTimer = this.schedule(() => { this.idleTimer = null; this.stopTyping(); }, 3000);
  }
  stopTyping() {
    if (this.idleTimer !== null) this.cancel(this.idleTimer);
    this.idleTimer = null;
    if (this.localTyping) this.transport?.setTyping?.(false);
    this.localTyping = false; this.lastTypingAt = -Infinity;
  }

  private updateTyping() {
    if (this.typingTimer !== null) this.cancel(this.typingTimer);
    const now = this.now() + this.snapshot.serverOffsetMs;
    this.patch({ typing: this.remoteTyping.members(now) });
    const expires = this.remoteTyping.nextExpiry(now);
    this.typingTimer = expires === null ? null : this.schedule(() => { this.typingTimer = null; this.updateTyping(); }, Math.max(1, expires - now));
  }
  private clearTyping() {
    if (this.typingTimer !== null) this.cancel(this.typingTimer);
    this.typingTimer = null; this.remoteTyping.clear(); this.patch({ typing: [] });
  }

  private applyState(joined: ChatJoined) {
    const previous = this.snapshot.joined;
    const same = previous?.membershipInstanceId === joined.membershipInstanceId;
    const settingsStale = same && previous.settingsRevision > joined.settingsRevision;
    const muteStale = same && previous.moderationRevision > joined.moderationRevision;
    const current = { ...joined,
      ...(settingsStale ? { slowModeSeconds: previous.slowModeSeconds, settingsRevision: previous.settingsRevision } : {}),
      ...(muteStale ? { isMuted: previous.isMuted, mutedUntil: previous.mutedUntil, moderationRevision: previous.moderationRevision } : {}) };
    const offset = Date.parse(joined.serverTime) - this.now();
    const allowed = joined.nextSendAllowedAt ? Date.parse(joined.nextSendAllowedAt) - offset : 0;
    // A state read started before an in-flight send must not shorten its cooldown.
    const revisionChanged = previous && !settingsStale && previous.settingsRevision !== joined.settingsRevision;
    const readSequence = /^\d{1,19}$/.test(joined.lastReadSequence ?? "") ? joined.lastReadSequence : "0";
    const membershipChanged = previous && !same;
    if (membershipChanged) {
      this.readTarget = null;
      if (this.readRetryTimer !== null) this.cancel(this.readRetryTimer);
      this.readRetryTimer = null;
    }
    const readAdvanced = membershipChanged || compareSequence(readSequence, this.snapshot.lastReadSequence) > 0;
    const readCurrent = !membershipChanged && compareSequence(readSequence, this.snapshot.lastReadSequence) === 0;
    const unread = Number.isSafeInteger(joined.unreadCount) && joined.unreadCount >= 0 ? joined.unreadCount : 0;
    this.patch({ joined: current, muted: current.isMuted, mutedUntil: current.mutedUntil, serverOffsetMs: offset,
      lastReadSequence: readAdvanced ? readSequence : this.snapshot.lastReadSequence,
      unseen: readAdvanced ? unread : readCurrent
        ? Math.max(this.snapshot.unseen, unread) : this.snapshot.unseen,
      cooldownUntil: settingsStale ? this.snapshot.cooldownUntil : revisionChanged ? allowed : Math.max(this.snapshot.cooldownUntil, allowed) });
    if (this.isMuted()) this.stopTyping();
  }

  refreshState(): Promise<void> {
    if (!this.active || this.snapshot.revoked) return Promise.resolve();
    this.stateDirty = true;
    if (this.stateRequest) return this.stateRequest;
    const epoch = this.epoch;
    this.stateRequest = (async () => {
      do {
        this.stateDirty = false;
        try {
          const state = await this.api.state(this.boardId, this.abort.signal);
          if (!this.valid(epoch) || state.boardId !== this.boardId) return;
          if (this.snapshot.joined && state.membershipInstanceId !== this.snapshot.joined.membershipInstanceId) this.transport?.rejoin();
          else this.applyState(state);
        } catch (cause) { if (this.valid(epoch)) this.readFailure(cause); }
      } while (this.stateDirty && this.valid(epoch));
    })().finally(() => { if (this.valid(epoch)) this.stateRequest = null; });
    return this.stateRequest;
  }

  setViewingLatest(viewing: boolean, pinHistory: boolean) {
    this.viewingLatest = viewing; this.pinHistory = pinHistory;
  }

  markReadThrough(message: Pick<ChatMessage, "boardId" | "cursor" | "sequence">) {
    if (!this.active || this.snapshot.revoked || !this.snapshot.joined || message.boardId !== this.boardId ||
      compareSequence(message.sequence, this.snapshot.lastReadSequence) <= 0) return;
    if (!this.readTarget || compareSequence(message.sequence, this.readTarget.sequence) > 0)
      this.readTarget = { cursor: message.cursor, sequence: message.sequence };
    this.flushRead();
  }

  private flushRead() {
    if (this.readTarget && compareSequence(this.readTarget.sequence, this.snapshot.lastReadSequence) <= 0)
      this.readTarget = null;
    if (this.readInFlight || this.readRetryTimer !== null || !this.readTarget || !this.active ||
      this.snapshot.revoked || !this.snapshot.joined) return;
    const target = this.readTarget;
    this.readTarget = null;
    const epoch = this.epoch, instance = this.snapshot.joined.membershipInstanceId;
    const task = this.api.read(this.boardId, target.cursor, instance, this.abort.signal).then(result => {
      if (!this.valid(epoch) || this.snapshot.joined?.membershipInstanceId !== instance) return;
      if (!/^\d{1,19}$/.test(result.lastReadSequence) || !Number.isSafeInteger(result.unreadCount) || result.unreadCount < 0)
        throw new ChatApiError("chat_protocol_error", 503);
      if (compareSequence(result.lastReadSequence, this.snapshot.lastReadSequence) >= 0)
        this.patch({ lastReadSequence: result.lastReadSequence, unseen: result.unreadCount });
    }).catch(cause => {
      if (!this.valid(epoch)) return;
      if (cause instanceof ChatApiError && cause.status === 404) { this.revoke(); return; }
      if (cause instanceof ChatApiError && cause.code === "chat_membership_changed") {
        this.transport?.rejoin(); return;
      }
      if (!this.readTarget || compareSequence(target.sequence, this.readTarget.sequence) > 0) this.readTarget = target;
      this.readRetryTimer = this.schedule(() => { this.readRetryTimer = null; this.flushRead(); }, 5000);
    }).finally(() => {
      if (this.readInFlight === task) this.readInFlight = null;
      if (this.readTarget && this.readRetryTimer === null) this.flushRead();
    });
    this.readInFlight = task;
  }

  isMuted(now = this.now()) {
    return this.snapshot.muted && (this.snapshot.mutedUntil === null ||
      Date.parse(this.snapshot.mutedUntil) > now + this.snapshot.serverOffsetMs);
  }
  canSend(now = this.now()) {
    return this.active && this.snapshot.loaded && !this.snapshot.revoked && !this.snapshot.sending &&
      !this.isMuted(now) && this.snapshot.cooldownUntil <= now && !!(this.snapshot.draft.trim() || this.snapshot.selectedFile);
  }
  canSchedule(now = this.now()) {
    return this.active && this.snapshot.loaded && !this.snapshot.revoked && !this.snapshot.sending &&
      !this.isMuted(now) && this.snapshot.cooldownUntil <= now;
  }

  async refresh() { this.transport?.rejoin(); await this.reconcile(); await this.refreshUnresolvedAttachments(); }

  private async refreshUnresolvedAttachments() {
    if (this.now() - this.lastAttachmentSweep < 60_000) return;
    this.lastAttachmentSweep = this.now();
    const ids = this.snapshot.messages.filter(message => message.type === "attachment" &&
      message.attachment && !["Available", "Rejected"].includes(message.attachment.scanStatus)).map(message => message.id);
    // Board policy bounds unresolved uploads. Four workers bound HTTP fan-out on reconnect.
    const queue = ids.slice(0, 40);
    await Promise.all(Array.from({ length: Math.min(4, queue.length) }, async () => {
      while (queue.length && this.active && !this.snapshot.revoked) await this.refreshMessage(queue.shift()!);
    }));
  }

  private async refreshMessage(id: string) {
    if (!this.active || !this.snapshot.messages.some(message => message.id === id)) return;
    const current = this.refreshingAttachments.get(id);
    if (current) { this.attachmentRefreshDirty.add(id); await current; return; }
    const epoch = this.epoch;
    const task = (async () => {
      do {
        this.attachmentRefreshDirty.delete(id);
        try {
          const message = await this.api.message(this.boardId, id, this.abort.signal);
          if (!this.valid(epoch) || message.boardId !== this.boardId || message.id !== id) return;
          if (this.snapshot.messages.some(item => item.id === id))
            this.patch({ messages: mergeChatMessages(this.snapshot.messages, [message], this.boardId) });
        } catch (cause) {
          if (this.valid(epoch) && cause instanceof ChatApiError && cause.status === 404) this.revoke();
          return;
        }
      } while (this.attachmentRefreshDirty.has(id) && this.valid(epoch));
    })();
    this.refreshingAttachments.set(id, task);
    try { await task; } finally {
      if (this.refreshingAttachments.get(id) === task) {
        this.refreshingAttachments.delete(id); this.attachmentRefreshDirty.delete(id);
      }
    }
  }

  async showLatest() {
    this.setViewingLatest(true, false);
    if (!this.snapshot.windowHasNewer) return;
    ++this.historyVersion; this.cursor = null; this.through = null;
    await this.reconcile();
  }

  reconcile(): Promise<void> {
    if (!this.active || this.snapshot.revoked) return Promise.resolve();
    this.dirty = true;
    if (this.syncing) return this.syncing;
    if (this.retryTimer !== null) this.cancel(this.retryTimer);
    this.retryTimer = null;
    const epoch = this.epoch;
    const task = this.syncHistory(epoch).finally(() => {
      if (!this.valid(epoch)) return;
      this.syncing = null;
      this.patch({ syncing: false });
      // Bound each job to ten HTTP pages; continue the same fixed batch in a new job.
      if (this.dirty && this.retryTimer === null) this.retryTimer = this.schedule(() => {
        this.retryTimer = null; void this.reconcile();
      }, this.snapshot.error ? 5000 : 0);
    });
    this.syncing = task;
    return task;
  }

  async loadOlder() {
    const first = this.snapshot.messages[0];
    if (!this.active || !first || !this.snapshot.hasOlder || this.snapshot.loadingOlder || this.snapshot.revoked) return;
    const epoch = this.epoch, version = this.historyVersion;
    this.patch({ loadingOlder: true, error: null });
    try {
      const page = await this.api.history(this.boardId, { before: first.cursor, limit: 50 }, this.abort.signal);
      if (!this.valid(epoch) || version !== this.historyVersion) return;
      this.checkPage(page);
      const merged = mergeChatMessages(this.snapshot.messages, page.items, this.boardId);
      const dropped = merged.length > maxChatMessages;
      this.patch({ messages: merged.slice(0, maxChatMessages), hasOlder: page.hasMore,
        windowHasNewer: dropped || this.snapshot.windowHasNewer });
    } catch (cause) { if (this.valid(epoch)) this.readFailure(cause); }
    finally { if (this.valid(epoch)) this.patch({ loadingOlder: false }); }
  }

  async sendDraft() {
    if (!this.canSend()) return;
    this.stopTyping();
    const operation = this.options.operationId?.() ?? crypto.randomUUID();
    const message: PendingChatMessage = { clientMessageId: operation,
      body: this.snapshot.draft.trim().replaceAll("\r\n", "\n"), createdAt: new Date(this.now()).toISOString(),
      file: this.snapshot.selectedFile, type: this.snapshot.selectedFile ? "attachment" : "text", task: null,
      status: "sending", error: null };
    this.setDraft("");
    this.patch({ selectedFile: null, attachmentError: null, pending: [...this.snapshot.pending, message] });
    await this.sendPending(message);
  }

  async sendScheduledTask(task: ScheduledTaskWrite) {
    if (!this.canSchedule()) return;
    this.stopTyping();
    const message: PendingChatMessage = {
      clientMessageId: this.options.operationId?.() ?? crypto.randomUUID(), body: "",
      createdAt: new Date(this.now()).toISOString(), file: null, type: "scheduledTask", task,
      status: "sending", error: null,
    };
    this.patch({ scheduledDraft: emptyScheduledTaskDraft(), pending: [...this.snapshot.pending, message] });
    await this.sendPending(message);
  }

  async retry(clientMessageId: string) {
    const message = this.snapshot.pending.find(item => item.clientMessageId === clientMessageId);
    if (!message || message.status !== "failed" || this.snapshot.sending || !this.active || this.snapshot.revoked ||
      this.isMuted() || this.snapshot.cooldownUntil > this.now()) return;
    await this.sendPending(message);
  }

  discard(clientMessageId: string) {
    this.patch({ pending: this.snapshot.pending.filter(message => message.clientMessageId !== clientMessageId || message.status === "sending") });
  }

  private async sendPending(message: PendingChatMessage) {
    const epoch = this.epoch;
    this.patch({ sending: true, pending: this.snapshot.pending.map(item => item.clientMessageId === message.clientMessageId
      ? { ...item, status: "sending", error: null } : item) });
    try {
      const result = message.task
        ? await this.api.schedule(this.boardId, { clientMessageId: message.clientMessageId, ...message.task }, this.abort.signal)
        : message.file
        ? await this.api.upload(this.boardId, { clientMessageId: message.clientMessageId, body: message.body, file: message.file }, this.abort.signal)
        : await this.api.send(this.boardId, { clientMessageId: message.clientMessageId, body: message.body }, this.abort.signal);
      if (!this.valid(epoch)) return;
      if (result.message.boardId !== this.boardId || result.message.sender.userId !== this.userId ||
          result.message.clientMessageId !== message.clientMessageId || result.message.type !== message.type)
        throw new ChatApiError("chat_protocol_error", 503);
      this.integrate([result.message], false);
      if (!this.snapshot.joined || result.settingsRevision === undefined || result.settingsRevision >= this.snapshot.joined.settingsRevision)
        this.patch({ cooldownUntil: result.nextSendAllowedAt ? this.now() +
          Math.max(0, Date.parse(result.nextSendAllowedAt) - Date.parse(result.serverTime)) : 0 });
      // A successful single-message response must never skip intervening history.
      void this.reconcile();
    } catch (cause) {
      if (!this.valid(epoch)) return;
      if (cause instanceof ChatApiError && cause.status === 404) { this.revoke(); return; }
      // Catch-up may confirm the operation while its HTTP response is lost.
      if (!this.snapshot.pending.some(item => item.clientMessageId === message.clientMessageId)) return;
      if (cause instanceof ChatApiError) {
        const offset = cause.serverTime ? Date.parse(cause.serverTime) - this.now() : this.snapshot.serverOffsetMs;
        const current = this.snapshot.joined;
        const staleMute = current && (cause.membershipInstanceId !== null && cause.membershipInstanceId !== current.membershipInstanceId ||
          cause.moderationRevision !== null && cause.moderationRevision < current.moderationRevision);
        this.patch({ serverOffsetMs: offset,
          cooldownUntil: cause.settingsRevision !== null && this.snapshot.joined && cause.settingsRevision < this.snapshot.joined.settingsRevision
            ? this.snapshot.cooldownUntil : cause.nextSendAllowedAt ? Date.parse(cause.nextSendAllowedAt) - offset :
            cause.retryAfterSeconds ? this.now() + cause.retryAfterSeconds * 1000 : this.snapshot.cooldownUntil,
          ...(cause.code === "chat_muted" && !staleMute ? { muted: true, mutedUntil: cause.mutedUntil,
            ...(current ? { joined: { ...current, isMuted: true, mutedUntil: cause.mutedUntil,
              moderationRevision: cause.moderationRevision ?? current.moderationRevision } } : {}) } : {}) });
      }
      this.patch({ pending: this.snapshot.pending.map(item => item.clientMessageId === message.clientMessageId
        ? { ...item, status: "failed", error: chatErrorMessage(cause) } : item) });
      void this.reconcile();
    } finally { if (this.valid(epoch)) this.patch({ sending: false }); }
  }

  private async syncHistory(epoch: number) {
    this.patch({ syncing: true });
    try {
      for (let index = 0; index < 10 && this.dirty && this.valid(epoch); index++) {
        this.dirty = false;
        const initial = this.cursor === null, previous = this.cursor, version = this.historyVersion;
        const page = await this.api.history(this.boardId, initial ? { limit: 50 } :
          { after: this.cursor!, ...(this.through ? { through: this.through } : {}), limit: 50 }, this.abort.signal);
        if (!this.valid(epoch)) return;
        if (version !== this.historyVersion) { this.dirty = true; continue; }
        this.checkPage(page);
        if (!initial && page.hasMore && page.newerCursor === previous) throw new ChatApiError("chat_invalid_page", 503);
        if (initial) {
          const head = page.items.at(-1)?.sequence ?? "0";
          const knownNewer = this.snapshot.messages.filter(message => compareSequence(message.sequence, head) > 0);
          this.patch({ messages: mergeChatMessages(page.items, knownNewer, this.boardId).slice(-maxChatMessages),
            hasOlder: page.hasMore, windowHasNewer: false, error: null });
        }
        this.integrate(page.items, !initial);
        this.cursor = page.newerCursor;
        this.caughtSequence = page.items.at(-1)?.sequence ?? this.caughtSequence;
        this.through = !initial && page.hasMore ? page.catchUpThrough : null;
        this.dirty = this.dirty || !!this.through;
        this.patch({ loaded: true, error: null });
      }
    } catch (cause) {
      if (!this.valid(epoch)) return;
      this.readFailure(cause);
      if (!this.snapshot.revoked) this.dirty = true;
    }
  }

  private integrate(incoming: ChatMessage[], countUnseen: boolean) {
    const ownOperations = new Set(incoming.filter(message => message.boardId === this.boardId && message.sender.userId === this.userId)
      .map(message => message.clientMessageId));
    const merged = mergeChatMessages(this.snapshot.messages, incoming, this.boardId);
    const dropped = merged.length > maxChatMessages;
    const messages = this.pinHistory ? merged.slice(0, maxChatMessages) : merged.slice(-maxChatMessages);
    const unseen = countUnseen && !this.viewingLatest ? incoming.filter(message => message.boardId === this.boardId &&
      message.sender.userId !== this.userId && compareSequence(message.sequence, this.caughtSequence) > 0).length : 0;
    this.patch({ messages, pending: this.snapshot.pending.filter(message => !ownOperations.has(message.clientMessageId)),
      hasOlder: this.snapshot.hasOlder || dropped && !this.pinHistory,
      windowHasNewer: this.snapshot.windowHasNewer || dropped && this.pinHistory,
      unseen: this.snapshot.unseen + unseen });
  }

  private checkPage(page: ChatPage) {
    if (page.items.some(message => message.boardId !== this.boardId || !/^[1-9]\d{0,18}$/.test(message.sequence)))
      throw new ChatApiError("chat_invalid_page", 503);
    for (let index = 1; index < page.items.length; index++)
      if (compareSequence(page.items[index - 1].sequence, page.items[index].sequence) >= 0)
        throw new ChatApiError("chat_invalid_page", 503);
  }

  private readFailure(cause: unknown) {
    if (cause instanceof ChatApiError && cause.status === 404) this.revoke();
    else this.patch({ error: chatErrorMessage(cause) });
  }

  private handleRevocation(event: AccessRevoked) {
    if (event.boardId !== this.boardId || event.eventVersion !== 1) return;
    if (!this.snapshot.joined) {
      this.deferredRevocations.push(event);
      if (this.deferredRevocations.length > 8) this.deferredRevocations.shift();
      return;
    }
    if (event.membershipInstanceId === this.snapshot.joined.membershipInstanceId) this.revoke();
  }

  private revoke() {
    this.stop();
    saveChatDraft(this.options.storage ?? null, this.draftKey, "");
    this.patch({ messages: [], pending: [], draft: "", selectedFile: null, attachmentError: null,
      scheduledDraft: emptyScheduledTaskDraft(), revoked: true, unseen: 0, lastReadSequence: "0", loaded: false,
      error: "You no longer have access to this board chat.", joined: null, status: "offline" });
  }

  private valid(epoch: number) { return this.active && !this.snapshot.revoked && this.epoch === epoch; }
  private patch(update: Partial<ChatSnapshot>) {
    this.snapshot = { ...this.snapshot, ...update };
    this.listeners.forEach(listener => listener());
  }
}
