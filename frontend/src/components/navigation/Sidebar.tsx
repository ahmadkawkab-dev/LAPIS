import { TourHelpIcon } from "../../features/onboarding/TourHelpIcon";
import { notificationUnread, subscribeNotificationUnread, chatUnreadCounts } from "../../features/notifications/notificationState";
import { useSyncExternalStore, useEffect, useRef, useState, type FormEvent, type KeyboardEvent, type MouseEvent } from "react";
import { Bell, Boxes, CalendarDays, CheckSquare, Home, Inbox, LayoutDashboard, PanelLeftClose, PanelLeftOpen, Plus, Search } from "lucide-react";
import type { BoardListItemDto } from "../../api";
import { errorMessage } from "../../api";
import type { AuthSession } from "../../auth";
import { BoardActionsDialog } from "../../features/boards/BoardActionsDialog";
import { Avatar, identityLabel } from "../ui/Avatar";
import { Button, IconButton } from "../ui/Button";
import { useReplayTour } from "../../features/onboarding/OnboardingProvider";
import { Dialog } from "../ui/Dialog";
import { boardCardTone } from '../../features/boards/boardCardTone';

export function Sidebar({
  user, boards, activeBoardId, collapsed, onToggle, navigate, onCreateBoard,
  onRenameBoard, onDeleteBoard, onColorBoard, onOpenAccount,
}: {
  user: AuthSession["user"];
  boards: BoardListItemDto[];
  activeBoardId: string | null;
  collapsed: boolean;
  onToggle: () => void;
  navigate: (path: string) => void;
  onCreateBoard: (title: string) => Promise<void>;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
  onColorBoard: (id: string, color: BoardListItemDto['cardColor'], version: number) => Promise<void>;
  onOpenAccount: () => void;
}) {
  const replayTour = useReplayTour();
  const [actionTarget, setActionTarget] = useState<BoardListItemDto | null>(null);
  const actionBoard = actionTarget && boards.find(board => board.id === actionTarget.id);
  const [creating, setCreating] = useState(false);
  const [createTitle, setCreateTitle] = useState("");
  const [createBusy, setCreateBusy] = useState(false);
  const [createError, setCreateError] = useState("");
  const [search, setSearch] = useState("");
  const searchRef = useRef<HTMLInputElement>(null);
  const chatUnread = useSyncExternalStore(subscribeNotificationUnread, chatUnreadCounts);
  const unread = useSyncExternalStore(subscribeNotificationUnread, notificationUnread);
  const path = window.location.pathname;
  const visibleBoards = boards.filter((board) => board.title.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()));
  const owned = visibleBoards.filter((board) => board.role === 1);
  const shared = visibleBoards.filter((board) => board.role !== 1);

  useEffect(() => {
    const shortcut = (event: globalThis.KeyboardEvent) => {
      if (event.key.toLowerCase() === "k" && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        if (collapsed) onToggle();
        requestAnimationFrame(() => searchRef.current?.focus());
      }
    };
    window.addEventListener("keydown", shortcut);
    return () => window.removeEventListener("keydown", shortcut);
  }, [collapsed, onToggle]);

  async function submitBoard(event: FormEvent) {
    event.preventDefault();
    if (createBusy || !createTitle.trim()) return;
    setCreateBusy(true);
    setCreateError("");
    try {
      await onCreateBoard(createTitle.trim());
      setCreating(false);
      setCreateTitle("");
    } catch (cause) {
      setCreateError(errorMessage(cause));
    } finally {
      setCreateBusy(false);
    }
  }

  function contextMenu(event: MouseEvent<HTMLButtonElement>, board: BoardListItemDto) {
    if (board.role !== 1 && !board.canEdit) return;
    event.preventDefault();
    setActionTarget(board);
  }
  function contextKey(event: KeyboardEvent<HTMLButtonElement>, board: BoardListItemDto) {
    if ((board.role !== 1 && !board.canEdit) || !(event.key === "ContextMenu" || event.key === "F10" && event.shiftKey)) return;
    event.preventDefault();
    setActionTarget(board);
  }

  const links = [
    { label: "Home", icon: Home, target: "/home", tour: "home", active: path === "/home" },
    { label: "Boards", icon: LayoutDashboard, target: "/boards", tour: "boards", active: path.startsWith("/boards") },
    { label: "Week", icon: CheckSquare, target: "/tasks", active: path === "/tasks" || /^\/tasks\/[0-9a-f-]{36}$/i.test(path) },
    { label: "Calendar", icon: CalendarDays, target: "/calendar", tour: "calendar", active: path === "/calendar" },
    { label: "Quick tasks", icon: Inbox, target: "/tasks/quick", tour: "quick-tasks", active: path === "/tasks/quick" },
    { label: "Templates", icon: Boxes, target: "/tasks/templates", tour: "templates", active: path === "/tasks/templates" },
    { label: "Notifications", icon: Bell, target: "/notifications", active: path === "/notifications" },
  ];

  const boardLink = (board: BoardListItemDto) => (
    <button className={`wk-board-link${activeBoardId === board.id ? " wk-board-link--active" : ""}`}
      key={board.id} onClick={() => navigate(`/boards/${board.id}`)}
      data-board-id={board.id} data-board-tone={board.cardColor ?? boardCardTone(board.id)}
      onContextMenu={(event) => contextMenu(event, board)} onKeyDown={(event) => contextKey(event, board)}
      aria-current={activeBoardId === board.id ? "page" : undefined}
      aria-keyshortcuts={board.role === 1 || board.canEdit ? "Shift+F10" : undefined}
      title={board.role === 1 || board.canEdit ? `${board.title} · Right-click for board actions` : board.title}>
      <span className="wk-board-dot" aria-hidden="true" /><span className="wk-board-link-title">{board.title}</span>{(chatUnread.get(board.id) ?? 0) > 0 && <span className="wk-nav-badge" aria-label={`${chatUnread.get(board.id)} unread chat messages`}>{chatUnread.get(board.id)! > 99 ? "99+" : chatUnread.get(board.id)}</span>}
    </button>
  );

  return <aside className={`wk-sidebar${collapsed ? " wk-sidebar--collapsed" : ""}`}>
    <div className="wk-sidebar-header">
      <button className="wk-sidebar-brand" onClick={() => navigate("/home")} aria-label="Wukna home">
        <span className="wk-sidebar-monogram" aria-hidden="true">W</span>
        <span className="wk-sidebar-brand-copy"><strong>Wukna</strong><small>Personal workspace</small></span>
      </button>
      <IconButton label={collapsed ? "Expand sidebar" : "Collapse sidebar"} aria-expanded={!collapsed} onClick={onToggle}>
        {collapsed ? <PanelLeftOpen size={17} aria-hidden="true" /> : <PanelLeftClose size={17} aria-hidden="true" />}
      </IconButton>
    </div>

    <div className="wk-sidebar-utilities">
      <Button className="wk-sidebar-new" size="compact" aria-label="New board" title={collapsed ? 'New board' : undefined} onClick={() => setCreating(true)}><Plus size={15} aria-hidden="true" /><span>New board</span></Button>
      <label className="wk-sidebar-search">
        <Search size={14} aria-hidden="true" />
        <input ref={searchRef} value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search boards" aria-label="Search boards" />
      </label>
    </div>

    <nav data-tour="navigation" className="wk-sidebar-nav" aria-label="Primary navigation">
      <div className="wk-sidebar-primary-links">
        {links.map(({ label, icon: Icon, target, tour, active }) => <button key={target} data-tour={tour}
          className={`wk-shell-link${active ? " wk-shell-link--active" : ""}`}
          aria-current={active ? "page" : undefined} aria-label={target === "/notifications" && unread ? `${label}, ${unread} unread` : label} title={label}
          onClick={() => navigate(target)}><Icon size={16} aria-hidden="true" /><span>{label}</span>{target === "/notifications" && unread > 0 && <span className="wk-nav-badge" aria-hidden="true">{unread > 99 ? "99+" : unread}</span>}</button>)}
      </div>
      <div className="wk-sidebar-boards">
        <p>Your boards</p>
        <div>
          {owned.map(boardLink)}
          {shared.length > 0 && <p className="wk-sidebar-shared-label">Shared with you</p>}
          {shared.map(boardLink)}
          {search && visibleBoards.length === 0 && <p className="wk-sidebar-search-empty">No matching boards</p>}
        </div>
      </div>
    </nav>

    <div className="wk-sidebar-account-area">
    <button className="wk-sidebar-account" onClick={onOpenAccount} aria-label={`Open account for ${identityLabel(user)}`}>
      <Avatar identity={user} size="small" />
      <span className="wk-sidebar-account-copy"><strong>{identityLabel(user)}</strong><small>{user.email}</small></span>
    </button>
    <IconButton className="wk-tour-replay" data-tour="replay" label="Replay onboarding tour" title="Take the Wukna tour" onClick={() => replayTour?.()}><TourHelpIcon /></IconButton>
    </div>

    {creating && <Dialog title="New board" busy={createBusy} onClose={() => setCreating(false)}>
      <form className="wk-sidebar-create-form" onSubmit={(event) => void submitBoard(event)}>
        <label>Board name<input autoFocus maxLength={200} required value={createTitle} onChange={(event) => setCreateTitle(event.target.value)} /></label>
        {createError && <p role="alert">{createError}</p>}
        <div className="wk-dialog-actions"><Button type="submit" loading={createBusy} loadingLabel="Creating…" disabled={!createTitle.trim()}>Create board</Button>
          <Button variant="secondary" disabled={createBusy} onClick={() => setCreating(false)}>Cancel</Button></div>
      </form>
    </Dialog>}
    {actionBoard && <BoardActionsDialog key={actionBoard.id} board={actionBoard}
      onClose={() => setActionTarget(null)} onRenameBoard={onRenameBoard} onDeleteBoard={onDeleteBoard} onColorBoard={onColorBoard} />}
  </aside>;
}
