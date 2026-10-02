import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent, type MouseEvent } from "react";
import { Bell, Boxes, CalendarDays, CheckSquare, Home, Inbox, LayoutDashboard, PanelLeftClose, PanelLeftOpen, Plus, Search, Settings2 } from "lucide-react";
import type { BoardListItemDto } from "../../api";
import { errorMessage } from "../../api";
import type { AuthSession } from "../../auth";
import { BoardActionsDialog } from "../../features/boards/BoardActionsDialog";
import { Avatar, identityLabel } from "../ui/Avatar";
import { Button, IconButton } from "../ui/Button";
import { Dialog } from "../ui/Dialog";

export function Sidebar({
  user, boards, activeBoardId, collapsed, onToggle, navigate, onCreateBoard,
  onRenameBoard, onDeleteBoard, onOpenAccount,
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
  onOpenAccount: () => void;
}) {
  const [actionTarget, setActionTarget] = useState<BoardListItemDto | null>(null);
  const [creating, setCreating] = useState(false);
  const [createTitle, setCreateTitle] = useState("");
  const [createBusy, setCreateBusy] = useState(false);
  const [createError, setCreateError] = useState("");
  const [search, setSearch] = useState("");
  const searchRef = useRef<HTMLInputElement>(null);
  const path = window.location.pathname;
  const visibleBoards = boards.filter((board) => board.title.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()));
  const owned = visibleBoards.filter((board) => board.role === 1);
  const shared = visibleBoards.filter((board) => board.role !== 1);

  useEffect(() => {
    const shortcut = (event: globalThis.KeyboardEvent) => {
      if (event.key.toLowerCase() === "k" && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        searchRef.current?.focus();
      }
    };
    window.addEventListener("keydown", shortcut);
    return () => window.removeEventListener("keydown", shortcut);
  }, []);

  async function submitBoard(event: FormEvent) {
    event.preventDefault();
    if (!createTitle.trim()) return;
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
    if (board.role !== 1) return;
    event.preventDefault();
    setActionTarget(board);
  }
  function contextKey(event: KeyboardEvent<HTMLButtonElement>, board: BoardListItemDto) {
    if (board.role !== 1 || !(event.key === "ContextMenu" || event.key === "F10" && event.shiftKey)) return;
    event.preventDefault();
    setActionTarget(board);
  }

  const links = [
    { label: "Home", icon: Home, target: "/home", active: path === "/home" },
    { label: "Boards", icon: LayoutDashboard, target: "/boards", active: path.startsWith("/boards") },
    { label: "Week", icon: CheckSquare, target: "/tasks", active: path === "/tasks" || /^\/tasks\/[0-9a-f-]{36}$/i.test(path) },
    { label: "Calendar", icon: CalendarDays, target: "/calendar", active: path === "/calendar" },
    { label: "Quick tasks", icon: Inbox, target: "/tasks/quick", active: path === "/tasks/quick" },
    { label: "Templates", icon: Boxes, target: "/tasks/templates", active: path === "/tasks/templates" },
    { label: "Notifications", icon: Bell, target: "/notifications", active: path === "/notifications" },
  ];

  const boardLink = (board: BoardListItemDto) => (
    <button className={`wk-board-link${activeBoardId === board.id ? " wk-board-link--active" : ""}`}
      key={board.id} onClick={() => navigate(`/boards/${board.id}`)}
      onContextMenu={(event) => contextMenu(event, board)} onKeyDown={(event) => contextKey(event, board)}
      aria-current={activeBoardId === board.id ? "page" : undefined}
      aria-keyshortcuts={board.role === 1 ? "Shift+F10" : undefined}
      title={board.role === 1 ? `${board.title} · Right-click for board actions` : board.title}>
      <span className="wk-board-dot" aria-hidden="true" /><span>{board.title}</span>
    </button>
  );

  return <aside className={`wk-sidebar${collapsed ? " wk-sidebar--collapsed" : ""}`}>
    <div className="wk-sidebar-header">
      <button className="wk-sidebar-brand" onClick={() => navigate("/home")} aria-label="Wukna home">
        <span className="wk-sidebar-monogram" aria-hidden="true">W</span>
        <span className="wk-sidebar-brand-copy"><strong>Wukna</strong><small>Personal workspace</small></span>
      </button>
      <IconButton label={collapsed ? "Expand sidebar" : "Collapse sidebar"} aria-expanded={!collapsed} onClick={onToggle}>
        {collapsed ? <PanelLeftOpen size={17} /> : <PanelLeftClose size={17} />}
      </IconButton>
    </div>

    <div className="wk-sidebar-utilities">
      <Button className="wk-sidebar-new" size="compact" onClick={() => setCreating(true)}><Plus size={15} aria-hidden="true" /><span>New board</span></Button>
      <label className="wk-sidebar-search">
        <Search size={14} aria-hidden="true" />
        <input ref={searchRef} value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search boards" aria-label="Search boards" />
        <kbd>⌘ K</kbd>
      </label>
    </div>

    <nav className="wk-sidebar-nav" aria-label="Primary navigation">
      <div className="wk-sidebar-primary-links">
        {links.map(({ label, icon: Icon, target, active }) => <button key={target}
          className={`wk-shell-link${active ? " wk-shell-link--active" : ""}`}
          aria-current={active ? "page" : undefined} aria-label={label} title={label}
          onClick={() => navigate(target)}><Icon size={16} aria-hidden="true" /><span>{label}</span></button>)}
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

    <button className="wk-sidebar-account" onClick={onOpenAccount} aria-label={`Open account for ${identityLabel(user)}`}>
      <Avatar identity={user} size="small" />
      <span className="wk-sidebar-account-copy"><strong>{identityLabel(user)}</strong><small>{user.email}</small></span>
      <Settings2 size={15} aria-hidden="true" />
    </button>

    {creating && <Dialog title="New board" onClose={() => setCreating(false)}>
      <form className="wk-sidebar-create-form" onSubmit={(event) => void submitBoard(event)}>
        <label>Board name<input autoFocus maxLength={100} required value={createTitle} onChange={(event) => setCreateTitle(event.target.value)} /></label>
        {createError && <p role="alert">{createError}</p>}
        <div className="wk-dialog-actions"><Button type="submit" disabled={createBusy || !createTitle.trim()}>Create board</Button>
          <Button variant="secondary" disabled={createBusy} onClick={() => setCreating(false)}>Cancel</Button></div>
      </form>
    </Dialog>}
    {actionTarget && <BoardActionsDialog key={actionTarget.id} board={actionTarget}
      onClose={() => setActionTarget(null)} onRenameBoard={onRenameBoard} onDeleteBoard={onDeleteBoard} />}
  </aside>;
}
