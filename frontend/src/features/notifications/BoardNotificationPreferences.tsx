import { useEffect, useState } from "react";
import { errorMessage, notificationApi, type BoardNotificationPreferenceDto } from "../../api";
import { Button } from "../../components/ui/Button";
export function BoardNotificationPreferences({ boardId }: { boardId: string }) {
  const [value, setValue] = useState<BoardNotificationPreferenceDto | null>(null);
  const [failure, setFailure] = useState(""); const [busy, setBusy] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => { const request = new AbortController(); setFailure("");
    void notificationApi.boardPreferences(boardId, request.signal).then(value => { if (!request.signal.aborted) setValue(value); }).catch(cause => { if (!request.signal.aborted) setFailure(errorMessage(cause)); });
    return () => request.abort(); }, [boardId, attempt]);
  async function save() {
    if (!value) return; setBusy(true); setFailure("");
    try { setValue(await notificationApi.saveBoardPreferences(boardId, { mode: value.mode, soundsMuted: value.soundsMuted, mutedUntil: value.mutedUntil, revision: value.revision })); window.dispatchEvent(new Event("wukna:notification-state-updated")); }
    catch (cause) { setFailure(errorMessage(cause)); } finally { setBusy(false); }
  }
  return <section className="chat-notification-settings"><fieldset disabled={busy || !value}>
    <legend>Your chat notifications</legend>
    {!value && !failure && <p role="status">Loading chat preferences…</p>}
    {value && <><label>Notify me about<select value={value.mode} onChange={event => setValue({ ...value, mode: event.target.value as BoardNotificationPreferenceDto["mode"] })}>
      <option value="allActivity">All activity</option><option value="mentionsAndReplies">Mentions & replies</option><option value="muted">Muted</option></select></label>
      <label><input type="checkbox" checked={value.soundsMuted} onChange={event => setValue({ ...value, soundsMuted: event.target.checked })} /> Mute sounds for this board</label>
      <label>Temporary mute<select value={value.mutedUntil ? "hour" : "none"} onChange={event => setValue({ ...value, mutedUntil: event.target.value === "hour" ? new Date(Date.now() + 3600000).toISOString() : null })}>
        <option value="none">Off</option><option value="hour">For one hour</option></select></label>
      <p>Muted chat keeps unread counts. Mentions & replies includes direct mentions and replies with Notify author enabled.</p>
      <Button loading={busy} size="compact" type="button" onClick={() => void save()}>Save chat notifications</Button></>}
    </fieldset>{failure && <div role="alert"><p>{failure}</p><Button variant="secondary" disabled={busy} onClick={() => setAttempt(value => value + 1)}>Retry loading preferences</Button></div>}
  </section>;
}
