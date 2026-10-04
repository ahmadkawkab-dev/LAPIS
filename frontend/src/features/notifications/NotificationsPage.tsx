import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { useCallback, useEffect, useRef, useState } from "react";
import { Bell, Check, Clock3, ExternalLink, RefreshCw, Settings2, X } from "lucide-react";
import { errorMessage, notificationApi, calendarReminderApi, type NotificationDto, type UpcomingReminderDto } from "../../api";
import { Button } from "../../components/ui/Button";
import { notificationLabel, notificationPath } from "./notificationView";
import "./notifications.css";

const dateTime = (value: string) => new Intl.DateTimeFormat(undefined,
  { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value));

export function NotificationsPage({ navigate, notify }: {
  navigate: (path: string) => void; notify: (message: string) => void;
}) {
  const [items, setItems] = useState<NotificationDto[]>([]);
  const [upcoming, setUpcoming] = useState<UpcomingReminderDto[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [upcomingCursor, setUpcomingCursor] = useState<string | null>(null);
  const [totalCount, setTotalCount] = useState(0);
  const [unreadCount, setUnreadCount] = useState(0);
  const [upcomingCount, setUpcomingCount] = useState(0);
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [snoozeMinutes, setSnoozeMinutes] = useState(10);
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");
  const requestId = useRef(0);
  const load = useCallback(async (initial = false) => {
    const id = ++requestId.current;
    if (initial) setLoading(true);
    setLoadingMore(false);
    setError("");
    try {
      const [notifications, reminders] = await Promise.all([
        notificationApi.page(null, unreadOnly), notificationApi.upcomingPage(),
      ]);
      if (id !== requestId.current) return;
      setItems(notifications.items); setNextCursor(notifications.nextCursor);
      setTotalCount(notifications.totalCount); setUnreadCount(notifications.unreadCount);
      setUpcoming(reminders.items); setUpcomingCursor(reminders.nextCursor);
      setUpcomingCount(reminders.totalCount);
    } catch (cause) { if (id === requestId.current) setError(errorMessage(cause)); }
    finally { if (id === requestId.current) setLoading(false); }
  }, [unreadOnly]);
  useEffect(() => {
    void load(true);
    const timer = window.setInterval(() => { void load(); }, 30_000);
    const onFocus = () => { void load(); };
    window.addEventListener("focus", onFocus);
    window.addEventListener("wukna:notifications-changed", onFocus);
    return () => { requestId.current++; window.clearInterval(timer); window.removeEventListener("focus", onFocus); window.removeEventListener("wukna:notifications-changed", onFocus); };
  }, [load]);
  async function moreNotifications() {
    if (!nextCursor || loadingMore) return;
    const id = requestId.current;
    setLoadingMore(true); setError("");
    try {
      const page = await notificationApi.page(nextCursor, unreadOnly);
      if (id !== requestId.current) return;
      setItems((current) => [...current, ...page.items]); setNextCursor(page.nextCursor);
      setTotalCount(page.totalCount); setUnreadCount(page.unreadCount);
    } catch (cause) { if (id === requestId.current) setError(errorMessage(cause)); }
    finally { if (id === requestId.current) setLoadingMore(false); }
  }
  async function moreUpcoming() {
    if (!upcomingCursor || loadingMore) return;
    const id = requestId.current;
    setLoadingMore(true); setError("");
    try {
      const page = await notificationApi.upcomingPage(upcomingCursor);
      if (id !== requestId.current) return;
      setUpcoming((current) => [...current, ...page.items]);
      setUpcomingCursor(page.nextCursor); setUpcomingCount(page.totalCount);
    } catch (cause) { if (id === requestId.current) setError(errorMessage(cause)); }
    finally { if (id === requestId.current) setLoadingMore(false); }
  }
  async function act(item: NotificationDto, action: "read" | "dismiss" | "snooze") {
    if (busyId) return;
    setBusyId(item.id); setError("");
    try {
      if (action === "read") await notificationApi.read(item.id, item.revision);
      if (action === "dismiss") await notificationApi.dismiss(item.id, item.revision);
      if (action === "snooze") {
        if (item.taskId) await notificationApi.snooze(item.taskId, snoozeMinutes);
        else if (item.type === "scheduledTaskReminder" && item.resourceId) await calendarReminderApi.snooze(item.resourceId, snoozeMinutes);
        notify("Reminder snoozed");
      }
      window.dispatchEvent(new Event("wukna:notification-state-updated"));
      await load();
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusyId(null); }
  }
  async function readAll() {
    if (busyId) return;
    setBusyId("all"); setError("");
    try { await notificationApi.readAll(); window.dispatchEvent(new Event("wukna:notification-state-updated")); await load(); notify("Notifications marked as read"); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusyId(null); }
  }
  async function dismissRead() {
    if (busyId) return;
    setBusyId("dismiss-read"); setError("");
    try {
      const result = await notificationApi.dismissRead();
      window.dispatchEvent(new Event("wukna:notification-state-updated"));
      await load();
      notify(result.dismissedCount ? `Dismissed ${result.dismissedCount} read ${result.dismissedCount === 1 ? "notification" : "notifications"}` : "No read notifications to dismiss");
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusyId(null); }
  }
  async function openNotification(item: NotificationDto) {
    const path = notificationPath(item);
    if (busyId || !path) return;
    setBusyId(item.id); setError("");
    try { await notificationApi.read(item.id, item.revision); navigate(path); }
    catch (cause) { setError(errorMessage(cause)); setBusyId(null); }
  }
  return <section className="wk-notifications-page" aria-labelledby="wk-notifications-title">
    <header><div><span className="wk-notifications-eyebrow">Activity &amp; reminders</span><h1 id="wk-notifications-title">Notifications</h1>
      <p>Useful updates without the noise.</p></div>
      <Button variant="secondary" disabled={loading || busyId !== null || !unreadCount} loading={busyId === "all"} onClick={() => void readAll()}><Check size={16} aria-hidden="true" /> Mark all read</Button>
    </header>
    <div className="wk-notifications-toolbar"><button className={!unreadOnly ? "active" : ""} aria-pressed={!unreadOnly} onClick={() => setUnreadOnly(false)}>All · {totalCount}</button>
      <button className={unreadOnly ? "active" : ""} aria-pressed={unreadOnly} onClick={() => setUnreadOnly(true)}>Unread · {unreadCount}</button>
      <Button variant="quiet" size="compact" disabled={loading} onClick={() => void load()}><RefreshCw size={14} aria-hidden="true" /> Refresh</Button></div>
    {error && <div className="wk-notifications-error" role="alert"><p>{error}</p><Button variant="secondary" size="compact" onClick={() => void load(true)}>Try again</Button></div>}
    {loading ? <LoadingSkeleton label="Loading notifications…" /> : error && !items.length && !upcoming.length ? null : <div className="wk-notifications-layout">
      <div className="wk-notifications-list"><div className="wk-notifications-list-header">
        <h2>{unreadOnly ? "Unread" : "Recent"}</h2>
        <div className="wk-notifications-list-controls">
          <Button variant="quiet" loading={busyId === "dismiss-read"} disabled={busyId !== null || totalCount <= unreadCount}
            onClick={() => void dismissRead()}><X size={16} aria-hidden="true" /> Dismiss all read</Button>
          <Button variant="secondary" onClick={() => navigate("/account/preferences")}><Settings2 size={16} aria-hidden="true" /> Notification preferences</Button>
        </div>
      </div>
        {items.length ? <ul>{items.map((item) => <li key={item.id} className={item.isUnread ? "wk-notification-unread" : ""}>
          <span className="wk-notification-icon"><Bell size={17} aria-hidden="true" /></span><div className="wk-notification-copy"><strong>{item.title}</strong>{item.isUnread && <span className="wk-notification-unread-label">Unread</span>}<p>{notificationLabel(item.type)}{item.activityCount > 1 ? ` · ${item.activityCount} updates` : ""}</p><time dateTime={item.issuedAt}>{dateTime(item.issuedAt)}</time></div>
          <div className="wk-notification-actions">{notificationPath(item) && <Button variant="quiet" size="compact" disabled={busyId !== null} onClick={() => void openNotification(item)}><ExternalLink size={14} aria-hidden="true" /> {item.taskId ? "Open task" : item.boardId ? "Open board" : "Open calendar"}</Button>}
            {item.isUnread && <Button variant="quiet" size="compact" disabled={busyId !== null} onClick={() => void act(item, "read")}>Mark read</Button>}
            {(item.type === "taskReminder" || item.type === "scheduledTaskReminder") && <Button variant="quiet" size="compact" disabled={busyId !== null} onClick={() => void act(item, "snooze")}><Clock3 size={14} aria-hidden="true" /> Snooze</Button>}
            <Button variant="quiet" size="compact" disabled={busyId !== null} onClick={() => void act(item, "dismiss")}><X size={14} aria-hidden="true" /> Dismiss</Button></div>
        </li>)}</ul> : <div className="wk-notifications-empty"><Bell size={22} aria-hidden="true" /><h3>{unreadOnly ? "All caught up" : "No notifications yet"}</h3>
          <p>{unreadOnly ? "You’ve read every update. New activity will appear here." : "Board activity, mentions and reminders will appear here."}</p></div>}
        {nextCursor && <div className="wk-notifications-more"><Button variant="secondary" size="compact" loading={loadingMore} onClick={() => void moreNotifications()}>Load more notifications</Button></div>}
      </div><aside className="wk-notifications-side"><div><h2>Upcoming reminders</h2><p>{upcomingCount} scheduled in the next seven days</p>
        {upcoming.length ? <ul>{upcoming.map((item) => <li key={item.taskId}><button onClick={() => navigate(item.resourceKind === "calendarEvent" ? `/calendar?event=${item.taskId}` : `/tasks/${item.taskId}`)}>
          <time dateTime={item.dueAtUtc}>{dateTime(item.dueAtUtc)}</time><strong>{item.taskTitle}</strong><ExternalLink size={14} aria-hidden="true" /></button></li>)}</ul>
          : <p>No upcoming reminders. Open a timed task to set one.</p>}
        {upcomingCursor && <Button variant="secondary" size="compact" loading={loadingMore} onClick={() => void moreUpcoming()}>Load more reminders</Button>}</div>
        <div><h2>Snooze duration</h2><label>When a reminder is snoozed<select value={snoozeMinutes} onChange={(event) => setSnoozeMinutes(Number(event.target.value))}>
          <option value={5}>5 minutes</option><option value={10}>10 minutes</option><option value={30}>30 minutes</option>
          <option value={60}>1 hour</option><option value={1440}>1 day</option></select></label></div>
      </aside></div>}
  </section>;
}
