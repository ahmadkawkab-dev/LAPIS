import { notificationUnread, subscribeNotificationUnread } from "../../features/notifications/notificationState";
import { useSyncExternalStore, useState } from "react";
import { Bell, CalendarDays, CheckSquare, Home, LayoutDashboard, Menu, Boxes, Inbox } from "lucide-react";
import type { AuthSession } from "../../auth";
import { Avatar, identityLabel } from "../ui/Avatar";
import { IconButton } from "../ui/Button";
import { Dialog } from "../ui/Dialog";

export function MobileHeader({ onHome, onOpenAccount, user }: { onHome: () => void; onOpenAccount: () => void; user: AuthSession["user"] }) {
  return <header className="wk-mobile-header">
    <button className="wk-mobile-brand" onClick={onHome} aria-label="Wukna home">
      <span className="wk-sidebar-monogram" aria-hidden="true">W</span><strong>Wukna</strong>
    </button>
    <IconButton label={`Open account for ${identityLabel(user)}`} title={identityLabel(user)} onClick={onOpenAccount}>
      <Avatar identity={user} size="small" />
    </IconButton>
  </header>;
}

export function MobileNav({ onBoards, navigate }: { onBoards: () => void; navigate: (path: string) => void }) {
  const unread = useSyncExternalStore(subscribeNotificationUnread, notificationUnread);
  const path = window.location.pathname;
  const [menuOpen, setMenuOpen] = useState(false);
  const moreActive = path.startsWith("/boards") || path === "/tasks/quick" || path === "/tasks/templates" || path === "/notifications" || path.startsWith("/account");
  const go = (target: string) => { setMenuOpen(false); navigate(target); };
  return <>
    <nav className="wk-mobile-nav" aria-label="Primary navigation">
      <button className={`wk-mobile-nav-item${path === "/home" ? " wk-mobile-nav-item--active" : ""}`}
        aria-current={path === "/home" ? "page" : undefined} onClick={() => go("/home")}><Home size={19} aria-hidden="true" /><span>Home</span></button>
      <button className={`wk-mobile-nav-item${path === "/tasks" ? " wk-mobile-nav-item--active" : ""}`}
        aria-current={path === "/tasks" ? "page" : undefined} onClick={() => go("/tasks")}><CheckSquare size={19} aria-hidden="true" /><span>Week</span></button>
      <button className={`wk-mobile-nav-item${path === "/calendar" ? " wk-mobile-nav-item--active" : ""}`}
        aria-current={path === "/calendar" ? "page" : undefined} onClick={() => go("/calendar")}><CalendarDays size={19} aria-hidden="true" /><span>Calendar</span></button>
      <button className={`wk-mobile-nav-item${moreActive ? " wk-mobile-nav-item--active" : ""}`}
        aria-expanded={menuOpen} aria-haspopup="dialog" onClick={() => setMenuOpen(true)}><Menu size={19} aria-hidden="true" /><span>More{unread > 0 ? ` · ${unread > 99 ? "99+" : unread}` : ""}</span></button>
    </nav>
    {menuOpen && <Dialog title="More" onClose={() => setMenuOpen(false)} className="wk-mobile-more">
      <div className="wk-mobile-more-links">
        <button onClick={() => { setMenuOpen(false); onBoards(); }}><LayoutDashboard size={18} aria-hidden="true" /> Boards</button>
        <button onClick={() => go("/tasks/quick")}><Inbox size={18} aria-hidden="true" /> Quick tasks</button>
        <button onClick={() => go("/tasks/templates")}><Boxes size={18} aria-hidden="true" /> Templates</button>
        <button onClick={() => go("/notifications")}><Bell size={18} aria-hidden="true" /> Notifications{unread > 0 ? ` · ${unread}` : ""}</button>
        <button onClick={() => go("/account/profile")}><Menu size={18} aria-hidden="true" /> Account</button>
      </div>
    </Dialog>}
  </>;
}
