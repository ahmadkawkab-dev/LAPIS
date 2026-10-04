import { useEffect, useState } from "react";
import { calendarReminderApi, errorMessage, type CalendarReminderDto } from "../../api";
import { Button } from "../../components/ui/Button";
export function CalendarReminderPanel({ eventId }: { eventId: string }) {
  const [reminder, setReminder] = useState<CalendarReminderDto | null>(null), [minutes, setMinutes] = useState(10);
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [failure, setFailure] = useState("");
  useEffect(() => { const request = new AbortController(); setLoading(true);
    void calendarReminderApi.get(eventId, request.signal).then(value => { if (!request.signal.aborted) { setReminder(value ?? null); setMinutes(value?.minutesBefore ?? 10); } })
      .catch(cause => { if (!request.signal.aborted) setFailure(errorMessage(cause)); }).finally(() => { if (!request.signal.aborted) setLoading(false); });
    return () => request.abort(); }, [eventId]);
  async function save(remove: boolean) {
    setBusy(true); setFailure("");
    try { if (remove) { await calendarReminderApi.remove(eventId); setReminder(null); } else setReminder(await calendarReminderApi.put(eventId, minutes));
      window.dispatchEvent(new Event("wukna:notification-state-updated")); }
    catch (cause) { setFailure(errorMessage(cause)); } finally { setBusy(false); }
  }
  return <fieldset disabled={loading || busy}><legend>Scheduled-task reminder</legend>
    <p>{reminder ? `Reminder: ${new Date(reminder.dueAtUtc).toLocaleString()}` : "No reminder set"}</p>
    <label>Remind me<select value={minutes} onChange={event => setMinutes(Number(event.target.value))}>
      <option value={0}>At start time</option><option value={5}>5 minutes before</option><option value={10}>10 minutes before</option>
      <option value={30}>30 minutes before</option><option value={60}>1 hour before</option><option value={1440}>1 day before</option></select></label>
    <Button type="button" variant="secondary" size="compact" onClick={() => void save(false)}>Save reminder</Button>
    {reminder && <Button type="button" variant="quiet" size="compact" onClick={() => void save(true)}>Remove reminder</Button>}
    <p>Saved calendar reminders continue after the chat card expires.</p>{failure && <p role="alert">{failure}</p>}
  </fieldset>;
}
