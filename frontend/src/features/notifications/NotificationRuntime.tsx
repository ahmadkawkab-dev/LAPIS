import { useEffect, useState } from "react";
import { notificationApi, pushApi, type NotificationDto, type NotificationSettings } from "../../api";
import { realtimeConnection } from "../../realtime/connection";
import { Button } from "../../components/ui/Button";
import { notificationPath } from "./notificationView";
import { bindNotificationSession, chatNotificationView, claimNotification, installationId, notificationSoundKey, setNotificationUnread, setChatUnreadCounts } from "./notificationState";
import { notificationSoundVolume } from "./notificationSounds";
import { notificationAudio } from "./notificationAudio";
import { clearBrowserNotificationSession } from "./browserPush";

const categoryKey = { chatActivity: "chatNotificationsEnabled", taskReminder: "taskReminderNotificationsEnabled",
  scheduledTaskReminder: "scheduledTaskReminderNotificationsEnabled", sharedBoardActivity: "sharedBoardNotificationsEnabled",
  boardInvitation: "boardInvitationNotificationsEnabled", taskActivity: "taskActivityNotificationsEnabled" } as const;

/** One listener uses the existing connection; reconnects reconcile quietly through REST. */
export function NotificationRuntime({ userId, navigate }: { userId: string; navigate: (path: string) => void }) {
  const [alert, setAlert] = useState<NotificationDto | null>(null);
  const [audioBlocked, setAudioBlocked] = useState(false);
  const [audioFailed, setAudioFailed] = useState(false);
  const enableSound = async () => { setAudioBlocked(!await notificationAudio.unlock()); };
  useEffect(() => {
    const abort = new AbortController(), installation = installationId(), tabId = crypto.randomUUID();
    let settings: NotificationSettings | null = null, timer: number | null = null;
    let syncVersion = 0;
    const stopAudio = () => notificationAudio.stop();
    const refresh = async () => {
      const version = ++syncVersion;
      try { const [preferences, count, chatCounts] = await Promise.all([notificationApi.preferences(abort.signal), notificationApi.unreadCount(), notificationApi.chatUnread()]);
        if (abort.signal.aborted || version !== syncVersion) return;
        if (settings && JSON.stringify(settings) !== JSON.stringify(preferences.settings)) stopAudio();
        settings = preferences.settings; setNotificationUnread(count.unreadCount); setChatUnreadCounts(chatCounts);
      } catch { /* The center exposes request errors; the next focus/reconnect retries without history alerts. */ }
    };
    const presence = async () => {
      const view = chatNotificationView();
      const foreground = document.visibilityState === "visible" && document.hasFocus();
      try { await pushApi.presence({ installationId: installation, tabId, visible: foreground,
        boardId: view?.boardId ?? null, chatVisible: foreground && view?.visible === true && view.latest }, abort.signal); } catch { /* Presence expires after 30 seconds. */ }
    };
    const reconcile = async () => { await Promise.all([refresh(), presence()]); };
    const incoming = async (event: { userId: string; notification: NotificationDto }) => {
      if (event.userId !== userId || abort.signal.aborted) return;
      const count = notificationApi.unreadCount();
      void count.then(value => { if (!abort.signal.aborted) setNotificationUnread(value.unreadCount); }).catch(() => undefined);
      if (!settings || !settings.inAppEnabled) return;
      try {
        const item = await notificationApi.get(event.notification.id, abort.signal);
        if (abort.signal.aborted || !item.isUnread || item.revision !== event.notification.revision || Date.now() - Date.parse(item.updatedAt) > 60_000 || !settings[categoryKey[item.type]]) return;
        const readingChat = () => {
          const view = chatNotificationView();
          return document.visibilityState === "visible" && document.hasFocus()
            && item.type === "chatActivity" && view?.boardId === item.boardId && view.visible && view.latest;
        };
        if (readingChat()) return;
        let soundMuted = false;
        if (item.boardId) {
          const board = await notificationApi.boardPreferences(item.boardId, abort.signal);
          if (item.type === "chatActivity" && (board.effectiveMode === "muted" || board.effectiveMode === "mentionsAndReplies" && item.activityKind !== "mention" && item.activityKind !== "reply")) return;
          soundMuted = board.soundsMuted;
        }
        if (abort.signal.aborted || readingChat()) return;
        const foreground = document.visibilityState === "visible" && document.hasFocus();
        const key = notificationSoundKey(item), volume = key && !soundMuted ? notificationSoundVolume(settings, key) : 0;
        // A live background page can play an already-unlocked clip. Leave the claim to push
        // when muted or blocked, so an unavailable page player cannot silence the OS fallback.
        if (!foreground && (!key || volume <= 0)) return;
        if (!foreground && !notificationAudio.ready) { setAudioBlocked(true); return; }
        if (!await claimNotification(userId, item.id, item.revision)) return;
        if (abort.signal.aborted) return;
        if (document.visibilityState === "visible" && document.hasFocus()) {
          setAlert(item); if (timer !== null) clearTimeout(timer); timer = window.setTimeout(() => setAlert(null), 8000);
        }
        const currentVolume = key && !soundMuted ? notificationSoundVolume(settings, key) : 0;
        if (key && currentVolume > 0) {
          const result = await notificationAudio.play(key, currentVolume);
          if (!abort.signal.aborted) { setAudioBlocked(result === "blocked"); setAudioFailed(result === "failed"); }
        }
      } catch { /* Missing or revoked resources never produce an alert. */ }
    };
    void bindNotificationSession(userId, installation).catch(() => undefined);
    // Refresh an enrolled worker on app startup without another permission prompt or subscription.
    void navigator.serviceWorker?.getRegistration("/").then(registration => registration?.update()).catch(() => undefined);
    void reconcile();
    const removeEvent = realtimeConnection.on("NotificationChanged", incoming);
    const removeState = realtimeConnection.on<{ userId: string }>("NotificationStateChanged", event => { if (event.userId === userId) { setAlert(null); stopAudio(); void refresh(); } });
    const removePreferences = realtimeConnection.on<{ userId: string }>("NotificationPreferencesChanged", event => { if (event.userId === userId) { stopAudio(); void refresh(); } });
    const removeReconnect = realtimeConnection.onReconnected(reconcile);
    const focus = () => void reconcile();
    const changed = () => { if (document.visibilityState !== "visible" || !document.hasFocus()) setAlert(null); void reconcile(); };
    const readChanged = () => { stopAudio(); void refresh(); };
    const interaction = (event: Event) => {
      if (event.isTrusted) void notificationAudio.unlock().then(unlocked => { if (unlocked && !abort.signal.aborted) setAudioBlocked(false); });
    };
    const workerMessage = (event: MessageEvent) => {
      if (event.data?.type !== "wukna:push-shown" || event.data.userId !== userId) return;
      setAlert(current => current?.id === event.data.notificationId ? null : current);
      // A push that follows a page claim is silent and must let that page's clip finish.
      if (event.data.alreadyHandled !== true) stopAudio();
      void refresh();
    };
    window.addEventListener("focus", focus); window.addEventListener("blur", changed); document.addEventListener("visibilitychange", changed);
    window.addEventListener("pointerup", interaction, { capture: true, passive: true }); window.addEventListener("keydown", interaction, { capture: true });
    window.addEventListener("wukna:notification-presence", focus); window.addEventListener("wukna:notification-state-updated", readChanged);
    navigator.serviceWorker?.addEventListener("message", workerMessage);
    const interval = setInterval(() => void reconcile(), 15_000);
    return () => {
      abort.abort(); removeEvent(); removeState(); removePreferences(); removeReconnect(); clearInterval(interval);
      window.removeEventListener("focus", focus); window.removeEventListener("blur", changed); document.removeEventListener("visibilitychange", changed);
      window.removeEventListener("pointerup", interaction, true); window.removeEventListener("keydown", interaction, true);
      window.removeEventListener("wukna:notification-presence", focus); window.removeEventListener("wukna:notification-state-updated", readChanged);
      navigator.serviceWorker?.removeEventListener("message", workerMessage); stopAudio(); if (timer !== null) clearTimeout(timer);
      setNotificationUnread(0); setChatUnreadCounts([]); void clearBrowserNotificationSession(false, userId).catch(() => undefined);
    };
  }, [userId, navigate]);
  return <>{alert && <div className="wk-notification-toast" role="status" aria-live="polite"><span>{alert.title}</span>
    <Button size="compact" variant="quiet" onClick={() => { const path = notificationPath(alert); if (path) navigate(path); setAlert(null); }}>Open</Button>
    <Button size="compact" variant="quiet" aria-label="Dismiss notification alert" onClick={() => setAlert(null)}>Dismiss</Button></div>}
    {audioBlocked && <div className="wk-notification-toast" role="status"><span>Enable notification sound for this tab.</span>
      <Button size="compact" onClick={() => void enableSound()}>Enable sound</Button>
      <Button size="compact" variant="quiet" onClick={() => setAudioBlocked(false)}>Dismiss</Button></div>}
    {audioFailed && <div className="wk-notification-toast" role="status"><span>Notification sound could not load. Try “Test sound” in account settings.</span>
      <Button size="compact" variant="quiet" onClick={() => setAudioFailed(false)}>Dismiss</Button></div>}</>;
}
