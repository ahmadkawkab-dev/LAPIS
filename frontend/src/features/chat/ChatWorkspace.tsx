import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type RefObject } from "react";
import { createPortal } from "react-dom";
import { MessageCircle } from "lucide-react";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { ChatController } from "./ChatController";
import { chatApi } from "./chatApi";
import { createChatTransport } from "./chatTransport";
import { ChatPanel, type ChatScrollPosition } from "./ChatPanel";
import "./chat.css";

function draftStorage() {
  try { return window.sessionStorage; } catch { return null; }
}

/** Incoming chat traffic stays inside this subtree, separate from canvas rendering. */
export function ChatWorkspace({ boardId, userId, boardTitle, host }: {
  boardId: string; userId: string; boardTitle: string; host: RefObject<HTMLElement | null>;
}) {
  const [controller] = useState(() => new ChatController(boardId, userId, chatApi, createChatTransport,
    { storage: draftStorage() }));
  const snapshot = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  const [open, setOpen] = useState(() => new URLSearchParams(window.location.search).get("chat") === "1");
  const toggle = useRef<HTMLButtonElement>(null);
  const scroll = useRef<ChatScrollPosition>({ top: 0, latest: true, initialized: false });
  const media = useMemo(() => window.matchMedia("(max-width: 900px)"), []);
  const narrow = useSyncExternalStore(
    useMemo(() => (listener: () => void) => {
      media.addEventListener("change", listener);
      return () => media.removeEventListener("change", listener);
    }, [media]), () => media.matches,
  );

  useEffect(() => {
    controller.activate();
    void controller.refreshState();
    const refresh = () => { if (document.visibilityState === "visible") void controller.refresh();
      else { controller.stopTyping(); controller.setViewingLatest(false, !scroll.current.latest); } };
    const timer = window.setInterval(refresh, 30_000);
    window.addEventListener("focus", refresh);
    document.addEventListener("visibilitychange", refresh);
    return () => {
      clearInterval(timer);
      window.removeEventListener("focus", refresh);
      document.removeEventListener("visibilitychange", refresh);
      controller.stop();
    };
  }, [controller]);

  useEffect(() => {
    const navigate = () => {
      const params = new URLSearchParams(window.location.search), messageId = params.get("message");
      if (params.get("chat") !== "1") return;
      setOpen(true);
      if (messageId && /^[0-9a-f-]{36}$/i.test(messageId)) {
        scroll.current = { top: 0, latest: false, initialized: false };
        void controller.focusMessage(messageId).then(() => requestAnimationFrame(() => {
          document.querySelector<HTMLElement>(`[data-chat-message="${messageId}"]`)?.scrollIntoView({ block: "center" });
        }));
      }
    };
    navigate(); window.addEventListener("wukna:navigation", navigate); window.addEventListener("popstate", navigate);
    return () => { window.removeEventListener("wukna:navigation", navigate); window.removeEventListener("popstate", navigate); };
  }, [controller]);

  function close() {
    controller.stopTyping();
    setOpen(false);
    controller.setViewingLatest(false, !scroll.current.latest);
    toggle.current?.focus({ preventScroll: true });
  }
  const content = <ChatPanel controller={controller} snapshot={snapshot} boardTitle={boardTitle}
    onClose={close} scrollPosition={scroll} modal={narrow} />;
  return <>
    <Button ref={toggle} variant="secondary" size="compact" className={`board-tool-button${open ? " active" : ""}`}
      aria-label={snapshot.unseen ? `Chat, ${snapshot.unseen} new messages` : "Chat"} aria-expanded={open}
      aria-controls={open ? "board-chat" : undefined}
      onClick={() => { if (open) close(); else setOpen(true); }}>
      <MessageCircle size={16} aria-hidden="true" /><span>Chat</span>
      {snapshot.unseen > 0 && <span className="chat-badge" aria-hidden="true">{snapshot.unseen > 99 ? "99+" : snapshot.unseen}</span>}
    </Button>
    {open && (narrow ? <Dialog title="Board chat" className="chat-dialog" onClose={close}>{content}</Dialog> :
      host.current ? createPortal(content, host.current) : null)}
  </>;
}
