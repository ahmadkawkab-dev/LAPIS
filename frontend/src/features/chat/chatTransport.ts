import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { realtimeAccessToken } from "../../auth.ts";
import type { AccessRevoked, ChatJoined, ChatTransport, ChatTransportFactory, ChatTransportHandlers, MessageCreated, ChatTyping, ChatSettingsChanged, ChatMemberStateChanged, ChatAttachmentChanged } from "./types.ts";

type Connection = Pick<HubConnection, "state" | "start" | "stop" | "invoke" | "on" | "onclose" | "onreconnected" | "onreconnecting">;
type Timers = { schedule: (callback: () => void, delay: number) => ReturnType<typeof setTimeout>;
  cancel: (timer: ReturnType<typeof setTimeout>) => void };

export class BoardChatTransport implements ChatTransport {
  private desired = false;
  private starting: Promise<void> | null = null;
  private joining: Promise<void> | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;
  private joined = false;

  private readonly boardId: string;
  private readonly connection: Connection;
  private readonly handlers: ChatTransportHandlers;
  private readonly timers: Timers;

  constructor(boardId: string, connection: Connection, handlers: ChatTransportHandlers,
    timers: Timers = { schedule: (callback, delay) => setTimeout(callback, delay), cancel: timer => clearTimeout(timer) }) {
    this.boardId = boardId; this.connection = connection;
    this.handlers = handlers; this.timers = timers;
    connection.on("ChatMessageCreated", (event: MessageCreated) => {
      if (this.desired && event.boardId === boardId && event.eventVersion === 1) handlers.message(event);
    });
    connection.on("ChatAttachmentChanged", (event: ChatAttachmentChanged) => {
      if (this.desired && event.boardId === boardId && event.eventVersion === 1) handlers.attachment(event);
    });
    connection.on("ChatAccessRevoked", (event: AccessRevoked) => {
      if (this.desired && event.boardId === boardId && event.eventVersion === 1) handlers.revoked(event);
    });
    connection.on("ChatTypingChanged", (event: ChatTyping) => {
      if (this.desired && event.boardId === boardId && event.eventVersion === 1) handlers.typing?.(event);
    });
    connection.on("ChatSettingsChanged", (event: ChatSettingsChanged) => {
      if (this.desired && event.boardId === boardId && event.eventVersion === 1) handlers.settings?.(event);
    });
    connection.on("ChatMemberStateChanged", (event: ChatMemberStateChanged) => {
      if (this.desired && event.boardId === boardId && event.eventVersion === 1) handlers.memberState?.(event);
    });
    connection.onreconnecting(() => { this.joined = false; if (this.desired) handlers.status("reconnecting"); });
    connection.onreconnected(() => { if (this.desired) this.rejoin(); });
    connection.onclose(() => {
      this.joined = false;
      if (!this.desired) return;
      handlers.status("offline"); this.scheduleRetry();
    });
  }

  start() { this.desired = true; void this.ensureStarted(); }

  rejoin() {
    if (!this.desired) return;
    if (this.connection.state === HubConnectionState.Connected) void this.join();
    else void this.ensureStarted();
  }

  async stop() {
    this.desired = false; this.joined = false;
    if (this.timer !== null) this.timers.cancel(this.timer);
    this.timer = null;
    await this.connection.stop().catch(() => undefined);
  }

  setTyping(active: boolean) {
    if (!this.desired || !this.joined || this.connection.state !== HubConnectionState.Connected) return;
    void this.connection.invoke("SetTyping", this.boardId, active).catch(cause => {
      if (!this.desired) return;
      if (cause instanceof Error && cause.message.includes("chat_forbidden")) this.handlers.forbidden();
      else if (cause instanceof Error && cause.message.includes("chat_muted")) this.handlers.typingDenied?.();
    });
  }

  private async ensureStarted() {
    if (!this.desired || this.starting || this.connection.state !== HubConnectionState.Disconnected) return;
    this.handlers.status("connecting");
    this.starting = (async () => {
      try {
        await this.connection.start();
        if (this.desired) await this.join();
      } catch {
        if (this.desired) { this.handlers.status("offline"); this.scheduleRetry(); }
      }
    })().finally(() => { this.starting = null; });
    await this.starting;
  }

  private async join() {
    if (!this.desired || this.joining) return this.joining;
    this.handlers.status("reconnecting");
    this.joining = (async () => {
      try {
        const joined = await this.connection.invoke<ChatJoined>("JoinBoard", this.boardId);
        if (!this.desired || joined.boardId !== this.boardId) return;
        this.attempt = 0;
        if (this.timer !== null) this.timers.cancel(this.timer);
        this.timer = null;
        this.joined = true;
        this.handlers.joined(joined);
        if (this.desired) this.handlers.status("connected");
      } catch (cause) {
        if (!this.desired) return;
        if (cause instanceof Error && cause.message.includes("chat_forbidden")) {
          this.handlers.forbidden();
          await this.stop();
        } else { this.handlers.status("offline"); this.scheduleRetry(); }
      }
    })().finally(() => { this.joining = null; });
    await this.joining;
  }

  private scheduleRetry() {
    if (!this.desired || this.timer !== null) return;
    const delays = [1000, 2000, 5000, 10000, 30000];
    this.timer = this.timers.schedule(() => {
      this.timer = null;
      if (this.connection.state === HubConnectionState.Connected) void this.join();
      else void this.ensureStarted();
    }, delays[Math.min(this.attempt++, delays.length - 1)]);
  }
}

export const createChatTransport: ChatTransportFactory = (boardId, handlers) => new BoardChatTransport(
  boardId,
  new HubConnectionBuilder().withUrl("/hubs/chat", { accessTokenFactory: realtimeAccessToken })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    // UI reports connection status; raw transport URLs/errors must not expose tokens.
    .configureLogging(LogLevel.None).build(), handlers,
);
