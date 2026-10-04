import { LoadingSkeleton } from "../../components/ui/LoadingSkeleton";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { errorMessage, notificationApi, type NotificationPreferenceDto, type NotificationSettings } from "../../api";
import { Button } from "../../components/ui/Button";
import { notificationSounds, notificationSoundVolume, type NotificationSoundKey } from "./notificationSounds";
import "./notification-preferences.css";
import { BrowserPushPreferences } from "./BrowserPushPreferences";
import { notificationAudio } from "./notificationAudio";

const categories = [
  ["chatNotificationsEnabled", "Chat activity"], ["taskReminderNotificationsEnabled", "Task reminders"],
  ["scheduledTaskReminderNotificationsEnabled", "Scheduled-task reminders"], ["sharedBoardNotificationsEnabled", "Shared-board activity"],
  ["boardInvitationNotificationsEnabled", "Board invitations"], ["taskActivityNotificationsEnabled", "Task activity"],
] as const;

export function NotificationPreferences({ notify, userId }: { notify: (message: string) => void; userId: string }) {
  const [value, setValue] = useState<NotificationPreferenceDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");
  const [soundFailure, setSoundFailure] = useState("");
  const request = useRef<AbortController | null>(null);
  const previewVersion = useRef(0);

  const stopSound = useCallback(() => {
    previewVersion.current++;
    notificationAudio.stop();
  }, []);
  const load = useCallback(async () => {
    stopSound();
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setLoading(true); setFailure("");
    try {
      const next = await notificationApi.preferences(controller.signal);
      if (!controller.signal.aborted) setValue(next);
    } catch (cause) { if (!controller.signal.aborted) setFailure(errorMessage(cause)); }
    finally { if (!controller.signal.aborted) setLoading(false); }
  }, [stopSound]);
  useEffect(() => {
    void load();
    return () => { request.current?.abort(); stopSound(); };
  }, [load, stopSound]);

  function change<K extends keyof NotificationSettings>(key: K, next: NotificationSettings[K]) {
    stopSound(); setSoundFailure("");
    setValue((current) => current ? { ...current, settings: { ...current.settings, [key]: next } } : current);
  }
  async function save(event: FormEvent) {
    event.preventDefault();
    if (!value || busy) return;
    request.current?.abort();
    const controller = new AbortController(); request.current = controller;
    setBusy(true); setFailure(""); stopSound();
    try {
      const next = await notificationApi.savePreferences(value.settings, value.revision, controller.signal);
      if (!controller.signal.aborted) { setValue(next); window.dispatchEvent(new Event("wukna:notification-state-updated")); notify("Notification settings saved"); }
    } catch (cause) { if (!controller.signal.aborted) setFailure(errorMessage(cause)); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  }
  async function preview(key: NotificationSoundKey) {
    if (!value) return;
    const volume = notificationSoundVolume(value.settings, key);
    stopSound(); setSoundFailure("");
    if (volume === 0) return;
    const version = previewVersion.current;
    const unlocked = await notificationAudio.unlock();
    if (version !== previewVersion.current) return;
    if (!unlocked) { setSoundFailure("Your browser blocked audio. Allow sound in its site settings, then try again."); return; }
    const result = await notificationAudio.play(key, volume);
    if (version !== previewVersion.current) return;
    if (result === "failed") setSoundFailure("Could not load this sound. Please try again.");
    if (result === "blocked") setSoundFailure("Your browser blocked audio. Allow sound in its site settings, then try again.");
  }

  return <section className="wk-account-section wk-notification-preferences" aria-labelledby="wk-notification-preferences-title">
    <div><h2 id="wk-notification-preferences-title">Notifications & sounds</h2>
      <p>Your choices are saved to your account. Sound mute keeps visual notifications and unread counts enabled.</p></div>
    <BrowserPushPreferences userId={userId} />
    {failure && <div className="wk-account-error" role="alert"><p>{failure}</p>
      <Button variant="secondary" loading={busy} onClick={() => void load()}>Reload settings</Button></div>}
    {loading ? <LoadingSkeleton layout="form" label="Loading notification settings…" /> : value && <form onSubmit={(event) => void save(event)}>
      <fieldset disabled={busy}>
        <legend>Notification delivery</legend>
        <label className="wk-notification-toggle"><input type="checkbox" checked={value.settings.pushEnabled}
          onChange={event => change("pushEnabled", event.target.checked)} /> Browser notifications</label>
        <label className="wk-notification-toggle"><input type="checkbox" checked={value.settings.privatePreviewsEnabled}
          onChange={event => change("privatePreviewsEnabled", event.target.checked)} /> Show activity titles in browser notifications</label>
        <label className="wk-notification-toggle"><input type="checkbox" checked={value.settings.inAppEnabled}
          onChange={(event) => change("inAppEnabled", event.target.checked)} /> In-app alerts</label>
      </fieldset>
      <fieldset disabled={busy}>
        <legend>Activity categories</legend>
        {categories.map(([key, label]) => <label className="wk-notification-toggle" key={key}>
          <input type="checkbox" checked={value.settings[key]} onChange={(event) => change(key, event.target.checked)} /> {label}</label>)}
      </fieldset>
      <fieldset disabled={busy}>
        <legend>Notification sounds</legend>
        <label className="wk-notification-toggle"><input type="checkbox" checked={value.settings.soundsMuted}
          onChange={(event) => change("soundsMuted", event.target.checked)} /> Mute notification sounds</label>
        <label className="wk-notification-volume">Volume <output>{Math.round(value.settings.soundVolume * 100)}%</output>
          <input type="range" min="0" max="1" step="0.05" value={value.settings.soundVolume}
            disabled={value.settings.soundsMuted} onChange={(event) => change("soundVolume", Number(event.target.value))} /></label>
        {notificationSounds.map(({ key, label }) => <div className="wk-notification-sound-row" key={key}>
          <label className="wk-notification-toggle"><input type="checkbox" checked={value.settings[key]}
            onChange={(event) => change(key, event.target.checked)} /> {label}</label>
          <Button type="button" variant="quiet" size="compact" aria-label={`Test sound: ${label}`}
            disabled={notificationSoundVolume(value.settings, key) === 0} onClick={() => void preview(key)}>Test sound</Button>
        </div>)}
        <p>These clips play in Wukna. Background notification sound follows your browser and device settings.</p>
        {soundFailure && <p role="status">{soundFailure}</p>}
      </fieldset>
      <Button type="submit" loading={busy}>Save notification settings</Button>
    </form>}
  </section>;
}
