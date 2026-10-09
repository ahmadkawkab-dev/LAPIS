import { useEffect, useRef, useState, type ReactNode } from "react";
import type { BoardListItemDto } from "../../api";
import type { AuthSession } from "../../auth";
import { MobileHeader, MobileNav } from "./MobileNav";
import { Sidebar } from "./Sidebar";

export function AppShell({
  user,
  boards,
  activeBoardId,
  navigate,
  onCreateBoard,
  onRenameBoard,
  onDeleteBoard,
  signOut,
  signOutEverywhere,
  notify,
  children,
}: {
  user: AuthSession["user"];
  boards: BoardListItemDto[];
  activeBoardId: string | null;
  navigate: (path: string) => void;
  onCreateBoard: (title: string) => Promise<void>;
  onRenameBoard: (id: string, title: string) => Promise<void>;
  onDeleteBoard: (id: string) => Promise<void>;
  signOut: () => void;
  signOutEverywhere: () => void;
  notify: (message: string) => void;
  children: ReactNode;
}) {
  const [collapsed, setCollapsed] = useState(() => window.matchMedia('(min-width: 768px) and (max-width: 1199px)').matches);
  useEffect(() => {
    const tablet = window.matchMedia('(min-width: 768px) and (max-width: 1199px)');
    const adaptNavigation = () => setCollapsed(tablet.matches);
    tablet.addEventListener('change', adaptNavigation);
    return () => tablet.removeEventListener('change', adaptNavigation);
  }, []);
  const path = window.location.pathname;
  const main = useRef<HTMLElement>(null);
  const previousPath = useRef(path);
  useEffect(() => {
    const labels: Record<string, string> = { '/home': 'Home', '/boards': 'Boards', '/tasks': 'Week',
      '/tasks/quick': 'Quick tasks', '/tasks/templates': 'Templates', '/calendar': 'Calendar',
      '/notifications': 'Notifications', '/account/profile': 'Profile', '/account/preferences': 'Preferences & security' };
    document.title = `${labels[path] ?? (path.startsWith('/boards/') ? 'Board' : 'Wukna')} · Wukna`;
    if (previousPath.current === path) return;
    previousPath.current = path;
    const frame = requestAnimationFrame(() => {
      window.scrollTo(0, 0);
      main.current?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [path]);
  const openBoards = () => navigate("/boards");

  return (
    <div className={`wk-app-shell${collapsed ? " wk-app-shell--collapsed" : ""}`}>
      <a className="wk-skip-link" href="#wk-main-content">Skip to content</a>
      <Sidebar
        user={user}
        boards={boards}
        activeBoardId={activeBoardId}
        collapsed={collapsed}
        onToggle={() => setCollapsed((value) => !value)}
        navigate={navigate}
        onCreateBoard={onCreateBoard}
        onRenameBoard={onRenameBoard}
        onDeleteBoard={onDeleteBoard}
        onOpenAccount={() => navigate("/account/profile")}
      />
      <MobileHeader user={user} onHome={() => navigate("/home")} onOpenAccount={() => navigate("/account/profile")} />
      <main ref={main} tabIndex={-1} className="wk-shell-main" id="wk-main-content">
        <div className="wk-feature-stage">{children}</div>
      </main>
      <MobileNav onBoards={openBoards} navigate={navigate} />
    </div>
  );
}
